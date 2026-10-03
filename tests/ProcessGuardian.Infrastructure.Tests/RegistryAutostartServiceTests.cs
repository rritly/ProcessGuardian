using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using ProcessGuardian.Domain;
using ProcessGuardian.Infrastructure.Autostart;
using Xunit;

namespace ProcessGuardian.Infrastructure.Tests;

public class RegistryAutostartServiceTests : IDisposable
{
    private static readonly string KeyName = AppEdition.AutostartValueName;
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly RegistryAutostartService _service;
    private readonly object? _originalValue;

    public RegistryAutostartServiceTests()
    {
        _service = new RegistryAutostartService(NullLogger<RegistryAutostartService>.Instance);
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, false);
        _originalValue = key?.GetValue(KeyName);
    }

    public void Dispose()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, true);
            if (key == null) return;
            if (_originalValue is string s)
                key.SetValue(KeyName, s);
            else
                key.DeleteValue(KeyName, false);
        }
        catch
        {
            // Best-effort restore; never throw from Dispose
        }
    }

    [Fact]
    public async Task SetEnabledTrue_IsEnabledReturnsTrue()
    {
        await _service.SetEnabledAsync(true);

        (await _service.IsEnabledAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task SetEnabledTrue_WritesQuotedExePathWithBackgroundFlag()
    {
        await _service.SetEnabledAsync(true);

        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, false);
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
        await _service.SetEnabledAsync(false);

        (await _service.IsEnabledAsync()).Should().BeFalse();
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, false);
        key?.GetValue(KeyName).Should().BeNull();
    }

    [Fact]
    public async Task SetEnabledFalseWhenNotSet_DoesNotThrow()
    {
        await _service.SetEnabledAsync(false);

        Func<Task> act = () => _service.SetEnabledAsync(false);
        await act.Should().NotThrowAsync();
    }
}
