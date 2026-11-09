using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FluentAssertions;
using ProcessGuardian.Domain;
using ProcessGuardian.Domain.Entities;
using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Wpf.Converters;
using ProcessGuardian.Wpf.Views;
using Xunit;
using Xunit.Sdk;

namespace ProcessGuardian.Wpf.Tests;

// R-UI-STATUS-ROW-LAYOUT: the monitoring control row must stay inside the Monitoring card for
// both IsDegraded states. The row is a fixed single-line strip inside the ~540.8 DIP inner card
// width (DesignRoot 573 - 2x15 outer margin - 2x1 card border, measured with a real WPF
// measure/arrange). Without the DEGRADED banner the row is ~527 DIP and the status pill keeps
// its balanced ~15 DIP insets; when the banner appears it previously overflowed the card by
// ~52.8 DIP (banner 8 margin + 58.4 text) and was clipped by the window edge. The fix compresses
// the row gaps ONLY while the banner is visible (15->10, 35->12 x2, 8->4 = 55 DIP reclaimed) and
// keeps the geometry untouched when it is hidden.
//
// These tests build the REAL MainWindow visual tree on an STA thread with a minimal stub
// DataContext, run a real measure/arrange at the DesignRoot width, and assert actual arranged
// bounds — no XAML string matching. Limitation: they run at the process DPI (96 DPI on CI);
// rendering at 125/150/200 % scaling stays a manual Windows acceptance item.
public class StatusRowLayoutTests
{
    private const int TimeoutMs = 10000;

    private static async Task RunOnStaThreadAsync(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeoutMs));
        if (completed != tcs.Task)
            throw new XunitException("STA work did not complete in time (possible UI-thread deadlock)");
        await tcs.Task;
    }

    // Minimal DataContext for the real MainWindow visual tree: only the members the monitoring
    // row binds to. Commands are null (bound buttons are simply disabled); the production
    // MainViewModel is not constructed in layout tests.
    private sealed class StubViewModel
    {
        public GuardianStatus Status { get; set; }
        public bool IsDegraded { get; set; }
        public string StatusReason { get; set; } = string.Empty;
        public AppSettings Settings { get; } = new();
        public bool AutostartEffective { get; set; }
        public string LogText { get; set; } = string.Empty;
        public bool IsSettingsEditable => true;
        public bool IsTargetPathEditable => true;
        public bool IsAutostartEditable => true;
        public System.Windows.Input.ICommand? StartCommand => null;
        public System.Windows.Input.ICommand? StopCommand => null;
        public System.Windows.Input.ICommand? BrowseExecutableCommand => null;
    }

    private static MainWindow CreateMeasuredWindow(GuardianStatus status, bool degraded)
    {
        // The content is measured/arranged directly (the window is never shown), so the stub is
        // set on the DesignRoot itself: property inheritance to the measured content from the
        // unshown window is not reliable, while the content's own subtree is fully live.
        var window = new MainWindow();
        var root = (FrameworkElement)window.Content;
        root.DataContext = new StubViewModel { Status = status, IsDegraded = degraded, StatusReason = "test reason" };
        // Binding (and style DataTrigger) activation is queued on the WPF Dispatcher; an
        // unshown window never pumps it, so force the pending binding work items to run now.
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() => { }));
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        root.Arrange(new Rect(new Point(0, 0), root.DesiredSize));
        return window;
    }

    private static Rect BoundsWithinCard(FrameworkElement child, FrameworkElement card)
        => child.TransformToAncestor(card).TransformBounds(new Rect(child.RenderSize));

    // ---------------------------------------------------------------------
    // Converter contract (unchanged by the layout fix)
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(GuardianStatus.Stopped, "STOPPED")]
    [InlineData(GuardianStatus.Monitoring, "MONITORING")]
    [InlineData(GuardianStatus.Waiting, "MONITORING")]
    [InlineData(GuardianStatus.Restarting, "MONITORING")]
    [InlineData(GuardianStatus.Error, "ERROR")]
    public void StatusToDisplayConverter_MapsStatusesToCaptions(GuardianStatus status, string expected)
    {
        var converter = new StatusToDisplayConverter();

        converter.Convert(status, typeof(string), null!, CultureInfo.InvariantCulture)
            .Should().Be(expected);
    }

    // ---------------------------------------------------------------------
    // Start/Disable visibility matrix (regression guard; unchanged by the fix)
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(GuardianStatus.Stopped, Visibility.Visible, Visibility.Collapsed)]
    [InlineData(GuardianStatus.Error, Visibility.Visible, Visibility.Collapsed)]
    [InlineData(GuardianStatus.Monitoring, Visibility.Collapsed, Visibility.Visible)]
    [InlineData(GuardianStatus.Waiting, Visibility.Collapsed, Visibility.Visible)]
    [InlineData(GuardianStatus.Restarting, Visibility.Collapsed, Visibility.Visible)]
    public async Task ActionButtons_VisibilityMatchesStatusMatrix(
        GuardianStatus status, Visibility startVisibility, Visibility disableVisibility)
    {
        await RunOnStaThreadAsync(() =>
        {
            var window = CreateMeasuredWindow(status, degraded: false);

            var start = (Button)window.FindName("StartButton")!;
            var disable = (Button)window.FindName("DisableButton")!;

            start.Visibility.Should().Be(startVisibility, $"Start for {status}");
            disable.Visibility.Should().Be(disableVisibility, $"Disable for {status}");
        });
    }

    // ---------------------------------------------------------------------
    // Real status-row layout, IsDegraded=false
    // ---------------------------------------------------------------------

    [Fact]
    public async Task DegradedFalse_RowFitsCard_BannerHidden_PillInsetBalancedAndPreserved()
    {
        await RunOnStaThreadAsync(() =>
        {
            var window = CreateMeasuredWindow(GuardianStatus.Error, degraded: false);
            var card = (Border)window.FindName("MonitoringCard")!;
            var row = (StackPanel)window.FindName("MonitoringRow")!;
            var pill = (Border)window.FindName("StatusPill")!;
            var degraded = (TextBlock)window.FindName("DegradedText")!;

            // The hidden banner reserves no layout width.
            degraded.Visibility.Should().Be(Visibility.Collapsed, "a hidden banner must never reserve width");
            degraded.DesiredSize.Width.Should().Be(0, "Collapsed must not occupy any row width");

            // The whole strip fits the inner card width.
            var innerWidth = ((Grid)card.Child).ActualWidth;
            row.DesiredSize.Width.Should().BeLessThanOrEqualTo(innerWidth);

            // The pre-fix balanced geometry is preserved (baseline measured at 416/527.2 on a
            // 542.4-wide card): insets are taken from the card border bounds.
            var pillBounds = BoundsWithinCard(pill, card);
            var firstChild = (FrameworkElement)VisualTreeHelper.GetChild(row, 0);
            var firstBounds = BoundsWithinCard(firstChild, card);
            var leftInset = firstBounds.Left;
            var rightInset = card.ActualWidth - pillBounds.Right;

            pillBounds.Left.Should().BeApproximately(416, 1.0, "the pill position must not move when the banner is hidden");
            leftInset.Should().BeApproximately(rightInset, 2.0, "the pill keeps its balanced left/right insets");
            pillBounds.Right.Should().BeLessThanOrEqualTo(card.ActualWidth);
        });
    }

    // ---------------------------------------------------------------------
    // Real status-row layout, IsDegraded=true
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(GuardianStatus.Error)]
    [InlineData(GuardianStatus.Monitoring)]
    public async Task DegradedTrue_BannerAndPillStayWithinCardBounds_WithoutOverlap(GuardianStatus status)
    {
        await RunOnStaThreadAsync(() =>
        {
            var window = CreateMeasuredWindow(status, degraded: true);
            var card = (Border)window.FindName("MonitoringCard")!;
            var row = (StackPanel)window.FindName("MonitoringRow")!;
            var pill = (Border)window.FindName("StatusPill")!;
            var degraded = (TextBlock)window.FindName("DegradedText")!;

            degraded.Visibility.Should().Be(Visibility.Visible, "the degraded banner must be shown in full");

            // The compressed strip fits the inner card width: nothing overflows the card.
            var innerWidth = ((Grid)card.Child).ActualWidth;
            row.DesiredSize.Width.Should().BeLessThanOrEqualTo(innerWidth,
                "the visible strip must fit the card's inner width with the banner shown");

            var pillBounds = BoundsWithinCard(pill, card);
            var degradedBounds = BoundsWithinCard(degraded, card);

            pillBounds.Right.Should().BeLessThanOrEqualTo(card.ActualWidth,
                "the status pill must stay inside the Monitoring card");
            degradedBounds.Left.Should().BeGreaterThanOrEqualTo(pillBounds.Right - 0.01,
                "the degraded banner must never overlap the status pill");
            degradedBounds.Right.Should().BeLessThanOrEqualTo(innerWidth,
                "the DEGRADED banner must be fully visible inside the card (previously clipped ~52.8 DIP)");

            // The banner must not leak past the window content either.
            degradedBounds.Right.Should().BeLessThanOrEqualTo(((FrameworkElement)window.Content).ActualWidth);
        });
    }

    // ---------------------------------------------------------------------
    // Status pill text fit (real WPF text metrics)
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(GuardianStatus.Stopped, "STOPPED")]
    [InlineData(GuardianStatus.Monitoring, "MONITORING")]
    [InlineData(GuardianStatus.Error, "ERROR")]
    public async Task StatusText_FullyFitsPill_WithoutTrimming(GuardianStatus status, string expectedCaption)
    {
        await RunOnStaThreadAsync(() =>
        {
            var window = CreateMeasuredWindow(status, degraded: true);
            var pill = (Border)window.FindName("StatusPill")!;
            var statusText = (TextBlock)window.FindName("StatusText")!;

            statusText.Text.Should().Be(expectedCaption);
            statusText.TextTrimming.Should().Be(TextTrimming.None, "no caption may be truncated with an ellipsis");

            // Real glyph width must fit the fixed 111 DIP pill; the arranged TextBlock is not
            // clipped by the pill either.
            var typeface = new Typeface(
                statusText.FontFamily, FontStyles.Normal, statusText.FontWeight, FontStretches.Normal);
            var glyphWidth = new FormattedText(
                expectedCaption, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, statusText.FontSize, Brushes.Black,
                VisualTreeHelper.GetDpi(statusText).PixelsPerDip).WidthIncludingTrailingWhitespace;

            glyphWidth.Should().BeLessThanOrEqualTo(pill.Width,
                $"the {expectedCaption} glyphs must fit the status pill width");
            statusText.ActualWidth.Should().BeLessThanOrEqualTo(pill.ActualWidth);
        });
    }
}
