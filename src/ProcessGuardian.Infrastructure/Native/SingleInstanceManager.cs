using System.Runtime.InteropServices;

namespace ProcessGuardian.Infrastructure.Native;

public sealed class SingleInstanceManager : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private bool _owned;
    private bool _disposed;
    private Thread? _watcherThread;

    public event EventHandler? ShowRequested;

    public SingleInstanceManager()
    {
        // LOCAL (no Global\) names: the Global\ prefix requires elevation and crashes without it.
        _mutex = new Mutex(true, "ProcessGuardian_SingleInstance", out _owned);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "ProcessGuardian_ShowRequested", out _);
    }

    public bool IsFirstInstance() => _owned;

    public void ActivateFirstInstance()
    {
        try { _showEvent.Set(); } catch { }

        try
        {
            var hwnd = NativeMethods.FindWindow(null, "ProcessGuardian");
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(hwnd)) return;
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        catch (Exception)
        {
            // Best-effort activation; ignore failures
        }
    }

    public void StartWatching()
    {
        if (_disposed || _watcherThread != null) return;

        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (_showEvent.WaitOne(200))
                        ShowRequested?.Invoke(this, EventArgs.Empty);
                }
                catch
                {
                    // Ignore transient wait failures; loop continues until disposed.
                }

                if (_disposed) return;
            }
        })
        { IsBackground = true };

        _watcherThread = thread;
        thread.Start();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _showEvent.Set(); } catch { }
        try { _showEvent.Dispose(); } catch { }
        try { _mutex.Dispose(); } catch { }
    }

    private static class NativeMethods
    {
        public const int SW_RESTORE = 9;
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);
    }
}
