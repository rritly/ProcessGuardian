using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace ProcessGuardian.Wpf.Views;

public partial class MainWindow : Window
{
    private TextBox? _logTextBox;
    private bool _stickToBottom = true;
    private HwndSource? _hwndSource;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
    }

    // An explicit commit point for the target-path draft (R-CD): focus leaving the TextBox
    // commits the draft as the controller-owned committed path via the ViewModel, so the
    // draft is only ever touched by Browse / focus-lost / an explicit commit command.
    private void TargetPathTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel vm)
            vm.CommitTargetPathDraft();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // Maximize must not resize the fixed-size window: snap straight back.
        if (WindowState == WindowState.Maximized)
            WindowState = WindowState.Normal;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyCaptionChrome();
        DisableMaximize();
        _logTextBox = LogTextBox;
        LogViewer.ScrollChanged += OnLogScrollChanged;
        if (_logTextBox is not null)
            _logTextBox.TextChanged += OnLogTextChanged;
        LogViewer.ScrollToEnd();
    }

    // Color the Win32 title bar (#1A252F) and request the dark caption theme. The
    // WPF Window.Background does not paint the OS caption; DwmSetWindowAttribute does.
    private void ApplyCaptionChrome()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            int useDark = 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, Marshal.SizeOf<int>());

            // COLORREF is 0x00BBGGRR; for #1A252F that is BGR = 0x002F251A.
            int captionColor = 0x002F251A;
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref captionColor, Marshal.SizeOf<int>());
        }
        catch
        {
            // DWM caption attributes are best-effort; ignore when unavailable.
        }
    }

    // Maximize is not wanted for this fixed-size window. The caption button must STAY
    // visible but be grayed and inert: gray via the system menu, swallow SC_MAXIMIZE/RESTORE.
    private void DisableMaximize()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WndProc);
            Closed += (_, __) => _hwndSource?.RemoveHook(WndProc);

            var sysMenu = GetSystemMenu(hwnd, false);
            if (sysMenu != IntPtr.Zero)
                EnableMenuItem(sysMenu, ScMaximize, MfByCommand | MfGrayed | MfDisabled);
        }
        catch
        {
            // Best-effort; a non-grayed maximize button is acceptable if unavailable.
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmSysCommand)
        {
            int cmd = wParam.ToInt32() & 0xFFF0;
            if (cmd == ScMaximize || (cmd == ScRestore && WindowState == WindowState.Maximized))
            {
                handled = true;
                return IntPtr.Zero;
            }
        }
        return IntPtr.Zero;
    }

    // ---- Scrollbar ----------------------------------------------------------

    private void UpdateScrollThumb()
    {
        try
        {
            if (LogViewer.Template.FindName("PART_VerticalScrollBar", LogViewer) is not ScrollBar sb ||
                sb.Template.FindName("ThumbPart", sb) is not Thumb thumb)
            {
                return;
            }

            if (LogViewer.ScrollableHeight <= 0)
            {
                // Nothing to scroll: thumb fills the whole track between the arrows.
                // ViewportHeight and the arrow rows (15 and 16) are all in DIP.
                double trackDip = LogViewer.ViewportHeight - 15.0 - 16.0;
                thumb.Height = Math.Max(0, trackDip);
            }
            else
            {
                thumb.Height = double.NaN; // let the Track compute proportionally
            }
        }
        catch
        {
            // Best-effort thumb sizing.
        }
    }

    // ---- Autoscroll (LogViewer) --------------------------------------------

    private void OnLogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange != 0)
        {
            if (_stickToBottom)
                LogViewer.ScrollToEnd();
            return;
        }

        _stickToBottom = IsAtBottom();
        UpdateScrollThumb();
    }

    private void OnLogTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_logTextBox is not null && _logTextBox.Text.Length == 0)
        {
            _stickToBottom = true;
            LogViewer.ScrollToEnd();
            return;
        }

        if (_stickToBottom)
            LogViewer.ScrollToEnd();
    }

    private bool IsAtBottom()
    {
        return LogViewer.VerticalOffset + LogViewer.ViewportHeight >= LogViewer.ExtentHeight - 2;
    }

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;

    private const int WmSysCommand = 0x0112;
    private const int ScMaximize = 0xF030;
    private const int ScRestore = 0xF120;
    private const uint MfByCommand = 0x00000000;
    private const uint MfGrayed = 0x00000001;
    private const uint MfDisabled = 0x00000002;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

    [DllImport("user32.dll")]
    private static extern bool EnableMenuItem(IntPtr hMenu, uint uIDEnableItem, uint uEnable);
}
