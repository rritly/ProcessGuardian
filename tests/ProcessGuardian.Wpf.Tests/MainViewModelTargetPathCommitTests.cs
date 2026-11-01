using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ProcessGuardian.Application.Services;
using ProcessGuardian.Domain.Entities;
using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Domain.Interfaces;
using ProcessGuardian.Infrastructure.Logging;
using ProcessGuardian.Wpf.ViewModels;
using Xunit;

namespace ProcessGuardian.Wpf.Tests;

// Target-path commit discipline (R-CD): the TextBox is a draft and only an explicit commit
// (Browse / focus-lost / commit command) moves it into the controller-owned committed path.
// Typing alone never mutates the committed path, never schedules a save of an incomplete
// path, and never clears the pin; an active session rejects path editing in the UI.
public class MainViewModelTargetPathCommitTests
{
    private sealed class Harness
    {
        public IMonitoringController Controller { get; }
        public INotificationService Notifications { get; } = Substitute.For<INotificationService>();

        public string LoadedPath { get; }
        public List<string> CommittedPathArgs { get; } = new();

        public Harness(string loadedPath = @"C:\original\app.exe")
        {
            LoadedPath = loadedPath;
            Controller = Substitute.For<IMonitoringController>();
            Controller.GetCurrentSettingsAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AppSettings { TargetProcessPath = loadedPath }));
            Controller.CurrentState.Returns(new MonitorState(GuardianStatus.Stopped, false, null, false));
            Controller.CommitSettingsAsync(Arg.Any<CommittedSettingsSnapshot>(), Arg.Do<string>(p => CommittedPathArgs.Add(p)))
                .Returns(CommitSettingsResult.Persisted(autostart: null));
        }

        public MainViewModel CreateVm() =>
            new(Controller, Notifications, NullLogger<MainViewModel>.Instance,
                new InMemoryLogSink(Path.Combine(Path.GetTempPath(), "ProcessGuardian.Tests",
                    "pathcommit-" + Guid.NewGuid().ToString("N") + ".log")));
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

    [Fact]
    public async Task TypingDraft_NeverCommitted_NeverChangesCommittedPath()
    {
        var h = new Harness(loadedPath: @"C:\original\app.exe");
        var vm = h.CreateVm();
        await WaitUntilAsync(() => vm.Settings.TargetProcessPath == h.LoadedPath, "settings to load");

        // Typing a partial, incomplete path is only a draft: it must not be sent to the
        // controller, and the committed-path argument stays the fully-committed original.
        vm.Settings.TargetProcessPath = @"C:\partial";
        await Task.Delay(900); // well past the 500 ms debounce

        h.CommittedPathArgs.Should().NotBeEmpty("a debounced settings save still runs");
        h.CommittedPathArgs.Should().OnlyContain(p => p == h.LoadedPath,
            "the draft must never reach the controller as the committed path (R-CD)");
        h.CommittedPathArgs.Should().NotContain(@"C:\partial");
    }

    [Fact]
    public async Task ExplicitCommitDraft_PersistsCompletedPath()
    {
        var h = new Harness(loadedPath: @"C:\original\app.exe");
        var vm = h.CreateVm();
        await WaitUntilAsync(() => vm.Settings.TargetProcessPath == h.LoadedPath, "settings to load");

        // A completed draft, explicitly committed (focus-lost / commit command), becomes the
        // committed path and is persisted.
        vm.Settings.TargetProcessPath = @"C:\new\app.exe";
        vm.CommitTargetPathDraft();
        await Task.Delay(300);

        h.CommittedPathArgs.Last().Should().Be(@"C:\new\app.exe",
            "the explicit commit is the sole thing that sends the final path (R-CD)");
    }

    [Fact]
    public void PathEditing_ActiveSession_NotEditable()
    {
        var h = new Harness();
        var vm = h.CreateVm();

        vm.IsTargetPathEditable.Should().BeTrue("stopped: editing is allowed");
        foreach (var s in new[]
                 {
                     GuardianStatus.Waiting,
                     GuardianStatus.Monitoring,
                     GuardianStatus.Restarting,
                 })
        {
            vm.Status = s;
            vm.IsTargetPathEditable.Should().BeFalse(
                $"an active session ({s}) must reject target-path editing (R-CD)");
        }

        vm.Status = GuardianStatus.Stopped;
        vm.IsTargetPathEditable.Should().BeTrue();
        vm.Status = GuardianStatus.Error;
        vm.IsTargetPathEditable.Should().BeTrue("a terminal Error still allows re-selecting the target");
    }
}
