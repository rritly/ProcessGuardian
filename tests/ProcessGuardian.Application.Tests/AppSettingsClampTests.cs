using FluentAssertions;
using ProcessGuardian.Domain.Entities;
using Xunit;

namespace ProcessGuardian.Application.Tests;

// Clamp regression tests: out-of-range numeric settings previously reached the
// orchestrator unchecked and caused hot loops or restart spam.
public class AppSettingsClampTests
{
    [Fact]
    public void OutOfRangeValues_AreClampedToSafeMinimums()
    {
        var settings = new AppSettings
        {
            CheckIntervalSeconds = -5,
            InitialDelaySeconds = -3,
            MaxRestartAttempts = -2,
            RestartDelaySeconds = -7,
            StartupVerificationTimeoutSeconds = 0,
        };

        settings.CheckIntervalSeconds.Should().Be(1);
        settings.InitialDelaySeconds.Should().Be(0);
        settings.MaxRestartAttempts.Should().Be(0);
        settings.RestartDelaySeconds.Should().Be(0);
        settings.StartupVerificationTimeoutSeconds.Should().Be(1);
    }

    [Fact]
    public void ValidValues_AreNotModified()
    {
        var settings = new AppSettings
        {
            CheckIntervalSeconds = 5,
            InitialDelaySeconds = 0,
            MaxRestartAttempts = 5,
            RestartDelaySeconds = 10,
            StartupVerificationTimeoutSeconds = 5,
        };

        settings.CheckIntervalSeconds.Should().Be(5);
        settings.InitialDelaySeconds.Should().Be(0);
        settings.MaxRestartAttempts.Should().Be(5);
        settings.RestartDelaySeconds.Should().Be(10);
        settings.StartupVerificationTimeoutSeconds.Should().Be(5);
    }

    [Fact]
    public void ValuesAbove999_AreClampedDown()
    {
        var settings = new AppSettings
        {
            CheckIntervalSeconds = 1000,
            InitialDelaySeconds = 1500,
            MaxRestartAttempts = 100000,
            RestartDelaySeconds = 3600,
            StartupVerificationTimeoutSeconds = 1234,
        };

        settings.CheckIntervalSeconds.Should().Be(999);
        settings.InitialDelaySeconds.Should().Be(999);
        settings.MaxRestartAttempts.Should().Be(999);
        settings.RestartDelaySeconds.Should().Be(999);
        settings.StartupVerificationTimeoutSeconds.Should().Be(999);
    }

    [Fact]
    public void BoundaryValues_999_AreKept()
    {
        var settings = new AppSettings
        {
            CheckIntervalSeconds = 999,
            InitialDelaySeconds = 999,
            MaxRestartAttempts = 999,
            RestartDelaySeconds = 999,
            StartupVerificationTimeoutSeconds = 999,
        };

        settings.CheckIntervalSeconds.Should().Be(999);
        settings.InitialDelaySeconds.Should().Be(999);
        settings.MaxRestartAttempts.Should().Be(999);
        settings.RestartDelaySeconds.Should().Be(999);
        settings.StartupVerificationTimeoutSeconds.Should().Be(999);
    }
}
