using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using FluentAssertions;
using ProcessGuardian.Wpf.Notifications;
using Xunit;
using Xunit.Sdk;

namespace ProcessGuardian.Wpf.Tests;

// R-UI-TOAST card layout: the card is a fixed 191x61 DIP surface and every standard title must
// fit without ellipsis. The text column is wrap-enabled with trimming disabled (no ellipsis), so
// each standard single-line title must never exceed the available column width. The same
// Typeface / em-size / column width are measured via FormattedText on the STA thread with real
// WPF text metrics (no DPI assumption).
public class ToastCardLayoutTests
{
    private const int TimeoutMs = 10000;

    // Card geometry from ToastCard.xaml.cs (DIP): 191 width, 61 height. The text column spans
    // from after the icon (IconLeftMargin + IconSize + gap) to the RightMargin, minus the 1 DIP
    // border on each side of the root border.
    private const double TextColumnWidth = 191 - 2 - 12.75 - 30 - 6 - 9;

    // The five standard toast titles (R-UI-TOAST): any other title reaching the overlay is a
    // regression. Each must fit on one line so the fixed single-line column shows it fully.
    private static readonly string[] StandardTitles =
    {
        "Monitoring Started",
        "Monitoring Stopped",
        "Process Recovered",
        "Recovery Failed",
        "Target Changed",
    };

    private static async Task RunOnStaThreadAsync(Action<ToastCard> action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                // Force a real measure pass so FontFamily/PixelWidth derive from actual
                // presentation fonts rather than a default fabricated face.
                var card = new ToastCard(string.Empty);
                card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                action(card);
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

    [Fact]
    public async Task Card_PresentsSingleLineWrappedText_WithinFixedSurface()
    {
        await RunOnStaThreadAsync(card =>
        {
            var text = card.MessageText;
            text.TextWrapping.Should().Be(TextWrapping.Wrap,
                "the fixed card must wrap long content instead of clipping it");
            text.TextTrimming.Should().Be(TextTrimming.None,
                "no title may be truncated with an ellipsis inside the fixed surface");
            text.FontWeight.Should().Be(FontWeights.SemiBold);
            // WPF TextBlock has no MaxLines and uses wrap with no trimming; the fixed 61 DIP card
            // height combined with the single-line-fit guarantees below keeps every real title on
            // one intact line with no ellipsis.
        });
    }

    [Theory]
    [MemberData(nameof(StandardTitleCases))]
    public async Task EachStandardTitle_FitsOnOneLine(string title)
    {
        await RunOnStaThreadAsync(card =>
        {
            var t = card.MessageText;
            var typeface = new Typeface(t.FontFamily, FontStyles.Normal, t.FontWeight, FontStretches.Normal);
            var oneLine = new FormattedText(
                title, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, t.FontSize,
                Brushes.Black, VisualTreeHelper.GetDpi(card).PixelsPerDip);

            oneLine.WidthIncludingTrailingWhitespace.Should().BeLessThanOrEqualTo(
                TextColumnWidth,
                $"standard title \"{title}\" must fit the fixed single-line text column without wrapping/ellipsis");
            oneLine.Height.Should().BeLessThanOrEqualTo(card.Height,
                "a single line must stay inside the fixed 61 DIP card height");
        });
    }

    public static TheoryData<string> StandardTitleCases()
    {
        var data = new TheoryData<string>();
        foreach (var title in StandardTitles)
            data.Add(title);
        return data;
    }
}
