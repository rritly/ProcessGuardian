using System.Threading;
using System.Windows;
using FluentAssertions;
using ProcessGuardian.Wpf.Notifications;
using Xunit;
using Xunit.Sdk;

namespace ProcessGuardian.Wpf.Tests;

public class ToastEvictionTests
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
            throw new XunitException("STA work did not complete in time (possible UI-thread hang)");
        await tcs.Task;
    }

    [Fact]
    public async Task Eviction_FifthCard_RemovesOldestSynchronously_KeepsStackAtMax4()
    {
        await RunOnStaThreadAsync(() =>
        {
            var window = new OverlayWindow();
            for (var i = 0; i < 5; i++)
            {
                // Same eviction contract as ToastHost.ShowCard: the loop must terminate,
                // so RemoveOldestCard has to decrease the count synchronously.
                while (window.CardCount >= 4)
                {
                    window.RemoveOldestCard();
                }

                var card = new ToastCard("Card " + i);
                window.AddCard(card);
                card.Show(() => window.RemoveCard(card));
            }

            window.CardCount.Should().Be(4, "the stack must never exceed 4 cards");
        });
    }

    [Fact]
    public async Task Eviction_LiveCard_LateDismissIsNoOp_DoesNotInvokeClose()
    {
        await RunOnStaThreadAsync(() =>
        {
            var window = new OverlayWindow();
            var card = new ToastCard("Victim");
            window.AddCard(card);
            var closeCount = 0;
            card.Show(() => closeCount++);

            window.RemoveOldestCard();
            window.CardCount.Should().Be(0, "eviction removes the card synchronously");

            // Simulates a late auto-hide tick or click after eviction: the detached
            // card must never call back into the overlay.
            card.Dismiss();
            card.IsDismissed.Should().BeTrue();
            closeCount.Should().Be(0);
        });
    }

    [Fact]
    public async Task Dismiss_DuplicateCalls_AreNoOps()
    {
        await RunOnStaThreadAsync(() =>
        {
            var card = new ToastCard("Test");
            var closeCount = 0;
            card.Show(() => closeCount++);

            card.Dismiss();
            card.IsDismissed.Should().BeTrue("the first Dismiss must latch the guard");

            card.Dismiss();
            card.Dismiss();
            card.IsDismissed.Should().BeTrue();
            closeCount.Should().Be(0, "close is deferred to the exit animation completion, exactly once");
        });
    }
}
