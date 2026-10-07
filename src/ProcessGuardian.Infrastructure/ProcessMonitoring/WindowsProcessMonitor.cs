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

            // MainModule can be unreadable for other users' processes; skip those.
            foreach (var p in processes)
            {
                using (p)
                {
                    try
                    {
                        var path = p.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path) &&
                            path.Equals(target.FullPath, StringComparison.OrdinalIgnoreCase))
                        {
                            return Task.FromResult(new ProcessCheckResult(ProcessOutcome.Running, p.Id));
                        }
                    }
                    catch (Win32Exception ex)
                    {
                        _logger.LogWarning(ex, "Access denied inspecting process {Pid}", p.Id);
                        continue;
                    }
                    catch (InvalidOperationException)
                    {
                        continue;
                    }
                }
            }

            return Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning));
        }
        catch (Exception ex)
        {
            // Enumeration failed: the state is unknown. Reporting NotRunning here
            // would trigger a restart of a possibly still-running target.
            _logger.LogError(ex, "CheckAsync failed unexpectedly");
            return Task.FromResult(new ProcessCheckResult(ProcessOutcome.Unknown));
        }
    }

    public Task<bool> StartAsync(ProcessTarget target, CancellationToken ct)
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
            return Task.FromResult(proc != null);
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogError(ex, "Target executable not found: {Path}", target.FullPath);
            return Task.FromResult(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied starting process: {Path}", target.FullPath);
            return Task.FromResult(false);
        }
        catch (Win32Exception ex)
        {
            _logger.LogError(ex, "Win32 error starting process: {Path}", target.FullPath);
            return Task.FromResult(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error starting process: {Path}", target.FullPath);
            return Task.FromResult(false);
        }
    }

    public async Task<bool> VerifyStartupAsync(ProcessTarget target, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                var result = await CheckAsync(target, ct);
                if (result.Outcome == ProcessOutcome.Running) return true;
                await Task.Delay(250, ct);
            }
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VerifyStartupAsync failed unexpectedly");
            return false;
        }
    }
}
