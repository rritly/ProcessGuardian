using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ProcessGuardian.Domain.Entities;
using ProcessGuardian.Domain.Interfaces;
using ProcessGuardian.Infrastructure.Configuration;
using ProcessGuardian.Infrastructure.Logging;
using ProcessGuardian.Infrastructure.Status;
using ProcessGuardian.Wpf.ViewModels;
using Xunit;

namespace ProcessGuardian.Wpf.Tests;

// Boot-path regression for the A refusal/reset indicators: a refused store must surface an
// ordinary Error indicator and gate Start off (no monitoring launch), while a pending reset
// notice is surfaced without disabling Start (the user re-selects/re-pins the target).
public class MainViewModelInitIndicatorTests
{
    private const int TimeoutMs = 10000;

    private sealed class Harness
    {
        public IConfigurationStore Config { get; } = Substitute.For<IConfigurationStore>();
        public IAutostartService Autostart { get; } = Substitute.For<IAutostartService>();
        public IStatusReporter Status { get; } = new StatusReporter();
        public INotificationService Notifications { get; } = Substitute.For<INotificationService>();
        public ITargetIntegrityService Integrity { get; } = Substitute.For<ITargetIntegrityService>();

        public Harness()
        {
            Config.LoadAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AppSettings()));
            Autostart.IsEnabledAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(false));
        }

        public MainViewModel CreateVm() =>
            new(Config, Autostart, Status, Notifications, NullLogger<MainViewModel>.Instance,
                new InMemoryLogSink(Path.Combine(Path.GetTempPath(), "ProcessGuardian.Tests",
                    "init-" + Guid.NewGuid().ToString("N") + ".log")),
                Integrity);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failureMessage)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < TimeoutMs)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }
        throw new Xunit.Sdk.XunitException($"Timed out waiting for: {failureMessage}");
    }

    [Fact]
    public void Refused_SetsErrorIndicator_GatesStartOff()
    {
        var vm = new Harness().CreateVm();

        vm.ApplyInitializationStatus(ConfigurationInitializationStatus.Refused);

        vm.ConfigurationError.Should().NotBeEmpty();
        vm.ResetNotice.Should().BeEmpty();
        vm.StartCommand.CanExecute(null).Should().BeFalse("monitoring must not start while config is refused");
        vm.StopCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void ResetNoticePending_SetsResetNotice_StartRemainsAvailable()
    {
        var vm = new Harness().CreateVm();

        vm.ApplyInitializationStatus(ConfigurationInitializationStatus.ResetNoticePending);

        vm.ResetNotice.Should().NotBeEmpty();
        vm.ConfigurationError.Should().BeEmpty();
        vm.StartCommand.CanExecute(null).Should().BeTrue("after a reset the user must be able to re-select/re-pin the target");
    }

    [Fact]
    public async Task LoadAsync_ThrowsConfigurationUnavailable_SetsErrorIndicatorAndGatesStartOff()
    {
        var h = new Harness();
        h.Config.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<AppSettings>(new ConfigurationUnavailableException(
                "refused",
                "An administrator must repair HKLM\\SOFTWARE\\ProcessGuardianAdmin\\Recovery")));

        var vm = h.CreateVm();
        await WaitUntilAsync(() => vm.ConfigurationError.Length > 0, "the refused-load error indicator to be set");

        vm.ConfigurationError.Should().NotBeEmpty();
        vm.StartCommand.CanExecute(null).Should().BeFalse();
    }
}
