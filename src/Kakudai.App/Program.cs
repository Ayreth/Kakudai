using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kakudai.App;

internal static class Program
{
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

    [STAThread]
    private static void Main()
    {
        // Session-local, so another Windows user can run their own copy.
        using var mutex = new Mutex(true, @"Local\Kakudai-single-instance", out var owned);
        if (!owned)
        {
            if (BringRunningInstanceToFront()) return;
            // The other Kakudai has no window: it is closing (hidden until its display has let go of the WebView2
            // folder, a few seconds at most) or still starting. Wait for it to end, then start normally.
            owned = WaitFor(mutex, TimeSpan.FromSeconds(5));
            if (!owned)
            {
                BringRunningInstanceToFront(); // it was starting: it has its window by now
                return;
            }
        }

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
        finally
        {
            // Explicitly, now: the window only closed once its display had let go of the WebView2 folder.
            mutex.ReleaseMutex();
        }
    }

    private static bool WaitFor(Mutex mutex, TimeSpan timeout)
    {
        try
        {
            return mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            return true; // its owner ended without releasing it: it is ours now
        }
    }

    /// <summary>Shows the window of the Kakudai already running. False if no other Kakudai has a visible window.</summary>
    private static bool BringRunningInstanceToFront()
    {
        using var self = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(self.ProcessName))
        {
            using (process)
            {
                if (process.Id == self.Id || process.MainWindowHandle == IntPtr.Zero) continue;
                if (IsIconic(process.MainWindowHandle)) ShowWindow(process.MainWindowHandle, SW_RESTORE);
                SetForegroundWindow(process.MainWindowHandle);
                return true;
            }
        }
        return false;
    }
}
