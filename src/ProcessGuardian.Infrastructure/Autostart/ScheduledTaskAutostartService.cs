using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ProcessGuardian.Domain;
using ProcessGuardian.Domain.Interfaces;

namespace ProcessGuardian.Infrastructure.Autostart;

// HKCU Run keys cannot auto-elevate (UAC would prompt on every logon). The admin
// edition therefore registers a scheduled task (ONLOGON, RunLevel HIGHEST), which
// starts elevated silently. Created via schtasks.exe: no COM/WMI dependency.
public sealed class ScheduledTaskAutostartService : IAutostartService
{
    private readonly ILogger<ScheduledTaskAutostartService> _logger;
    private readonly SchTasksInvoker? _invoker;

    public ScheduledTaskAutostartService(ILogger<ScheduledTaskAutostartService> logger)
        : this(logger, null)
    {
    }

    // Internal seam so CI can script schtasks.exe results (exit code, output, exceptions)
    // without touching the real scheduled-task store; production leaves it null and runs
    // the real process.
    internal ScheduledTaskAutostartService(ILogger<ScheduledTaskAutostartService> logger, SchTasksInvoker? invoker)
    {
        _logger = logger;
        _invoker = invoker;
    }

    internal delegate Task<(int ExitCode, string Output)> SchTasksInvoker(string arguments, CancellationToken ct);

    public async Task<bool> IsEnabledAsync(CancellationToken ct = default)
    {
        var (exitCode, _) = await RunSchTasksAsync(BuildQueryArguments(), ct);
        return exitCode == 0;
    }

    public async Task<AutostartResult> SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        // Idempotent fast path: when the actual state already matches the requested one,
        // the operation has already succeeded; a redundant /Create or /Delete is not run
        // (a second /Delete of an absent task would itself report a nonzero exit code).
        var current = await IsEnabledAsync(ct).ConfigureAwait(false);
        if (current == enabled)
            return AutostartResult.Success(current);

        if (enabled)
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            // An empty path would create a task pointing nowhere.
            if (string.IsNullOrWhiteSpace(exePath))
            {
                _logger.LogError("Cannot enable autostart: current executable path is unavailable");
                return AutostartResult.Failure("the current executable path is unavailable for the autostart task", current);
            }

            var (code, output) = await RunSchTasksAsync(BuildCreateArguments(exePath), ct);
            if (code != 0)
            {
                // A nonzero exit code is an operation error; the diagnostic must not be lost.
                _logger.LogError("Failed to create autostart task (exit {Code}): {Output}", code, output);
                return AutostartResult.Failure($"task creation failed (exit {code}): {output}");
            }
        }
        else
        {
            var (deleteCode, deleteOutput) = await RunSchTasksAsync(BuildDeleteArguments(), ct);
            if (deleteCode != 0)
            {
                _logger.LogError("Failed to delete autostart task (exit {Code}): {Output}", deleteCode, deleteOutput);
                return AutostartResult.Failure($"task deletion failed (exit {deleteCode}): {deleteOutput}");
            }
        }

        // Verify the actual registration state rather than trusting the exit code alone: a
        // command that exits 0 without the state taking effect is still a failure.
        var actual = await IsEnabledAsync(ct).ConfigureAwait(false);
        if (actual == enabled)
            return AutostartResult.Success(actual);

        _logger.LogError("Autostart state ({Actual}) does not match the requested state ({Requested}) after the operation",
            actual, enabled);
        return AutostartResult.Failure(
            enabled
                ? "the autostart task was not actually registered after the create exited successfully"
                : "the autostart task is still registered after the delete exited successfully",
            actual);
    }

    internal static string BuildCreateArguments(string exePath) =>
        $"/Create /TN \"{AppEdition.ScheduledTaskName}\" /TR \"\\\"{exePath}\\\" --background\" /SC ONLOGON /RL HIGHEST /F";

    internal static string BuildDeleteArguments() =>
        $"/Delete /TN \"{AppEdition.ScheduledTaskName}\" /F";

    internal static string BuildQueryArguments() =>
        $"/Query /TN \"{AppEdition.ScheduledTaskName}\"";

    // Defeat a bare-name resolution that a hostile PATH entry could hijack: schtasks.exe
    // is always invoked through its absolute %SystemRoot%\System32 path.
    internal static string ResolveSchTasksPath() =>
        Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    // Internal seam so tests can assert the launched FileName is the absolute System32
    // path, not a bare "schtasks.exe" resolved through the process PATH.
    internal static ProcessStartInfo BuildProcessStartInfo(string arguments) =>
        new(ResolveSchTasksPath(), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

    // Runs schtasks.exe through the injected seam when present, otherwise the real process.
    // Both paths share the same exception handling so a seam-thrown error behaves exactly like
    // a real process-start failure (surfaced as a failure result, never lost).
    private async Task<(int ExitCode, string Output)> RunSchTasksAsync(string arguments, CancellationToken ct)
    {
        try
        {
            if (_invoker is not null)
                return await _invoker(arguments, ct).ConfigureAwait(false);

            var psi = BuildProcessStartInfo(arguments);
            using var process = Process.Start(psi);
            if (process is null)
            {
                _logger.LogError("schtasks.exe could not be started");
                return (-1, string.Empty);
            }

            // Read both streams concurrently: a sequential read can deadlock if
            // schtasks fills the stderr pipe buffer while stdout is still open.
            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return (process.ExitCode, $"{outputTask.Result} {errorTask.Result}".Trim());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A process-start/invocation failure is surfaced as a failure result, never lost:
            // the caller must be able to distinguish "operation failed" from "operation ok".
            _logger.LogError(ex, "schtasks.exe invocation failed");
            return (-1, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
