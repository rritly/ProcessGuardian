using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProcessGuardian.Domain.ValueObjects;
using ProcessGuardian.Infrastructure.ProcessMonitoring;
using Xunit;

namespace ProcessGuardian.Infrastructure.Tests;

public class WindowsProcessMonitorTests : IDisposable
{
    private readonly WindowsProcessMonitor _monitor;
    private readonly string _notepadPath;
    private readonly ProcessTarget _target;
    private int? _ownedPid;

    public WindowsProcessMonitorTests()
    {
        _monitor = new WindowsProcessMonitor(NullLogger<WindowsProcessMonitor>.Instance);
        _notepadPath = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        if (!File.Exists(_notepadPath))
            throw new Xunit.Sdk.XunitException($"notepad.exe not found at {_notepadPath}");
        _target = new ProcessTarget(
            _notepadPath,
            Path.GetFileNameWithoutExtension(_notepadPath),
            Path.GetDirectoryName(_notepadPath) ?? string.Empty);
    }

    public void Dispose()
    {
        if (_ownedPid.HasValue)
        {
            try
            {
                using var p = Process.GetProcessById(_ownedPid.Value);
                if (!p.HasExited)
                {
                    p.Kill();
                    p.WaitForExit(3000);
                }
            }
            catch
            {
                // Process already gone or inaccessible; ignore
            }
        }
    }

    private void StartOwnNotepad()
    {
        var psi = new ProcessStartInfo(_notepadPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var proc = Process.Start(psi)!;
        _ownedPid = proc.Id;
        proc.Dispose();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string message, int timeoutMs = 8000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException($"Timed out waiting for: {message}");
    }

    [Fact]
    public async Task AfterStart_CheckReturnsRunning_WithProcessId()
    {
        StartOwnNotepad();
        await WaitUntilAsync(
            async () => (await _monitor.CheckAsync(_target, CancellationToken.None)).Outcome == ProcessOutcome.Running,
            "notepad to be detected as Running");

        var result = await _monitor.CheckAsync(_target, CancellationToken.None);
        result.Outcome.Should().Be(ProcessOutcome.Running);
        result.ProcessId.Should().NotBeNull();
    }

    [Fact]
    public async Task FullPathMatch_IsCaseInsensitive()
    {
        StartOwnNotepad();
        await WaitUntilAsync(
            async () => (await _monitor.CheckAsync(_target, CancellationToken.None)).Outcome == ProcessOutcome.Running,
            "notepad to be detected as Running");

        var upperPath = _notepadPath.ToUpperInvariant();
        var targetUpper = new ProcessTarget(upperPath, "notepad", Path.GetDirectoryName(upperPath) ?? string.Empty);
        var result = await _monitor.CheckAsync(targetUpper, CancellationToken.None);
        result.Outcome.Should().Be(ProcessOutcome.Running);
    }

    [Fact]
    public async Task AfterKill_CheckReturnsNotRunning()
    {
        var preRunning = (await _monitor.CheckAsync(_target, CancellationToken.None)).Outcome == ProcessOutcome.Running;

        StartOwnNotepad();
        await WaitUntilAsync(
            async () => (await _monitor.CheckAsync(_target, CancellationToken.None)).Outcome == ProcessOutcome.Running,
            "notepad to be detected as Running");

        using (var proc = Process.GetProcessById(_ownedPid!.Value))
        {
            proc.Kill();
            proc.WaitForExit(3000);
        }
        _ownedPid = null;

        if (!preRunning)
        {
            await WaitUntilAsync(
                async () => (await _monitor.CheckAsync(_target, CancellationToken.None)).Outcome == ProcessOutcome.NotRunning,
                "notepad to be gone after kill");
        }
    }

    [Fact]
    public void TryStart_NonexistentPath_ReturnsMissing_NoThrow()
    {
        var bogus = new ProcessTarget(
            @"C:\DoesNotExist\missing.exe",
            "missing",
            @"C:\DoesNotExist");

        var result = _monitor.TryStart(bogus, CancellationToken.None);

        result.Status.Should().Be(ProcessStartStatus.Missing);
    }

    [Fact]
    public void EvaluateCandidates_ReadableMatch_IsRunning()
    {
        var candidates = new[]
        {
            new WindowsProcessMonitor.ProcessCandidate(101, @"C:\App\other.exe", Unreadable: false),
            new WindowsProcessMonitor.ProcessCandidate(202, @"C:\App\target.exe", Unreadable: false),
        };

        var result = WindowsProcessMonitor.EvaluateCandidates(@"C:\app\target.exe", candidates);

        result.Outcome.Should().Be(ProcessOutcome.Running);
        result.ProcessId.Should().Be(202);
    }

    [Fact]
    public void EvaluateCandidates_AllReadableNoMatch_IsNotRunning()
    {
        var candidates = new[]
        {
            new WindowsProcessMonitor.ProcessCandidate(101, @"C:\App\other.exe", Unreadable: false),
            new WindowsProcessMonitor.ProcessCandidate(202, @"C:\App\another.exe", Unreadable: false),
        };

        var result = WindowsProcessMonitor.EvaluateCandidates(@"C:\App\target.exe", candidates);

        result.Outcome.Should().Be(ProcessOutcome.NotRunning);
    }

    [Fact]
    public void EvaluateCandidates_UnreadableNoReadableMatch_IsUnknown_NotNotRunning()
    {
        // E discipline: at least one unreadable candidate with no readable match is Unknown,
        // never NotRunning (which would trigger a duplicate relaunch of a possibly-running target).
        var candidates = new[]
        {
            new WindowsProcessMonitor.ProcessCandidate(101, null, Unreadable: true),
        };

        var result = WindowsProcessMonitor.EvaluateCandidates(@"C:\App\target.exe", candidates);

        result.Outcome.Should().Be(ProcessOutcome.Unknown);
    }

    [Fact]
    public void EvaluateCandidates_UnreadablePlusReadableMatch_IsRunning_NotDowngraded()
    {
        // A single unreadable module must never downgrade a readable match (E).
        var candidates = new[]
        {
            new WindowsProcessMonitor.ProcessCandidate(101, null, Unreadable: true),
            new WindowsProcessMonitor.ProcessCandidate(202, @"C:\App\target.exe", Unreadable: false),
        };

        var result = WindowsProcessMonitor.EvaluateCandidates(@"C:\App\target.exe", candidates);

        result.Outcome.Should().Be(ProcessOutcome.Running);
        result.ProcessId.Should().Be(202);
    }

    [Fact]
    public void EvaluateCandidates_TwoReadableMatches_IsRunning_ExitOfOneIsNotAbsence()
    {
        // Multiple matching instances (v2.8 §4.3): any readable full-path match means Running;
        // the exit of one instance must never be read as target absence while another remains.
        var candidates = new[]
        {
            new WindowsProcessMonitor.ProcessCandidate(101, @"C:\App\target.exe", Unreadable: false),
            new WindowsProcessMonitor.ProcessCandidate(202, @"C:\APP\TARGET.EXE", Unreadable: false),
        };

        var result = WindowsProcessMonitor.EvaluateCandidates(@"C:\app\target.exe", candidates);

        result.Outcome.Should().Be(ProcessOutcome.Running);
        result.ProcessId.Should().BeOneOf(101, 202);
    }

    // R-KE: document and verify the native Win32 error-code mapping at the launch boundary.
    [Theory]
    [InlineData(2, ProcessStartStatus.Missing)]  // ERROR_FILE_NOT_FOUND
    [InlineData(3, ProcessStartStatus.Missing)]  // ERROR_PATH_NOT_FOUND
    [InlineData(5, ProcessStartStatus.ReadError)] // ERROR_ACCESS_DENIED
    [InlineData(32, ProcessStartStatus.ReadError)] // ERROR_SHARING_VIOLATION
    [InlineData(193, ProcessStartStatus.Failed)]  // ERROR_BAD_EXE_FORMAT
    [InlineData(740, ProcessStartStatus.Failed)]  // ERROR_ELEVATION_REQUIRED
    [InlineData(206, ProcessStartStatus.Failed)]  // ERROR_FILENAME_EXCED_RANGE
    public void ClassifyWin32Error_MapsDocumentedCodes(int nativeCode, ProcessStartStatus expected)
    {
        WindowsProcessMonitor.ClassifyWin32Error(nativeCode).Should().Be(expected);
    }

    // ---------------------------------------------------------------------
    // R-AS-RECOVERY-CORRECTNESS: per-process inspection classification (§5).
    // ERROR_ACCESS_DENIED (5) and ERROR_PARTIAL_COPY (299) keep their own
    // meanings; 299 never proves absence — the exit of exactly the inspected
    // process must be positively established before exclusion.
    // ---------------------------------------------------------------------

    [Fact]
    public void ClassifyInspection_ReadableModulePath_IsReadableCandidate()
    {
        var candidate = WindowsProcessMonitor.ClassifyInspection(101, @"C:\App\target.exe", null, null);

        candidate.Should().NotBeNull();
        candidate!.Value.ProcessId.Should().Be(101);
        candidate.Value.ModulePath.Should().Be(@"C:\App\target.exe");
        candidate.Value.Unreadable.Should().BeFalse();
    }

    [Fact]
    public void ClassifyInspection_AccessDenied5_IsUnreadable_NeverExcluded()
    {
        // Access denied (5): the state is indeterminate even when an exit probe result is
        // available — the process is never excluded on a denial, only on a positively
        // established exit through the clean-read or partial-copy discipline.
        var candidate = WindowsProcessMonitor.ClassifyInspection(101, null, 5, hasExited: true);

        candidate.Should().NotBeNull("an access-denied process is never excluded from the candidates");
        candidate!.Value.Unreadable.Should().BeTrue();
    }

    [Fact]
    public void ClassifyInspection_PartialCopy299_ExitedProcess_IsExcluded()
    {
        // §5.2: ERROR_PARTIAL_COPY (299) with the INSPECTED process's own positively
        // established exit — the process contributes nothing (reliably absent).
        var candidate = WindowsProcessMonitor.ClassifyInspection(202, null, 299, hasExited: true);

        candidate.Should().BeNull("a positively exited process is excluded from the candidate list");
    }

    [Fact]
    public void ClassifyInspection_PartialCopy299_StillAlive_IsUnreadable_NotExcluded()
    {
        // §5.2.4: a still-existing process whose full path could not be read stays
        // conservatively indeterminate (Unknown), never NotRunning.
        var candidate = WindowsProcessMonitor.ClassifyInspection(202, null, 299, hasExited: false);

        candidate.Should().NotBeNull();
        candidate!.Value.Unreadable.Should().BeTrue();
    }

    [Fact]
    public void ClassifyInspection_PartialCopy299_ExitUnverifiable_IsUnreadable()
    {
        // §5.2.5: when the exit could not be determined, the state stays indeterminate —
        // no automatic inference of absence from 299.
        var candidate = WindowsProcessMonitor.ClassifyInspection(202, null, 299, hasExited: null);

        candidate.Should().NotBeNull();
        candidate!.Value.Unreadable.Should().BeTrue();
    }

    [Fact]
    public void ClassifyInspection_UnknownWin32Code_IsUnreadable_NotAccessDenied()
    {
        // §5.3: an undocumented Win32 code keeps its own meaning — indeterminate, never
        // silently classified as access denied and never proof of absence.
        var candidate = WindowsProcessMonitor.ClassifyInspection(303, null, 87, hasExited: null);

        candidate.Should().NotBeNull();
        candidate!.Value.Unreadable.Should().BeTrue();
    }

    [Fact]
    public void ClassifyInspection_CleanRead_NoPath_Exited_IsExcluded()
    {
        // The existing positive-exit discipline for a clean read with no module path.
        var candidate = WindowsProcessMonitor.ClassifyInspection(404, null, null, hasExited: true);

        candidate.Should().BeNull();
    }

    [Fact]
    public void ClassifyInspection_CleanRead_NoPath_AliveOrUnverifiable_IsUnreadable()
    {
        var alive = WindowsProcessMonitor.ClassifyInspection(404, null, null, hasExited: false);
        alive.Should().NotBeNull();
        alive!.Value.Unreadable.Should().BeTrue();

        var unverifiable = WindowsProcessMonitor.ClassifyInspection(404, null, null, hasExited: null);
        unverifiable.Should().NotBeNull();
        unverifiable!.Value.Unreadable.Should().BeTrue();
    }

    // §5.2/§5.6: one of several same-name processes exited during the inspection — the exited
    // one contributes nothing while a remaining readable match still means Running, and a
    // live-but-unreadable candidate never becomes a false NotRunning.
    [Fact]
    public void EvaluateCandidates_OneExitedDuringInspection_RemainingReadableMatch_IsRunning()
    {
        // The exited process is excluded at the inspection seam (299 with a positively
        // established exit of the inspected process itself).
        var exited = WindowsProcessMonitor.ClassifyInspection(202, null, 299, hasExited: true);
        exited.Should().BeNull("the exited process contributes nothing to the candidate list");

        var result = WindowsProcessMonitor.EvaluateCandidates(@"C:\app\target.exe", new[]
        {
            new WindowsProcessMonitor.ProcessCandidate(101, @"C:\App\target.exe", Unreadable: false),
        });

        result.Outcome.Should().Be(ProcessOutcome.Running,
            "one exited PID never proves the absence of every process with the same path");
        result.ProcessId.Should().Be(101);
    }

    [Fact]
    public void EvaluateCandidates_LiveButUnreadable299Candidate_IsUnknown_NotNotRunning()
    {
        // §5.2.6: a live process with 299 stays indeterminate — no false NotRunning.
        var alive = WindowsProcessMonitor.ClassifyInspection(202, null, 299, hasExited: false);
        alive.Should().NotBeNull();

        var result = WindowsProcessMonitor.EvaluateCandidates(@"C:\app\target.exe", new[] { alive!.Value });

        result.Outcome.Should().Be(ProcessOutcome.Unknown);
    }

    // §5.4: rate-limited inspection diagnostics — one full diagnostic at the first detection
    // of a (PID, code) pair, bounded structured warnings without a repeated stack trace on
    // later repeats, and a recovery entry that restores full diagnostics.
    private sealed class CapturingLogger : ILogger<WindowsProcessMonitor>
    {
        public sealed record Entry(LogLevel Level, Exception? Exception, string Message);

        public List<Entry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new Entry(logLevel, exception, formatter(state, exception)));
    }

    [Fact]
    public void InspectionDiagnostics_FirstOccurrenceFull_RepeatsRateLimited_RecoveryResets()
    {
        var logger = new CapturingLogger();
        var monitor = new WindowsProcessMonitor(logger);
        var ex299 = new Win32Exception(299, "partial copy");
        var ex5 = new Win32Exception(5, "access denied");

        // First detection: one full diagnostic — the exception payload (with its stack trace),
        // the PID, the real native code, the operation and the process name.
        monitor.LogInspectionFailure(4242, 299, "target", ex299);
        logger.Entries.Should().HaveCount(1);
        logger.Entries[0].Level.Should().Be(LogLevel.Warning);
        logger.Entries[0].Exception.Should().BeSameAs(ex299,
            "the first diagnostic carries the full exception payload for investigation");
        logger.Entries[0].Message.Should().Contain("4242").And.Contain("299").And.Contain("target");

        // Nine repeats of the same persistent condition: rate-limited, no additional entries.
        for (var i = 0; i < 9; i++)
            monitor.LogInspectionFailure(4242, 299, "target", ex299);
        logger.Entries.Should().HaveCount(1, "repeats before the diagnostic interval produce no entries");

        // The tenth repeat: one compact structured warning WITHOUT the exception payload —
        // no repeated stack trace per cycle.
        monitor.LogInspectionFailure(4242, 299, "target", ex299);
        logger.Entries.Should().HaveCount(2);
        logger.Entries[1].Level.Should().Be(LogLevel.Warning);
        logger.Entries[1].Exception.Should().BeNull("repeated diagnostics carry no repeated stack trace");
        logger.Entries[1].Message.Should().Contain("still fails").And.Contain("4242");

        // A different (PID, code) pair is diagnosed in full immediately.
        monitor.LogInspectionFailure(777, 5, "other", ex5);
        logger.Entries.Should().HaveCount(3);
        logger.Entries[2].Exception.Should().BeSameAs(ex5);
        logger.Entries[2].Message.Should().Contain("777");

        // Recovery of PID 4242: a separate diagnostic entry and a rate-limit state reset.
        monitor.LogInspectionRecovered(4242);
        logger.Entries.Should().HaveCount(4);
        logger.Entries[3].Level.Should().Be(LogLevel.Information);
        logger.Entries[3].Message.Should().Contain("recovered").And.Contain("4242");

        // A new failure of PID 4242 is diagnosed in full again (the state was reset).
        monitor.LogInspectionFailure(4242, 299, "target", ex299);
        logger.Entries.Should().HaveCount(5);
        logger.Entries[4].Exception.Should().BeSameAs(ex299);
    }

    [Fact]
    public void InspectionDiagnostics_RecoveredWithoutPriorFailure_LogsNothing()
    {
        var logger = new CapturingLogger();
        var monitor = new WindowsProcessMonitor(logger);

        monitor.LogInspectionRecovered(999);

        logger.Entries.Should().BeEmpty("a recovery entry is only logged for a previously failing PID");
    }

    // ---------------------------------------------------------------------
    // R-AS-INSPECTION-CLEANUP (§4): the InvalidOperationException exit
    // classification and the stale diagnostic-state cleanup on a positively
    // confirmed exit of the inspected process.
    // ---------------------------------------------------------------------

    // §4.4 #1: an InvalidOperationException followed by a positively confirmed exit of the
    // inspected process — the candidate is excluded (reliably absent).
    [Fact]
    public void ClassifyInvalidInspection_ConfirmedExit_IsExcluded()
    {
        var candidate = WindowsProcessMonitor.ClassifyInvalidInspection(505, hasExited: true);

        candidate.Should().BeNull("the positively exited inspected process is excluded");
    }

    // §4.4 #2: an InvalidOperationException with HasExited == false — the process is alive
    // but its module path is unavailable: an unreadable candidate, never exclusion.
    [Fact]
    public void ClassifyInvalidInspection_StillAlive_IsUnreadable()
    {
        var candidate = WindowsProcessMonitor.ClassifyInvalidInspection(505, hasExited: false);

        candidate.Should().NotBeNull("a live process with an unavailable module path stays a candidate");
        candidate!.Value.ProcessId.Should().Be(505);
        candidate.Value.ModulePath.Should().BeNull();
        candidate.Value.Unreadable.Should().BeTrue();
    }

    // §4.4 #3: an InvalidOperationException with an unverifiable exit state — conservatively
    // unreadable, never proof of absence.
    [Fact]
    public void ClassifyInvalidInspection_ExitUnverifiable_IsUnreadable()
    {
        var candidate = WindowsProcessMonitor.ClassifyInvalidInspection(505, hasExited: null);

        candidate.Should().NotBeNull("an unverifiable exit state is never converted into absence");
        candidate!.Value.Unreadable.Should().BeTrue();
    }

    // §4.4 #4/#5: ERROR_PARTIAL_COPY (299) followed by a positively confirmed exit — the
    // candidate is excluded AND the stale (PID, code) diagnostic counters are cleared: no
    // misleading "recovered" entry is emitted, and a later failure for the same PID starts
    // with a full first-failure diagnostic again.
    [Fact]
    public void InspectionDiagnostics_PartialCopyConfirmedExit_Excluded_CountersCleared_NoRecoveredEntry()
    {
        var logger = new CapturingLogger();
        var monitor = new WindowsProcessMonitor(logger);
        var ex299 = new Win32Exception(299, "partial copy");

        // The Win32 failure is recorded (full first-failure diagnostic) before the exit check.
        monitor.LogInspectionFailure(4242, 299, "target", ex299);
        logger.Entries.Should().HaveCount(1);
        logger.Entries[0].Exception.Should().BeSameAs(ex299);

        // The classification excludes the positively exited process; the production flow then
        // clears the stale counters. No "recovered" entry may be emitted — the process exited;
        // inspection did not recover (§4.2).
        var candidate = WindowsProcessMonitor.ClassifyInspection(4242, null, 299, hasExited: true);
        candidate.Should().BeNull("the positively exited process is excluded from the candidate list");
        monitor.ClearInspectionDiagnosticsOnConfirmedExit(4242);
        logger.Entries.Should().HaveCount(1, "no misleading 'inspection recovered' entry for a confirmed exit");

        // A later failure for the same PID starts with a full first-failure diagnostic again
        // (the stale counters were cleared).
        monitor.LogInspectionFailure(4242, 299, "target", ex299);
        logger.Entries.Should().HaveCount(2);
        logger.Entries[1].Level.Should().Be(LogLevel.Warning);
        logger.Entries[1].Exception.Should().BeSameAs(ex299,
            "the counters were cleared on the confirmed exit, so this is a fresh first-failure diagnostic");
        logger.Entries[1].Message.Should().Contain("4242").And.Contain("299");
    }

    // §4.4 #6/#10: bounded, PID-safe diagnostic state — pruning removes only the entries of
    // processes absent from the completed snapshot; a present (possibly reused) PID keeps its
    // own counters (a newer live process instance's diagnostics are never erased), and a
    // pruned PID starts with a fresh first-failure diagnostic.
    [Fact]
    public void InspectionDiagnostics_Prune_RemovesAbsentPids_RetainsPresentOnes()
    {
        var logger = new CapturingLogger();
        var monitor = new WindowsProcessMonitor(logger);
        var ex299 = new Win32Exception(299, "partial copy");

        monitor.LogInspectionFailure(111, 299, "gone", ex299);
        monitor.LogInspectionFailure(222, 299, "alive", ex299);
        logger.Entries.Should().HaveCount(2);

        // The completed snapshot contains only PID 222: PID 111's stale entry is pruned,
        // PID 222's live counters are retained.
        monitor.PruneInspectionDiagnostics(new HashSet<int> { 222 });

        // A repeat of the still-present PID 222 is rate-limited (its counters were retained);
        // the pruned PID 111 is diagnosed in full again (fresh first-failure).
        monitor.LogInspectionFailure(222, 299, "alive", ex299);
        logger.Entries.Should().HaveCount(2, "the present PID's counters were not erased by the prune");

        monitor.LogInspectionFailure(111, 299, "gone", ex299);
        logger.Entries.Should().HaveCount(3);
        logger.Entries[2].Exception.Should().BeSameAs(ex299,
            "the pruned PID starts with a fresh full first-failure diagnostic");
    }

    [Fact]
    public void InspectionDiagnostics_Prune_EmptyState_NoThrow()
    {
        var monitor = new WindowsProcessMonitor(NullLogger<WindowsProcessMonitor>.Instance);

        var act = () => monitor.PruneInspectionDiagnostics(new HashSet<int> { 1, 2, 3 });
        act.Should().NotThrow("pruning an empty diagnostic state is a safe no-op");
    }
}
