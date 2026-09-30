using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

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
        // Card background is opaque; this re-enables ClearType inside the transparent window.
        RenderOptions.SetClearTypeHint(_card, ClearTypeHint.Enabled);

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
                    0 => "Show ProcessGuardian",
                    1 => "Start monitoring",
                    _ => "Exit ProcessGuardian",
                },
            };
            if (i == 1)
                _secondText = text;

            // Center the hit-area vertically on the highlight (hit is 1px taller).
            double hitY = hy + (DpiHoverHeight - DpiHitHeight) / 2;

            // Center the text vertically on the highlight using its measured height.
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double textY = hy + (DpiHoverHeight - text.DesiredSize.Height) / 2;

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

        Show();
        PlaceAtCursor();
        Activate();
    }

    private void PlaceAtCursor()
    {
        // Cursor is reported in physical (device) pixels; convert to DIP through the
        // WPF composition transform, then clamp to the work area in DIP coordinates.
        GetCursorPos(out var pt);
        var source = PresentationSource.FromVisual(this);
        var dip = source?.CompositionTarget.TransformFromDevice.Transform(new Point(pt.X, pt.Y))
                  ?? new Point(pt.X, pt.Y);

        var area = SystemParameters.WorkArea;

        // Grow upward from the click point (tray is at the bottom of the screen).
        double left = dip.X;
        double top = dip.Y - Height;

        if (left + Width > area.Right) left = area.Right - Width;
        if (left < area.Left) left = area.Left;
        if (top < area.Top) top = area.Top;
        if (top + Height > area.Bottom) top = area.Bottom - Height;

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

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
}
