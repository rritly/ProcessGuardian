using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ProcessGuardian.Wpf.Notifications;

public partial class ToastCard : UserControl
{
    private Action? _closeAction;
    private readonly DispatcherTimer _autoHideTimer;

    // Card geometry authored in DIP at 75% of the legacy physical size (254x81).
    // WPF scales these with the monitor DPI, so no manual DPI compensation is needed.
    private const double CardWidth = 191;
    private const double CardHeight = 61;
    private const double CardCornerRadius = 12;
    private const double CardBorder = 1;
    private const double IconSize = 30;
    private const double IconLeftMargin = 12.75;
    private const double TextFontSize = 12;
    private const double RightMargin = 9;
    private const double Gap = 6;

    public ToastCard(string message)
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
        MessageText.Text = message;
        MessageText.Foreground = ResolveBrush(message);
        LoadIcon();
        ApplyLayout();

        _autoHideTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _autoHideTimer.Tick += (_, __) => Dismiss();
    }

    private void ApplyLayout()
    {
        Width = CardWidth;
        Height = CardHeight;

        RootBorder.CornerRadius = new CornerRadius(CardCornerRadius);
        RootBorder.BorderThickness = new Thickness(CardBorder);

        IconContainer.Width = IconSize;
        IconContainer.Height = IconSize;
        IconContainer.Margin = new Thickness(IconLeftMargin, 0, 6, 0);

        IconImage.Width = IconSize;
        IconImage.Height = IconSize;

        MessageText.FontSize = TextFontSize;
        MessageText.Margin = new Thickness(0, 0, RightMargin, 0);

        // Gap between stacked cards
        Margin = new Thickness(0, 0, 0, Gap);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _autoHideTimer?.Stop();
    }

    private static Brush ResolveBrush(string message)
    {
        return message switch
        {
            "Process Recovered" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4CC86A")),
            "Recovery Failed" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FC3847")),
            "Target Changed" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FC3847")),
            _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF")),
        };
    }

    private void LoadIcon()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            int desiredPx = (int)Math.Ceiling(IconSize * dpi.DpiScaleX);

            // Load the embedded icon at a resolution suited for the current DPI scale so
            // we do not upsample a tiny 16×16 frame onto a 30×30 DIP slot.
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            // Relative pack URI (no assembly name): resolves against the local assembly,
            // which is renamed in the admin edition build.
            bitmap.UriSource = new Uri("pack://application:,,,/;component/Assets/AppIcon.ico");
            bitmap.DecodePixelWidth = Math.Max(1, desiredPx);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.EndInit();
            IconImage.Source = bitmap;
        }
        catch
        {
            IconImage.Visibility = Visibility.Collapsed;
        }
    }

    public void Show(Action closeAction)
    {
        _closeAction = closeAction;
        _autoHideTimer.Start();

        if (Resources["EntranceStoryboard"] is Storyboard entrance)
        {
            entrance.Begin();
        }
    }

    public void Dismiss()
    {
        if (_autoHideTimer.IsEnabled)
            _autoHideTimer.Stop();

        if (Resources["ExitStoryboard"] is Storyboard exit)
        {
            exit.Completed += (_, __) => _closeAction?.Invoke();
            exit.Begin();
        }
        else
        {
            _closeAction?.Invoke();
        }
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (_autoHideTimer.IsEnabled)
            _autoHideTimer.Stop();
        Dismiss();
    }
}
