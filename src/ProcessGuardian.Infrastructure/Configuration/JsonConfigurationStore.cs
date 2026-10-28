using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProcessGuardian.Domain;
using ProcessGuardian.Domain.Entities;
using ProcessGuardian.Domain.Interfaces;
using ProcessGuardian.Infrastructure.Recovery;
using ProcessGuardian.Infrastructure.Security;

namespace ProcessGuardian.Infrastructure.Configuration;

/// <summary>
/// File-backed <see cref="IConfigurationStore"/>.
///
/// Regular edition: legacy user-scoped behavior (fresh defaults on missing/corrupt file,
/// no hardening, atomic tmp+move saves).
///
/// Elevated edition: the A quarantine-and-recreate protocol. A trusted config (owner
/// BUILTIN\Administrators or LOCAL SYSTEM, hardened DACL, no reparse) is loaded as-is
/// with no quarantine. An untrusted or foreign-owned config subtree is renamed into a
/// quarantine sibling (never read, parsed, hashed, or migrated) and a born-hardened
/// config tree with fresh defaults (monitoring off, empty pin) is recreated through an
/// injected <see cref="IProtectedFileSystem"/> seam. The crash-safe HKLM recovery journal
/// makes recovery resumable. Refusals (reparse/unsafe location/unreadable security,
/// non-converging recreate) throw <see cref="ConfigurationUnavailableException"/> and must
/// route to the ordinary Error path — never the fatal startup MessageBox.
/// </summary>
public sealed class JsonConfigurationStore : IConfigurationStore
{
    private const int MaxCreateRetries = 3;

    private readonly string _dir;
    private readonly string _path;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<JsonConfigurationStore> _logger;
    private readonly IElevatedRecoveryJournal? _journal;
    private readonly IProtectedFileSystem _fs;
    private readonly bool _isElevated;

    private AppSettings? _current;
    private ConfigurationInitializationStatus _initStatus = ConfigurationInitializationStatus.Refused;
    private bool _initialized;

    public JsonConfigurationStore(ILogger<JsonConfigurationStore> logger, IElevatedRecoveryJournal? journal = null)
        : this(logger, ResolveDefaultDirectory(), journal, null, AppEdition.IsElevated)
    {
    }

    // Internal seam for tests: run the store against a scratch directory instead of the
    // real user/admin config location, with an optional in-memory journal and a fabricated
    // protected-file-system fake so the elevated protocol is exercisable in CI. The
    // constructor performs no I/O; all file/ACL/recovery work runs through InitializeAsync.
    internal JsonConfigurationStore(
        ILogger<JsonConfigurationStore> logger,
        string configDirectory,
        IElevatedRecoveryJournal? recoveryJournal = null,
        IProtectedFileSystem? protectedFileSystem = null,
        bool elevated = false)
    {
        _logger = logger;
        _dir = Path.GetFullPath(configDirectory);
        _path = Path.Combine(_dir, "settings.json");
        _journal = recoveryJournal;
        _isElevated = elevated;
        _fs = protectedFileSystem ?? new Win32ProtectedFileSystem();
    }

    private static string ResolveDefaultDirectory()
    {
        if (AppEdition.IsElevated)
        {
            // Elevated layout: %ProgramData%\ProcessGuardianAdmin\config\settings.json
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                AppEdition.DataDirName);
            return Path.Combine(root, "config");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppEdition.DataDirName);
    }

    // ---------------------------------------------------------------------
    // Initialization
    // ---------------------------------------------------------------------

    public async Task<ConfigurationInitializationStatus> InitializeAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_initialized)
                return _initStatus;

            if (!_isElevated)
            {
                try
                {
                    Directory.CreateDirectory(_dir);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not create config directory {Dir}", _dir);
                }
                _initialized = true;
                _initStatus = ConfigurationInitializationStatus.Success;
                return _initStatus;
            }

            _initStatus = await InitializeElevatedAsync(ct);
            _initialized = true;
            return _initStatus;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<ConfigurationInitializationStatus> InitializeElevatedAsync(CancellationToken ct)
    {
        var parent = ParentDirectory();

        try
        {
            _fs.EnsureParentContainer(parent);
        }
        catch (ConfigurationUnavailableException ex)
        {
            _logger.LogError(ex, "Elevated parent container bootstrap refused");
            return ConfigurationInitializationStatus.Refused;
        }

        RecoveryState journalState;
        try
        {
            journalState = await _journal!.ReadAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recovery journal could not be read; refusing config initialization");
            return ConfigurationInitializationStatus.Refused;
        }

        if (journalState == RecoveryState.RecoveryInProgress)
        {
            // Never load/parse/hash/migrate current settings while a recovery is in
            // progress; resume quarantine-and-recreate instead.
            return await RunRecoveryAsync(ct, quarantineConfig: true);
        }

        return await CompleteOrRecoverAsync(ct, journalState, parent);
    }

    private async Task<ConfigurationInitializationStatus> CompleteOrRecoverAsync(
        CancellationToken ct, RecoveryState journalState, string parent)
    {
        var verdict = EvaluateConfig();
        if (verdict == Verdict.Refuse)
            return ConfigurationInitializationStatus.Refused;

        var legacyPresent = HasLegacyParentFiles(parent);

        if (verdict == Verdict.UseExisting)
        {
            // The real config tree is trusted. Legacy layout leftovers (parent-level
            // settings.json[.tmp]) must not survive; quarantine them by rename, guarded
            // by the journal so a crash mid-cleanup resumes recovery without reading them.
            if (legacyPresent)
            {
                string? legacyError = null;
                var guarded = await TryGuardJournalAsync(ct, RecoveryState.RecoveryInProgress);
                var quarantined = guarded && TryQuarantineLegacy(parent, out legacyError);
                var completed = quarantined && await TryGuardJournalAsync(ct, RecoveryState.None);
                if (!completed)
                {
                    _logger.LogError("Legacy parent settings quarantine failed: {Error}", legacyError ?? "journal state could not be persisted");
                    return ConfigurationInitializationStatus.Refused;
                }
            }

            return journalState == RecoveryState.ResetNoticePending
                ? ConfigurationInitializationStatus.ResetNoticePending
                : ConfigurationInitializationStatus.Success;
        }

        return await RunRecoveryAsync(ct, quarantineConfig: verdict == Verdict.Recreate);
    }

    private enum Verdict
    {
        UseExisting,
        Recreate,
        CleanFirstRun,
        Refuse,
    }

    private Verdict EvaluateConfig()
    {
        if (Directory.Exists(_dir))
        {
            var dirClass = _fs.Classify(_dir, isDirectory: true);
            if (dirClass == ProtectionClassification.UnsafeLocation)
            {
                _logger.LogError("Config directory {Dir} is at an unsafe location (reparse/unreadable security); refusing", _dir);
                return Verdict.Refuse;
            }
            if (dirClass == ProtectionClassification.Untrusted)
                return Verdict.Recreate;

            // Trusted directory: the settings file decides.
            if (File.Exists(_path))
            {
                var fileClass = _fs.Classify(_path, isDirectory: false);
                if (fileClass == ProtectionClassification.UnsafeLocation)
                {
                    _logger.LogError("Settings file {Path} is at an unsafe location (reparse/unreadable security); refusing", _path);
                    return Verdict.Refuse;
                }
                return fileClass == ProtectionClassification.Trusted ? Verdict.UseExisting : Verdict.Recreate;
            }

            // Trusted directory with no settings file: nothing untrusted present; fresh defaults.
            return Verdict.UseExisting;
        }

        // Missing config directory: first run (or external wipe). Legacy parent files are
        // quarantined during recovery; a reparse/object at the intended path is handled by
        // create-exclusive collision + classification during recovery.
        return Verdict.CleanFirstRun;
    }

    // ---------------------------------------------------------------------
    // Quarantine and recreate
    // ---------------------------------------------------------------------

    private async Task<ConfigurationInitializationStatus> RunRecoveryAsync(CancellationToken ct, bool quarantineConfig)
    {
        var parent = ParentDirectory();

        // Crash-safe ordering: persist the recovery marker BEFORE touching any config object.
        if (!await TryGuardJournalAsync(ct, RecoveryState.RecoveryInProgress))
            return ConfigurationInitializationStatus.Refused;

        // Rename untrusted objects out of the way from the verified protected parent — never read.
        if (quarantineConfig && !TryQuarantine(_dir, parent, out var quarantineError))
        {
            _logger.LogError("Quarantine of config directory {Dir} failed: {Error}", _dir, quarantineError);
            return ConfigurationInitializationStatus.Refused;
        }
        if (!TryQuarantineLegacy(parent, out var legacyError))
        {
            _logger.LogError("Quarantine of legacy parent settings failed: {Error}", legacyError);
            return ConfigurationInitializationStatus.Refused;
        }

        if (!TryRecreateConfigDirectory())
            return ConfigurationInitializationStatus.Refused;

        if (!TryWriteFreshDefaults())
            return ConfigurationInitializationStatus.Refused;

        // Post-create verification: dir + settings.json must be born protected.
        if (_fs.Classify(_dir, isDirectory: true) != ProtectionClassification.Trusted
            || !File.Exists(_path)
            || _fs.Classify(_path, isDirectory: false) != ProtectionClassification.Trusted)
        {
            _logger.LogError("Fresh config did not verify as protected; refusing (recovery resumable)");
            return ConfigurationInitializationStatus.Refused;
        }

        // Only now advance the journal to the user-visible notice.
        if (!await TryGuardJournalAsync(ct, RecoveryState.ResetNoticePending))
        {
            _logger.LogError("Could not persist the reset-notice transition; refusing (recovery resumable)");
            return ConfigurationInitializationStatus.Refused;
        }

        // Values come from fresh defaults only; the quarantined content is never read.
        _current = new AppSettings();
        _logger.LogWarning("Elevated config quarantined and recreated with fresh defaults");
        return ConfigurationInitializationStatus.ResetNoticePending;
    }

    private async Task<bool> TryGuardJournalAsync(CancellationToken ct, RecoveryState state)
    {
        try
        {
            await _journal!.WriteAsync(state, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not persist recovery journal state '{State}'", state);
            return false;
        }
    }

    private bool TryRecreateConfigDirectory()
    {
        var security = ElevatedSecurity.CreateProtectedDirectorySecurity();
        for (int attempt = 0; attempt < MaxCreateRetries; attempt++)
        {
            bool created;
            bool alreadyExists;
            try
            {
                created = _fs.TryCreateDirectoryExclusive(_dir, security, out alreadyExists);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Create-exclusive of config directory {Dir} failed", _dir);
                return false;
            }

            if (created)
            {
                if (_fs.Classify(_dir, isDirectory: true) == ProtectionClassification.Trusted)
                    return true;

                // Created-but-not-verified is unusable: isolate and retry.
                _logger.LogWarning("Config directory {Dir} did not verify as protected after creation; quarantining and retrying", _dir);
                TryQuarantine(_dir, ParentDirectory(), out _);
                continue;
            }

            if (alreadyExists)
            {
                // Collision: another actor created it in the gap; isolate and retry.
                _logger.LogWarning("Config directory {Dir} already exists (create collision); quarantining and retrying", _dir);
                TryQuarantine(_dir, ParentDirectory(), out _);
                continue;
            }

            return false;
        }

        _logger.LogError("Config directory {Dir} could not be recreated after {Retries} attempts; refusing", _dir, MaxCreateRetries);
        return false;
    }

    private bool TryWriteFreshDefaults()
    {
        var parent = ParentDirectory();
        var tmpPath = _path + ".tmp";

        // Never create over a stale temp from an interrupted save; quarantine it.
        if (File.Exists(tmpPath) && !TryQuarantine(tmpPath, parent, out var tmpError))
        {
            _logger.LogError("Could not quarantine stale settings temp {Tmp}: {Error}", tmpPath, tmpError);
            return false;
        }

        // A collision on the live file is a refusal, never a silent overwrite.
        if (File.Exists(_path))
        {
            _logger.LogError("Settings file {Path} already exists during recovery; refusing", _path);
            return false;
        }

        var json = JsonSerializer.Serialize(new AppSettings(), _options);
        var bytes = Encoding.UTF8.GetBytes(json);
        try
        {
            _fs.CreateFileProtected(_path, ElevatedSecurity.CreateProtectedFileSecurity(), bytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not create fresh settings file {Path}", _path);
            return false;
        }

        if (_fs.Classify(_path, isDirectory: false) != ProtectionClassification.Trusted)
        {
            _logger.LogError("Fresh settings file {Path} did not verify as protected; refusing", _path);
            return false;
        }

        return true;
    }

    private bool HasLegacyParentFiles(string parent) =>
        File.Exists(Path.Combine(parent, "settings.json")) || File.Exists(Path.Combine(parent, "settings.json.tmp"));

    private bool TryQuarantineLegacy(string parent, out string? error)
    {
        error = null;
        foreach (var legacy in new[]
        {
            Path.Combine(parent, "settings.json"),
            Path.Combine(parent, "settings.json.tmp"),
        })
        {
            if (!File.Exists(legacy))
                continue;
            if (!TryQuarantine(legacy, parent, out var e))
            {
                error = e;
                return false;
            }
        }
        return true;
    }

    private static bool TryQuarantine(string target, string parent, out string? error)
    {
        error = null;
        if (!Directory.Exists(target) && !File.Exists(target))
            return true; // nothing to isolate

        var destination = UniqueQuarantineName(parent);
        try
        {
            if (Directory.Exists(target))
                Directory.Move(target, destination);
            else
                File.Move(target, destination);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string UniqueQuarantineName(string parent)
    {
        for (int n = 0; ; n++)
        {
            var candidate = Path.Combine(parent, $"quarantine-{DateTime.UtcNow.Ticks}-{n}");
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
                return candidate;
        }
    }

    // ---------------------------------------------------------------------
    // Load / Save
    // ---------------------------------------------------------------------

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_current != null)
                return _current;

            if (_isElevated)
            {
                // A refused store is unusable: no default-object workaround in elevated mode.
                if (!_initialized || _initStatus == ConfigurationInitializationStatus.Refused)
                    throw RefuseUnavailable("the configuration store was refused; no settings object can be loaded");
            }

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
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        var tmpPath = _path + ".tmp";

        if (_isElevated)
        {
            try
            {
                if (!_initialized || _initStatus == ConfigurationInitializationStatus.Refused)
                    throw RefuseUnavailable("the configuration store was refused; settings cannot be saved");

                // A stale temp from an interrupted save is never overwritten in place;
                // quarantine it by rename first.
                if (File.Exists(tmpPath))
                {
                    if (!TryQuarantine(tmpPath, ParentDirectory(), out var staleError))
                        throw RefuseUnavailable($"a stale settings temp could not be quarantined ({staleError})");
                }

                var json = JsonSerializer.Serialize(settings, _options);
                var bytes = Encoding.UTF8.GetBytes(json);
                _fs.CreateFileProtected(tmpPath, ElevatedSecurity.CreateProtectedFileSecurity(), bytes);

                if (_fs.Classify(tmpPath, isDirectory: false) != ProtectionClassification.Trusted)
                    throw RefuseUnavailable("the settings temp file did not verify as protected; refusing the save");

                File.Move(tmpPath, _path, overwrite: true);

                if (_fs.Classify(_path, isDirectory: false) != ProtectionClassification.Trusted)
                    throw RefuseUnavailable("the replaced settings file did not verify as protected; refusing the save");

                _current = settings;
            }
            catch (ConfigurationUnavailableException)
            {
                CleanupProtectedTmp(tmpPath);
                throw;
            }
            catch (OperationCanceledException)
            {
                CleanupProtectedTmp(tmpPath);
                throw;
            }
            catch (IOException ex)
            {
                CleanupProtectedTmp(tmpPath);
                _logger.LogError(ex, "IO error writing config file");
                throw;
            }
            catch (UnauthorizedAccessException ex)
            {
                CleanupProtectedTmp(tmpPath);
                _logger.LogError(ex, "Access denied writing config file");
                throw;
            }
            finally
            {
                _lock.Release();
            }
            return;
        }

        // Regular edition: legacy atomic tmp+move, no hardening.
        try
        {
            var json = JsonSerializer.Serialize(settings, _options);
            await File.WriteAllTextAsync(tmpPath, json, ct);
            File.Move(tmpPath, _path, overwrite: true);
            _current = settings;
        }
        catch (OperationCanceledException)
        {
            CleanupProtectedTmp(tmpPath);
            throw;
        }
        catch (IOException ex)
        {
            CleanupProtectedTmp(tmpPath);
            _logger.LogError(ex, "IO error writing config file");
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            CleanupProtectedTmp(tmpPath);
            _logger.LogError(ex, "Access denied writing config file");
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static void CleanupProtectedTmp(string tmpPath)
    {
        try
        {
            if (File.Exists(tmpPath))
                File.Delete(tmpPath);
        }
        catch
        {
            // Best-effort; the stale quarantine path keeps the next save safe.
        }
    }

    private string ParentDirectory() =>
        Path.GetDirectoryName(_dir) ?? throw RefuseUnavailable("the config directory has no parent");

    private ConfigurationUnavailableException RefuseUnavailable(string detail)
    {
        _logger.LogError("Elevated configuration unavailable: {Detail}", detail);
        return new ConfigurationUnavailableException(
            $"Elevated configuration is unavailable: {detail}.",
            "An administrator must inspect and repair the elevated configuration under %ProgramData%\\ProcessGuardianAdmin " +
            "(owner must be BUILTIN\\Administrators or SYSTEM; no reparse points) or the recovery state at " +
            "HKLM\\SOFTWARE\\ProcessGuardianAdmin\\Recovery, then start the application again.");
    }

    // ---------------------------------------------------------------------
    // Static helpers retained for the admin-Acl tests
    // ---------------------------------------------------------------------

    internal static void HardenForAdministratorsOnly(string directory)
    {
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(true, false);
        AddHardenedRules(security, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        info.SetAccessControl(security);
    }

    internal static void HardenFileForAdministratorsOnly(string filePath)
    {
        var info = new FileInfo(filePath);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(true, false);
        AddHardenedRules(security, InheritanceFlags.None);
        info.SetAccessControl(security);
    }

    internal static DirectorySecurity CreateHardenedDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        AddHardenedRules(security, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        return security;
    }

    private static void AddHardenedRules(FileSystemSecurity security, InheritanceFlags inheritance)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
    }
}
