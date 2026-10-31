using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using ProcessGuardian.Domain;
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
    public async Task SetEnabledTrue_ReportsVerifiedSucceeded()
    {
        var result = await _service.SetEnabledAsync(true);

        result.Succeeded.Should().BeTrue();
        result.ActualState.Should().BeTrue();
        (await _service.IsEnabledAsync()).Should().BeTrue();
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
    public async Task SetEnabledFalse_IsEnabledReturnsFalse()
    {
        await _service.SetEnabledAsync(true);
        var result = await _service.SetEnabledAsync(false);

        result.Succeeded.Should().BeTrue();
        result.ActualState.Should().BeFalse();
        (await _service.IsEnabledAsync()).Should().BeFalse();
        using var key = Registry.CurrentUser.OpenSubKey(_scratchPath, false);
        var value = key?.GetValue(KeyName);
        value.Should().BeNull();
    }

    [Fact]
    public async Task SetEnabledFalseWhenNotSet_IsIdempotentAndSucceeds()
    {
        var result = await _service.SetEnabledAsync(false);

        result.Succeeded.Should().BeTrue();
        result.ActualState.Should().BeFalse();
        Func<Task> act = () => _service.SetEnabledAsync(false);
        await act.Should().NotThrowAsync();
    }
}
