using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging;
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

        // When > 0, the next N removal attempts fail (using RemoveResult when set), then
        // succeed — for testing recovery that completes on a later attempt.
        public int FailFirstRemovalAttempts { get; set; }

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
            if (FailFirstRemovalAttempts > 0)
            {
                FailFirstRemovalAttempts--;
                return Task.FromResult(RemoveResult is { } result
                    ? result
                    : AutostartResult.Failure("the autostart task removal could not be verified", State));
            }

            if (RemoveResult is { } configured)
                return Task.FromResult(configured); // requested removal did not take effect / unverifiable
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

        // Deterministic time (v2.6 Amendment C): the inter-attempt Task.Delay resolves through the
        // injected TimeProvider; the fake records each requested delay and fires the timer
        // immediately (or holds it, for the cancellation test), so no test ever sleeps in real time.
        public InstantFireTimeProvider Time { get; } = new();

        public IReadOnlyList<TimeSpan> RecoveryDelays => Time.RequestedDelays;

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

        public void DefaultsInitializedPendingRecoveryInit() =>
            Config.InitializeAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(ConfigurationInitializationStatus.DefaultsInitializedPendingRecovery));

        public GuardianOrchestrator Create(ILogger<GuardianOrchestrator>? logger = null) => new(
            Monitor, Config, Notifications, logger ?? NullLogger<GuardianOrchestrator>.Instance,
            Status, Integrity, Autostart, Time, ResetNoticeAck);
    }

    // TimeProvider fake for the inter-attempt recovery delay (v2.6 Amendment C):
    // Task.Delay(dueTime, timeProvider, ct) creates its timer through CreateTimer; the fake
    // records each requested due time and (unless HoldTimers is set) fires the timer callback
    // synchronously, so the delay completes deterministically without any real wall-clock wait.
    // With HoldTimers set, the delay never completes until its cancellation token fires, which
    // lets a test exercise the cancellable-delay path end to end.
    internal sealed class InstantFireTimeProvider : TimeProvider
    {
        public List<TimeSpan> RequestedDelays { get; } = new();

        public bool HoldTimers { get; set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            RequestedDelays.Add(dueTime);
            if (!HoldTimers)
                callback?.Invoke(state);
            return new NeverFiringTimer();
        }

        private sealed class NeverFiringTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
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

    // Capturing logger asserting controller-level levels and rendered messages.
    private sealed class CapturingLogger : ILogger<GuardianOrchestrator>
    {
        public List<(LogLevel Level, string Message)> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task Reconcile_MissingDirectoryDefaultsInit_RunsTheSameRecoveryFlow_WithNeutralLog()
    {
        using var h = new Harness();
        h.DefaultsInitializedPendingRecoveryInit();
        h.Autostart.State = AutostartState.Enabled; // a pre-existing task that the delete removes
        var logger = new CapturingLogger();

        var controller = h.Create(logger);
        await controller.ReconcileAsync();

        // The neutral missing-directory init runs the EXACT same reconciliation: verified task
        // removal, persisted disabled state, journal clear, no enable/create, no session start.
        controller.AutostartActualState.Should().Be(AutostartState.Absent);
        controller.RecoveryPending.Should().BeFalse("a fully-verified recovery clears the pending gate");
        h.LastSaved.Should().NotBeNull();
        h.LastSaved!.AutostartEnabled.Should().BeFalse();
        h.LastSaved.IsMonitoringEnabled.Should().BeFalse();
        h.ResetNoticeAck.ClearCalls.Should().Be(1);
        h.Autostart.SetCalls.Should().Be(0, "recovery removes; it never enables a registration");
        _ = h.Monitor.DidNotReceive().TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        controller.CurrentState.Status.Should().Be(GuardianStatus.Stopped);

        // The controller reports the missing-directory path at Information with neutral wording,
        // never as a reset/quarantine Warning (R-AS-FIRST-RUN-DIAGNOSTICS).
        logger.Messages.Should().NotContain(m =>
            m.Level == LogLevel.Warning
            && m.Message.Contains("Reset notice pending", StringComparison.Ordinal));
        logger.Messages.Should().NotContain(m =>
            m.Message.Contains("quarantin", StringComparison.OrdinalIgnoreCase));
        logger.Messages.Should().Contain(m =>
            m.Level == LogLevel.Information
            && m.Message.Contains("Configuration directory was absent", StringComparison.Ordinal));
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
        // [v2.6 Amendment A] Exhausted recovery publishes a locked recovery-failure Error,
        // never a Stopped look-alike.
        controller.CurrentState.Status.Should().Be(GuardianStatus.Error);
        controller.CurrentState.IsDegraded.Should().BeFalse("IsDegraded is reserved for active session states");
        controller.CurrentState.Reason.Should().Be(GuardianOrchestrator.RecoveryFailedReason);
        controller.CurrentState.SaveFailed.Should().BeFalse("the settings persistence itself never failed in this scenario");
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
        // [v2.6 Amendment A] Exhausted recovery publishes a locked recovery-failure Error.
        controller.CurrentState.Status.Should().Be(GuardianStatus.Error);
        controller.CurrentState.IsDegraded.Should().BeFalse();
        controller.CurrentState.Reason.Should().Be(GuardianOrchestrator.RecoveryFailedReason);
        controller.CurrentState.SaveFailed.Should().BeFalse();
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
        // [v2.6 Amendment A] Exhausted recovery with a failing persistence step publishes Error
        // with SaveFailed=true — the SaveFailed flag is reserved for exactly this case.
        first.CurrentState.Status.Should().Be(GuardianStatus.Error);
        first.CurrentState.SaveFailed.Should().BeTrue("the recovery settings persistence itself failed on every attempt");
        first.CurrentState.Reason.Should().Be(GuardianOrchestrator.RecoveryFailedReason);

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
        // [v2.6 Amendment A] Exhausted recovery after journal-clear failures: Error with
        // SaveFailed=false — the settings save itself succeeded on every attempt.
        first.CurrentState.Status.Should().Be(GuardianStatus.Error);
        first.CurrentState.SaveFailed.Should().BeFalse("the settings save succeeded; only the journal clear failed");
        first.CurrentState.Reason.Should().Be(GuardianOrchestrator.RecoveryFailedReason);

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
        // [v2.6 Amendment A] Exhausted recovery publishes a locked recovery-failure Error.
        controller.CurrentState.Status.Should().Be(GuardianStatus.Error);
        controller.CurrentState.IsDegraded.Should().BeFalse();
        controller.CurrentState.Reason.Should().Be(GuardianOrchestrator.RecoveryFailedReason);
        controller.CurrentState.SaveFailed.Should().BeFalse();
        // [v2.6 Amendment C] Exactly one delay between each pair of failed attempts: 3 attempts
        // → exactly 2 delays of one second each, none before the first or after the final one.
        h.RecoveryDelays.Should().HaveCount(2);
        h.RecoveryDelays.Should().OnlyContain(d => d == TimeSpan.FromSeconds(1));
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

    // [v2.6 Amendment B / R-AS-GATE] While recovery is pending, a direct (user-originated)
    // CommitSettingsAsync call is rejected: no SaveAsync, no SetEnabledAsync /
    // RemoveRegistrationAsync, and no change to the committed path/pin state — verified by the
    // controller-owned persistence that a subsequent successful recovery performs.
    [Fact]
    public async Task CommitSettings_DuringPendingRecovery_IsRejected_NoSaveNoAutostart_NoPathOrPinChange()
    {
        using var h = new Harness();
        h.ResetNoticePendingInit();
        h.Autostart.State = AutostartState.Enabled;
        h.Autostart.RemoveResult = AutostartResult.Failure(
            "the autostart task is still present after the delete (state: Enabled)", AutostartState.Enabled);

        var controller = h.Create();
        await controller.ReconcileAsync();
        controller.RecoveryPending.Should().BeTrue();
        h.Config.ClearReceivedCalls();

        var snapshot = CommittedSettingsSnapshot.From(new AppSettings { AutostartEnabled = true });
        var result = await controller.CommitSettingsAsync(snapshot, @"C:\rejected\new-target.exe");

        result.Saved.Should().BeFalse("a user-originated commit must be rejected while recovery is pending");
        _ = h.Config.DidNotReceive().SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>());
        h.Autostart.SetCalls.Should().Be(0, "no autostart enable may run for a rejected commit");
        h.Autostart.RemoveCalls.Should().Be(3, "only the recovery's own bounded removal attempts ran");
        controller.RecoveryPending.Should().BeTrue("a rejected commit must not clear the pending gate");
        _ = h.Monitor.DidNotReceive().TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        controller.CurrentState.Status.Should().Be(GuardianStatus.Error,
            "the recovery was exhausted; a rejected commit must not republish any state over the failure");

        // The committed path/pin state was not touched: the controller-owned recovery save that
        // runs after the recovery finally succeeds persists the ORIGINAL (safe default) values,
        // never the rejected commit's path or the snapshot's settings.
        h.Autostart.RemoveResult = null;
        await controller.ReconcileAsync();
        controller.RecoveryPending.Should().BeFalse();
        h.LastSaved.Should().NotBeNull();
        h.LastSaved!.TargetProcessPath.Should().BeEmpty(
            "the rejected commit's path never reached the committed controller state");
        h.LastSaved.TargetHashSha256.Should().BeEmpty();
        h.LastSaved.CheckIntervalSeconds.Should().Be(new AppSettings().CheckIntervalSeconds,
            "the rejected snapshot's values never leaked into the recovery-owned save");
    }

    // [v2.6 Amendment A / C] Recovery that succeeds on a later attempt after exactly one failed
    // attempt: exactly one one-second delay was requested (only between attempts), the journal
    // was cleared, the pending gate reopened, and the clean Stopped state was published.
    [Fact]
    public async Task Reconcile_RecoverySucceedsOnSecondAttempt_OneDelay_StoppedPublished()
    {
        using var h = new Harness();
        h.ResetNoticePendingInit();
        h.Autostart.State = AutostartState.Enabled;
        h.Autostart.FailFirstRemovalAttempts = 1;

        var controller = h.Create();
        await controller.ReconcileAsync();

        h.Autostart.RemoveCalls.Should().Be(2, "the first removal failed and the second succeeded");
        controller.RecoveryPending.Should().BeFalse();
        h.ResetNoticeAck.ClearCalls.Should().Be(1);
        controller.CurrentState.Status.Should().Be(GuardianStatus.Stopped);
        controller.CurrentState.SaveFailed.Should().BeFalse();
        h.RecoveryDelays.Should().ContainSingle("exactly one delay sits between the two attempts")
            .Which.Should().Be(TimeSpan.FromSeconds(1));
    }

    // [v2.6 Amendment A / C] Recovery that succeeds on the third (final) attempt: exactly two
    // one-second delays were requested and none after the successful attempt.
    [Fact]
    public async Task Reconcile_RecoverySucceedsOnThirdAttempt_TwoDelaysBetweenAttemptsOnly()
    {
        using var h = new Harness();
        h.ResetNoticePendingInit();
        h.Autostart.State = AutostartState.Enabled;
        h.Autostart.FailFirstRemovalAttempts = 2;

        var controller = h.Create();
        await controller.ReconcileAsync();

        h.Autostart.RemoveCalls.Should().Be(3);
        controller.RecoveryPending.Should().BeFalse();
        h.ResetNoticeAck.ClearCalls.Should().Be(1);
        controller.CurrentState.Status.Should().Be(GuardianStatus.Stopped);
        h.RecoveryDelays.Should().HaveCount(2, "one delay between each pair of attempts");
        h.RecoveryDelays.Should().OnlyContain(d => d == TimeSpan.FromSeconds(1));
    }

    // [v2.6 Amendment C] The inter-attempt delay is cancellable: while the second attempt's
    // delay is held open, cancelling ReconcileAsync's token aborts the recovery mid-flow — the
    // journal stays pending, the pending gate stays set, and no further removal attempt runs.
    [Fact]
    public async Task Reconcile_CancelledDuringInterAttemptDelay_Aborts_JournalStaysPending_NoFurtherAttempts()
    {
        using var h = new Harness();
        h.ResetNoticePendingInit();
        h.Autostart.State = AutostartState.Enabled;
        h.Autostart.RemoveResult = AutostartResult.Failure(
            "the autostart task is still present after the delete (state: Enabled)", AutostartState.Enabled);
        h.Time.HoldTimers = true;

        using var cts = new CancellationTokenSource();
        var controller = h.Create();
        var reconcile = controller.ReconcileAsync(cts.Token);

        // Wait until the flow is parked on the second attempt's held delay (first attempt done).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (h.Autostart.RemoveCalls < 1 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        h.Autostart.RemoveCalls.Should().Be(1, "the first attempt completed before the held delay");
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconcile);

        h.Autostart.RemoveCalls.Should().Be(1, "no further attempt may run after cancellation");
        h.ResetNoticeAck.ClearCalls.Should().Be(0, "cancellation must never clear the journal");
        controller.RecoveryPending.Should().BeTrue("the recovery never completed");
        h.RecoveryDelays.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(1));
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
