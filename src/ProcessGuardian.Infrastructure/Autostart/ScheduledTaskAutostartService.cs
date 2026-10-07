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

    public ScheduledTaskAutostartService(ILogger<ScheduledTaskAutostartService> logger)
    {
        _logger = logger;
    }

    public async Task<bool> IsEnabledAsync(CancellationToken ct = default)
    {
        var (exitCode, _) = await RunSchTasksAsync(BuildQueryArguments(), ct);
        return exitCode == 0;
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        if (enabled)
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            // An empty path would create a task pointing nowhere.
            if (string.IsNullOrWhiteSpace(exePath))
            {
                _logger.LogError("Cannot enable autostart: current executable path is unavailable");
                return;
            }

            var (code, output) = await RunSchTasksAsync(BuildCreateArguments(exePath), ct);
            if (code != 0)
                _logger.LogError("Failed to create autostart task (exit {Code}): {Output}", code, output);
            return;
        }

        var (deleteCode, deleteOutput) = await RunSchTasksAsync(BuildDeleteArguments(), ct);
        if (deleteCode != 0)
            _logger.LogWarning("Failed to delete autostart task (exit {Code}): {Output}", deleteCode, deleteOutput);
    }

    internal static string BuildCreateArguments(string exePath) =>
        $"/Create /TN \"{AppEdition.ScheduledTaskName}\" /TR \"\\\"{exePath}\\\" --background\" /SC ONLOGON /RL HIGHEST /F";

    internal static string BuildDeleteArguments() =>
        $"/Delete /TN \"{AppEdition.ScheduledTaskName}\" /F";

    internal static string BuildQueryArguments() =>
        $"/Query /TN \"{AppEdition.ScheduledTaskName}\"";

    private async Task<(int ExitCode, string Output)> RunSchTasksAsync(string arguments, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
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
            _logger.LogError(ex, "schtasks.exe invocation failed");
            return (-1, string.Empty);
        }
    }
}
