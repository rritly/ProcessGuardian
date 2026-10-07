using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ProcessGuardian.Domain;

namespace ProcessGuardian.Infrastructure.Tray;

// Borderless transparent Window (not a Popup) so it hides on Deactivated and can clamp
// to the work area. All geometry is in DIP, so WPF scales it with the monitor DPI.
internal sealed class TrayMenuPopup : Window
{
    public event EventHandler? ShowClicked;
    public event EventHandler? StartStopClicked;
    public event EventHandler? ExitClicked;

    private const double DpiWidth = 158;
    private const double DpiHeight = 118;
    private const double DpiHoverHeight = 31;
    private const double DpiHitHeight = 32;
    private const double DpiRadius = 12;
    private const double DpiFontSize = 12.75;
    // Symmetric text inset (matches Specs.Tx) used to grow the window for long labels.
    private const double DpiRightPadding = 14.25;

    // DIP layout: (textX, highlightY). Text and hit-area are centered on the highlight;
    // the hit-area is 1px taller. Widths come from canvas ActualWidth (any-DPI exactness).
    private static readonly (double Tx, double Hy)[] Specs =
    {
        (14.25, 10.5),
        (14.25, 41.5),
        (14.25, 72.5),
    };

    private static readonly Color CardBg = (Color)ColorConverter.ConvertFromString("#1A252F");
    private static readonly Color CardBorder = (Color)ColorConverter.ConvertFromString("#3E4854");
    private static readonly Color HoverBg = (Color)ColorConverter.ConvertFromString("#2C3741");
    private static readonly Color TextColor = (Color)ColorConverter.ConvertFromString("#EAEBEC");

    private readonly Border _card;
    private readonly Canvas _canvas;
    private readonly TextBlock? _secondText;
    private readonly Border[] _hovers = new Border[Specs.Length];
    private readonly Border[] _hitAreas = new Border[Specs.Length];

    public TrayMenuPopup()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        ResizeMode = ResizeMode.NoResize;
        UseLayoutRounding = true;
        Width = DpiWidth;
        Height = DpiHeight;
        SourceInitialized += (_, __) => SetToolWindow();

        _canvas = new Canvas { Background = Brushes.Transparent };
        _canvas.SizeChanged += OnCanvasSizeChanged;
        _card = new Border
        {
            Background = new SolidColorBrush(CardBg),
            BorderBrush = new SolidColorBrush(CardBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(DpiRadius),
        };

        double maxTextWidth = 0;

        for (int i = 0; i < Specs.Length; i++)
        {
            var (tx, hy) = Specs[i];

            // Visual hover fill, not a hit target: the transparent hit-area below
            // catches the mouse so hover/click work over the full row.
            var hover = new Border
            {
                Background = Brushes.Transparent,
                IsHitTestVisible = false,
                Height = DpiHoverHeight,
            };
            _hovers[i] = hover;

            // Transparent hit-area; drives the hover fill and the click.
            var hit = new Border
            {
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Height = DpiHitHeight,
            };
            var index = i;
            hit.MouseEnter += (_, __) => hover.Background = new SolidColorBrush(HoverBg);
            hit.MouseLeave += (_, __) => hover.Background = Brushes.Transparent;
            hit.MouseLeftButtonUp += (_, __) => OnItemClick(index);
            _hitAreas[i] = hit;

            var text = new TextBlock
            {
                FontWeight = FontWeights.Regular,
                FontSize = DpiFontSize,
                Foreground = new SolidColorBrush(TextColor),
                IsHitTestVisible = false,
                Text = i switch
                {
                    0 => $"Show {AppEdition.Name}",
                    1 => "Start monitoring",
                    _ => $"Exit {AppEdition.Name}",
                },
            };
            if (i == 1)
                _secondText = text;

            // Center the hit-area vertically on the highlight (hit is 1px taller).
            double hitY = hy + (DpiHoverHeight - DpiHitHeight) / 2;

            // Center the text vertically on the highlight using its measured height.
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double textY = hy + (DpiHoverHeight - text.DesiredSize.Height) / 2;
            maxTextWidth = Math.Max(maxTextWidth, text.DesiredSize.Width);

            Canvas.SetLeft(hover, 0);
            Canvas.SetTop(hover, hy);
            Canvas.SetLeft(hit, 0);
            Canvas.SetTop(hit, hitY);
            Canvas.SetLeft(text, tx);
            Canvas.SetTop(text, textY);
            Canvas.SetZIndex(hit, 2);
            Canvas.SetZIndex(text, 1);

            _canvas.Children.Add(hover);
            _canvas.Children.Add(hit);
            _canvas.Children.Add(text);
        }

        // Width grows to fit the widest label ("Show {AppEdition.Name}"): text inset +
        // measured text + right padding + 1 DIP card border on each side. Hover/hit rows
        // and the work-area clamp follow the actual width; never below the designed 158 DIP.
        double required = Specs[0].Tx + maxTextWidth + DpiRightPadding + 2;
        Width = Math.Max(DpiWidth, Math.Ceiling(required));

        _card.Child = _canvas;
        Content = _card;

        Deactivated += (_, __) => Hide();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Width from the canvas ActualWidth matches the snapped pixel grid at any DPI,
        // avoiding a 1px gap or overlap with the card border.
        double w = _canvas.ActualWidth;
        for (int i = 0; i < _hovers.Length; i++)
        {
            _hovers[i].Width = w;
            _hitAreas[i].Width = w;
        }
    }

    // Second item label switches between "Start monitoring" and "Disable".
    public void SetSecondLabel(string label)
    {
        if (_secondText is not null)
            _secondText.Text = label;
    }

    public string GetSecondLabel() => _secondText?.Text ?? string.Empty;

    public void Open()
    {
        if (IsVisible)
        {
            // Menu already open: toggle hidden rather than leave it stuck.
            Hide();
            return;
        }

        // Position before showing: the menu appears directly at the cursor instead of
        // flashing at the default (centered) position for one frame.
        PlaceAtCursor();
        Show();
        Activate();
    }

    private void PlaceAtCursor()
    {
        // The cursor and monitor rects are physical (device) pixels; the window's
        // Left/Top are DIP. The monitor under the cursor (not the primary) decides
        // placement and clamping, and its effective DPI does the physical→DIP
        // conversion — correct for secondary monitors and mixed-DPI setups.
        GetCursorPos(out var pt);
        var hMonitor = MonitorFromPoint(pt, MonitorDefaultToNearest);

        var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
        if (!GetMonitorInfo(new HandleRef(this, hMonitor), ref info))
        {
            // Fallback: no monitor info — use the primary work area at unscaled DIP.
            var area = SystemParameters.WorkArea;
            double fallbackLeft = pt.X;
            double fallbackTop = pt.Y - Height;
            if (fallbackLeft + Width > area.Right) fallbackLeft = area.Right - Width;
            if (fallbackLeft < area.Left) fallbackLeft = area.Left;
            if (fallbackTop < area.Top) fallbackTop = area.Top;
            if (fallbackTop + Height > area.Bottom) fallbackTop = area.Bottom - Height;
            Left = fallbackLeft;
            Top = fallbackTop;
            return;
        }

        double scale = 1.0;
        if (GetDpiForMonitor(hMonitor, MdtEffectiveDpi, out uint dpiX, out _) == S_OK && dpiX > 0)
            scale = dpiX / 96.0;

        // Physical → DIP of the target monitor.
        double dipX = pt.X / scale;
        double dipY = pt.Y / scale;
        double workLeft = info.rcWork.left / scale;
        double workRight = info.rcWork.right / scale;
        double workTop = info.rcWork.top / scale;
        double workBottom = info.rcWork.bottom / scale;

        // Grow upward from the click point (tray is at the bottom of the screen).
        double left = dipX;
        double top = dipY - Height;

        if (left + Width > workRight) left = workRight - Width;
        if (left < workLeft) left = workLeft;
        if (top < workTop) top = workTop;
        if (top + Height > workBottom) top = workBottom - Height;

        Left = left;
        Top = top;
    }

    private void OnItemClick(int index)
    {
        switch (index)
        {
            case 0: ShowClicked?.Invoke(this, EventArgs.Empty); break;
            case 1: StartStopClicked?.Invoke(this, EventArgs.Empty); break;
            case 2: ExitClicked?.Invoke(this, EventArgs.Empty); break;
        }
        Hide();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    private void SetToolWindow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        int exStyle = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle, exStyle | WsExToolwindow);
    }

    private const int GwlExStyle = -20;
    private const int WsExToolwindow = 0x00000080;
    private const uint MonitorDefaultToNearest = 2; // MONITOR_DEFAULTTONEAREST
    private const int MdtEffectiveDpi = 0; // MDT_EFFECTIVE_DPI
    private const int S_OK = 0;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    // GetWindowLong/SetWindowLongPtr shim: on 64-bit the *Ptr variants are the
    // documented API; GWL_EXSTYLE values fit an int either way.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    private static int GetWindowLong(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex).ToInt32() : GetWindowLong32(hWnd, nIndex);

    private static void SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong)
    {
        if (IntPtr.Size == 8)
            SetWindowLongPtr64(hWnd, nIndex, new IntPtr(dwNewLong));
        else
            SetWindowLong32(hWnd, nIndex, dwNewLong);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(HandleRef hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
}
