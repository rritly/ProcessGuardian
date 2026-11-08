using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ProcessGuardian.Domain.Interfaces;
using ProcessGuardian.Domain.ValueObjects;

namespace ProcessGuardian.Infrastructure.ProcessMonitoring;

public sealed class WindowsProcessMonitor : IProcessMonitor
{
    // Win32 error codes of the per-process inspection probe (§5.1): every code must keep its
    // own meaning — an access denial is not a partial copy, and neither of them is proof of
    // process absence.
    private const int ErrorAccessDenied = 5;
    private const int ErrorPartialCopy = 299;

    // The operation whose Win32 failure the inspection diagnostics report.
    private const string InspectionOperation = "read the main module path";

    // Rate-limited inspection diagnostics (§5.4): after the first full diagnostic of a
    // (PID, Win32 code) pair, a compact structured warning is logged only every Nth repeat.
    private const int InspectionDiagnosticRepeatInterval = 10;

    private readonly ILogger<WindowsProcessMonitor> _logger;

    // Rate-limit state of the inspection diagnostics: per PID, per Win32 error code, how many
    // times the failure has recurred since its first detection. Cleared (with a recovery log
    // entry) when the inspection of that PID succeeds again.
    private readonly object _inspectionDiagnosticsGate = new();
    private readonly Dictionary<long, Dictionary<int, int>> _inspectionFailureCounts = new();

    public WindowsProcessMonitor(ILogger<WindowsProcessMonitor> logger)
    {
        _logger = logger;
    }

    public Task<ProcessCheckResult> CheckAsync(ProcessTarget target, CancellationToken ct)
    {
        try
        {
            var processes = Process.GetProcessesByName(target.NameWithoutExtension);
            var candidates = new List<ProcessCandidate>(processes.Length);
            var presentPids = new HashSet<int>();
            foreach (var p in processes)
            {
                using (p)
                {
                    presentPids.Add(p.Id);
                    // A positively-exited process contributes nothing (reliably absent); every
                    // other inspected process yields a candidate snapshot (readable or
                    // indeterminate).
                    if (InspectCandidate(p) is { } candidate)
                        candidates.Add(candidate);
                }
            }

            // Bounded diagnostic state (§4.2): drop the stale diagnostic entries of processes
            // that are no longer present in this completed snapshot.
            PruneInspectionDiagnostics(presentPids);

            return Task.FromResult(EvaluateCandidates(target.FullPath, candidates));
        }
        catch (Exception ex)
        {
            // Enumeration failed: the state is unknown. Reporting NotRunning here
            // would trigger a restart of a possibly still-running target.
            _logger.LogError(ex, "CheckAsync failed unexpectedly");
            return Task.FromResult(new ProcessCheckResult(ProcessOutcome.Unknown));
        }
    }

    // One per-process inspection (v2.8 §4.3 / §5): a readable module path makes the process a
    // readable candidate; every unreadable probe stays an indeterminate candidate, except a
    // positively established exit of the inspected process itself. ERROR_PARTIAL_COPY (299)
    // never proves absence by itself: the exit of exactly the inspected process is verified
    // through its own Process object (bound to the original handle, so a reused PID cannot
    // confound the check) before the process may be excluded (§5.2). PID reuse therefore can
    // never turn a live (or unverifiable) process into a NotRunning outcome. An
    // InvalidOperationException from the module probe may mean the process exited DURING the
    // inspection: the exit is verified through the same original Process object (never a
    // reopened PID) — a positively confirmed exit excludes the process (and drops its stale
    // diagnostic state), an alive or unverifiable one stays conservatively indeterminate
    // (R-AS-INSPECTION-CLEANUP §4.1).
    private ProcessCandidate? InspectCandidate(Process p)
    {
        string? path = null;
        int? win32Error = null;
        try
        {
            path = p.MainModule?.FileName;
        }
        catch (Win32Exception ex)
        {
            win32Error = ex.NativeErrorCode;
            LogInspectionFailure(p.Id, ex.NativeErrorCode, TryGetProcessName(p), ex);
        }
        catch (InvalidOperationException)
        {
            // The process object became invalid — it may have exited during the inspection.
            // The exit is verified through the ORIGINAL process object/handle (never a PID
            // reopened with GetProcessById, which could refer to a different process after
            // PID reuse): a positively confirmed exit excludes the process; an alive or
            // unverifiable one stays conservatively indeterminate — never proof of absence.
            var candidate = ClassifyInvalidInspection(p.Id, TryGetHasExited(p));
            if (candidate is null)
                ClearInspectionDiagnosticsOnConfirmedExit(p.Id);
            return candidate;
        }

        if (path is not null)
        {
            LogInspectionRecovered(p.Id);
            return new ProcessCandidate(p.Id, path, Unreadable: false);
        }

        // The exit is probed only when the module path is unavailable and the failure either
        // requires exit verification (partial copy, §5.2) or was a clean read with no path
        // (the existing positive-exit discipline). A non-299 Win32 failure (e.g. access
        // denied) stays indeterminate without an exit check, as before.
        bool? exited = win32Error is null || win32Error == ErrorPartialCopy
            ? TryGetHasExited(p)
            : null;
        var classified = ClassifyInspection(p.Id, path, win32Error, exited);
        if (classified is null)
        {
            // Positively confirmed exit of the inspected process (299+exited or clean-read-
            // exited): the (PID, code) diagnostic counters recorded above are now stale —
            // drop them WITHOUT a "recovered" entry (the process exited; inspection did not
            // recover, §4.2) so a later process reusing the PID starts with fresh
            // first-failure diagnostics.
            ClearInspectionDiagnosticsOnConfirmedExit(p.Id);
        }

        return classified;
    }

    // Pure classification of an InvalidOperationException module-probe failure (testable
    // seam, §4.1): a positively confirmed exit of the inspected process excludes it from the
    // candidate list; an alive or unverifiable state stays conservatively indeterminate —
    // never proof of absence.
    internal static ProcessCandidate? ClassifyInvalidInspection(int pid, bool? hasExited) =>
        hasExited == true ? null : new ProcessCandidate(pid, null, Unreadable: true);

    // Pure classification of one per-process inspection (testable seam): maps the probe's raw
    // results to a candidate snapshot without live OS access-control scenarios.
    // modulePath: the read module path (null when the read failed).
    // win32ErrorCode: the failing probe's Win32 error code (null when the read succeeded or
    // failed with a non-Win32 failure).
    // hasExited: the exit check result (null when it could not be determined).
    // Returns null when the process is positively exited and contributes nothing to the
    // candidate list; a single excluded PID never proves the absence of every process with
    // the same path — the remaining candidates are evaluated as usual.
    internal static ProcessCandidate? ClassifyInspection(
        int pid, string? modulePath, int? win32ErrorCode, bool? hasExited)
    {
        if (modulePath is not null)
            return new ProcessCandidate(pid, modulePath, Unreadable: false);

        if (win32ErrorCode == ErrorPartialCopy)
        {
            // ERROR_PARTIAL_COPY (299) never proves absence by itself (§5.2): exclusion
            // requires the inspected process's own positively established exit; a still-alive
            // process or an unverifiable state stays conservatively indeterminate (Unknown),
            // never NotRunning.
            return hasExited == true
                ? null
                : new ProcessCandidate(pid, null, Unreadable: true);
        }

        if (win32ErrorCode is not null)
        {
            // Access denied (5) or any other Win32 code: the process state is indeterminate.
            return new ProcessCandidate(pid, null, Unreadable: true);
        }

        // No module path and no Win32 failure: a positively established exit is reliably
        // absent; a still-running (or unverifiable) process stays indeterminate.
        return hasExited == true
            ? null
            : new ProcessCandidate(pid, null, Unreadable: true);
    }

    // Safe exit check: the result is bound to this Process object's original handle (PID
    // reuse cannot confound it), and any failure to determine the state yields null — the
    // caller must then stay conservative (Unknown), never infer absence (§5.2.5).
    private static bool? TryGetHasExited(Process p)
    {
        try
        {
            return p.HasExited;
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? TryGetProcessName(Process p)
    {
        try
        {
            return p.ProcessName;
        }
        catch
        {
            // The name is optional diagnostic context; its unavailability must never turn an
            // inspection failure into a check failure.
            return null;
        }
    }

    // Rate-limited inspection diagnostics (§5.4): the FIRST failure of a (PID, Win32 code)
    // pair is logged in full — with the exception payload, including its stack trace, plus
    // the PID, the real native error code, the operation type and the process name when
    // known — so the diagnostic information stays available for investigation. Further
    // repeats of the same persistent condition are bounded structured warnings WITHOUT the
    // exception payload (at most one per repeat interval), so a permanent access restriction
    // or bitness mismatch cannot flood the log with an identical stack trace on every
    // inspection cycle. A later successful inspection of the PID logs a recovery entry and
    // restores full diagnostics.
    internal void LogInspectionFailure(int pid, int errorCode, string? processName, Win32Exception ex)
    {
        var repeatCount = 0;
        lock (_inspectionDiagnosticsGate)
        {
            if (!_inspectionFailureCounts.TryGetValue(pid, out var codes))
                codes = _inspectionFailureCounts[pid] = new Dictionary<int, int>();
            codes.TryGetValue(errorCode, out repeatCount);
            codes[errorCode] = repeatCount + 1;
        }

        if (repeatCount == 0)
        {
            // First detection: the full diagnostic (the exception carries the stack trace).
            _logger.LogWarning(ex,
                "Inspecting process {Pid}{Name} failed with Win32 {Code} during {Operation}; the process state requires verification",
                pid, processName is null ? string.Empty : $" ({processName})", errorCode, InspectionOperation);
            return;
        }

        if (repeatCount % InspectionDiagnosticRepeatInterval == 0)
        {
            // Rate-limited structured warning without the exception payload (no repeated
            // stack trace per cycle); the cumulative count keeps the condition observable.
            _logger.LogWarning(
                "Inspecting process {Pid} still fails with Win32 {Code} during {Operation} (repeat {Count} since first detection); first-failure details were logged earlier",
                pid, errorCode, InspectionOperation, repeatCount + 1);
        }
    }

    // Recovery of a previously failing inspection (§5.4): a separate diagnostic entry and a
    // reset of the rate-limit state, so a later failure is diagnosed in full again.
    internal void LogInspectionRecovered(int pid)
    {
        bool hadFailures;
        lock (_inspectionDiagnosticsGate)
            hadFailures = _inspectionFailureCounts.Remove(pid);
        if (hadFailures)
            _logger.LogInformation("Process {Pid} inspection recovered; the earlier Win32 failure no longer occurs", pid);
    }

    // Positively confirmed exit of the inspected process (§4.2): its (PID, code) diagnostic
    // counters are stale — the process exited, inspection did not recover — so they are
    // removed WITHOUT the "recovered" Information entry. A later process reusing the PID
    // therefore starts with a fresh first-failure diagnostic. The removal is keyed by PID
    // only, which is safe under the existing concurrency model: CheckAsync inspections of
    // one monitor instance never overlap (a single serialized session loop), so a cleanup
    // triggered by the ORIGINAL process's own confirmed exit can never erase the counters
    // of a newer live instance that reused the PID during an in-progress inspection.
    internal void ClearInspectionDiagnosticsOnConfirmedExit(int pid)
    {
        lock (_inspectionDiagnosticsGate)
            _inspectionFailureCounts.Remove(pid);
    }

    // Bounded diagnostic state (§4.2): after a completed process snapshot, drop every
    // diagnostic entry whose PID is no longer present in the snapshot, so the counters of
    // processes that are gone cannot be retained indefinitely. Thread-safe (the shared
    // diagnostics gate); a concurrent inspection of a just-pruned PID is benign — it would
    // only be re-diagnosed in full.
    internal void PruneInspectionDiagnostics(HashSet<int> presentPids)
    {
        lock (_inspectionDiagnosticsGate)
        {
            if (_inspectionFailureCounts.Count == 0)
                return;
            List<long>? stale = null;
            foreach (var pid in _inspectionFailureCounts.Keys)
            {
                if (!presentPids.Contains((int)pid))
                    (stale ??= new List<long>()).Add(pid);
            }
            if (stale is not null)
            {
                foreach (var pid in stale)
                    _inspectionFailureCounts.Remove(pid);
            }
        }
    }

    // Unknown discipline (E), deterministic unit-test seam: Running only on a readable module
    // match; Unknown only when no readable match exists and at least one candidate is unreadable;
    // NotRunning only when every candidate was readable and none matched. A single unreadable
    // module never downgrades a readable match. Production CheckAsync never submits a readable
    // candidate with a null path (it is classified unreadable unless exit is positively
    // established, v2.8 §4.3); the seam keeps accepting such candidates as plain non-matches
    // because a test that deliberately supplies them asserts the pre-classification semantics.
    internal static ProcessCheckResult EvaluateCandidates(string targetFullPath, IReadOnlyList<ProcessCandidate> candidates)
    {
        bool anyUnreadable = false;
        foreach (var c in candidates)
        {
            if (c.Unreadable)
            {
                anyUnreadable = true;
                continue;
            }
            if (c.ModulePath is not null &&
                c.ModulePath.Equals(targetFullPath, StringComparison.OrdinalIgnoreCase))
            {
                return new ProcessCheckResult(ProcessOutcome.Running, c.ProcessId);
            }
        }
        return new ProcessCheckResult(anyUnreadable ? ProcessOutcome.Unknown : ProcessOutcome.NotRunning);
    }

    // A synthetic per-process snapshot for the EvaluateCandidates seam (tests).
    internal readonly record struct ProcessCandidate(int ProcessId, string? ModulePath, bool Unreadable);

    public ProcessStartResult TryStart(ProcessTarget target, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(target.FullPath)
            {
                WorkingDirectory = target.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // The Process handle is not needed after the start decision; dispose it
            // immediately instead of holding one handle per restart until finalization.
            using var proc = Process.Start(psi);
            return proc != null ? ProcessStartResult.Started() : ProcessStartResult.Failed("the process could not be created");
        }
        catch (OperationCanceledException)
        {
            // Cancellation is never a launch failure (R-KE).
            return ProcessStartResult.Cancelled();
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogError(ex, "Target executable not found: {Path}", target.FullPath);
            return ProcessStartResult.Missing();
        }
        catch (DirectoryNotFoundException ex)
        {
            _logger.LogError(ex, "Target directory not found: {Path}", target.FullPath);
            return ProcessStartResult.Missing();
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied reading target: {Path}", target.FullPath);
            return ProcessStartResult.ReadError();
        }
        catch (Win32Exception ex)
        {
            var status = ClassifyWin32Error(ex.NativeErrorCode);
            switch (status)
            {
                case ProcessStartStatus.Missing:
                    _logger.LogError(ex, "Target executable not found (Win32 {Code}): {Path}",
                        ex.NativeErrorCode, target.FullPath);
                    return ProcessStartResult.Missing();
                case ProcessStartStatus.ReadError:
                    _logger.LogError(ex, "Target executable could not be read (Win32 {Code}): {Path}",
                        ex.NativeErrorCode, target.FullPath);
                    return ProcessStartResult.ReadError();
                default:
                    _logger.LogError(ex, "Win32 error starting process: {Path}", target.FullPath);
                    return ProcessStartResult.Failed($"Win32 error {ex.NativeErrorCode}: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error starting process: {Path}", target.FullPath);
            return ProcessStartResult.Failed(ex.Message);
        }
    }

    // Documented native-code mapping at the launch boundary (R-KE). Only codes proven to mean
    // the target is absent or unreadable are classified as Missing/ReadError; every other
    // process-creation failure (bad format, elevation required, etc.) stays a genuine Failed.
    //   ERROR_FILE_NOT_FOUND = 2, ERROR_PATH_NOT_FOUND = 3  -> Missing
    //   ERROR_ACCESS_DENIED = 5, ERROR_SHARING_VIOLATION = 32 -> ReadError
    //   anything else (e.g. 193 bad format, 740 elevation required) -> Failed
    internal static ProcessStartStatus ClassifyWin32Error(int nativeErrorCode) =>
        nativeErrorCode is 2 or 3 ? ProcessStartStatus.Missing
        : nativeErrorCode is 5 or 32 ? ProcessStartStatus.ReadError
        : ProcessStartStatus.Failed;
}
