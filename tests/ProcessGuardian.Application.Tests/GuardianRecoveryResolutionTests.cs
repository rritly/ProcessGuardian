using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ProcessGuardian.Application.Services;
using ProcessGuardian.Domain.Entities;
using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Domain.Interfaces;
using ProcessGuardian.Domain.ValueObjects;
using Xunit;

namespace ProcessGuardian.Application.Tests;

// Recovery-resolution behavior (A/B) at the controller level: the notice is cleared only by an
// explicit user choice whose operations all succeeded and were verified, never on plain start,
// never on re-select/re-pin alone, and errors keep the notice pending and the action retryable.
public class GuardianRecoveryResolutionTests
{
    // Simulates the scheduled-task autostart service: registers state in memory, reports that
    // state as the "actual" registration, and can be told to fail in a way that leaves the
    // actual state unchanged (like a create that exits 0 but never registers the task).
    private sealed class FakeAutostart : IAutostartService
    {
        public bool Enabled { get; set; }
        public AutostartResult? ForceResult { get; set; }
        public int SetCalls { get; private set; }

        public Task<bool> IsEnabledAsync(CancellationToken ct = default) => Task.FromResult(Enabled);

        public Task<AutostartResult> SetEnabledAsync(bool enabled, CancellationToken ct = default)
        {
            SetCalls++;
            if (ForceResult is { } result)
                return Task.FromResult(result); // requested change did not take effect
            Enabled = enabled;
            return Task.FromResult(AutostartResult.Success(enabled));
        }
    }

    private sealed class FakeResetNoticeAck : IResetNoticeAck
    {
        public int ClearCalls { get; private set; }
        public Exception? ThrowOnClear { get; set; }

        public Task ClearAsync(CancellationToken ct = default)
        {
            ClearCalls++;
            return ThrowOnClear is not null
                ? Task.FromException(ThrowOnClear)
                : Task.CompletedTask;
        }
    }

    private sealed class Harness : IDisposable
    {
        public IProcessMonitor Monitor { get; } = Substitute.For<IProcessMonitor>();
        public IConfigurationStore Config { get; } = Substitute.For<IConfigurationStore>();
        public INotificationService Notifications { get; } = Substitute.For<INotificationService>();
        public IStatusReporter Status { get; } = Substitute.For<IStatusReporter>();
        public ITargetIntegrityService Integrity { get; } = Substitute.For<ITargetIntegrityService>();
        public FakeAutostart Autostart { get; } = new();
        public FakeResetNoticeAck ResetNoticeAck { get; } = new();
        public AppSettings? LastSaved { get; set; }

        public void Dispose()
        {
        }

        public Harness()
        {
            Config.LoadAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AppSettings()));
            Config.SaveAsync(Arg.Do<AppSettings>(s => LastSaved = s), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
            Notifications.ShowAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
        }

        public GuardianOrchestrator Create() => new(
            Monitor, Config, Notifications, NullLogger<GuardianOrchestrator>.Instance,
            Status, Integrity, Autostart, null, ResetNoticeAck);

        // Records a pin through Reconcile (elevated-style persisted pin, monitoring off) so an
        // option-A resolution sees HasRecordedPin true without starting a live session.
        public async Task<GuardianOrchestrator> CreateWithRecordedPinAsync(string targetPath)
        {
            Config.LoadAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AppSettings
                {
                    TargetProcessPath = targetPath,
                    TargetHashSha256 = "PIN",
                    IsMonitoringEnabled = false,
                }));
            var controller = Create();
            await controller.ReconcileAsync();
            return controller;
        }

        public CommittedSettingsSnapshot Snapshot(bool autostartEnabled) =>
            CommittedSettingsSnapshot.From(new AppSettings
            {
                AutostartEnabled = autostartEnabled,
                MaxRestartAttempts = 5,
                CheckIntervalSeconds = 1,
            });
    }

    private static string TargetPath() => @"C:\Tools\ProcessGuardian\TargetApp.exe";

    [Fact]
    public async Task Reconcile_ResetNoticePending_LeavesPreExistingTaskUntouched()
    {
        using var h = new Harness();
        h.Config.InitializeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ConfigurationInitializationStatus.ResetNoticePending));
        h.Autostart.Enabled = true; // a pre-existing scheduler task

        var controller = h.Create();
        await controller.ReconcileAsync();

        h.Autostart.SetCalls.Should().Be(0, "a pending reset notice must never auto-disable a pre-existing task");
        h.Autostart.Enabled.Should().BeTrue();
        _ = h.Monitor.DidNotReceive().StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        controller.CurrentState.Status.Should().Be(GuardianStatus.Stopped);
    }

    [Fact]
    public async Task Reconcile_ResetNoticePending_DoesNotAutoEnableAutostart()
    {
        using var h = new Harness();
        h.Config.InitializeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ConfigurationInitializationStatus.ResetNoticePending));
        h.Autostart.Enabled = false;

        var controller = h.Create();
        await controller.ReconcileAsync();

        h.Autostart.SetCalls.Should().Be(0, "a pending reset notice must not auto-enable autostart either");
        h.Autostart.Enabled.Should().BeFalse();
        controller.CurrentState.Status.Should().Be(GuardianStatus.Stopped);
    }

    [Fact]
    public async Task KeepAutostart_WithoutRecordedPin_IsRejected_NoticeStaysPending()
    {
        using var h = new Harness();
        h.Config.InitializeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ConfigurationInitializationStatus.ResetNoticePending));
        var controller = h.Create(); // fresh: no pin recorded

        var result = await controller.ResolveRecoveryAsync(
            RecoveryResolutionChoice.KeepAutostart,
            h.Snapshot(autostartEnabled: false),
            "C:\\path\\target.exe");

        result.Resolved.Should().BeFalse("option A is unavailable until the target is re-selected and re-pinned");
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        h.ResetNoticeAck.ClearCalls.Should().Be(0, "the notice must not be cleared while the resolution is refused");
        h.Autostart.SetCalls.Should().Be(0);
    }

    [Fact]
    public async Task KeepAutostart_Success_EnablesVerified_PersistsTrue_ClearsNotice()
    {
        using var h = new Harness();
        h.Autostart.Enabled = false;
        var target = TargetPath();
        var controller = await h.CreateWithRecordedPinAsync(target);

        var result = await controller.ResolveRecoveryAsync(
            RecoveryResolutionChoice.KeepAutostart,
            h.Snapshot(autostartEnabled: false),
            target);

        result.Resolved.Should().BeTrue();
        h.Autostart.Enabled.Should().BeTrue("option A must enable autostart");
        h.Autostart.SetCalls.Should().Be(1);
        h.ResetNoticeAck.ClearCalls.Should().Be(1, "the notice is cleared only after a fully-verified resolution");
        h.LastSaved.Should().NotBeNull();
        h.LastSaved!.AutostartEnabled.Should().BeTrue("option A persists AutostartEnabled=true");
    }

    [Fact]
    public async Task KeepAutostart_EnableFails_LeavesNoticePending_LeavesTaskUntouched()
    {
        using var h = new Harness();
        h.Autostart.Enabled = false;
        h.Autostart.ForceResult = AutostartResult.Failure("task creation failed (exit 1)");
        var target = TargetPath();
        var controller = await h.CreateWithRecordedPinAsync(target);

        var result = await controller.ResolveRecoveryAsync(
            RecoveryResolutionChoice.KeepAutostart,
            h.Snapshot(autostartEnabled: false),
            target);

        result.Resolved.Should().BeFalse();
        result.ErrorMessage.Should().Contain("exit 1");
        h.Autostart.Enabled.Should().BeFalse("the requested enable did not take effect");
        h.ResetNoticeAck.ClearCalls.Should().Be(0, "a failed enable must keep the notice pending");
    }

    [Fact]
    public async Task KeepAutostart_SaveFails_LeavesNoticePending_AndRetryIsClean()
    {
        using var h = new Harness();
        h.Autostart.Enabled = false;
        var target = TargetPath();
        var controller = await h.CreateWithRecordedPinAsync(target);

        // First attempt: the settings save fails after autostart was already enabled.
        h.Config.SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("disk full")));

        var first = await controller.ResolveRecoveryAsync(
            RecoveryResolutionChoice.KeepAutostart,
            h.Snapshot(autostartEnabled: false),
            target);
        first.Resolved.Should().BeFalse();
        h.ResetNoticeAck.ClearCalls.Should().Be(0, "a failed save must keep the notice pending");

        // Second attempt after the transient failure clears: idempotent and succeeds once.
        h.Config.SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var retry = await controller.ResolveRecoveryAsync(
            RecoveryResolutionChoice.KeepAutostart,
            h.Snapshot(autostartEnabled: false),
            target);
        retry.Resolved.Should().BeTrue();
        h.Autostart.Enabled.Should().BeTrue();
        h.ResetNoticeAck.ClearCalls.Should().Be(1, "the notice is cleared exactly once, on the successful attempt");
        h.LastSaved!.AutostartEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveAutostart_Success_RemovesVerified_PersistsFalse_ClearsNotice()
    {
        using var h = new Harness();
        h.Autostart.Enabled = true; // a pre-existing scheduler task
        var controller = h.Create();

        var result = await controller.ResolveRecoveryAsync(
            RecoveryResolutionChoice.RemoveAutostart,
            h.Snapshot(autostartEnabled: true));

        result.Resolved.Should().BeTrue();
        h.Autostart.Enabled.Should().BeFalse("option B must remove the task");
        h.Autostart.SetCalls.Should().Be(1);
        h.ResetNoticeAck.ClearCalls.Should().Be(1);
        h.LastSaved.Should().NotBeNull();
        h.LastSaved!.AutostartEnabled.Should().BeFalse("option B persists AutostartEnabled=false");
    }

    [Fact]
    public async Task RemoveAutostart_DeleteFails_LeavesNoticePending_TaskStillRegistered()
    {
        using var h = new Harness();
        h.Autostart.Enabled = true;
        h.Autostart.ForceResult = AutostartResult.Failure("task deletion failed (exit 1)");
        var controller = h.Create();

        var result = await controller.ResolveRecoveryAsync(
            RecoveryResolutionChoice.RemoveAutostart,
            h.Snapshot(autostartEnabled: true));

        result.Resolved.Should().BeFalse();
        result.ErrorMessage.Should().Contain("exit 1");
        h.Autostart.Enabled.Should().BeTrue("a failed delete must leave the task registered");
        h.ResetNoticeAck.ClearCalls.Should().Be(0, "a failed removal must keep the notice pending");
    }

    [Fact]
    public async Task CommitSettings_AutostartFailure_IsNotMaskedBySuccessfulSave()
    {
        using var h = new Harness();
        h.Autostart.Enabled = false;
        h.Autostart.ForceResult = AutostartResult.Failure("task registration failed");
        var controller = h.Create();

        var result = await controller.CommitSettingsAsync(
            h.Snapshot(autostartEnabled: true), targetPath: null);

        result.Saved.Should().BeTrue("the settings document itself was durably saved");
        result.Autostart.Should().NotBeNull();
        result.Autostart!.Value.Succeeded.Should().BeFalse("an autostart failure must not be masked by the save");
        result.Autostart.Value.ErrorMessage.Should().Contain("task registration failed");
    }
}
