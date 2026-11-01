using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ProcessGuardian.Application.Services;
using ProcessGuardian.Domain.Entities;
using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Domain.Exceptions;
using ProcessGuardian.Domain.Interfaces;
using ProcessGuardian.Infrastructure.Configuration;
using ProcessGuardian.Infrastructure.Logging;
using ProcessGuardian.Wpf.ViewModels;
using Xunit;

namespace ProcessGuardian.Wpf.Tests;

// Boot-path regression for the A refusal/reset indicators: a refused store must surface an
// ordinary Error indicator and gate Start off (no monitoring launch), while a pending reset
// notice is surfaced without disabling Start (the user re-selects/re-pins the target).
public class MainViewModelInitIndicatorTests
{
    private sealed class Harness
    {
        public IMonitoringController Controller { get; } = Substitute.For<IMonitoringController>();
        public INotificationService Notifications { get; } = Substitute.For<INotificationService>();

        public Harness()
        {
            Controller.GetCurrentSettingsAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AppSettings()));
            Controller.CurrentState.Returns(new MonitorState(GuardianStatus.Stopped, false, null, false));
        }

        public MainViewModel CreateVm() =>
            new(Controller, Notifications,
                NullLogger<MainViewModel>.Instance,
                new InMemoryLogSink(Path.Combine(Path.GetTempPath(), "ProcessGuardian.Tests",
                    "init-" + Guid.NewGuid().ToString("N") + ".log")));
    }

    [Fact]
    public void Refused_SetsErrorIndicator_ProjectsErrorState_GatesStartOff()
    {
        var vm = new Harness().CreateVm();

        vm.ApplyInitializationStatus(ConfigurationInitializationStatus.Refused);

        vm.ConfigurationError.Should().NotBeEmpty();
        vm.ResetNotice.Should().BeEmpty();
        vm.Status.Should().Be(GuardianStatus.Error,
            "a config refusal must produce one coherent ordinary Error state in the window (R-A)");
        vm.IsDegraded.Should().BeFalse("IsDegraded is reserved for active session states, never a refusal");
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

    // Monitoring Card layout: the guidance row must be fully collapsed (HasStatusGuidance ==
    // false) when no message and no recovery indication exists, and visible whenever any of the
    // three source messages is present.
    [Fact]
    public void FreshStart_NoGuidance_HasStatusGuidanceFalse_ContainerCollapses()
    {
        var vm = new Harness().CreateVm();

        vm.ConfigurationError.Should().BeEmpty();
        vm.ResetNotice.Should().BeEmpty();
        vm.AutostartError.Should().BeEmpty();
        vm.HasStatusGuidance.Should().BeFalse("an empty guidance state must collapse the row so no vertical space is reserved");
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void HasStatusGuidance_IsTrueWhenAnyMessagePresent(bool config, bool reset, bool autostart)
    {
        var vm = new Harness().CreateVm();
        if (config)
            vm.ConfigurationError = "config refused";
        if (reset)
            vm.ResetNotice = "reset pending";
        if (autostart)
            vm.AutostartError = "autostart failed";

        vm.HasStatusGuidance.Should().BeTrue("any guidance message must keep the guidance row visible");
    }

    [Fact]
    public async Task GetCurrentSettings_ThrowsConfigurationUnavailable_SetsErrorIndicatorAndGatesStartOff()
    {
        var h = new Harness();
        h.Controller.GetCurrentSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<AppSettings>(new ConfigurationUnavailableException(
                "refused",
                "An administrator must repair HKLM\\SOFTWARE\\ProcessGuardianAdmin\\Recovery")));

        var vm = h.CreateVm();
        await WaitUntilAsync(() => vm.ConfigurationError.Length > 0, "the refused-load error indicator to be set");

        vm.ConfigurationError.Should().NotBeEmpty();
        vm.Status.Should().Be(GuardianStatus.Error, "the refused load must project Error, not plain STOPPED (R-A)");
        vm.IsDegraded.Should().BeFalse();
        vm.StartCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task RefusedLoad_FallbackAssignment_SchedulesNoDebouncedCommit_NoSecondPersistenceError()
    {
        var h = new Harness();
        h.Controller.GetCurrentSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<AppSettings>(new ConfigurationUnavailableException(
                "refused",
                "An administrator must repair HKLM\\SOFTWARE\\ProcessGuardianAdmin\\Recovery")));

        var vm = h.CreateVm();
        await WaitUntilAsync(() => vm.ConfigurationError.Length > 0, "the refused-load error indicator to be set");

        // Give the (buggy) 500 ms debounce window room to fire: the fallback AppSettings assignment
        // must not schedule a commit against a refused store (R-A), so no CommitSettingsAsync may run.
        await Task.Delay(700);

        await h.Controller.Received(0)
            .CommitSettingsAsync(Arg.Any<CommittedSettingsSnapshot>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        vm.Settings.Should().NotBeNull("a fallback instance remains installed for bindings");
    }

    // R-F: the view shows the degraded state's reason independently of EnableToasts. StatusReason
    // is projected straight from the controller state channel (both the initial CurrentState sync
    // and every live StateChanged publication).
    [Fact]
    public void StatusReason_ProjectedFromControllerCurrentState_IndependentOfToasts()
    {
        var h = new Harness();
        h.Controller.CurrentState.Returns(new MonitorState(
            GuardianStatus.Monitoring, true,
            "the target identity or reachability cannot be verified", false));

        var vm = h.CreateVm();

        vm.Status.Should().Be(GuardianStatus.Monitoring);
        vm.IsDegraded.Should().BeTrue();
        vm.StatusReason.Should().Be("the target identity or reachability cannot be verified",
            "the degraded reason must flow to the view bindings regardless of toast settings (R-F)");
    }

    [Fact]
    public void StatusReason_UpdatedOnLiveStateChange()
    {
        var h = new Harness();
        var vm = h.CreateVm();
        vm.StatusReason.Should().BeEmpty("an initial Stopped state carries no reason");

        // A live publication of a degraded active state must update StatusReason immediately.
        h.Controller.StateChanged += Raise.Event<EventHandler<MonitorState>>(
            h.Controller,
            new MonitorState(
                GuardianStatus.Monitoring, true,
                "the target identity or reachability cannot be verified", false));

        vm.IsDegraded.Should().BeTrue();
        vm.StatusReason.Should().Be("the target identity or reachability cannot be verified");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failureMessage)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 10000)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }
        throw new Xunit.Sdk.XunitException($"Timed out waiting for: {failureMessage}");
    }
}
