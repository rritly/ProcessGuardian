using System.Diagnostics;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ProcessGuardian.Application.Services;
using ProcessGuardian.Domain.Entities;
using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Domain.Interfaces;
using ProcessGuardian.Infrastructure.Logging;
using ProcessGuardian.Wpf.ViewModels;
using Xunit;

namespace ProcessGuardian.Wpf.Tests;

// The ViewModel's busy gate no longer guards persistence directly (C+D): all Start/Stop
// now flow through IMonitoringController, which serializes internally. These tests assert
// the gate still ignores reentrant clicks while an operation is in flight, and that a
// settlement from the controller's StateChanged channel releases it.
public class MainViewModelReentrancyTests
{
    private const int TimeoutMs = 10000;

    private static async Task WaitUntilAsync(Func<bool> condition, string failureMessage, int timeoutMs = TimeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        throw new Xunit.Sdk.XunitException($"Timed out waiting for: {failureMessage}");
    }

    private sealed class Harness
    {
        public IMonitoringController Controller { get; } = Substitute.For<IMonitoringController>();
        public INotificationService Notifications { get; } = Substitute.For<INotificationService>();

        public int StartCalls { get; set; }
        public int StopCalls { get; set; }

        public Harness()
        {
            Controller.GetCurrentSettingsAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AppSettings()));
            Controller.CurrentState.Returns(new MonitorState(GuardianStatus.Stopped, false, null, false));
            Notifications.ShowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
            // Controller calls complete immediately; the busy gate is held by the VM's
            // WaitUntilReady (Status stays unsettled), not by the controller task itself.
            Controller.StartAsync(Arg.Any<StartRequest>(), Arg.Any<CancellationToken>())
                .Returns(_ => { StartCalls++; return Task.FromResult(new StartResult(true, null)); });
            Controller.StopAsync(Arg.Any<CommittedSettingsSnapshot>(), Arg.Any<CancellationToken>())
                .Returns(_ => { StopCalls++; return Task.FromResult(StopResult.Succeeded()); });
        }

        public MainViewModel CreateVm() =>
            new(Controller, Notifications, NullLogger<MainViewModel>.Instance,
                new InMemoryLogSink(Path.Combine(Path.GetTempPath(), "ProcessGuardian.Tests",
                    "wm-" + Guid.NewGuid().ToString("N") + ".log")));
    }

    [Fact]
    public async Task Start_WhileBusy_IgnoresReentrantClick_ThenWorksAfterReady()
    {
        var h = new Harness();
        var vm = h.CreateVm();

        // First Start is accepted: the busy gate holds while the controller is in flight
        // and Status is still Stopped, so the operation is NOT ready.
        var first = vm.StartCommand.ExecuteAsync(null!);
        await WaitUntilAsync(() => h.StartCalls >= 1, "first Start to reach the controller");
        vm.StartCommand.CanExecute(null).Should().BeFalse("Start must be disabled while busy");
        vm.StopCommand.CanExecute(null).Should().BeFalse("Stop must be disabled while busy");

        // Reentrant Start and Stop while busy are ignored: no second controller call.
        var reStart = vm.StartCommand.ExecuteAsync(null!);
        var reStop = vm.StopCommand.ExecuteAsync(null!);
        await Task.WhenAll(reStart, reStop);
        h.StartCalls.Should().Be(1, "a reentrant Start/Stop must not enter the handler while one is in flight");
        h.StopCalls.Should().Be(0);

        // The controller settles Status=Monitoring -> the operation becomes ready.
        vm.Status = GuardianStatus.Monitoring;
        await first;
        vm.StartCommand.CanExecute(null).Should().BeFalse("after Start, CanExecute reflects Status=Monitoring");
        vm.StopCommand.CanExecute(null).Should().BeTrue("after Start completes, Stop becomes available");
    }

    [Fact]
    public async Task Stop_WhileBusy_IgnoresReentrantClick_ThenWorksAfterReady()
    {
        var h = new Harness();
        var vm = h.CreateVm();
        vm.Status = GuardianStatus.Monitoring; // so CanStop is true

        var first = vm.StopCommand.ExecuteAsync(null!);
        await WaitUntilAsync(() => h.StopCalls >= 1, "first Stop to reach the controller");
        vm.StopCommand.CanExecute(null).Should().BeFalse("Stop must be disabled while busy");
        vm.StartCommand.CanExecute(null).Should().BeFalse("Start must be disabled while busy");

        // Reentrant Stop while busy is ignored: no second controller call.
        var second = vm.StopCommand.ExecuteAsync(null!);
        await second;
        h.StopCalls.Should().Be(1, "a reentrant Stop must not enter the handler while one is in flight");

        // Controller settles Status=Stopped -> the operation becomes ready and completes.
        vm.Status = GuardianStatus.Stopped;
        await first;

        // After ready, buttons are live again: a new Stop is accepted.
        vm.Status = GuardianStatus.Monitoring;
        var again = vm.StopCommand.ExecuteAsync(null!);
        await WaitUntilAsync(() => h.StopCalls >= 2, "second Stop to reach the controller");
        vm.Status = GuardianStatus.Stopped;
        await again;
        h.StopCalls.Should().Be(2);
    }
}
