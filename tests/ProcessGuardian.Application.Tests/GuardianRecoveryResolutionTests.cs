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
        public AutostartState State { get; set; } = AutostartState.Absent;
        public AutostartResult? ForceResult { get; set; }
        public int SetCalls { get; private set; }

        public Task<AutostartState> ReadStateAsync(CancellationToken ct = default) => Task.FromResult(State);

        public Task<AutostartResult> SetEnabledAsync(bool enabled, CancellationToken ct = default)
        {
            SetCalls++;
            if (ForceResult is { } result)
                return Task.FromResult(result); // requested change did not take effect
            State = enabled ? AutostartState.Enabled : AutostartState.Absent;
            return Task.FromResult(AutostartResult.Success(State));
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
        h.Autostart.State = AutostartState.Enabled; // a pre-existing scheduler task

        var controller = h.Create();
        await controller.ReconcileAsync();

        h.Autostart.SetCalls.Should().Be(0, "a pending reset notice must never auto-disable a pre-existing task");
        h.Autostart.State.Should().Be(AutostartState.Enabled);
        _ = h.Monitor.DidNotReceive().TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        controller.CurrentState.Status.Should().Be(GuardianStatus.Stopped);
    }

    [Fact]
    public async Task Reconcile_ResetNoticePending_DoesNotAutoEnableAutostart()
    {
        using var h = new Harness();
        h.Config.InitializeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ConfigurationInitializationStatus.ResetNoticePending));
        h.Autostart.State = AutostartState.Absent;

        var controller = h.Create();
        await controller.ReconcileAsync();

        h.Autostart.SetCalls.Should().Be(0, "a pending reset notice must not auto-enable autostart either");
        h.Autostart.State.Should().Be(AutostartState.Absent);
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
        h.Autostart.State = AutostartState.Absent;
        var target = TargetPath();
        var controller = await h.CreateWithRecordedPinAsync(target);

        var result = await controller.ResolveRecoveryAsync(
            RecoveryResolutionChoice.KeepAutostart,
            h.Snapshot(autostartEnabled: false),
            target);

        result.Resolved.Should().BeTrue();
        h.Autostart.State.Should().Be(AutostartState.Enabled, "option A must enable autostart");
        h.Autostart.SetCalls.Should().Be(1);
        h.ResetNoticeAck.ClearCalls.Should().Be(1, "the notice is cleared only after a fully-verified resolution");
        h.LastSaved.Should().NotBeNull();
        h.LastSaved!.AutostartEnabled.Should().BeTrue("option A persists AutostartEnabled=true");
    }

    [Fact]
    public async Task KeepAutostart_EnableFails_LeavesNoticePending_LeavesTaskUntouched()
    {
        using var h = new Harness();
        h.Autostart.State = AutostartState.Absent;
        h.Autostart.ForceResult = AutostartResult.Failure("task creation failed (exit 1)");
        var target = TargetPath();
        var controller = await h.CreateWithRecordedPinAsync(target);

        var result = await controller.ResolveRecoveryAsync(
            RecoveryResolutionChoice.KeepAutostart,
            h.Snapshot(autostartEnabled: false),
            target);

        result.Resolved.Should().BeFalse();
        result.ErrorMessage.Should().Contain("exit 1");
        h.Autostart.State.Should().Be(AutostartState.Absent, "the requested enable did not take effect");
        h.ResetNoticeAck.ClearCalls.Should().Be(0, "a failed enable must keep the notice pending");
    }

    [Fact]
    public async Task KeepAutostart_SaveFails_LeavesNoticePending_AndRetryIsClean()
    {
        using var h = new Harness();
        h.Autostart.State = AutostartState.Absent;
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
        h.Autostart.State.Should().Be(AutostartState.Enabled);
        h.ResetNoticeAck.ClearCalls.Should().Be(1, "the notice is cleared exactly once, on the successful attempt");
        h.LastSaved!.AutostartEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveAutostart_Success_RemovesVerified_PersistsFalse_ClearsNotice()
    {
        using var h = new Harness();
        h.Autostart.State = AutostartState.Enabled; // a pre-existing scheduler task
        var controller = h.Create();

        var result = await controller.ResolveRecoveryAsync(
            RecoveryResolutionChoice.RemoveAutostart,
            h.Snapshot(autostartEnabled: true));

        result.Resolved.Should().BeTrue();
        h.Autostart.State.Should().Be(AutostartState.Absent, "option B must remove the task");
        h.Autostart.SetCalls.Should().Be(1);
        h.ResetNoticeAck.ClearCalls.Should().Be(1);
        h.LastSaved.Should().NotBeNull();
        h.LastSaved!.AutostartEnabled.Should().BeFalse("option B persists AutostartEnabled=false");
    }

    [Fact]
    public async Task RemoveAutostart_DeleteFails_LeavesNoticePending_TaskStillRegistered()
    {
        using var h = new Harness();
        h.Autostart.State = AutostartState.Enabled;
        h.Autostart.ForceResult = AutostartResult.Failure("task deletion failed (exit 1)");
        var controller = h.Create();

        var result = await controller.ResolveRecoveryAsync(
            RecoveryResolutionChoice.RemoveAutostart,
            h.Snapshot(autostartEnabled: true));

        result.Resolved.Should().BeFalse();
        result.ErrorMessage.Should().Contain("exit 1");
        h.Autostart.State.Should().Be(AutostartState.Enabled, "a failed delete must leave the task registered");
        h.ResetNoticeAck.ClearCalls.Should().Be(0, "a failed removal must keep the notice pending");
    }

    [Fact]
    public async Task CommitSettings_AutostartFailure_IsNotMaskedBySuccessfulSave()
    {
        using var h = new Harness();
        h.Autostart.State = AutostartState.Absent;
        h.Autostart.ForceResult = AutostartResult.Failure("task registration failed");
        var controller = h.Create();

        var result = await controller.CommitSettingsAsync(
            h.Snapshot(autostartEnabled: true), targetPath: null);

        result.Saved.Should().BeTrue("the settings document itself was durably saved");
        result.Autostart.Should().NotBeNull();
        result.Autostart!.Value.Succeeded.Should().BeFalse("an autostart failure must not be masked by the save");
        result.Autostart.Value.ErrorMessage.Should().Contain("task registration failed");
    }

    // ------------------------------------------------------------------
    // 2.1 — an ordinary settings commit while a reset notice is pending must never
    // enable/disable autostart nor overwrite the persisted AutostartEnabled flag.
    // ------------------------------------------------------------------

    [Fact]
    public async Task CommitSettings_WhileResetNoticePending_DoesNotTouchAutostartTask()
    {
        using var h = new Harness();
        h.Config.InitializeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ConfigurationInitializationStatus.ResetNoticePending));
        h.Config.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AppSettings { AutostartEnabled = true, IsMonitoringEnabled = false }));
        h.Autostart.State = AutostartState.Enabled; // a pre-existing scheduler task
        var controller = h.Create();
        await controller.ReconcileAsync();

        // The UI snapshot requests autostart=false, but while the notice is pending the
        // operation must be suppressed entirely — no enable and no disable of the real task.
        var result = await controller.CommitSettingsAsync(
            h.Snapshot(autostartEnabled: false), targetPath: null);

        result.Saved.Should().BeTrue("other committed settings are still saved");
        h.Autostart.SetCalls.Should().Be(0, "a plain commit during ResetNoticePending must not call SetEnabledAsync");
        h.Autostart.State.Should().Be(AutostartState.Enabled, "the pre-existing task must be left untouched");
        h.LastSaved.Should().NotBeNull();
        // The persisted AutostartEnabled must be preserved, not overwritten by the snapshot default.
        h.LastSaved!.AutostartEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task CommitSettings_WhileResetNoticePending_DoesNotAutoEnableEither()
    {
        using var h = new Harness();
        h.Config.InitializeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ConfigurationInitializationStatus.ResetNoticePending));
        h.Config.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AppSettings { AutostartEnabled = false, IsMonitoringEnabled = false }));
        h.Autostart.State = AutostartState.Absent;
        var controller = h.Create();
        await controller.ReconcileAsync();

        // Snapshot requests autostart=true; still suppressed while the notice is pending.
        var result = await controller.CommitSettingsAsync(
            h.Snapshot(autostartEnabled: true), targetPath: @"C:\path\target.exe");

        result.Saved.Should().BeTrue();
        h.Autostart.SetCalls.Should().Be(0, "a plain commit during ResetNoticePending must not enable autostart either");
        h.Autostart.State.Should().Be(AutostartState.Absent);
        h.LastSaved!.AutostartEnabled.Should().BeFalse("the persisted flag must not be overwritten while pending");
    }

    // ------------------------------------------------------------------
    // 2.4 — the startup synchronization result is surfaced to the presentation layer
    // (AutostartActualState / AutostartSyncError), never masked or left only to logs.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Reconcile_StartupSyncFailure_IsSurfaced_NotMasked()
    {
        using var h = new Harness();
        h.Config.InitializeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ConfigurationInitializationStatus.Success));
        // Persisted intent = enabled but the task is absent (e.g. cleaned up externally); the
        // sync tries to restore it and fails — the failure must reach the UI, not just logs.
        h.Config.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AppSettings { AutostartEnabled = true, IsMonitoringEnabled = false }));
        h.Autostart.State = AutostartState.Absent;
        h.Autostart.ForceResult = AutostartResult.Failure("task creation failed (exit 1)", AutostartState.Absent);
        var controller = h.Create();

        await controller.ReconcileAsync();

        controller.AutostartActualState.Should().Be(AutostartState.Absent,
            "the best-known actual state after the failed sync must be surfaced");
        controller.AutostartSyncError.Should().Contain("exit 1", "a startup-sync failure must be surfaced, not masked by a Stopped status");
        controller.CurrentState.Status.Should().Be(GuardianStatus.Stopped, "the stopped status does not mask the autostart failure");
    }

    [Fact]
    public async Task Reconcile_StartupSync_UnverifiableState_IsSurfaced_WithoutMutation()
    {
        using var h = new Harness();
        h.Config.InitializeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ConfigurationInitializationStatus.Success));
        h.Config.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AppSettings { AutostartEnabled = true, IsMonitoringEnabled = false }));
        h.Autostart.State = AutostartState.Unknown; // the registration could not be read reliably
        var controller = h.Create();

        await controller.ReconcileAsync();

        controller.AutostartActualState.Should().Be(AutostartState.Unknown);
        controller.AutostartSyncError.Should().NotBeNullOrWhiteSpace();
        h.Autostart.SetCalls.Should().Be(0, "an Unknown startup state must never trigger a mutating operation");
    }

    [Fact]
    public async Task Reconcile_NoPendingNotice_VerifiedSyncedState_IsSurfaced()
    {
        using var h = new Harness();
        h.Config.InitializeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ConfigurationInitializationStatus.Success));
        h.Config.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AppSettings { AutostartEnabled = false, IsMonitoringEnabled = false }));
        h.Autostart.State = AutostartState.Absent; // already matches the persisted off state
        var controller = h.Create();

        await controller.ReconcileAsync();

        controller.AutostartActualState.Should().Be(AutostartState.Absent);
        controller.AutostartSyncError.Should().BeNull("a clean idempotent sync must carry no error");
        h.Autostart.SetCalls.Should().Be(0);
    }

    [Fact]
    public async Task Reconcile_WhileResetNoticePending_DoesNotPublishAutostartResult()
    {
        using var h = new Harness();
        h.Config.InitializeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ConfigurationInitializationStatus.ResetNoticePending));
        var controller = h.Create();

        await controller.ReconcileAsync();

        controller.RecoveryPending.Should().BeTrue();
        controller.AutostartActualState.Should().BeNull("no autostart read/sync happens while the notice is pending");
        controller.AutostartSyncError.Should().BeNull();
    }
}
