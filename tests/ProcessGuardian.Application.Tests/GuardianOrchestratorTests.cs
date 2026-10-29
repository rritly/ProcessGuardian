using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ProcessGuardian.Application.Services;
using ProcessGuardian.Domain;
using ProcessGuardian.Domain.Entities;
using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Domain.Interfaces;
using ProcessGuardian.Domain.ValueObjects;
using Xunit;

namespace ProcessGuardian.Application.Tests;

// Controller-driven orchestration tests (C+D). The monitoring session is started and
// stopped exclusively through IMonitoringController; the orchestrator's hosted
// lifecycle (Reconcile + idle) is exercised by the reconcile/boot tests, while the
// check-loop behaviour is driven through StartAsync(StartRequest).
public class GuardianOrchestratorTests
{
    private const int TimeoutMs = 10000;

    private sealed class Harness : IDisposable
    {
        // A real on-disk .exe is required: ProcessTarget.TryCreate (FR-16) rejects
        // paths whose file does not exist before any monitor call happens.
        public string TargetPath { get; }

        public IProcessMonitor Monitor { get; } = Substitute.For<IProcessMonitor>();
        public IConfigurationStore Config { get; } = Substitute.For<IConfigurationStore>();
        public INotificationService Notifications { get; } = Substitute.For<INotificationService>();
        public IStatusReporter Status { get; } = Substitute.For<IStatusReporter>();
        public ITargetIntegrityService Integrity { get; } = Substitute.For<ITargetIntegrityService>();
        public IAutostartService Autostart { get; } = Substitute.For<IAutostartService>();

        public ConcurrentBag<GuardianStatus> Statuses { get; } = new();
        public ConcurrentBag<string> ToastTitles { get; } = new();
        public int LoadCount { get; set; }
        public int CheckCount { get; set; }
        public int StartCallCount { get; set; }

        // Set when the first launch critical section is entered (used by launch-gate tests).
        public bool MonitorInitialLaunchCalled { get; set; }

        public Harness()
        {
            var dir = Path.Combine(Path.GetTempPath(), "ProcessGuardian.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            TargetPath = Path.Combine(dir, "target.exe");
            File.WriteAllText(TargetPath, string.Empty);

            Config.LoadAsync(Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    LoadCount++;
                    return Task.FromResult(new AppSettings());
                });
            Config.SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
            Autostart.IsEnabledAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(false));
            Autostart.SetEnabledAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
            Status.Report(Arg.Do<GuardianStatus>(s => Statuses.Add(s)));

            Notifications.ShowAsync(
                    Arg.Do<string>(t => ToastTitles.Add(t)),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            // Default: the pin always reads as the freshly computed value (K): Start
            // computes a pin and every launch re-verifies against it.
            Integrity.ComputeHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(TargetHashResult.Computed("PIN")));
        }

        public IMonitoringController CreateController() =>
            new GuardianOrchestrator(
                Monitor, Config, Notifications, NullLogger<GuardianOrchestrator>.Instance,
                Status, Integrity, Autostart);

        public StartRequest Request(
            int maxRestarts = 5,
            int checkInterval = 1,
            int restartDelay = 1,
            int startupTimeout = 1,
            int initialDelay = 0,
            bool enableToasts = true)
        {
            var s = new AppSettings
            {
                MaxRestartAttempts = maxRestarts,
                CheckIntervalSeconds = checkInterval,
                RestartDelaySeconds = restartDelay,
                StartupVerificationTimeoutSeconds = startupTimeout,
                InitialDelaySeconds = initialDelay,
                EnableToasts = enableToasts,
            };
            return new StartRequest(CommittedSettingsSnapshot.From(s), TargetPath);
        }

        public void Dispose()
        {
            try
            {
                var dir = Path.GetDirectoryName(TargetPath);
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; never throw from Dispose
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failureMessage, int timeoutMs = TimeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException($"Timed out waiting for: {failureMessage}");
    }

    private static async Task StartSessionAsync(IMonitoringController controller, Harness h)
    {
        var result = await controller.StartAsync(h.Request());
        result.Started.Should().BeTrue("the session must start in this harness");
    }

    [Fact]
    public async Task Stop_DuringInitialDelay_ExitsPromptly_DoesNotWaitFullDelay()
    {
        using var h = new Harness();
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.Running, 42)));

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(initialDelay: 999));
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Waiting),
            "Status Waiting to be reported during the initial delay");

        // User stops monitoring mid-delay: the session is cancelled promptly.
        var stop = await controller.StopAsync();
        stop.RuntimeStopped.Should().BeTrue();
        await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Stopped),
            "Status Stopped to be reported shortly after Stop");

        // The session must have exited long before the 999 s delay elapsed.
        _ = h.Monitor.DidNotReceive().CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FailedLaunch_HonorsRestartDelay_BetweenAttempts()
    {
        using var h = new Harness();
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));
        h.Monitor.StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(maxRestarts: 0, restartDelay: 2));
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Restarting),
            "first budgeted restart to begin");
        await WaitUntilAsync(() => h.Monitor.ReceivedCalls().Count(c =>
                c.GetMethodInfo().Name == nameof(IProcessMonitor.StartAsync)) >= 2,
            "free launch plus first budgeted attempt to have run");

        await Task.Delay(1200);

        // RestartDelaySeconds (2 s) has not elapsed since the failed launch, so no
        // further attempt is allowed; the old behavior re-attempted every
        // CheckInterval (1 s) and would already have made a third launch.
        h.Monitor.ReceivedCalls().Count(c =>
                c.GetMethodInfo().Name == nameof(IProcessMonitor.StartAsync))
            .Should().Be(2, "RestartDelaySeconds must be honored between failed launches");

        await controller.StopAsync();
    }

    [Fact]
    public async Task UnknownCheck_SkipsCycle_NoRestart_NoStatusFlip()
    {
        using var h = new Harness();
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromResult(new ProcessCheckResult(ProcessOutcome.Unknown));
            });

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request());
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.CheckCount >= 3,
            "the loop to keep polling after Unknown results");
        await controller.StopAsync();

        // Unknown must never look like "dead": no restart, no status flip, no disable.
        _ = h.Monitor.DidNotReceive().StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        h.Statuses.Should().NotContain(GuardianStatus.Monitoring);
        h.Statuses.Should().NotContain(GuardianStatus.Error);
        h.Statuses.Should().NotContain(GuardianStatus.Restarting);
    }

    [Fact]
    public async Task UnknownThenRunning_RecoversNormally_NoRestart()
    {
        using var h = new Harness();
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromResult(new ProcessCheckResult(
                    h.CheckCount <= 2 ? ProcessOutcome.Unknown : ProcessOutcome.Running, 42));
            });

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request());
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Monitoring),
            "Monitoring status once the state is determinable again");
        await controller.StopAsync();

        // Transient enumeration failures must not consume the restart budget or
        // trigger launches: the target was likely running all along.
        _ = h.Monitor.DidNotReceive().StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        h.Statuses.Should().Contain(GuardianStatus.Monitoring);
    }

    [Fact]
    public async Task TargetRunning_NoRestart_StatusMonitoring()
    {
        using var h = new Harness();
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.Running, 42)));

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request());
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Monitoring),
            "Status Monitoring to be reported");
        await controller.StopAsync();

        await h.Monitor.DidNotReceive().StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        h.Statuses.Should().Contain(GuardianStatus.Monitoring);
    }

    [Fact]
    public async Task TargetMissing_RestartTriggered_RecoveryToast_StatusMonitoring()
    {
        using var h = new Harness();
        bool running = true;
        int checkCount = 0;
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                checkCount++;
                if (checkCount == 1) return Task.FromResult(new ProcessCheckResult(ProcessOutcome.Running, 42));
                return Task.FromResult(new ProcessCheckResult(running ? ProcessOutcome.Running : ProcessOutcome.NotRunning));
            });
        h.Monitor.StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => { running = true; return Task.FromResult(true); });
        h.Monitor.VerifyStartupAsync(Arg.Any<ProcessTarget>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request());
        start.Started.Should().BeTrue();

        // Let the first check see the target running (monitoring established).
        await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Monitoring) && checkCount >= 1,
            "Initial Monitoring status");
        running = false;
        await WaitUntilAsync(() => h.ToastTitles.Any(t => t.Contains("Recovered")),
            "Recovered toast to be shown");
        await controller.StopAsync();

        await h.Monitor.Received(1).StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        await h.Monitor.Received(1).VerifyStartupAsync(Arg.Any<ProcessTarget>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        h.Statuses.Should().Contain(GuardianStatus.Monitoring);
        h.Statuses.Should().Contain(GuardianStatus.Restarting,
            "a budgeted restart is user-visible while it is in flight");
        h.ToastTitles.Should().Contain(t => t.Contains("Recovered"));
    }

    [Fact]
    public async Task Exhaustion_SetsError_DisablesMonitoring_NoAutoRestart()
    {
        using var h = new Harness();
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));
        h.Monitor.StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(maxRestarts: 3));
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Error),
            "Status Error to be reported");
        await WaitUntilAsync(() => h.ToastTitles.Any(t => t.Contains("Failed") || t.Contains("attempt")),
            "Failed/exhaustion toast to be shown");
        await WaitUntilAsync(() => h.Config.ReceivedCalls().Any(c =>
            c.GetMethodInfo().Name == nameof(IConfigurationStore.SaveAsync)),
            "SaveAsync to be called to persist disabled state");

        h.Statuses.Should().Contain(GuardianStatus.Error);
        h.ToastTitles.Should().Contain(t => t.Contains("Failed") || t.Contains("attempt"));
        // First Start is the free initial launch; then Max=3 budgeted attempts, then exhaustion.
        await h.Monitor.Received(4).StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NewStartAfterError_RestoresFullAttemptBudget()
    {
        using var h = new Harness();
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));
        h.Monitor.StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => { h.StartCallCount++; return Task.FromResult(false); });

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(maxRestarts: 1));
        start.Started.Should().BeTrue();

        // First session: one free initial launch + one budgeted attempt (Max=1), then Error.
        await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Error),
            "First session to exhaust");
        await WaitUntilAsync(() => h.StartCallCount == 2, "first session launches (free + budgeted)");

        // A brand-new Start restores the full budget: one free launch + attempt (Max=1),
        // then Error again. The counter must not leak from the previous session.
        h.Monitor.ClearReceivedCalls();
        h.StartCallCount = 0;
        var start2 = await controller.StartAsync(h.Request(maxRestarts: 1));
        start2.Started.Should().BeTrue();
        await WaitUntilAsync(() => h.StartCallCount == 2, "second session launches (free + budgeted)");
        await h.Monitor.Received(2).StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FirstStart_ConsumesNoAttempts_StatusMonitoring_NoExhaustion()
    {
        using var h = new Harness();
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));
        h.Monitor.StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        h.Monitor.VerifyStartupAsync(Arg.Any<ProcessTarget>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(maxRestarts: 1));
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.Monitor.ReceivedCalls().Any(c =>
                c.GetMethodInfo().Name == nameof(IProcessMonitor.StartAsync)),
            "First StartAsync to be called");
        await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Monitoring),
            "Monitoring status after initial launch");
        await controller.StopAsync();

        await h.Monitor.Received(1).StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        h.Statuses.Should().NotContain(GuardianStatus.Error);
    }

    [Fact]
    public async Task HashMismatch_LaunchBlocked_MonitoringDisabled_TargetChangedToast()
    {
        using var h = new Harness();
        // The pin captured at Start ("PIN") and the value read at first launch ("BB")
        // differ, so the launch is blocked fail-closed.
        int hashCalls = 0;
        h.Integrity.ComputeHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(TargetHashResult.Computed(hashCalls++ == 0 ? "PIN" : "BB")));
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));
        h.Monitor.StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        h.Monitor.VerifyStartupAsync(Arg.Any<ProcessTarget>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request());
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.ToastTitles.Contains("Target Changed"),
            "Target Changed toast to be shown");
        await WaitUntilAsync(() => h.Config.ReceivedCalls().Any(c =>
            c.GetMethodInfo().Name == nameof(IConfigurationStore.SaveAsync)),
            "SaveAsync to persist the disabled state");

        await h.Monitor.DidNotReceive().StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        h.Statuses.Should().Contain(GuardianStatus.Error);
        h.ToastTitles.Should().Contain("Target Changed");
    }

    [Fact]
    public async Task HashMissingAtLaunch_Degraded_NoLaunch_NoBudget_SessionSurvives()
    {
        using var h = new Harness();
        // Pin at Start is readable; at first launch the file is cleanly missing.
        int hashCalls = 0;
        h.Integrity.ComputeHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(hashCalls++ == 0 ? TargetHashResult.Computed("PIN") : TargetHashResult.Missing()));
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning));
            });

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request());
        start.Started.Should().BeTrue();

        // Missing is distinct from Mismatch: no launch, degraded, no budget, no Error and no
        // disable — the session keeps polling and the free launch stays pending.
        await WaitUntilAsync(() => h.CheckCount >= 3,
            "the loop to keep polling after a transiently-missing target");
        await controller.StopAsync();

        await h.Monitor.DidNotReceive().StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        h.Statuses.Should().NotContain(GuardianStatus.Error,
            "a clean 'no file' is not a fatal hash mismatch (K)");
        h.Statuses.Should().NotContain(GuardianStatus.Monitoring);
        // The degraded free launch stays pending: the session is alive (Waiting) and has not
        // spent budget, so the free initial launch remains available for the next cycle.
        h.Statuses.Should().Contain(GuardianStatus.Waiting,
            "the free initial launch stays pending while the target is degraded (K)");
    }

    [Fact]
    public async Task HashReadErrorAtLaunch_Degraded_NoLaunch_NoBudget_NoError()
    {
        using var h = new Harness();
        // Pin at Start is readable; at first launch the file cannot be opened (permission/IO).
        int hashCalls = 0;
        h.Integrity.ComputeHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(hashCalls++ == 0 ? TargetHashResult.Computed("PIN") : TargetHashResult.ReadError()));
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning));
            });

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request());
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.CheckCount >= 3,
            "the loop to keep polling after a read-error target");
        await controller.StopAsync();

        await h.Monitor.DidNotReceive().StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        h.Statuses.Should().NotContain(GuardianStatus.Error,
            "a permission/IO read failure is ReadError, not Mismatch (K)");
    }

    [Fact]
    public async Task HashMatches_LaunchProceeds_StatusMonitoring()
    {
        using var h = new Harness();
        // The controller computes a fresh pin at Start and re-verifies at launch; both
        // read the same value, so the re-check matches (case-insensitively).
        h.Integrity.ComputeHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(TargetHashResult.Computed("AA")));
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));
        h.Monitor.StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        h.Monitor.VerifyStartupAsync(Arg.Any<ProcessTarget>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request());
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.Monitor.ReceivedCalls().Any(c =>
                c.GetMethodInfo().Name == nameof(IProcessMonitor.StartAsync)),
            "StartAsync to be called when the hash matches");
        await controller.StopAsync();

        await h.Monitor.Received(1).StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        h.Statuses.Should().Contain(GuardianStatus.Monitoring);
    }

    [Fact]
    public async Task ControllerAlwaysComputesAndPersistsPin_OnStart()
    {
        using var h = new Harness();
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.Running, 42)));

        AppSettings? saved = null;
        h.Config.SaveAsync(Arg.Do<AppSettings>(s => saved = s), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request());
        start.Started.Should().BeTrue();

        // The controller computed the pin (K) and persisted it together with the path
        // and enabled flag BEFORE the session began; the ViewModel never supplies one.
        saved.Should().NotBeNull();
        saved!.TargetHashSha256.Should().Be("PIN");
        saved!.TargetProcessPath.Should().Be(h.TargetPath);
        saved!.IsMonitoringEnabled.Should().BeTrue();

        await controller.StopAsync();
    }

    [Fact]
    public async Task HugeCheckInterval_DoesNotBusyLoop()
    {
        using var h = new Harness();
        // Upper bound is intentionally unclamped at the entity level. Before the wait cap,
        // the int cast overflowed and CheckAsync was invoked thousands of times per second.
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromResult(new ProcessCheckResult(ProcessOutcome.Running, 42));
            });

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(checkInterval: int.MaxValue));
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Monitoring),
            "Monitoring status after the first check");
        await Task.Delay(700);
        h.CheckCount.Should().Be(1);

        await controller.StopAsync();
    }

    [Fact]
    public async Task CheckAsyncThrows_StatusError_EndsSession()
    {
        using var h = new Harness();
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromException<ProcessCheckResult>(new InvalidOperationException("check boom"));
            });

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request());
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Error),
            "Status Error to be reported after CheckAsync throws");

        h.Statuses.Should().Contain(GuardianStatus.Error);
        // A thrown check is a fatal cycle condition: the session ends, not an infinite loop.
        h.CheckCount.Should().Be(1);
    }

    [Fact]
    public async Task SecondStart_WhileSessionActive_Rejected()
    {
        using var h = new Harness();
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.Running, 42)));

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request());
        start.Started.Should().BeTrue();

        var duplicate = await controller.StartAsync(h.Request());
        duplicate.Started.Should().BeFalse("a second Start while a session is active must be rejected");
        duplicate.Reason.Should().NotBeNullOrWhiteSpace();

        await controller.StopAsync();
    }

    [Fact]
    public async Task Stop_WhilePreSessionStartInFlight_CancelsSession_NoLaunchAfter()
    {
        using var h = new Harness();
        // The first post-delay cycle is a free launch; hold its launch inside the gate so
        // we can prove a completed Stop releases no launch afterwards.
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));
        h.Monitor.StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => { h.MonitorInitialLaunchCalled = true; return Task.FromResult(true); });
        h.Monitor.VerifyStartupAsync(Arg.Any<ProcessTarget>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var controller = (GuardianOrchestrator)h.CreateController();
        var start = await controller.StartAsync(h.Request(initialDelay: 1));
        start.Started.Should().BeTrue();

        // Pause inside the launch critical section so Stop must wait for the in-flight
        // launch to settle (making RuntimeStopped honest) before it can cancel.
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.InsideLaunchGateHook = async () =>
        {
            h.MonitorInitialLaunchCalled = true;
            await release.Task;
        };

        await WaitUntilAsync(() => h.MonitorInitialLaunchCalled,
            "initial launch critical section to be entered");

        // Stop is now pending: it waits on the launch gate (an in-flight launch holds it).
        var stop = controller.StopAsync();
        await Task.Delay(300);
        h.Statuses.Should().NotContain(GuardianStatus.Stopped,
            "Stop must wait for the in-flight launch before settling");

        // Release the launch; Stop then cancels the session and settles.
        release.TrySetResult(true);
        var stopResult = await stop;
        stopResult.RuntimeStopped.Should().BeTrue();
        stopResult.SettingsSaved.Should().BeTrue();
        await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Stopped),
            "Status Stopped once the session settles");

        await Task.Delay(600);
        h.Monitor.ReceivedCalls().Count(c =>
                c.GetMethodInfo().Name == nameof(IProcessMonitor.StartAsync))
            .Should().Be(1, "no launch may occur after a completed Stop");
    }

    [Fact]
    public async Task Reconcile_WeakConfig_EmptyTarget_Error_NoAutoStart()
    {
        using var h = new Harness();
        // Persisted enabled=true but no target selected: boot must surface Error +
        // target-confirmation guidance, and must never auto-launch monitoring.
        h.Config.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AppSettings
            {
                IsMonitoringEnabled = true,
                TargetProcessPath = string.Empty,
            }));

        var controller = h.CreateController();
        await controller.ReconcileAsync();

        h.Statuses.Should().Contain(GuardianStatus.Error);
        controller.CurrentState.Status.Should().Be(GuardianStatus.Error);
        controller.CurrentState.IsDegraded.Should().BeTrue();
        controller.CurrentState.Reason.Should().Contain("target");

        _ = h.Monitor.DidNotReceive().CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        _ = h.Monitor.DidNotReceive().StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reconcile_EmptyPin_EditionScoped()
    {
        using var h = new Harness();
        // Persisted enabled=true with a valid path but legacy empty pin: the behavior is
        // edition-scoped (K). Elevated blocks auto-resume with Error + "target confirmation
        // required" (independent of EnableToasts); regular preserves the compatibility skip
        // and resumes without a pin.
        h.Config.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AppSettings
            {
                IsMonitoringEnabled = true,
                TargetProcessPath = h.TargetPath,
                TargetHashSha256 = string.Empty,
                CheckIntervalSeconds = 1,
                EnableToasts = false,
            }));
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.Running, 42)));

        var controller = h.CreateController();
        await controller.ReconcileAsync();

        if (AppEdition.IsElevated)
        {
            h.Statuses.Should().Contain(GuardianStatus.Error);
            controller.CurrentState.Status.Should().Be(GuardianStatus.Error);
            controller.CurrentState.IsDegraded.Should().BeTrue();
            controller.CurrentState.Reason.Should().Contain("pin");
            _ = h.Monitor.DidNotReceive().StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
            // No pin is ever created automatically at boot: no hash is read.
            _ = h.Integrity.DidNotReceive().ComputeHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        else
        {
            // [preserve] Regular edition: empty legacy pin keeps the compatibility skip.
            await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Monitoring),
                "regular edition auto-resumes without a pin (compatibility skip)");
            await controller.StopAsync();
            h.Statuses.Should().Contain(GuardianStatus.Monitoring);
        }
    }

    [Fact]
    public async Task Reconcile_HealthyConfig_AutoStarts_SessionBegins()
    {
        using var h = new Harness();
        h.Config.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AppSettings
            {
                IsMonitoringEnabled = true,
                TargetProcessPath = h.TargetPath,
                TargetHashSha256 = "PIN",
                CheckIntervalSeconds = 1,
            }));
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.Running, 42)));

        var controller = h.CreateController();
        await controller.ReconcileAsync();

        await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Monitoring),
            "auto-started session to reach Monitoring");
        await controller.StopAsync();
        h.Statuses.Should().Contain(GuardianStatus.Monitoring);
    }

    [Fact]
    public async Task CommitSettings_StoppedPathChange_ClearsPin_PersistsViaController()
    {
        using var h = new Harness();
        AppSettings? saved = null;
        h.Config.SaveAsync(Arg.Do<AppSettings>(s => saved = s), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        h.Config.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AppSettings { TargetProcessPath = h.TargetPath }));

        var controller = h.CreateController();
        await controller.ReconcileAsync(); // loads committed path + empty pin

        // Change the committed target while stopped -> the controller clears the pin.
        var otherDir = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(h.TargetPath)!, "sub")).FullName;
        var otherPath = Path.Combine(otherDir, "other.exe");
        File.WriteAllText(otherPath, string.Empty);

        var snapshot = CommittedSettingsSnapshot.From(new AppSettings
        {
            TargetProcessPath = otherPath,
            MaxRestartAttempts = 5,
            CheckIntervalSeconds = 1,
        });
        var committed = await controller.CommitSettingsAsync(snapshot, otherPath);
        committed.Should().BeTrue();

        saved.Should().NotBeNull();
        saved!.TargetProcessPath.Should().Be(otherPath);
        saved!.TargetHashSha256.Should().BeEmpty("a committed path change in the stopped state must clear the pin");
        saved!.IsMonitoringEnabled.Should().BeFalse();
    }
}
