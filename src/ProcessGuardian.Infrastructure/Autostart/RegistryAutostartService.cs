using System.Diagnostics;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ProcessGuardian.Domain;
using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Domain.Interfaces;

namespace ProcessGuardian.Infrastructure.Autostart;

/// <summary>
/// HKCU Run-based autostart for the regular (non-elevated) edition. Semantically the
/// user-scoped counterpart of the scheduled-task service used by the admin edition. It
/// reports a verified <see cref="AutostartResult"/> and a typed <see cref="AutostartState"/>
/// read so a failed write, a state that did not take effect, or a read/access failure can
/// never be lost or mistaken for a verified "off" registration.
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

    public Task<AutostartState> ReadStateAsync(CancellationToken ct = default)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_registryPath, false);
            var value = key?.GetValue(KeyName) as string;
            if (string.IsNullOrWhiteSpace(value))
                return Task.FromResult(AutostartState.Absent);
            return Task.FromResult(MatchesExpected(value) ? AutostartState.Enabled : AutostartState.Misconfigured);
        }
        catch (Exception ex)
        {
            // A read/access failure is NEVER a reliable "off" state: the caller could otherwise
            // claim a verified-off registration or run a destructive disable on bad data.
            _logger.LogError(ex, "Failed to read autostart registry state");
            return Task.FromResult(AutostartState.Unknown);
        }
    }

    public async Task<AutostartResult> SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        var state = await ReadStateAsync(ct).ConfigureAwait(false);
        if (state == AutostartState.Unknown)
        {
            _logger.LogWarning("Cannot set autostart={Enabled}: the current state could not be read reliably", enabled);
            return AutostartResult.Failure(
                "the current autostart state could not be read reliably; no change was made",
                AutostartState.Unknown);
        }

        // Idempotent fast path over the typed state.
        if (enabled && state == AutostartState.Enabled)
            return AutostartResult.Success(AutostartState.Enabled);
        if (!enabled && state == AutostartState.Absent)
            return AutostartResult.Success(AutostartState.Absent);

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_registryPath, true)
                            ?? Registry.CurrentUser.CreateSubKey(_registryPath);

            if (enabled)
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                // An empty path would persist a useless Run entry ("\"\" --background")
                // that cannot launch the application on the next logon.
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    _logger.LogError("Cannot enable autostart: current executable path is unavailable");
                    return AutostartResult.Failure("the current executable path is unavailable for autostart", state);
                }
                key.SetValue(KeyName, $"\"{exePath}\" --background");
            }
            else
            {
                key.DeleteValue(KeyName, false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to modify autostart registry");
            return AutostartResult.Failure($"an error occurred while modifying the autostart registry: {ex.Message}", state);
        }

        // Verify the actual registry value rather than trusting the write call alone.
        var actual = await ReadStateAsync(ct).ConfigureAwait(false);
        var expected = enabled ? AutostartState.Enabled : AutostartState.Absent;
        if (actual == expected)
            return AutostartResult.Success(actual);
        if (actual == AutostartState.Unknown)
            return AutostartResult.Failure(
                "the operation completed but the resulting autostart state could not be verified",
                AutostartState.Unknown);

        return AutostartResult.Failure(
            enabled
                ? "the autostart registry value was not set correctly after the write"
                : "the autostart registry value is still present after the delete",
            actual);
    }

    // The expected Run value is "\"<currentExe>\" --background". A value that does not point at
    // the running executable (a moved/old install, or a foreign entry) is Misconfigured.
    internal static bool MatchesExpected(string value)
    {
        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        var trimmed = value.Trim();
        if (!trimmed.StartsWith('"') || !trimmed.EndsWith("--background", StringComparison.OrdinalIgnoreCase))
            return false;

        var endQuote = trimmed.IndexOf('"', 1);
        if (endQuote <= 1)
            return false;

        var quotedExe = trimmed.Substring(1, endQuote - 1);
        return exePath is not null
            && string.Equals(exePath, quotedExe, StringComparison.OrdinalIgnoreCase);
    }
}
