using System.IO;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProcessGuardian.Domain.Entities;
using ProcessGuardian.Domain.Exceptions;
using ProcessGuardian.Domain.Interfaces;
using ProcessGuardian.Infrastructure.Configuration;
using ProcessGuardian.Infrastructure.Recovery;
using ProcessGuardian.Infrastructure.Security;
using Xunit;

namespace ProcessGuardian.Infrastructure.Tests;

// Fail-closed protocol tests for the elevated quarantine-and-recreate path. The parent
// container bootstrap (real Win32 handle-pinning) is faked as a no-op — its live ACL/owner
// behavior is the manual elevated checklist — so the store's quarantine/journal/recreate
// decision logic is fully exercised in CI without elevation.
public class ElevatedConfigFailClosedTests : IDisposable
{
    private readonly string _root;
    private readonly string _configDir;
    private readonly string _settingsPath;
    private readonly InMemoryRecoveryJournal _journal = new();
    private readonly FakeProtectedFileSystem _fs = new();

    public ElevatedConfigFailClosedTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ProcessGuardian.Tests", "elev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _configDir = Path.Combine(_root, "config");
        _settingsPath = Path.Combine(_configDir, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; never throw from Dispose.
        }
    }

    private JsonConfigurationStore NewStore() =>
        new(NullLogger<JsonConfigurationStore>.Instance, _configDir, _journal, _fs, elevated: true);

    private string[] QuarantineSiblings() =>
        Directory.GetDirectories(_root, "quarantine-*")
            .Concat(Directory.GetFiles(_root, "quarantine-*"))
            .ToArray();

    private static AppSettings ReadRealSettings(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    [Fact]
    public async Task CleanFirstRun_CreatesProtectedDefaults_ResetNoticePending_MonitoringOffEmptyPin()
    {
        var status = await NewStore().InitializeAsync();

        status.Should().Be(ConfigurationInitializationStatus.ResetNoticePending);
        _journal.State.Should().Be(RecoveryState.ResetNoticePending);
        Directory.Exists(_configDir).Should().BeTrue();
        File.Exists(_settingsPath).Should().BeTrue();

        var defaults = ReadRealSettings(_settingsPath);
        defaults.IsMonitoringEnabled.Should().BeFalse();
        defaults.TargetProcessPath.Should().BeEmpty();
        defaults.TargetHashSha256.Should().BeEmpty();
        defaults.AutostartEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task TrustedConfig_JournalNone_LoadsAsIs_NoResetNoQuarantine_PreservesSettings()
    {
        // This build's protected config: dir + file created through the protected seam.
        _fs.DefaultForExisting = ProtectionClassification.Trusted;
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(
            _settingsPath,
            JsonSerializer.Serialize(new AppSettings
            {
                TargetProcessPath = @"C:\App\target.exe",
                IsMonitoringEnabled = true,
                TargetHashSha256 = "ABCDEF",
                AutostartEnabled = true,
            }));

        var store = NewStore();
        var status = await store.InitializeAsync();

        status.Should().Be(ConfigurationInitializationStatus.Success);
        QuarantineSiblings().Should().BeEmpty("a trusted config must not be quarantined");

        var loaded = await store.LoadAsync();
        loaded.TargetProcessPath.Should().Be(@"C:\App\target.exe");
        loaded.IsMonitoringEnabled.Should().BeTrue();
        loaded.TargetHashSha256.Should().Be("ABCDEF");
        loaded.AutostartEnabled.Should().BeTrue();

        // Second consecutive start on the same protected config: still no quarantine/reset.
        var second = await NewStore().InitializeAsync();
        second.Should().Be(ConfigurationInitializationStatus.Success);
        QuarantineSiblings().Should().BeEmpty();
    }

    [Fact]
    public async Task UntrustedConfig_QuarantinedByRename_OldPinNeverReused_FreshDefaults()
    {
        // Pre-existing config that fails the protection check (foreign/weak owner).
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(
            _settingsPath,
            JsonSerializer.Serialize(new AppSettings
            {
                TargetProcessPath = @"C:\Evil\pinned.exe",
                TargetHashSha256 = "OLDPIN",
                IsMonitoringEnabled = true,
            }));

        var store = NewStore();
        var status = await store.InitializeAsync();
        var quarantine = QuarantineSiblings();

        status.Should().Be(ConfigurationInitializationStatus.ResetNoticePending);
        _journal.State.Should().Be(RecoveryState.ResetNoticePending);
        quarantine.Should().ContainSingle("the untrusted config directory must be renamed, not deleted or loaded");
        Directory.Exists(_configDir).Should().BeTrue("the protected directory must have been recreated");

        var oldContent = ReadRealSettings(Path.Combine(quarantine[0], "settings.json"));
        oldContent.TargetHashSha256.Should().Be("OLDPIN");

        var fresh = await store.LoadAsync();
        fresh.TargetHashSha256.Should().BeEmpty("the old pin must never be read back");
        fresh.TargetProcessPath.Should().BeEmpty();
        fresh.IsMonitoringEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task UnsafeReparseLocation_Refused_NothingTouched()
    {
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(_settingsPath, "{}");
        _fs.SetClassification(_configDir, ProtectionClassification.UnsafeLocation);

        var status = await NewStore().InitializeAsync();

        status.Should().Be(ConfigurationInitializationStatus.Refused);
        _journal.State.Should().Be(RecoveryState.None, "no recovery may begin on a refused unsafe location");
        Directory.Exists(_configDir).Should().BeTrue("the unsafe object must not be touched");
        QuarantineSiblings().Should().BeEmpty();
    }

    [Fact]
    public async Task RefusedStore_LoadAndSave_ThrowConfigurationUnavailable()
    {
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(_settingsPath, "{}");
        _fs.SetClassification(_configDir, ProtectionClassification.UnsafeLocation);

        var store = NewStore();
        (await store.InitializeAsync()).Should().Be(ConfigurationInitializationStatus.Refused);

        Func<Task> load = () => store.LoadAsync();
        await load.Should().ThrowAsync<ConfigurationUnavailableException>();
        Func<Task> save = () => store.SaveAsync(new AppSettings());
        await save.Should().ThrowAsync<ConfigurationUnavailableException>();
    }

    [Fact]
    public async Task CreateCollision_BoundedRetries_ThenRefused_ConfigNotUsed_JournalResumable()
    {
        // The directory repeatedly appears between quarantine and create (attacker owns the
        // gap): bounded retries must not silently apply an ACL to the collider's object.
        _fs.MakeDirectoryCollide(_configDir);

        var status = await NewStore().InitializeAsync();

        status.Should().Be(ConfigurationInitializationStatus.Refused);
        _journal.State.Should().Be(RecoveryState.RecoveryInProgress,
            "the recovery marker must persist for the next launch to resume safekly");
        Directory.Exists(_configDir).Should().BeFalse("the collider's/config object must never be used");
    }

    [Fact]
    public async Task JournalWrite_RecoveryInProgress_Crashes_NothingTouched()
    {
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(_settingsPath, "{}");
        _journal.ShouldCrash = s => s == RecoveryState.RecoveryInProgress;

        var status = await NewStore().InitializeAsync();

        status.Should().Be(ConfigurationInitializationStatus.Refused);
        _journal.State.Should().Be(RecoveryState.None);
        Directory.Exists(_configDir).Should().BeTrue("if the journal cannot be written, no config object may be touched");
        QuarantineSiblings().Should().BeEmpty();
    }

    [Fact]
    public async Task JournalTransition_ResetNoticePending_Crashes_Refused_ResumesOnNextStart()
    {
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(_settingsPath, "{}");
        _journal.ShouldCrash = s => s == RecoveryState.ResetNoticePending;

        var store = NewStore();
        var status = await store.InitializeAsync();

        status.Should().Be(ConfigurationInitializationStatus.Refused,
            "a failed notice transition must not report a successful initialization");
        _journal.State.Should().Be(RecoveryState.RecoveryInProgress, "recovery must be resumable");
        Directory.Exists(_configDir).Should().BeTrue();
        File.Exists(_settingsPath).Should().BeTrue();

        // Next launch sees RecoveryInProgress and resumes: quarantine whatever is there
        // (never reads it) and converges to protected fresh defaults + the pending notice.
        _journal.ShouldCrash = null;
        var resumed = await NewStore().InitializeAsync();
        resumed.Should().Be(ConfigurationInitializationStatus.ResetNoticePending);
        _journal.State.Should().Be(RecoveryState.ResetNoticePending);
        var fresh = ReadRealSettings(_settingsPath);
        fresh.IsMonitoringEnabled.Should().BeFalse();
        fresh.TargetProcessPath.Should().BeEmpty();
    }

    [Fact]
    public async Task LegacyParentSettingsFile_Quarantined_OnCleanFirstRun_NeverReadOrMigrated()
    {
        var legacy = Path.Combine(_root, "settings.json");
        File.WriteAllText(legacy, JsonSerializer.Serialize(new AppSettings
        {
            TargetProcessPath = @"C:\Legacy\old.exe",
            TargetHashSha256 = "LEGACYPIN",
            IsMonitoringEnabled = true,
        }));

        var status = await NewStore().InitializeAsync();

        status.Should().Be(ConfigurationInitializationStatus.ResetNoticePending);
        File.Exists(legacy).Should().BeFalse("the legacy parent-level file must be moved out of the way");
        Directory.Exists(_configDir).Should().BeTrue();
        ReadRealSettings(_settingsPath).TargetProcessPath.Should().BeEmpty("legacy content must never be migrated");
    }

    [Fact]
    public async Task LegacyParentSettingsFile_Quarantined_EvenWhenConfigTrusted()
    {
        _fs.DefaultForExisting = ProtectionClassification.Trusted;
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new AppSettings
        {
            TargetProcessPath = @"C:\App\target.exe",
            IsMonitoringEnabled = true,
        }));
        var legacy = Path.Combine(_root, "settings.json");
        File.WriteAllText(legacy, "{\"TargetProcessPath\":\"C:\\Legacy\\old.exe\"}");

        var status = await NewStore().InitializeAsync();

        status.Should().Be(ConfigurationInitializationStatus.Success);
        _journal.State.Should().Be(RecoveryState.None, "legacy cleanup that needed no recreate completes the journal");
        File.Exists(legacy).Should().BeFalse();
        QuarantineSiblings().Should().ContainSingle();
        ReadRealSettings(_settingsPath).TargetProcessPath.Should().Be(@"C:\App\target.exe", "the trusted config is untouched");
    }
}
