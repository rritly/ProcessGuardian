using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ProcessGuardian.Domain.Entities;
using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Domain.Interfaces;
using ProcessGuardian.Infrastructure.Logging;
using ProcessGuardian.Infrastructure.Status;
using ProcessGuardian.Wpf.ViewModels;
using Xunit;

namespace ProcessGuardian.Wpf.Tests;

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
        public IConfigurationStore Config { get; } = Substitute.For<IConfigurationStore>();
        public IAutostartService Autostart { get; } = Substitute.For<IAutostartService>();
        public IStatusReporter Status { get; } = new StatusReporter();
        public INotificationService Notifications { get; } = Substitute.For<INotificationService>();

        public TaskCompletionSource CurrentSaveGate { get; private set; } = CreateGate();
        public int SaveCount { get; set; }

        public Harness()
        {
            Config.LoadAsync(Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(new AppSettings()));
            Autostart.IsEnabledAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(false));
            Notifications.ShowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            Config.SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    SaveCount++;
                    CurrentSaveGate = CreateGate();
                    return CurrentSaveGate.Task;
                });
        }

        private static TaskCompletionSource CreateGate() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void CompleteCurrentSave() => CurrentSaveGate.SetResult();

        public MainViewModel CreateVm() =>
            new(Config, Autostart, Status, Notifications, NullLogger<MainViewModel>.Instance, new InMemoryLogSink(),
                Substitute.For<ITargetIntegrityService>());
    }

    [Fact]
    public async Task Start_WhileBusy_IgnoresReentrantClick_DoesNotChangeFlag_ThenWorksAfterReady()
    {
        var h = new Harness();
        var vm = h.CreateVm();

        // First Start is accepted: the busy gate holds while the persist blocks on the
        // save gate and Status is still Stopped, so the operation is NOT ready.
        var first = vm.StartCommand.ExecuteAsync(null!);
        await WaitUntilAsync(() => h.SaveCount >= 1, "first Start save to be in flight");
        vm.StartCommand.CanExecute(null).Should().BeFalse("Start must be disabled while busy");
        vm.StopCommand.CanExecute(null).Should().BeFalse("Stop must be disabled while busy");

        // Reentrant Start and Stop while busy are ignored: no second save, flag unchanged.
        var reStart = vm.StartCommand.ExecuteAsync(null!);
        var reStop = vm.StopCommand.ExecuteAsync(null!);
        await Task.WhenAll(reStart, reStop);
        h.SaveCount.Should().Be(1, "a reentrant Start/Stop must not enter the handler while one is in flight");
        vm.Settings.IsMonitoringEnabled.Should().BeTrue("the flag reflects only the first accepted Start");

        // The orchestrator settles Status -> the operation becomes ready and completes.
        h.CompleteCurrentSave();
        h.Status.Report(GuardianStatus.Monitoring);
        await first;
        vm.StartCommand.CanExecute(null).Should().BeFalse("after Start, CanExecute reflects Status=Monitoring");

        // After the operation is ready the buttons are live again: a new Start is accepted.
        h.Config.ClearReceivedCalls();
        var again = vm.StartCommand.ExecuteAsync(null!);
        await WaitUntilAsync(() => h.SaveCount >= 2, "second Start save to be in flight");
        h.CompleteCurrentSave();
        h.Status.Report(GuardianStatus.Monitoring);
        await again;
        await h.Config.Received(1).SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stop_WhileBusy_IgnoresReentrantClick_ThenWorksAfterReady()
    {
        var h = new Harness();
        var vm = h.CreateVm();
        vm.Status = GuardianStatus.Monitoring;

        var first = vm.StopCommand.ExecuteAsync(null!);
        await WaitUntilAsync(() => h.SaveCount >= 1, "first Stop save to be in flight");
        vm.StopCommand.CanExecute(null).Should().BeFalse("Stop must be disabled while busy");
        vm.StartCommand.CanExecute(null).Should().BeFalse("Start must be disabled while busy");

        // Reentrant Stop while busy is ignored: no second save.
        var second = vm.StopCommand.ExecuteAsync(null!);
        await second;
        h.SaveCount.Should().Be(1, "a reentrant Stop must not enter the handler while one is in flight");

        // Orchestrator settles Status=Stopped -> the operation becomes ready and completes.
        h.CompleteCurrentSave();
        h.Status.Report(GuardianStatus.Stopped);
        await first;

        // After ready, buttons are live again: a new Stop is accepted.
        vm.Status = GuardianStatus.Monitoring;
        h.Config.ClearReceivedCalls();
        var again = vm.StopCommand.ExecuteAsync(null!);
        await WaitUntilAsync(() => h.SaveCount >= 2, "second Stop save to be in flight");
        h.CompleteCurrentSave();
        h.Status.Report(GuardianStatus.Stopped);
        await again;
        await h.Config.Received(1).SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>());
    }
}
