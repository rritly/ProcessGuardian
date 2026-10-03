using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProcessGuardian.Domain;
using ProcessGuardian.Domain.Entities;
using ProcessGuardian.Domain.Interfaces;

namespace ProcessGuardian.Infrastructure.Configuration;

public sealed class JsonConfigurationStore : IConfigurationStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<JsonConfigurationStore> _logger;
    private AppSettings? _current;

    public JsonConfigurationStore(ILogger<JsonConfigurationStore> logger)
    {
        _logger = logger;
        var dir = AppEdition.IsElevated
            ? GetAdminConfigDirectory()
            : GetUserConfigDirectory();
        try
        {
            Directory.CreateDirectory(dir);
            if (AppEdition.IsElevated)
                HardenForAdministratorsOnly(dir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create config directory {Dir}", dir);
            throw;
        }
        _path = Path.Combine(dir, "settings.json");
    }

    private static string GetUserConfigDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppEdition.DataDirName);

    // Admin edition: the settings file selects which binary the elevated guardian
    // launches, so it must not be writable by user-level processes. ProgramData
    // with inheritance stripped and only Administrators/SYSTEM granted closes
    // that escalation path.
    private static string GetAdminConfigDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppEdition.DataDirName);

    internal static void HardenForAdministratorsOnly(string directory)
    {
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(true, false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        // The deserialized instance is cached and shared: UI and orchestrator
        // both see live updates through the same AppSettings object.
        await _lock.WaitAsync(ct);
        try
        {
            if (_current != null)
                return _current;

            if (!File.Exists(_path))
            {
                _logger.LogInformation("Config file not found, returning defaults");
                _current = new AppSettings();
                return _current;
            }

            try
            {
                var json = await File.ReadAllTextAsync(_path, ct);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, _options);
                if (settings == null)
                {
                    _logger.LogWarning("Config file deserialized to null, returning defaults");
                    _current = new AppSettings();
                    return _current;
                }
                _current = settings;
                return _current;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Config file contains invalid JSON, returning defaults");
                _current = new AppSettings();
                return _current;
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "IO error reading config file, returning defaults");
                _current = new AppSettings();
                return _current;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "Access denied reading config file, returning defaults");
                _current = new AppSettings();
                return _current;
            }
        }
        finally { _lock.Release(); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        var tmpPath = _path + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(settings, _options);
            // Honor cancellation up to the point of writing; once writing starts,
            // the save must complete atomically to avoid a truncated file.
            await File.WriteAllTextAsync(tmpPath, json, ct);
            File.Move(tmpPath, _path, overwrite: true);
            // Publish _current only after the file is replaced: on a failed write,
            // LoadAsync must keep returning the last successfully persisted settings.
            _current = settings;
        }
        catch (OperationCanceledException)
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            throw;
        }
        catch (IOException ex)
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            _logger.LogError(ex, "IO error writing config file");
        }
        catch (UnauthorizedAccessException ex)
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            _logger.LogError(ex, "Access denied writing config file");
        }
        finally { _lock.Release(); }
    }
}
