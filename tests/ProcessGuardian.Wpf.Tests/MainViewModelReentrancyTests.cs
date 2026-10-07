using System.Diagnostics;
using System.IO;
using System.Reflection;
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
        public ITargetIntegrityService Integrity { get; } = Substitute.For<ITargetIntegrityService>();

        // The single instance LoadAsync returns, so tests can mutate settings after
        // the initial load has settled (the VM replaces its Settings with it).
        public AppSettings Loaded { get; } = new();

        public TaskCompletionSource CurrentSaveGate { get; private set; } = CreateGate();
        public int SaveCount { get; set; }

        public Harness()
        {
            Config.LoadAsync(Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(Loaded));
            Autostart.IsEnabledAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(false));
            Notifications.ShowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
            Integrity.ComputeHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<string?>("PIN"));
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
            new(Config, Autostart, Status, Notifications, NullLogger<MainViewModel>.Instance,
                // Throwaway path: the default constructor writes the real user log file.
                new InMemoryLogSink(Path.Combine(Path.GetTempPath(), "ProcessGuardian.Tests",
                    "wm-" + Guid.NewGuid().ToString("N") + ".log")),
                Integrity);
    }

    private static Task WaitUntilHashRequestedAsync(Harness h) =>
        WaitUntilAsync(
            () => h.Integrity.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ITargetIntegrityService.ComputeHashAsync)),
            "hash computation to be requested");

    [Fact]
    public async Task Start_WhenHashUnreadable_AbortsFailClosed_KeepsPreviousPin()
    {
        var h = new Harness();
        var vm = h.CreateVm();
        await WaitUntilAsync(() => ReferenceEquals(vm.Settings, h.Loaded), "initial settings load to complete");
        h.Loaded.TargetProcessPath = @"C:\App\target.exe";
        h.Loaded.TargetHashSha256 = "OLDPIN";
        h.Integrity.ComputeHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        await vm.StartCommand.ExecuteAsync(null!);

        vm.Settings.IsMonitoringEnabled.Should().BeFalse("fail-closed: monitoring must not start without a fresh pin");
        vm.Settings.TargetHashSha256.Should().Be("OLDPIN", "a failed hash read must not erase the existing pin");
        _ = h.Config.DidNotReceive().SaveAsync(
            Arg.Is<AppSettings>(s => s.IsMonitoringEnabled), Arg.Any<CancellationToken>());
        await h.Notifications.Received(1).ShowAsync("Target Unreadable", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_PinsHashBeforeEnablingMonitoring()
    {
        var h = new Harness();
        var vm = h.CreateVm();
        await WaitUntilAsync(() => ReferenceEquals(vm.Settings, h.Loaded), "initial settings load to complete");
        vm.Settings.TargetProcessPath = @"C:\App\target.exe";

        var hashGate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Integrity.ComputeHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(hashGate.Task);

        var start = vm.StartCommand.ExecuteAsync(null!);
        await WaitUntilHashRequestedAsync(h);

        // While the pin is still being computed, monitoring must stay disabled: the
        // orchestrator's config poll must never observe enabled + stale pin.
        vm.Settings.IsMonitoringEnabled.Should().BeFalse("the pin must be recorded before monitoring is enabled");

        hashGate.SetResult("ABC123");
        await WaitUntilAsync(() => h.SaveCount >= 1, "the pin+enable save to be in flight");
        h.CompleteCurrentSave();
        h.Status.Report(GuardianStatus.Monitoring);
        await start;

        vm.Settings.IsMonitoringEnabled.Should().BeTrue();
        vm.Settings.TargetHashSha256.Should().Be("ABC123", "the fresh pin must be recorded");
        await h.Config.Received().SaveAsync(
            Arg.Is<AppSettings>(s => s.IsMonitoringEnabled && s.TargetHashSha256 == "ABC123"),
            Arg.Any<CancellationToken>());
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
