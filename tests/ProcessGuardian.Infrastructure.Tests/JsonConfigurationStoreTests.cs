using System.Diagnostics;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProcessGuardian.Domain.Entities;
using ProcessGuardian.Infrastructure.Configuration;
using Xunit;

namespace ProcessGuardian.Infrastructure.Tests;

public class JsonConfigurationStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private readonly JsonConfigurationStore _store;

    public JsonConfigurationStoreTests()
    {
        // Isolated scratch directory: tests must never touch the real user or admin
        // config location (the admin edition hardens its directory, so a non-elevated
        // test host could not even construct a store pointed at it).
        _dir = Path.Combine(Path.GetTempPath(), "ProcessGuardian.Tests", "config-" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_dir, "settings.json");
        _store = new JsonConfigurationStore(NullLogger<JsonConfigurationStore>.Instance, _dir, hardenWhenElevated: false);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; never throw from Dispose
        }
    }

    private static AppSettings CreateFullSettings() => new()
    {
        TargetProcessPath = @"C:\App\target.exe",
        CheckIntervalSeconds = 3,
        InitialDelaySeconds = 4,
        MaxRestartAttempts = 7,
        RestartDelaySeconds = 8,
        StartupVerificationTimeoutSeconds = 9,
        AutostartEnabled = true,
        IsMonitoringEnabled = true,
    };

    [Fact]
    public async Task RoundTrip_SaveThenLoad_ReturnsSameValues()
    {
        var settings = CreateFullSettings();

        await _store.SaveAsync(settings);

        var loaded = await _store.LoadAsync();
        loaded.Should().NotBeNull();
        loaded.TargetProcessPath.Should().Be(settings.TargetProcessPath);
        loaded.CheckIntervalSeconds.Should().Be(settings.CheckIntervalSeconds);
        loaded.InitialDelaySeconds.Should().Be(settings.InitialDelaySeconds);
        loaded.MaxRestartAttempts.Should().Be(settings.MaxRestartAttempts);
        loaded.RestartDelaySeconds.Should().Be(settings.RestartDelaySeconds);
        loaded.StartupVerificationTimeoutSeconds.Should().Be(settings.StartupVerificationTimeoutSeconds);
        loaded.AutostartEnabled.Should().Be(settings.AutostartEnabled);
        loaded.IsMonitoringEnabled.Should().Be(settings.IsMonitoringEnabled);
    }

    [Fact]
    public async Task MissingFile_LoadReturnsDefaults_NoThrow()
    {
        File.Delete(_path);

        var loaded = await _store.LoadAsync();

        loaded.Should().NotBeNull();
        loaded.TargetProcessPath.Should().BeEmpty();
        loaded.CheckIntervalSeconds.Should().Be(5);
        loaded.MaxRestartAttempts.Should().Be(5);
    }

    [Fact]
    public async Task CorruptedJson_LoadReturnsDefaults_NoThrow()
    {
        File.WriteAllText(_path, "{{not json");

        var loaded = await _store.LoadAsync();

        loaded.Should().NotBeNull();
        loaded.TargetProcessPath.Should().BeEmpty();
    }

    [Fact]
    public async Task Load_InvalidIntervals_AreClampedToSafeMinimums()
    {
        // settings.json is user-editable; out-of-range numeric values must not reach
        // the orchestrator (hot loop / restart spam). The entity clamps on deserialization.
        File.WriteAllText(_path,
            """
            {
              "TargetProcessPath": "C:\\App\\target.exe",
              "CheckIntervalSeconds": 0,
              "InitialDelaySeconds": -5,
              "MaxRestartAttempts": -1,
              "RestartDelaySeconds": -7,
              "StartupVerificationTimeoutSeconds": 0,
              "IsMonitoringEnabled": false
            }
            """);

        var freshStore = new JsonConfigurationStore(NullLogger<JsonConfigurationStore>.Instance, _dir, hardenWhenElevated: false);
        var loaded = await freshStore.LoadAsync();

        loaded.CheckIntervalSeconds.Should().Be(1);
        loaded.InitialDelaySeconds.Should().Be(0);
        loaded.MaxRestartAttempts.Should().Be(0);
        loaded.RestartDelaySeconds.Should().Be(0);
        loaded.StartupVerificationTimeoutSeconds.Should().Be(1);
    }

    [Fact]
    public async Task Load_ValuesAbove999_AreClampedDown()
    {
        // Upper bound mirrors the 3-char UI boxes; hand-edited settings.json must not
        // bypass it. The entity clamps on deserialization.
        File.WriteAllText(_path,
            """
            {
              "TargetProcessPath": "C:\\App\\target.exe",
              "CheckIntervalSeconds": 1000,
              "InitialDelaySeconds": 1500,
              "MaxRestartAttempts": 100000,
              "RestartDelaySeconds": 3600,
              "StartupVerificationTimeoutSeconds": 1234,
              "IsMonitoringEnabled": false
            }
            """);

        var freshStore = new JsonConfigurationStore(NullLogger<JsonConfigurationStore>.Instance, _dir, hardenWhenElevated: false);
        var loaded = await freshStore.LoadAsync();

        loaded.CheckIntervalSeconds.Should().Be(999);
        loaded.InitialDelaySeconds.Should().Be(999);
        loaded.MaxRestartAttempts.Should().Be(999);
        loaded.RestartDelaySeconds.Should().Be(999);
        loaded.StartupVerificationTimeoutSeconds.Should().Be(999);
    }

    [Fact]
    public async Task ConcurrentAccess_NoThrow_AndLoadSucceeds()
    {
        var settings = CreateFullSettings();
        var tasks = new List<Task>();
        for (int i = 0; i < 8; i++)
        {
            tasks.Add(_store.SaveAsync(settings));
            tasks.Add(_store.LoadAsync());
        }

        Func<Task> act = () => Task.WhenAll(tasks);
        await act.Should().NotThrowAsync();

        var loaded = await _store.LoadAsync();
        loaded.Should().NotBeNull();
    }

    [Fact]
    public async Task Save_IsMonitoringEnabled_PersistedToDisk_NewStoreLoadsIt()
    {
        var settings = CreateFullSettings();
        settings.IsMonitoringEnabled = true;
        settings.TargetProcessPath = @"C:\App\persisted.exe";

        await _store.SaveAsync(settings);

        var freshStore = new JsonConfigurationStore(NullLogger<JsonConfigurationStore>.Instance, _dir, hardenWhenElevated: false);
        var loaded = await freshStore.LoadAsync();

        loaded.IsMonitoringEnabled.Should().BeTrue();
        loaded.TargetProcessPath.Should().Be(@"C:\App\persisted.exe");
    }
}
