using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ProcessGuardian.Application.Services;
using ProcessGuardian.Domain.Entities;
using ProcessGuardian.Domain.Interfaces;
using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Domain.ValueObjects;
using Xunit;

namespace ProcessGuardian.Application.Tests;

// Automatic recovery (v2.5 R-AS-AUTO) at the controller level: a ResetNoticePending init runs a
// bounded, idempotent recovery BEFORE any ordinary autostart sync or Start. The app-owned task is
// removed and positively verified absent, AutostartEnabled=false + IsMonitoringEnabled=false are
// persisted, and ONLY then is the journal cleared. Every failure keeps the journal pending so the
// next startup re-runs the same flow; Start is blocked by the controller until the journal clears;
// no autostart enable/create and no session may run while pending.
public class GuardianAutomaticRecoveryTests
{
    // Simulates the scheduled-task autostart service: registers state in memory and can be told to
    // fail a removal in a way that leaves the actual registration unchanged (delete that exits
    // without the task going away). By default RemoveRegistrationAsync removes and reports Success.
    private sealed class FakeAutostart : IAutostartService
    {
        public AutostartState State { get; set; } = AutostartState.Absent;
        public AutostartResult? RemoveResult { get; set; }
        public int SetCalls { get; private set; }
        public int RemoveCalls { get; private set; }

        public Task<AutostartState> ReadStateAsync(CancellationToken ct = default) => Task.FromResult(State);

        public Task<AutostartResult> SetEnabledAsync(bool enabled, CancellationToken ct = default)
        {
            SetCalls++;
            State = enabled ? AutostartState.Enabled : AutostartState.Absent;
            return Task.FromResult(AutostartResult.Success(State));
        }

        public Task<AutostartResult> RemoveRegistrationAsync(CancellationToken ct = default)
        {
            RemoveCalls++;
            if (RemoveResult is { } result)
                return Task.FromResult(result); // requested removal did not take effect / unverifiable
            State = AutostartState.Absent;
            return Task.FromResult(AutostartResult.Success(AutostartState.Absent));
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
        public string TargetPath { get; }

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
            var dir = Path.Combine(Path.GetTempPath(), "ProcessGuardian.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            TargetPath = Path.Combine(dir, "target.exe");
            File.WriteAllText(TargetPath, string.Empty);

            Config.LoadAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AppSettings()));
            Config.SaveAsync(Arg.Do<AppSettings>(s => LastSaved = s), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
            Integrity.ComputeHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(TargetHashResult.Computed("PIN")));
            Notifications.ShowAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
        }

        public void ResetNoticePendingInit() =>
            Config.InitializeAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(ConfigurationInitializationStatus.ResetNoticePending));

        public GuardianOrchestrator Create() => new(
            Monitor, Config, Notifications, NullLogger<GuardianOrchestrator>.Instance,
            Status, Integrity, Autostart, null, ResetNoticeAck);
    }

    [Fact]
    public async Task Reconcile_ResetNoticePending_TaskAlreadyAbsent_VerifiesAbsent_PersistsFalse_ClearsJournal()
    {
        using var h = new Harness();
        h.ResetNoticePendingInit();
        h.Autostart.State = AutostartState.Enabled; // a pre-existing task that the delete removes

        var controller = h.Create();
        await controller.ReconcileAsync();

        // The app-owned task was removed and positively verified absent via the removal primitive.
        controller.AutostartActualState.Should().Be(AutostartState.Absent);
        controller.RecoveryPending.Should().BeFalse("a fully-verified recovery clears the pending gate");

        // AutostartEnabled=false + IsMonitoringEnabled=false were persisted as one snapshot.
        h.LastSaved.Should().NotBeNull();
        h.LastSaved!.AutostartEnabled.Should().BeFalse();
        h.LastSaved.IsMonitoringEnabled.Should().BeFalse();

        // Journal cleared only after task-absent + save succeeded.
        h.ResetNoticeAck.ClearCalls.Should().Be(1);

        // No autostart enable/create and no session start ever ran during recovery.
        h.Autostart.SetCalls.Should().Be(0, "recovery removes; it never enables a registration");
        _ = h.Monitor.DidNotReceive().TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        controller.CurrentState.Status.Should().Be(GuardianStatus.Stopped);
    }

    [Fact]
    public async Task Reconcile_ResetNoticePending_DeleteNonZeroButQueryAbsent_StillSucceeds()
    {
        using var h = new Harness();
        h.ResetNoticePendingInit();
        h.Autostart.State = AutostartState.Enabled;
        // Simulates a /Delete that exits nonzero (e.g. not-found) while the follow-up query
        // positively confirms Absent — the deletion is still verified.
        h.Autostart.RemoveResult = AutostartResult.Success(AutostartState.Absent);

        var controller = h.Create();
        await controller.ReconcileAsync();

        controller.RecoveryPending.Should().BeFalse();
        h.ResetNoticeAck.ClearCalls.Should().Be(1);
        h.LastSaved!.AutostartEnabled.Should().BeFalse();
        h.LastSaved!.IsMonitoringEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Reconcile_ResetNoticePending_TaskStillPresent_RecoveryFails_JournalStaysPending()
    {
        using var h = new Harness();
        h.ResetNoticePendingInit();
        h.Autostart.State = AutostartState.Enabled;
        h.Autostart.RemoveResult = AutostartResult.Failure(
            "the autostart task is still present after the delete (state: Enabled)", AutostartState.Enabled);

        var controller = h.Create();
        await controller.ReconcileAsync();

        controller.RecoveryPending.Should().BeTrue("a task that is still present keeps the recovery pending");
        h.ResetNoticeAck.ClearCalls.Should().Be(0, "nothing is cleared until the task is verified absent");
        h.LastSaved.Should().BeNull("no disabled-state save happens while the task is still present");
        _ = h.Monitor.DidNotReceive().TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        controller.CurrentState.Status.Should().Be(GuardianStatus.Stopped);
    }

    [Fact]
    public async Task Reconcile_ResetNoticePending_QueryUnknown_RecoveryFails_JournalStaysPending()
    {
        using var h = new Harness();
        h.ResetNoticePendingInit();
        h.Autostart.State = AutostartState.Unknown;
        h.Autostart.RemoveResult = AutostartResult.Failure(
            "the autostart task removal could not be verified; the state is unknown", AutostartState.Unknown);

        var controller = h.Create();
        await controller.ReconcileAsync();

        controller.RecoveryPending.Should().BeTrue("an Unknown verification must fail closed and stay pending");
        h.ResetNoticeAck.ClearCalls.Should().Be(0);
        h.LastSaved.Should().BeNull();
        controller.AutostartActualState.Should().Be(AutostartState.Unknown);
    }

    [Fact]
    public async Task Reconcile_ResetNoticePending_SaveFails_JournalStaysPending_NextStartupRetries()
    {
        using var h = new Harness();
        h.ResetNoticePendingInit();
        h.Autostart.State = AutostartState.Enabled;
        // Crash boundary after the task is removed but before the save: the journal stays pending.
        h.Config.SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("disk full")));

        var first = h.Create();
        await first.ReconcileAsync();

        first.RecoveryPending.Should().BeTrue();
        h.ResetNoticeAck.ClearCalls.Should().Be(0, "journal is not cleared when the save failed");
        h.Autostart.State.Should().Be(AutostartState.Absent, "the task was already removed");

        // Next startup: the same idempotent flow finishes — removal re-verifies the already-absent
        // task (no recreate), the save succeeds, and the journal is cleared.
        h.Config.SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var second = h.Create();
        await second.ReconcileAsync();

        second.RecoveryPending.Should().BeFalse();
        h.ResetNoticeAck.ClearCalls.Should().Be(1);
        h.LastSaved!.AutostartEnabled.Should().BeFalse();
        h.LastSaved!.IsMonitoringEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Reconcile_ResetNoticePending_JournalClearFails_RecoveryIncomplete_NextStartupRetries()
    {
        using var h = new Harness();
        h.ResetNoticePendingInit();
        h.Autostart.State = AutostartState.Enabled;
        h.ResetNoticeAck.ThrowOnClear = new InvalidOperationException("HKEY access refused");

        var first = h.Create();
        await first.ReconcileAsync();

        // The task is absent and the disabled state was saved, but the journal is not cleared:
        // the recovery is NOT complete and the pending gate stays closed. Each bounded attempt
        // reaches the (throwing) journal clear after re-verifying the now-absent task.
        first.RecoveryPending.Should().BeTrue();
        h.ResetNoticeAck.ClearCalls.Should().Be(3, "the clear was attempted on each of the three bounded attempts but threw");
        h.LastSaved!.AutostartEnabled.Should().BeFalse();

        h.ResetNoticeAck.ThrowOnClear = null;
        var second = h.Create();
        await second.ReconcileAsync();

        second.RecoveryPending.Should().BeFalse();
        h.ResetNoticeAck.ClearCalls.Should().Be(4, "the retry clears the journal exactly when it finally succeeds (3 failed attempts + 1 success)");
    }

    [Fact]
    public async Task Reconcile_ResetNoticePending_RecoveryNeverCreatesAutostart_NeverStartsSession()
    {
        using var h = new Harness();
        h.ResetNoticePendingInit();
        h.Autostart.State = AutostartState.Absent; // already absent: delete-not-found path, still verified

        var controller = h.Create();
        await controller.ReconcileAsync();

        h.Autostart.SetCalls.Should().Be(0, "recovery must never create or enable a registration");
        h.Autostart.RemoveCalls.Should().Be(1);
        _ = h.Monitor.DidNotReceive().TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        controller.CurrentState.Status.Should().Be(GuardianStatus.Stopped);
    }

    [Fact]
    public async Task Reconcile_ResetNoticePending_PersistentFailure_BoundedToThreeAttempts()
    {
        using var h = new Harness();
        h.ResetNoticePendingInit();
        h.Autostart.State = AutostartState.Enabled;
        h.Autostart.RemoveResult = AutostartResult.Failure(
            "the autostart task removal could not be verified; the state is unknown", AutostartState.Unknown);

        var controller = h.Create();
        await controller.ReconcileAsync();

        h.Autostart.RemoveCalls.Should().Be(3, "recovery is bounded to exactly three attempts per startup");
        controller.RecoveryPending.Should().BeTrue();
        h.ResetNoticeAck.ClearCalls.Should().Be(0);
    }

    [Fact]
    public async Task Start_WhileRecoveryPending_IsRejected_NoHashNoSaveNoSession()
    {
        using var h = new Harness();
        h.ResetNoticePendingInit();
        h.Autostart.State = AutostartState.Enabled;
        h.Autostart.RemoveResult = AutostartResult.Failure(
            "the autostart task is still present after the delete (state: Enabled)", AutostartState.Enabled);

        var controller = h.Create();
        await controller.ReconcileAsync();
        controller.RecoveryPending.Should().BeTrue();

        var start = await controller.StartAsync(new StartRequest(
            CommittedSettingsSnapshot.From(new AppSettings()), h.TargetPath));

        start.Started.Should().BeFalse("Start is blocked by the controller while recovery is pending, not only by the UI");
        _ = h.Integrity.DidNotReceive().ComputeHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        _ = h.Config.DidNotReceive().SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>());
        _ = h.Monitor.DidNotReceive().TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        h.Autostart.SetCalls.Should().Be(0, "a rejected Start must never enable autostart");
    }

    [Fact]
    public async Task Start_AfterSuccessfulRecovery_IsAllowed()
    {
        using var h = new Harness();
        h.ResetNoticePendingInit();
        h.Autostart.State = AutostartState.Enabled;

        var controller = h.Create();
        await controller.ReconcileAsync();
        controller.RecoveryPending.Should().BeFalse();

        var start = await controller.StartAsync(new StartRequest(
            CommittedSettingsSnapshot.From(new AppSettings()), h.TargetPath));

        start.Started.Should().BeTrue(
            "once the journal is cleared the pending gate reopens and the Start request reaches the normal session path");
        // Start itself never mutates autostart — that is the settings-commit path's responsibility;
        // only that a Start is no longer blocked by the (now-clear) recovery gate matters here.
        h.Autostart.SetCalls.Should().Be(0, "Start does not enable autostart; only a settings commit does");
    }
}
