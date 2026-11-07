using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ProcessGuardian.Domain.Interfaces;
using ProcessGuardian.Domain.ValueObjects;

namespace ProcessGuardian.Infrastructure.ProcessMonitoring;

public sealed class WindowsProcessMonitor : IProcessMonitor
{
    private readonly ILogger<WindowsProcessMonitor> _logger;

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
            foreach (var p in processes)
            {
                using (p)
                {
                    try
                    {
                        string? path = p.MainModule?.FileName;
                        if (path is not null)
                        {
                            candidates.Add(new ProcessCandidate(p.Id, path, Unreadable: false));
                        }
                        else if (!p.HasExited)
                        {
                            // A readable process object with no module path is not proof of
                            // absence (v2.8 §4.3): while the process may still be alive the
                            // state is indeterminate, so it must not count as a readable
                            // non-match (which would look like reliable NotRunning).
                            candidates.Add(new ProcessCandidate(p.Id, null, Unreadable: true));
                        }
                        // A positively-exited process contributes nothing: it is reliably absent.
                    }
                    catch (Win32Exception ex)
                    {
                        _logger.LogWarning(ex, "Access denied inspecting process {Pid}", p.Id);
                        candidates.Add(new ProcessCandidate(p.Id, null, Unreadable: true));
                    }
                    catch (InvalidOperationException)
                    {
                        candidates.Add(new ProcessCandidate(p.Id, null, Unreadable: true));
                    }
                }
            }

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
