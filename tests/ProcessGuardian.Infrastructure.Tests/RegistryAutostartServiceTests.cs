using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using ProcessGuardian.Domain;
using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Infrastructure.Autostart;
using Xunit;

namespace ProcessGuardian.Infrastructure.Tests;

// Tests run against a scratch HKCU key (never the real Run entry) and delete it
// afterwards, so no machine state is touched and no restore bookkeeping is needed.
public class RegistryAutostartServiceTests : IDisposable
{
    private static readonly string KeyName = AppEdition.AutostartValueName;

    private readonly RegistryAutostartService _service;
    private readonly string _scratchPath;

    public RegistryAutostartServiceTests()
    {
        _scratchPath = @"Software\ProcessGuardian.Tests\Run-" + Guid.NewGuid().ToString("N");
        _service = new RegistryAutostartService(NullLogger<RegistryAutostartService>.Instance, _scratchPath);
    }

    public void Dispose()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(_scratchPath, throwOnMissingSubKey: false);
        }
        catch
        {
            // Best-effort cleanup; never throw from Dispose
        }
    }

    [Fact]
    public async Task SetEnabledTrue_ReportsVerifiedSucceeded_AndReadsEnabled()
    {
        var result = await _service.SetEnabledAsync(true);

        result.Succeeded.Should().BeTrue();
        result.ActualState.Should().Be(AutostartState.Enabled);
        (await _service.ReadStateAsync()).Should().Be(AutostartState.Enabled);
    }

    [Fact]
    public async Task SetEnabledTrue_WritesQuotedExePathWithBackgroundFlag()
    {
        await _service.SetEnabledAsync(true);

        using var key = Registry.CurrentUser.OpenSubKey(_scratchPath, false);
        var value = key?.GetValue(KeyName) as string;
        value.Should().NotBeNullOrEmpty();
        value!.Should().Contain("--background");
        value.Should().Contain("\"");
        value.Should().StartWith("\"");
        value.Should().EndWith("--background");
    }

    [Fact]
    public async Task SetEnabledFalse_RemovesValue_AndReadsAbsent()
    {
        await _service.SetEnabledAsync(true);
        var result = await _service.SetEnabledAsync(false);

        result.Succeeded.Should().BeTrue();
        result.ActualState.Should().Be(AutostartState.Absent);
        (await _service.ReadStateAsync()).Should().Be(AutostartState.Absent);
        using var key = Registry.CurrentUser.OpenSubKey(_scratchPath, false);
        var value = key?.GetValue(KeyName);
        value.Should().BeNull();
    }

    [Fact]
    public async Task SetEnabledFalseWhenNotSet_IsIdempotentAndSucceeds()
    {
        var result = await _service.SetEnabledAsync(false);

        result.Succeeded.Should().BeTrue();
        result.ActualState.Should().Be(AutostartState.Absent);
        Func<Task> act = () => _service.SetEnabledAsync(false);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ReadState_WhenNoValue_IsAbsent_NotUnknown()
    {
        (await _service.ReadStateAsync()).Should().Be(AutostartState.Absent);
    }

    [Fact]
    public async Task ReadState_WhenValueIsNotTheExpectedBackgroundEntry_IsMisconfigured()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_scratchPath, true)
                        ?? Registry.CurrentUser.CreateSubKey(_scratchPath);
        key.SetValue(KeyName, "\"C:\\Other\\app.exe\" --background");

        (await _service.ReadStateAsync()).Should().Be(AutostartState.Misconfigured);
    }
}
