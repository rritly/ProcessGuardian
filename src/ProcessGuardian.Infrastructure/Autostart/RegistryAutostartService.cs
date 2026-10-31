using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ProcessGuardian.Domain;
using ProcessGuardian.Domain.Interfaces;
using System.Security;

namespace ProcessGuardian.Infrastructure.Autostart;

/// <summary>
/// HKCU Run-based autostart for the regular (non-elevated) edition. Semantically the
/// user-scoped counterpart of the scheduled-task service used by the admin edition. It
/// reports a verified <see cref="AutostartResult"/> so a failed write or a state that did
/// not take effect is never lost.
/// </summary>
public sealed class RegistryAutostartService : IAutostartService
{
    private static readonly string KeyName = AppEdition.AutostartValueName;
    private const string DefaultRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _registryPath;
    private readonly ILogger<RegistryAutostartService> _logger;

    public RegistryAutostartService(ILogger<RegistryAutostartService> logger)
        : this(logger, DefaultRegistryPath)
    {
    }

    // Test seam: run against a scratch key instead of the real HKCU Run entry.
    internal RegistryAutostartService(ILogger<RegistryAutostartService> logger, string registryPath)
    {
        _logger = logger;
        _registryPath = registryPath;
    }

    public Task<bool> IsEnabledAsync(CancellationToken ct = default)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_registryPath, false);
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

    public async Task<AutostartResult> SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        var current = await IsEnabledAsync(ct).ConfigureAwait(false);
        if (current == enabled)
            return AutostartResult.Success(current); // idempotent: already in the desired state

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_registryPath, true)
                            ?? Registry.CurrentUser.CreateSubKey(_registryPath);

            if (enabled)
            {
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                // An empty path would persist a useless Run entry ("\"\" --background")
                // that cannot launch the application on the next logon.
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    _logger.LogError("Cannot enable autostart: current executable path is unavailable");
                    return AutostartResult.Failure("the current executable path is unavailable for autostart", current);
                }
                key.SetValue(KeyName, $"\"{exePath}\" --background");
            }
            else
            {
                key.DeleteValue(KeyName, false);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied modifying autostart registry");
            return AutostartResult.Failure("access was denied while modifying the autostart registry", current);
        }
        catch (SecurityException ex)
        {
            _logger.LogError(ex, "Security exception modifying autostart registry");
            return AutostartResult.Failure("a security exception occurred while modifying the autostart registry", current);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error modifying autostart registry");
            return AutostartResult.Failure($"an unexpected error occurred while modifying the autostart registry: {ex.Message}", current);
        }

        // Verify the actual registry value rather than trusting the write call alone.
        var actual = await IsEnabledAsync(ct).ConfigureAwait(false);
        return actual == enabled
            ? AutostartResult.Success(actual)
            : AutostartResult.Failure(
                enabled
                    ? "the autostart registry value was not set after the write"
                    : "the autostart registry value is still present after the delete",
                actual);
    }
}
