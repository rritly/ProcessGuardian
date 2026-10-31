using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ProcessGuardian.Application.Services;
using ProcessGuardian.Domain.Entities;
using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Domain.Interfaces;
using ProcessGuardian.Infrastructure.Configuration;
using ProcessGuardian.Infrastructure.Logging;
using ProcessGuardian.Wpf.ViewModels;
using Xunit;

namespace ProcessGuardian.Wpf.Tests;

// Recovery-resolution and autostart-error projection in the ViewModel: the A/B actions are
// enabled only while a reset notice is pending (option A additionally requires a recorded
// pin), a successful resolution clears the notice, a failed one keeps it and surfaces the
// error, and an autostart failure from a settings commit is never masked by a saved flag.
public class MainViewModelRecoveryTests
{
    private sealed class Harness
    {
        public IMonitoringController Controller { get; }
        public INotificationService Notifications { get; } = Substitute.For<INotificationService>();

        public Harness(bool hasPin = false)
        {
            Controller = Substitute.For<IMonitoringController>();
            Controller.HasRecordedPin.Returns(hasPin);
            Controller.GetCurrentSettingsAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AppSettings()));
            Controller.CurrentState.Returns(new MonitorState(GuardianStatus.Stopped, false, null, false));
        }

        public MainViewModel CreateVm() =>
            new(Controller, Notifications, NullLogger<MainViewModel>.Instance,
                new InMemoryLogSink(Path.Combine(Path.GetTempPath(), "ProcessGuardian.Tests",
                    "recovery-" + Guid.NewGuid().ToString("N") + ".log")));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failureMessage)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 10000)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }
        throw new Xunit.Sdk.XunitException($"Timed out waiting for: {failureMessage}");
    }

    [Fact]
    public void ResetNoticePending_KeepDisabledWithoutPin_RemoveEnabled()
    {
        var vm = new Harness(hasPin: false).CreateVm();
        vm.ApplyInitializationStatus(ConfigurationInitializationStatus.ResetNoticePending);

        vm.ResetNotice.Should().NotBeEmpty();
        vm.RemoveAutostartCommand.CanExecute(null).Should().BeTrue("option B needs only the pending notice");
        vm.KeepAutostartCommand.CanExecute(null).Should().BeFalse(
            "option A is unavailable until the target is re-selected and re-pinned");
    }

    [Fact]
    public void ResetNoticePending_KeepEnabledOncePinRecorded()
    {
        var vm = new Harness(hasPin: true).CreateVm();
        vm.ApplyInitializationStatus(ConfigurationInitializationStatus.ResetNoticePending);

        vm.KeepAutostartCommand.CanExecute(null).Should().BeTrue();
        vm.RemoveAutostartCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void NoResetNotice_BothActionsDisabled()
    {
        var vm = new Harness(hasPin: true).CreateVm();

        vm.ResetNotice.Should().BeEmpty();
        vm.KeepAutostartCommand.CanExecute(null).Should().BeFalse();
        vm.RemoveAutostartCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task KeepAutostart_Success_ClearsResetNoticeAndError()
    {
        var h = new Harness(hasPin: true);
        h.Controller.ResolveRecoveryAsync(
                RecoveryResolutionChoice.KeepAutostart,
                Arg.Any<CommittedSettingsSnapshot>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(RecoveryResolutionResult.Succeeded()));

        var vm = h.CreateVm();
        vm.ApplyInitializationStatus(ConfigurationInitializationStatus.ResetNoticePending);

        await vm.KeepAutostartCommand.ExecuteAsync(null);

        vm.ResetNotice.Should().BeEmpty("a successful resolution clears the notice");
        vm.AutostartError.Should().BeEmpty();
    }

    [Fact]
    public async Task KeepAutostart_Failure_KeepsResetNoticeAndSurfacesError()
    {
        var h = new Harness(hasPin: true);
        h.Controller.ResolveRecoveryAsync(
                RecoveryResolutionChoice.KeepAutostart,
                Arg.Any<CommittedSettingsSnapshot>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(RecoveryResolutionResult.Failed(
                "autostart could not be enabled: task creation failed (exit 1)")));

        var vm = h.CreateVm();
        vm.ApplyInitializationStatus(ConfigurationInitializationStatus.ResetNoticePending);

        await vm.KeepAutostartCommand.ExecuteAsync(null);

        vm.ResetNotice.Should().NotBeEmpty("a failed resolution must keep the notice pending and retryable");
        vm.AutostartError.Should().Contain("exit 1");
    }

    [Fact]
    public async Task RemoveAutostart_Success_ClearsResetNotice()
    {
        var h = new Harness();
        h.Controller.ResolveRecoveryAsync(
                RecoveryResolutionChoice.RemoveAutostart,
                Arg.Any<CommittedSettingsSnapshot>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(RecoveryResolutionResult.Succeeded()));

        var vm = h.CreateVm();
        vm.ApplyInitializationStatus(ConfigurationInitializationStatus.ResetNoticePending);

        await vm.RemoveAutostartCommand.ExecuteAsync(null);

        vm.ResetNotice.Should().BeEmpty();
        vm.AutostartError.Should().BeEmpty();
    }

    [Fact]
    public async Task Commit_AutostartFailure_IsSurfaceD_CheckboxNeverFalsePositive()
    {
        var h = new Harness();
        // The settings save succeeds, but the autostart registration fails: the commit must
        // surface the autostart error so the checkbox never reads "on" without a real task.
        h.Controller.CommitSettingsAsync(
                Arg.Any<CommittedSettingsSnapshot>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommitSettingsResult(true,
                AutostartResult.Failure("task registration failed (exit 1)"))));

        var vm = h.CreateVm();
        await Task.Delay(100); // let the initial LoadSettingsAsync settle

        // A settings change triggers the 500 ms debounced commit (the controller result above).
        vm.Settings.AutostartEnabled = true;

        await WaitUntilAsync(() => vm.AutostartError.Length > 0,
            "an autostart failure from a settings commit to surface in the UI");
        vm.AutostartError.Should().Contain("exit 1");
    }
}
