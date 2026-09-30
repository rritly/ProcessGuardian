using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ProcessGuardian.Domain.Interfaces;
using System.Security;

namespace ProcessGuardian.Infrastructure.Autostart;

public sealed class RegistryAutostartService : IAutostartService
{
    private const string KeyName = "ProcessGuardian";
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly ILogger<RegistryAutostartService> _logger;

    public RegistryAutostartService(ILogger<RegistryAutostartService> logger)
    {
        _logger = logger;
    }

    public Task<bool> IsEnabledAsync(CancellationToken ct = default)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, false);
            var value = key?.GetValue(KeyName) as string;
            return Task.FromResult(!string.IsNullOrEmpty(value));
        }
        catch (SecurityException ex)
        {
            _logger.LogWarning(ex, "Security exception reading autostart registry");
            return Task.FromResult(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error reading autostart registry");
            return Task.FromResult(false);
        }
    }

    public Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, true)
                            ?? Registry.CurrentUser.CreateSubKey(RegistryPath);

            if (enabled)
            {
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                // An empty path would persist a useless Run entry ("\"\" --background")
                // that cannot launch the application on the next logon.
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    _logger.LogError("Cannot enable autostart: current executable path is unavailable");
                    return Task.CompletedTask;
                }
                key.SetValue(KeyName, $"\"{exePath}\" --background");
            }
            else
            {
                key.DeleteValue(KeyName, false);
            }
            return Task.CompletedTask;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied modifying autostart registry");
            return Task.CompletedTask;
        }
        catch (SecurityException ex)
        {
            _logger.LogError(ex, "Security exception modifying autostart registry");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error modifying autostart registry");
            return Task.CompletedTask;
        }
    }
}
