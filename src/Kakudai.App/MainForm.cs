using System.Runtime.InteropServices;
using Kakudai.Core.Game;
using Kakudai.Host;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Kakudai.App;

/// <summary>
/// The single window: the page of wwwroot in a WebView2, talking to the Core through <see cref="Bridge"/>. It is a
/// compact tool window of fixed size: 430 px wide, as tall as the page asks (window action "height"). It has no native
/// title bar or border, the page draws edge to edge and drives the window with "window" messages, but it keeps a
/// sizable frame style so that Windows 11 still gives it its shadow and rounded corners.
/// </summary>
internal sealed class MainForm : Form
{
    private const string HostName = "kakudai.local";
    private const string StartPage = "https://kakudai.local/index.html";

    // Window size in pixels at 96 DPI, which are the CSS px of the page. The height is a first guess, replaced by the
    // height the page measures for itself as soon as it is shown.
    private const int LogicalWidth = 430;
    private const int DefaultLogicalHeight = 525;

#if DEBUG
    private static readonly bool DevBuild = true;
#else
    private static readonly bool DevBuild = false;
#endif

    // Same colour as the page, so neither the window nor the WebView flashes white while loading.
    private static readonly Color Ink = Color.FromArgb(0x0B, 0x0B, 0x0C);

    /// <summary>How long a closing window waits, hidden, for the WebView2 browser process to exit.</summary>
    private const int ReleaseTimeoutMs = 3000;

    private static readonly string UserDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kakudai", "WebView2");

    private WebView2 _web; // replaced by a new one if the display has to start again
    private CoreWebView2Environment? _environment; // of the current _web, once created
    private readonly Bridge _bridge;
    private volatile bool _closing;
    private bool _releasing; // closing: waiting, hidden, for the browser process to exit
    private bool _webReleased; // that process is gone (or waited for long enough): the window can close
    private System.Windows.Forms.Timer? _releaseTimer;
    private double _logicalHeight = DefaultLogicalHeight;
    private bool _sizePending;

    public MainForm()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Kakudai";
        if (LoadAppIcon() is { } icon) Icon = icon; // taskbar and Alt+Tab; the exe gets it from <ApplicationIcon>
        Size =new Size(LogicalWidth, DefaultLogicalHeight);
        MaximizeBox = false; // also keeps Win+Up and the system menu from maximizing it
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Ink;

        _web = NewWebView();
        Controls.Add(_web);
        ResumeLayout(false);

        var dataDir = OsuPaths.ActiveLazerDataDir(); // read only: nothing is ever written there
        _bridge = new Bridge(
            dataDir,
            OsuPaths.LazerDataDirs,
            OsuPaths.TempDir,
            OsuPaths.GeneratedDir,
            Path.Combine(OsuPaths.KakudaiDataDir, "settings.json"),
            PostToPage);
        _bridge.WindowAction += OnWindowAction;
        _bridge.HeightRequested += OnHeightRequested;
    }

    /// <summary>The Kakudai icon (all its sizes), embedded from kakudai.ico; null if it is missing.</summary>
    private static Icon? LoadAppIcon()
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("Kakudai.App.kakudai.ico");
        return stream is null ? null : new Icon(stream);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplySize();
        CenterToScreen();
        _bridge.Start(); // reads the game base while WebView2 starts
        await StartWebViewAsync(fresh: false);
    }

    private static WebView2 NewWebView() => new()
    {
        Dock = DockStyle.Fill,
        DefaultBackgroundColor = Ink,
        AllowExternalDrop = false,
    };

    /// <summary>
    /// Starts the display, on a new WebView2 control if <paramref name="fresh"/>. A failure is tried once more, on a
    /// new control, after a second (the browser process of a Kakudai that just closed may still hold the WebView2
    /// folder); a second failure tells the player and closes the app. Never throws.
    /// </summary>
    private async Task StartWebViewAsync(bool fresh)
    {
        for (var attempt = 1; ; attempt++)
        {
            var web = _web;
            try
            {
                if (fresh || attempt > 1) ReplaceWebView();
                web = _web;
                await InitializeWebViewAsync();
                return;
            }
            catch (Exception ex)
            {
                // Closing, or a restart (OnProcessFailed) replaced this control meanwhile and starts its own.
                if (_closing || web != _web) return;
                if (attempt == 1)
                {
                    await Task.Delay(1000);
                    if (_closing) return;
                    continue;
                }
                MessageBox.Show(this,
                    "The display could not start. Check that the Microsoft Edge WebView2 Runtime is installed.\n\n" + ex.Message,
                    "Kakudai", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }
        }
    }

    /// <summary>Puts a new, not yet started WebView2 control in place of the current one.</summary>
    private void ReplaceWebView()
    {
        var old = _web;
        _environment = null;
        _web = NewWebView();
        SuspendLayout();
        Controls.Add(_web);
        Controls.Remove(old);
        ResumeLayout(true); // docks the new control now: the window is already shown
        try
        {
            old.Dispose();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // Its browser process is already gone.
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var round = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int)); // Windows 11 only
        // Recompute the frame now that WndProc removes it.
        Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_FRAMECHANGED | Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            // The whole window is client area: no caption, no visible border, no edge to resize it with.
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplySize(); // same logical size on the new screen
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_closing)
        {
            _closing = true;
            _bridge.Dispose();
        }

        // The WebView2 browser process outlives the window by a second or so, and keeps the WebView2 folder until it
        // exits: a Kakudai started meanwhile would share that dying process and lose its display with it. So the
        // window hides, lets go of the WebView and only closes once that process has exited (3 s at most). Not when
        // Windows shuts down: nobody starts Kakudai again then.
        if (!_webReleased && _environment is { } environment && e.CloseReason != CloseReason.WindowsShutDown)
        {
            e.Cancel = true;
            if (_releasing) return;
            _releasing = true;
            Hide();
            environment.BrowserProcessExited += (_, _) => OnWebReleased();
            _releaseTimer = new System.Windows.Forms.Timer { Interval = ReleaseTimeoutMs };
            _releaseTimer.Tick += (_, _) => OnWebReleased();
            _releaseTimer.Start();
            // Not right here: "close" comes from the page, inside a WebView2 event.
            BeginInvoke((MethodInvoker)(() =>
            {
                try
                {
                    _web.Dispose();
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException)
                {
                    // Its browser process is already gone: the timer closes the window.
                }
            }));
            return;
        }
        base.OnFormClosing(e);
    }

    private void OnWebReleased()
    {
        if (_webReleased) return;
        _webReleased = true;
        _releaseTimer?.Dispose();
        BeginInvoke((MethodInvoker)Close);
    }

    private async Task InitializeWebViewAsync()
    {
        var web = _web;
        var environment = await CoreWebView2Environment.CreateAsync(null, UserDataDir);
        if (web != _web) return; // replaced meanwhile
        _environment = environment;
        await web.EnsureCoreWebView2Async(environment);
        if (_closing || web != _web) return;

        var core = web.CoreWebView2;
        var settings = core.Settings;
        settings.AreDevToolsEnabled = DevBuild;
        settings.AreBrowserAcceleratorKeysEnabled = DevBuild; // no reload, find or print outside of debug builds
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false; // Ctrl + wheel and pinch would scale the page
        // Only the app's own local page is shown. SmartScreen would send its URL over the network and holds
        // every load of the page for about 2 s.
        settings.IsReputationCheckingRequired = false;

        core.ContextMenuRequested += (_, e) =>
        {
            // Keep the copy / paste menu of the text fields, nothing else.
            if (!DevBuild && !e.ContextMenuTarget.IsEditable) e.Handled = true;
        };
        core.ProcessFailed += OnProcessFailed;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith("https://" + HostName + "/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
        };

        core.SetVirtualHostNameToFolderMapping(HostName, Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            CoreWebView2HostResourceAccessKind.Allow);
        // WebResourceRequested is never raised for a folder-mapped host (WebView2 157), so the backgrounds
        // have a host of their own.
        core.AddWebResourceRequestedFilter(Bridge.DefaultBackgroundPrefix + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.WebMessageReceived += (_, e) => _bridge.HandleMessage(e.WebMessageAsJson);

        core.Navigate(StartPage);
    }

    private readonly List<DateTime> _reloads = new();
    private readonly List<DateTime> _restarts = new();

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (_closing) return;
        var now = DateTime.UtcNow;
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                // At most 3 reloads a minute, so a page that keeps crashing does not reload forever.
                _reloads.RemoveAll(t => now - t > TimeSpan.FromMinutes(1));
                if (_reloads.Count >= 3)
                {
                    GiveUp();
                    break;
                }
                _reloads.Add(now);
                _web.CoreWebView2?.Reload(); // the page sends "ready" again and gets the current state
                break;
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                // The browser process is gone (for instance the one of a Kakudai that was closing when this one
                // started): the display starts again on a new WebView2, and the page gets its state back with
                // "ready". At most twice a minute (a browser process started right after one was killed can still
                // crash once); beyond that the app gives up.
                _restarts.RemoveAll(t => now - t > TimeSpan.FromMinutes(1));
                if (_restarts.Count < 2)
                {
                    _restarts.Add(now);
                    RestartWhenReleased(_environment);
                    break;
                }
                GiveUp();
                break;
        }
    }

    private void GiveUp()
    {
        MessageBox.Show(this, "Kakudai's display component stopped. The app will close; please start it again.",
            "Kakudai", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Close();
    }

    /// <summary>
    /// Starts the display again on a new WebView2 once the dead one's processes have let go of the WebView2 folder
    /// (<paramref name="dead"/>'s BrowserProcessExited, or 3 s at most): a new environment created before that could
    /// join the dying browser process and die with it.
    /// </summary>
    private void RestartWhenReleased(CoreWebView2Environment? dead)
    {
        var started = false;
        var timer = new System.Windows.Forms.Timer { Interval = ReleaseTimeoutMs };
        void Start()
        {
            if (started) return;
            started = true;
            timer.Dispose();
            if (!_closing) _ = StartWebViewAsync(fresh: true);
        }
        timer.Tick += (_, _) => Start();
        timer.Start();
        // Through BeginInvoke: never inside the WebView2 event being raised.
        if (dead is not null)
            dead.BrowserProcessExited += (_, _) =>
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke((MethodInvoker)Start);
            };
    }

    /// <summary>Serves a map background straight from the game files (read only, shared with the game).</summary>
    private async void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        CoreWebView2Deferral? deferral = null;
        try
        {
            var environment = ((CoreWebView2)sender!).Environment;
            var hash = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
            if (!_bridge.TryGetBackground(hash, out var file, out var contentType))
            {
                e.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                return;
            }

            deferral = e.GetDeferral();
            var content = await Task.Run(() => ReadShared(file));
            e.Response = content is null
                ? environment.CreateWebResourceResponse(null, 404, "Not Found", "")
                : environment.CreateWebResourceResponse(content, 200, "OK",
                    $"Content-Type: {contentType}\r\nAccess-Control-Allow-Origin: https://{HostName}\r\nCache-Control: max-age=31536000, immutable");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException
                                       or UriFormatException or ArgumentException)
        {
            // The window is closing, or the request cannot be answered: the page shows no background.
        }
        finally
        {
            deferral?.Complete();
        }
    }

    private static MemoryStream? ReadShared(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var copy = new MemoryStream();
            file.CopyTo(copy);
            copy.Position = 0;
            return copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Sends a message to the page. Called from any thread; dropped before the page exists or once closing.</summary>
    private void PostToPage(string json)
    {
        if (_closing || !IsHandleCreated) return;
        try
        {
            BeginInvoke((MethodInvoker)(() =>
            {
                if (_closing || _web.CoreWebView2 is not { } core) return;
                try
                {
                    core.PostWebMessageAsJson(json);
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
                {
                    // The page is going away; it asks for the state again with "ready".
                }
            }));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The window is being destroyed.
        }
    }

    /// <summary>
    /// Handles {type:"window", action} from the page. Runs on the UI thread, inside WebMessageReceived: "drag"
    /// must start the move while the mouse button is still down.
    /// </summary>
    private void OnWindowAction(string action)
    {
        switch (action)
        {
            case "minimize":
                WindowState = FormWindowState.Minimized;
                break;
            case "close":
                Close();
                break;
            case "drag":
                // Hands the drag to Windows as if the title bar was pressed.
                Native.ReleaseCapture();
                Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, Native.HTCAPTION, IntPtr.Zero);
                break;
        }
    }

    /// <summary>The page asks for its own height, in CSS px, when its content grows or shrinks. UI thread.</summary>
    private void OnHeightRequested(HeightRequest request)
    {
        // No title bar here: the page is the whole window, its content height is the window's.
        _logicalHeight = request.Height;
        if (WindowState == FormWindowState.Minimized) _sizePending = true;
        else ApplySize();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!_sizePending || WindowState == FormWindowState.Minimized) return;
        _sizePending = false;
        ApplySize(); // the page changed its height while the window was minimized
    }

    /// <summary>
    /// Gives the window its fixed size at the current DPI: 430 × the page height. Minimum = maximum, so Snap cannot
    /// stretch it. The top edge stays put unless the window would leave the screen; a screen too short makes the page
    /// scroll.
    /// </summary>
    private void ApplySize()
    {
        var area = Screen.FromControl(this).WorkingArea;
        var size = new Size(
            Math.Min(area.Width, (int)Math.Ceiling(LogicalWidth * DeviceDpi / 96.0)),
            Math.Min(area.Height, (int)Math.Ceiling(_logicalHeight * DeviceDpi / 96.0)));
        if (size == Size && MinimumSize == size && MaximumSize == size) return;

        var bounds = new Rectangle(Location, size);
        if (bounds.Bottom > area.Bottom) bounds.Y = Math.Max(area.Top, area.Bottom - size.Height);
        MinimumSize = Size.Empty;
        MaximumSize = Size.Empty;
        Bounds = bounds;
        MinimumSize = size;
        MaximumSize = size;
    }
}

internal static class Native
{
    public const int WM_NCCALCSIZE = 0x0083;
    public const int WM_NCLBUTTONDOWN = 0x00A1;

    public static readonly IntPtr HTCAPTION = 2;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;

    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_ROUND = 2;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
