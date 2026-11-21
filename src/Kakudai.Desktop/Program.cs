using Kakudai.Core.Game;
using Kakudai.Host;
using Photino.NET;

namespace Kakudai.Desktop;

/// <summary>
/// Kakudai's window on Linux and macOS: the page of wwwroot in Photino, talking to the Core through <see cref="Bridge"/>,
/// as Kakudai.App does on Windows. The window keeps the system's title bar (Photino cannot drag a window without one)
/// and the page hides its own buttons (<c>?chrome=native</c>). It is 430 px wide and as tall as the page asks.
/// </summary>
internal static class Program
{
    // WebKit (Linux, macOS) only lets an app answer requests of a scheme of its own; WebView2 (a Windows test build)
    // will not navigate to one, but lets the app answer http requests. As Photino.Blazor does.
    private static readonly string Scheme = OperatingSystem.IsWindows() ? "http" : "kakudai";
    private static readonly string StartPage = $"{Scheme}://app/index.html?chrome=native";
    private static readonly string BackgroundPrefix = $"{Scheme}://bg/";
    private const int LogicalWidth = 430;
    private const int DefaultLogicalHeight = 525;

#if DEBUG
    private const bool DevBuild = true;
#else
    private const bool DevBuild = false;
#endif

    private static readonly string WebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");

    /// <summary>KAKUDAI_TRACE=&lt;file&gt;: every message, request and size change is appended there (for a bug report).</summary>
    private static readonly string? TracePath = Environment.GetEnvironmentVariable("KAKUDAI_TRACE");
    private static readonly Lock TraceLock = new();

    private static void Trace(string line)
    {
        if (string.IsNullOrEmpty(TracePath)) return;
        try
        {
            lock (TraceLock) File.AppendAllText(TracePath, $"{DateTime.Now:HH:mm:ss.fff} {line}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static PhotinoWindow? _window;
    private static Bridge? _bridge;
    private static volatile bool _ready;
    private static volatile bool _closing;

    // Outer height minus the page's visible height: the system title bar, learnt from the page's first answers.
    private static int _chrome;
    private static double _scale = 1;

    [STAThread]
    private static void Main()
    {
        // One Kakudai per user: a second one would follow the same game and write the same settings.
        using var mutex = new Mutex(true, "Kakudai-single-instance-" + Environment.UserName, out var owned);
        if (!owned) return;

        var dataDir = OsuPaths.ActiveLazerDataDir(); // read only: nothing is ever written there
        Directory.CreateDirectory(OsuPaths.KakudaiDataDir);
        _bridge = new Bridge(
            dataDir,
            OsuPaths.LazerDataDirs,
            OsuPaths.TempDir,
            OsuPaths.GeneratedDir,
            Path.Combine(OsuPaths.KakudaiDataDir, "settings.json"),
            PostToPage) { BackgroundPrefix = BackgroundPrefix };
        _bridge.WindowAction += OnWindowAction;
        _bridge.HeightRequested += OnHeightRequested;

        var window = new PhotinoWindow()
            .SetTitle("Kakudai")
            .SetUseOsDefaultSize(false)
            .SetSize(LogicalWidth, DefaultLogicalHeight)
            .SetResizable(false)
            .SetDevToolsEnabled(DevBuild)
            .SetContextMenuEnabled(DevBuild)
            .Center()
            .RegisterCustomSchemeHandler(Scheme, Serve)
            .RegisterWebMessageReceivedHandler((_, message) =>
            {
                Trace("page → " + message);
                _bridge.HandleMessage(message);
            });
        var icon = Path.Combine(AppContext.BaseDirectory, "kakudai.png");
        if (File.Exists(icon) && !OperatingSystem.IsMacOS()) window.SetIconFile(icon); // macOS: the .app's icon
        window.WindowCreated += (_, _) =>
        {
            _ready = true;
            _bridge.Start(); // reads the game base while the page loads
        };
        window.WindowClosing += (_, _) =>
        {
            _closing = true;
            _bridge.Dispose();
            return false; // close
        };
        _window = window;

        window.Load(new Uri(StartPage));
        window.WaitForClose();
        mutex.ReleaseMutex();
    }

    /// <summary>Sends a message to the page. Called from any thread; dropped before the window exists or once closing.</summary>
    private static void PostToPage(string json)
    {
        if (!_ready || _closing || _window is not { } window) return;
        Trace("→ page " + json);
        try
        {
            window.SendWebMessage(json);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or ApplicationException)
        {
            // The page is going away; it asks for the state again with "ready".
        }
    }

    private static void OnWindowAction(string action)
    {
        if (_window is not { } window) return;
        switch (action)
        {
            case "minimize":
                window.SetMinimized(true);
                break;
            case "close":
                window.Close();
                break;
            // "drag": the system title bar moves the window.
        }
    }

    /// <summary>
    /// Makes the page's content fit: the window's outer height is the content plus the title bar, which the page's
    /// visible height tells (outer − viewport). A wrong guess is corrected on the page's next answer, sent when the
    /// window changes size.
    /// </summary>
    private static void OnHeightRequested(HeightRequest request)
    {
        if (_window is not { } window) return;
        // Photino sizes are device pixels on Windows, logical (points) on Linux and macOS.
        _scale = OperatingSystem.IsWindows() ? request.Scale : 1;
        try
        {
            var outer = window.Height;
            if (request.Viewport is { } viewport)
            {
                var chrome = outer - (int)Math.Round(viewport * _scale);
                if (chrome is >= 0 and < 200) _chrome = chrome;
            }
            var height = (int)Math.Ceiling(request.Height * _scale) + _chrome;
            var width = (int)Math.Ceiling(LogicalWidth * _scale);
            Trace($"size: outer {window.Width}x{outer}, page {request.Height} (viewport {request.Viewport}, scale {request.Scale}), title bar {_chrome} → {width}x{height}");
            if (height == outer && width == window.Width) return;
            window.SetSize(width, height);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ApplicationException)
        {
        }
    }

    /// <summary>
    /// <c>kakudai://app/…</c>: the files of wwwroot. <c>kakudai://bg/&lt;key&gt;</c>: the background of a map the page
    /// was sent, straight from the game's files (read only, shared with the game). Anything else: nothing.
    /// </summary>
    private static Stream? Serve(object sender, string scheme, string url, out string contentType)
    {
        contentType = "text/plain";
        Trace("request " + url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');

        if (uri.Host == "bg")
        {
            if (_bridge is null || !_bridge.TryGetBackground(path, out var file, out var type)) return null;
            contentType = type;
            return ReadShared(file);
        }

        if (uri.Host != "app" || path.Length == 0) return null;
        var full = Path.GetFullPath(Path.Combine(WebRoot, path));
        if (!full.StartsWith(WebRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full)) return null;
        contentType = Path.GetExtension(full).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".woff2" => "font/woff2",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".json" => "application/json",
            _ => "text/plain; charset=utf-8",
        };
        return ReadShared(full);
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
}
