using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.Core;
using ProcessGuardian.Application.Services;
using ProcessGuardian.Domain;
using ProcessGuardian.Domain.Entities;
using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Domain.Exceptions;
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

        // Deterministic clock for the degraded-threshold tests (E); TimeProvider.System by default.
        public TimeProvider Time { get; set; } = TimeProvider.System;

        // Set when the first launch critical section is entered (used by launch-gate tests).
        public bool MonitorInitialLaunchCalled { get; set; }

        // Incremented through the orchestrator's StabilityConfirmedHook seam every time a
        // stability window completes; budget-reset tests wait on this counter instead of
        // racy CheckCount thresholds (poll cadence is decoupled from CheckIntervalSeconds).
        private int _stableCount;
        public int StableCount => Volatile.Read(ref _stableCount);

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
            Autostart.ReadStateAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(AutostartState.Absent));
            Autostart.SetEnabledAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(AutostartResult.Success(AutostartState.Enabled)));
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

        public IMonitoringController CreateController()
        {
            var controller = new GuardianOrchestrator(
                Monitor, Config, Notifications, NullLogger<GuardianOrchestrator>.Instance,
                Status, Integrity, Autostart, Time);
            controller.StabilityConfirmedHook = () => Interlocked.Increment(ref _stableCount);
            return controller;
        }

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
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(ProcessStartResult.Failed("launch failed"));

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(maxRestarts: 0, restartDelay: 2));
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Restarting),
            "first budgeted restart to begin");
        await WaitUntilAsync(() => h.Monitor.ReceivedCalls().Count(c =>
                c.GetMethodInfo().Name == nameof(IProcessMonitor.TryStart)) >= 2,
            "free launch plus first budgeted attempt to have run");

        await Task.Delay(1200);

        // RestartDelaySeconds (2 s) has not elapsed since the failed launch, so no
        // further attempt is allowed; the old behavior re-attempted every
        // CheckInterval (1 s) and would already have made a third launch.
        h.Monitor.ReceivedCalls().Count(c =>
                c.GetMethodInfo().Name == nameof(IProcessMonitor.TryStart))
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
        _ = h.Monitor.DidNotReceive().TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
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
        _ = h.Monitor.DidNotReceive().TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
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

        h.Monitor.DidNotReceive().TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
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
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => { running = true; return ProcessStartResult.Started(); });

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

        h.Monitor.Received(1).TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
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
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(ProcessStartResult.Failed("launch failed"));

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
        h.Monitor.Received(4).TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NewStartAfterError_RestoresFullAttemptBudget()
    {
        using var h = new Harness();
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => { h.StartCallCount++; return ProcessStartResult.Failed("launch failed"); });

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
        h.Monitor.Received(2).TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FirstStart_ConsumesNoAttempts_StatusMonitoring_NoExhaustion()
    {
        using var h = new Harness();
        // The free initial launch appears within the startup timeout and survives the full
        // stability window (v2.8): it consumes no attempt and never trips exhaustion.
        bool running = false;
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromResult(new ProcessCheckResult(
                    running ? ProcessOutcome.Running : ProcessOutcome.NotRunning, 42));
            });
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => { running = true; return ProcessStartResult.Started(); });

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(maxRestarts: 1));
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.Monitor.ReceivedCalls().Any(c =>
                c.GetMethodInfo().Name == nameof(IProcessMonitor.TryStart)),
            "First StartAsync to be called");
        await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Monitoring),
            "Monitoring status after initial launch");
        await controller.StopAsync();

        h.Monitor.Received(1).TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        h.Statuses.Should().NotContain(GuardianStatus.Error);
    }

    [Fact]
    public async Task Launch_MissingAtStartBoundary_IsDegraded_SessionSurvives_NoBudgetSpent()
    {
        using var h = new Harness();
        // Hash succeeds, then the target disappears right before the OS process-creation call
        // (R-KE): typed Missing is degraded (no budget spent), never a session-fatal error.
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(ProcessStartResult.Missing());

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(maxRestarts: 1));
        start.Started.Should().BeTrue();

        // Let several poll cycles run: a Missing launch must stay degraded and must NOT
        // consume the consecutive budget, so no Error/exhaustion may ever surface.
        await Task.Delay(900);
        h.Statuses.Should().NotContain(GuardianStatus.Error,
            "a Missing launch must not spend budget or trip exhaustion (R-KE)");
        await controller.StopAsync();
    }

    [Fact]
    public async Task Launch_ReadErrorAtStartBoundary_IsDegraded_SessionSurvives_NoBudgetSpent()
    {
        using var h = new Harness();
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(ProcessStartResult.ReadError());

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(maxRestarts: 1));
        start.Started.Should().BeTrue();

        await Task.Delay(900);
        h.Statuses.Should().NotContain(GuardianStatus.Error,
            "a ReadError launch must not spend budget or trip exhaustion (R-KE)");
        await controller.StopAsync();
    }

    [Fact]
    public async Task Launch_CancelledBeforeCreation_SessionEnds_NoError_NoFurtherLaunch()
    {
        using var h = new Harness();
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(ProcessStartResult.Cancelled());

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request());
        start.Started.Should().BeTrue();

        // The initial (cancelled) launch attempt happens; the session then ends without firing
        // any further launch and without publishing a session-fatal Error (R-KE).
        await WaitUntilAsync(() => h.Monitor.ReceivedCalls().Any(c =>
                c.GetMethodInfo().Name == nameof(IProcessMonitor.TryStart)),
            "the initial launch to be attempted");
        await Task.Delay(600);

        h.Statuses.Should().NotContain(GuardianStatus.Error,
            "cancellation before process creation is never a launch failure (R-KE)");
        // Only the initial (cancelled) launch attempt: no further launches follow a cancelled start.
        h.Monitor.Received(1).TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
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
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(ProcessStartResult.Started());

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request());
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.ToastTitles.Contains("Target Changed"),
            "Target Changed toast to be shown");
        await WaitUntilAsync(() => h.Config.ReceivedCalls().Any(c =>
            c.GetMethodInfo().Name == nameof(IConfigurationStore.SaveAsync)),
            "SaveAsync to persist the disabled state");

        h.Monitor.DidNotReceive().TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
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

        h.Monitor.DidNotReceive().TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
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

        h.Monitor.DidNotReceive().TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        h.Statuses.Should().NotContain(GuardianStatus.Error,
            "a permission/IO read failure is ReadError, not Mismatch (K)");
    }

    [Fact]
    public async Task HashMatches_LaunchProceeds_StatusMonitoring()
    {
        using var h = new Harness();
        // The controller computes a fresh pin at Start and re-verifies at launch; both
        // read the same value, so the re-check matches (case-insensitively). The launched
        // process appears afterwards and is observed Running.
        bool running = false;
        h.Integrity.ComputeHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(TargetHashResult.Computed("AA")));
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(
                running ? ProcessOutcome.Running : ProcessOutcome.NotRunning, 42)));
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => { running = true; return ProcessStartResult.Started(); });

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request());
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.Monitor.ReceivedCalls().Any(c =>
                c.GetMethodInfo().Name == nameof(IProcessMonitor.TryStart)),
            "StartAsync to be called when the hash matches");
        await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Monitoring),
            "the launched process to be observed Running");
        await controller.StopAsync();

        h.Monitor.Received(1).TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
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
        // The stability window for the already-running target polls at its own bounded
        // cadence (v2.8 §4.2), then normal monitoring settles into the huge check interval.
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
        h.CheckCount.Should().BeLessThan(100,
            "the stability window polls at its bounded cadence, never a busy loop");

        await controller.StopAsync();
    }

    [Fact]
    public async Task CheckAsyncThrows_SetsDegraded_NoSessionTermination()
    {
        using var h = new Harness();
        var time = new ManualTimeProvider();
        h.Time = time;
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromException<ProcessCheckResult>(new InvalidOperationException("check boom"));
            });

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(checkInterval: 1));
        start.Started.Should().BeTrue();

        // A throwing check behaves like Unknown (F, SPEC §7.2.1/EH-02): the session stays alive.
        await WaitUntilAsync(() => h.CheckCount >= 2, "two throwing checks to run");
        h.Statuses.Should().NotContain(GuardianStatus.Error, "a throwing check alone must never terminate or Error the session");

        // After the time threshold the degraded indicator appears, but never Error.
        time.Advance(TimeSpan.FromSeconds(120));
        await WaitUntilAsync(() => h.CheckCount >= 3, "a third throwing check to run after time elapsed");
        await WaitUntilAsync(() => controller.CurrentState.IsDegraded, "degraded indicator after prolonged throwing checks");

        // The loop keeps polling: still no Error, still alive.
        await WaitUntilAsync(() => h.CheckCount >= 5, "the loop to keep polling after repeated throwing checks");
        h.Statuses.Should().NotContain(GuardianStatus.Error);
        controller.CurrentState.Status.Should().NotBe(GuardianStatus.Error);
        await controller.StopAsync();
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
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => { h.MonitorInitialLaunchCalled = true; return ProcessStartResult.Started(); });

        var controller = (GuardianOrchestrator)h.CreateController();
        var start = await controller.StartAsync(h.Request(initialDelay: 1));
        start.Started.Should().BeTrue();

        // Pause inside the launch critical section so Stop must wait for the in-flight
        // launch to settle (making RuntimeStopped honest) before it can cancel. The inside
        // hook is synchronous: while it runs the launch gate IS held (R-CD), so it blocks on
        // the event instead of awaiting.
        using var paused = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        controller.InsideLaunchGateHook = () =>
        {
            h.MonitorInitialLaunchCalled = true;
            paused.Set();
            release.Wait(TimeSpan.FromSeconds(20));
        };

        await WaitUntilAsync(() => h.MonitorInitialLaunchCalled,
            "initial launch critical section to be entered");

        // Stop is now pending: it waits on the launch gate (an in-flight launch holds it).
        var stop = controller.StopAsync();
        await Task.Delay(300);
        h.Statuses.Should().NotContain(GuardianStatus.Stopped,
            "Stop must wait for the in-flight launch before settling");

        // Release the launch; Stop then cancels the session and settles.
        release.Set();
        var stopResult = await stop;
        stopResult.RuntimeStopped.Should().BeTrue();
        stopResult.SettingsSaved.Should().BeTrue();
        await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Stopped),
            "Status Stopped once the session settles");

        await Task.Delay(600);
        h.Monitor.ReceivedCalls().Count(c =>
                c.GetMethodInfo().Name == nameof(IProcessMonitor.TryStart))
            .Should().Be(1, "no launch may occur after a completed Stop");
    }

    [Fact]
    public async Task Stop_VerificationBarrier_NotWaitingForVerificationTimeout_CancelsPromptly()
    {
        using var h = new Harness();
        // The free initial launch succeeds, then the startup-observation phase (v2.8) is held
        // behind a controllable CheckAsync barrier. The configured verification timeout is huge
        // (999 s) on purpose: Stop must cancel the session token and settle promptly, not wait
        // out the timeout (R-CD). Because observation runs OUTSIDE the launch gate, Stop can
        // reach the gate immediately and turn the cancellation key.
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(ProcessStartResult.Started());

        int checkIndex = 0;
        var observationEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observationBarrier = new TaskCompletionSource<ProcessCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns((CallInfo ci) =>
            {
                var ct = ci.Arg<CancellationToken>();
                // The first check reliably reports the target down (the free launch follows);
                // the next one is the startup-appearance observation, parked on the barrier.
                if (Interlocked.Increment(ref checkIndex) == 1)
                    return Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning));
                observationEntered.TrySetResult(true);
                // Kept registered: the cancellation key turns when the session token is
                // cancelled (the barrier only ever completes for us via cancellation, never
                // by the test).
                ct.Register(() => observationBarrier.TrySetResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));
                return observationBarrier.Task;
            });

        var controller = (GuardianOrchestrator)h.CreateController();
        var start = await controller.StartAsync(h.Request(startupTimeout: 999, checkInterval: 60));
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => observationEntered.Task.IsCompleted,
            "the session to enter startup observation");

        var sw = Stopwatch.StartNew();
        var stop = await controller.StopAsync();
        sw.Stop();
        stop.RuntimeStopped.Should().BeTrue();
        stop.SettingsSaved.Should().BeTrue();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10),
            "Stop must cancel promptly instead of waiting out the verification timeout (R-CD)");

        (await observationBarrier.Task).Outcome.Should().Be(ProcessOutcome.NotRunning,
            "observation was unblocked by the cancellation, not by the test");
        await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Stopped),
            "Status Stopped once the session settles");
    }

    [Fact]
    public async Task Stop_ClosesGateBeforeLaunch_ProcessNotStarted()
    {
        using var h = new Harness();
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(ProcessStartResult.Started());

        var controller = (GuardianOrchestrator)h.CreateController();
        var start = await controller.StartAsync(h.Request(initialDelay: 1, checkInterval: 60));
        start.Started.Should().BeTrue();

        // Park the session BEFORE the launch gate so Stop is guaranteed to win the race and
        // close the gate first: Stop acquires the (free) gate, marks it closed and cancels
        // while the launch attempt is still parked. Releasing the park then hits the closed
        // gate — the process is never started (R-CD).
        var parked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePark = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.BeforeLaunchGateHook = (_, __) =>
        {
            parked.TrySetResult(true);
            return releasePark.Task;
        };

        await WaitUntilAsync(() => parked.Task.IsCompleted,
            "the session to be parked before the launch gate");

        var stop = controller.StopAsync();
        await Task.Delay(200);
        h.Statuses.Should().NotContain(GuardianStatus.Stopped,
            "the session is still parked; Stop closed the gate but the session has not exited yet");

        releasePark.TrySetResult(true);
        var stopResult = await stop;
        stopResult.RuntimeStopped.Should().BeTrue();
        await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Stopped),
            "Status Stopped once the parked session exits");

        h.Monitor.ReceivedCalls().Count(c =>
                c.GetMethodInfo().Name == nameof(IProcessMonitor.TryStart))
            .Should().Be(0, "Stop closed the gate first, so the process was never started (R-CD)");
    }

    [Fact]
    public async Task Suspend_VerificationBarrier_CancelsPromptly_NotWaitingForVerificationTimeout()
    {
        using var h = new Harness();
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(ProcessStartResult.Started());

        int checkIndex = 0;
        var observationEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observationBarrier = new TaskCompletionSource<ProcessCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns((CallInfo ci) =>
            {
                var ct = ci.Arg<CancellationToken>();
                if (Interlocked.Increment(ref checkIndex) == 1)
                    return Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning));
                observationEntered.TrySetResult(true);
                ct.Register(() => observationBarrier.TrySetResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));
                return observationBarrier.Task;
            });

        var controller = (GuardianOrchestrator)h.CreateController();
        var start = await controller.StartAsync(h.Request(startupTimeout: 999, checkInterval: 60));
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => observationEntered.Task.IsCompleted,
            "the session to enter startup observation");

        var sw = Stopwatch.StartNew();
        await controller.SuspendAsync();
        sw.Stop();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10),
            "Suspend must cancel promptly instead of waiting out the verification timeout (R-CD)");

        (await observationBarrier.Task).Outcome.Should().Be(ProcessOutcome.NotRunning,
            "observation was unblocked by the cancellation, not by the test");
        await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Stopped),
            "Status Stopped once the suspended session settles");
        h.Statuses.Should().NotContain(GuardianStatus.Error, "a suspend is never an Error");
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
        controller.CurrentState.IsDegraded.Should().BeFalse(
            "an Error state is never degraded (R-F)");
        controller.CurrentState.Reason.Should().Contain("target");
        controller.CurrentState.Reason.Should().NotBeNullOrWhiteSpace();

        _ = h.Monitor.DidNotReceive().CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        _ = h.Monitor.DidNotReceive().TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
    }

    // R-A: after a refused configuration initialization, every mutation entry point is blocked
    // at the controller level — no settings save, no autostart mutation, no session — even when
    // the methods are called directly (the disabled-UI gate alone is not the guard).
    [Fact]
    public async Task ConfigRefused_CommitSettings_DoesNotSaveDoesNotTouchAutostart()
    {
        using var h = new Harness();
        h.Config.InitializeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ConfigurationInitializationStatus.Refused));

        var controller = h.CreateController();
        await controller.ReconcileAsync();

        controller.CurrentState.Status.Should().Be(GuardianStatus.Error);
        controller.CurrentState.IsDegraded.Should().BeFalse("a refusal is never degraded (R-F)");

        var snapshot = CommittedSettingsSnapshot.From(new AppSettings
        {
            CheckIntervalSeconds = 7,
            AutostartEnabled = true,
            EnableToasts = false,
        });
        var result = await controller.CommitSettingsAsync(snapshot, @"C:\app.exe");

        result.Saved.Should().BeFalse("a refused store must reject the commit outright");
        _ = h.Config.DidNotReceive().SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>());
        _ = h.Autostart.DidNotReceive().SetEnabledAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
        controller.AutostartActualState.Should().BeNull(
            "no autostart state may be read or changed after a refusal");
        controller.CurrentState.Status.Should().Be(GuardianStatus.Error,
            "the refused Error state must be preserved, not overwritten by a commit");
    }

    [Fact]
    public async Task ConfigRefused_Start_Rejected_NoHashNoLaunchNoPersistence()
    {
        using var h = new Harness();
        h.Config.InitializeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ConfigurationInitializationStatus.Refused));

        var controller = h.CreateController();
        await controller.ReconcileAsync();

        var result = await controller.StartAsync(h.Request());

        result.Started.Should().BeFalse("Start must be blocked while the configuration is refused");
        result.Reason.Should().NotBeNullOrWhiteSpace();
        _ = h.Integrity.DidNotReceive().ComputeHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        h.Monitor.DidNotReceive().TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        _ = h.Config.DidNotReceive().SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>());
        _ = h.Autostart.DidNotReceive().SetEnabledAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
        controller.CurrentState.Status.Should().Be(GuardianStatus.Error);
    }

    [Fact]
    public async Task ConfigRefused_StopAndSuspend_DoNotPersist_DoNotTouchAutostart_ErrorPreserved()
    {
        using var h = new Harness();
        h.Config.InitializeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ConfigurationInitializationStatus.Refused));

        var controller = h.CreateController();
        await controller.ReconcileAsync();
        controller.CurrentState.Status.Should().Be(GuardianStatus.Error);
        var refusedReason = controller.CurrentState.Reason;
        refusedReason.Should().NotBeNullOrWhiteSpace("the refused boot publishes Error with a stable reason");

        // [v2.6 Amendment E / R-A-ERROR-STATE] Stop and Suspend must not overwrite the refused
        // Error with Stopped: the state and its reason stay intact, nothing is persisted and
        // autostart is never touched.
        var stop = await controller.StopAsync();
        stop.RuntimeStopped.Should().BeTrue("nothing is running after a refused boot");
        controller.CurrentState.Status.Should().Be(GuardianStatus.Error);
        controller.CurrentState.Reason.Should().Be(refusedReason, "the refused Error reason is preserved");
        controller.CurrentState.IsDegraded.Should().BeFalse("a refused config is never a degraded session (R-F)");
        controller.CurrentState.SaveFailed.Should().BeFalse("a refused Stop performs no persistence at all");
        _ = h.Config.DidNotReceive().SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>());
        _ = h.Autostart.DidNotReceive().SetEnabledAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
        _ = h.Autostart.DidNotReceive().RemoveRegistrationAsync(Arg.Any<CancellationToken>());

        await controller.SuspendAsync();
        controller.CurrentState.Status.Should().Be(GuardianStatus.Error);
        controller.CurrentState.Reason.Should().Be(refusedReason, "the refused Error reason is preserved");
        controller.CurrentState.IsDegraded.Should().BeFalse();
        controller.CurrentState.SaveFailed.Should().BeFalse();
        _ = h.Config.DidNotReceive().SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>());
        _ = h.Autostart.DidNotReceive().SetEnabledAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
        _ = h.Autostart.DidNotReceive().RemoveRegistrationAsync(Arg.Any<CancellationToken>());
    }

    // R-AS-AUTO: a refused configuration never reaches automatic recovery (Refused and
    // ResetNoticePending are mutually exclusive init outcomes, and Reconcile returns at the
    // refusal). No autostart removal, no save, no autostart mutation.
    [Fact]
    public async Task ConfigRefused_NoAutomaticRecovery_NoAutostartMutation()
    {
        using var h = new Harness();
        h.Config.InitializeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ConfigurationInitializationStatus.Refused));

        var controller = h.CreateController();
        await controller.ReconcileAsync();

        controller.CurrentState.Status.Should().Be(GuardianStatus.Error);
        _ = h.Config.DidNotReceive().SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>());
        _ = h.Autostart.DidNotReceive().SetEnabledAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
        _ = h.Autostart.DidNotReceive().RemoveRegistrationAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InitThrowsConfigurationUnavailable_ObservableAsRefused_MutationsBlocked()
    {
        using var h = new Harness();
        h.Config.InitializeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ConfigurationInitializationStatus>(new ConfigurationUnavailableException(
                "refused", "An administrator must repair the elevated configuration.")));

        var controller = h.CreateController();
        await controller.ReconcileAsync();

        controller.CurrentState.Status.Should().Be(GuardianStatus.Error);
        controller.CurrentState.IsDegraded.Should().BeFalse();

        var commit = await controller.CommitSettingsAsync(
            CommittedSettingsSnapshot.From(new AppSettings()), @"C:\app.exe");
        commit.Saved.Should().BeFalse();
        _ = h.Config.DidNotReceive().SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>());
        _ = h.Autostart.DidNotReceive().SetEnabledAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    // R-A2: a ConfigurationUnavailableException thrown by the post-init LoadAsync (not by
    // InitializeAsync) must also flip the controller-level refusal flag — the one R-A1 branch that was
    // still observable-as-refused. After the load-time refusal every mutation entry point stays
    // blocked: no SaveAsync, no autostart mutation, no hash read, no process start and no session.
    [Fact]
    public async Task LoadThrowsConfigurationUnavailable_ObservableAsRefused_MutationsBlocked()
    {
        using var h = new Harness();
        h.Config.InitializeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ConfigurationInitializationStatus.Success));
        h.Config.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<AppSettings>(new ConfigurationUnavailableException(
                "refused", "An administrator must repair the elevated configuration.")));

        var controller = h.CreateController();
        await controller.ReconcileAsync();

        controller.CurrentState.Status.Should().Be(GuardianStatus.Error);
        controller.CurrentState.IsDegraded.Should().BeFalse();

        // CommitSettingsAsync is rejected: no settings write reaches the store.
        var commit = await controller.CommitSettingsAsync(
            CommittedSettingsSnapshot.From(new AppSettings { AutostartEnabled = true }), @"C:\app.exe");
        commit.Saved.Should().BeFalse();
        _ = h.Config.DidNotReceive().SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>());

        // Start is rejected before any hash read and before any autostart mutation or process launch.
        var start = await controller.StartAsync(h.Request());
        start.Started.Should().BeFalse();
        _ = h.Config.DidNotReceive().SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>());
        _ = h.Autostart.DidNotReceive().SetEnabledAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
        _ = h.Integrity.DidNotReceive().ComputeHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        _ = h.Monitor.DidNotReceive().TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());

        // Stop does not persist and touches no autostart; a stopped session cannot be started.
        await controller.StopAsync();
        _ = h.Config.DidNotReceive().SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>());
        _ = h.Autostart.DidNotReceive().SetEnabledAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    // R-autostart rollback: a failed autostart ENABLE on a settings commit must not leave a stale
    // AutostartEnabled=true in the persisted config or the controller's committed snapshot — a
    // later launch (or a follow-up persist from the committed state) would repeat the failed
    // registration. The rollback re-persists false as one consistent operation and the autostart
    // failure is still surfaced, never masked; no second enable is ever attempted.
    [Fact]
    public async Task CommitSettings_AutostartEnableFails_RollbackPersistsFalse_CommittedStaysFalse_NoRepeatEnable()
    {
        using var h = new Harness();
        h.Config.InitializeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ConfigurationInitializationStatus.Success));
        h.Autostart.SetEnabledAsync(true, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AutostartResult.Failure(
                "task registration failed (exit 1)", AutostartState.Absent)));

        var savedDocs = new List<AppSettings>();
        h.Config.SaveAsync(Arg.Do<AppSettings>(d => savedDocs.Add(d)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var controller = h.CreateController();
        await controller.ReconcileAsync();

        var snapshot = CommittedSettingsSnapshot.From(new AppSettings
        {
            AutostartEnabled = true,
            CheckIntervalSeconds = 5,
            EnableToasts = false,
        });
        var result = await controller.CommitSettingsAsync(snapshot, @"C:\app.exe");

        // The plain settings save succeeded; only the autostart step failed and that failure is
        // surfaced (Saved=true + Autostart failure), never masked as a full commit success.
        result.Saved.Should().BeTrue("the settings save itself succeeded");
        result.Autostart.Should().NotBeNull("the autostart outcome must always accompany the commit");
        result.Autostart!.Value.Succeeded.Should().BeFalse("an autostart failure must never be masked");

        _ = h.Autostart.Received(1).SetEnabledAsync(true, Arg.Any<CancellationToken>());
        savedDocs.Should().NotBeEmpty();
        savedDocs.Last().AutostartEnabled.Should().BeFalse(
            "a failed autostart enable must roll the persisted AutostartEnabled back to false");

        // The controller's committed state follows the rollback: a follow-up Stop persisting from
        // the committed snapshot (no fresh snapshot passed) writes false, not the stale true.
        var stop = await controller.StopAsync();
        stop.RuntimeStopped.Should().BeTrue();
        savedDocs.Last().AutostartEnabled.Should().BeFalse(
            "the committed snapshot must keep AutostartEnabled=false after the rollback");
        _ = h.Autostart.Received(1).SetEnabledAsync(true, Arg.Any<CancellationToken>());
    }

    // The rollback must not fire when the requested autostart state was already off (a disable
    // outcome leaves the persisted flag as requested); only an enable that the scheduler rejected
    // triggers the corrective re-persist, so a successful disable is stored unchanged.
    [Fact]
    public async Task CommitSettings_AutostartDisableFailure_DoesNotRollbackToTrue_PersistsFalse()
    {
        using var h = new Harness();
        h.Config.InitializeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ConfigurationInitializationStatus.Success));
        h.Autostart.ReadStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AutostartState.Enabled));
        h.Autostart.SetEnabledAsync(false, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AutostartResult.Failure(
                "task deletion failed (exit 1)", AutostartState.Enabled)));

        var savedDocs = new List<AppSettings>();
        h.Config.SaveAsync(Arg.Do<AppSettings>(d => savedDocs.Add(d)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var controller = h.CreateController();
        await controller.ReconcileAsync();

        var snapshot = CommittedSettingsSnapshot.From(new AppSettings { AutostartEnabled = false });
        var result = await controller.CommitSettingsAsync(snapshot, @"C:\app.exe");

        result.Saved.Should().BeTrue();
        result.Autostart!.Value.Succeeded.Should().BeFalse();
        savedDocs.Last().AutostartEnabled.Should().BeFalse(
            "a disable failure must keep the persisted AutostartEnabled=false (the requested off state)");
    }

    // R-AS-TXN §4.2 #1: a failed initial settings save must never be followed by an autostart
    // side effect based on an uncommitted desired value — the external task is only touched after
    // a confirmed settings write.
    [Fact]
    public async Task CommitSettings_InitialSaveFails_AutostartNeverCalled_NotSaved()
    {
        using var h = new Harness();
        h.Config.InitializeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ConfigurationInitializationStatus.Success));
        h.Config.SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("disk full")));

        var controller = h.CreateController();
        await controller.ReconcileAsync();

        var snapshot = CommittedSettingsSnapshot.From(new AppSettings { AutostartEnabled = true });
        var result = await controller.CommitSettingsAsync(snapshot, @"C:\app.exe");

        result.Saved.Should().BeFalse("the initial settings save failed, so the commit is not persisted");
        // The external scheduled task is never mutated on the back of a failed settings save.
        _ = h.Autostart.DidNotReceive().SetEnabledAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
        _ = h.Autostart.DidNotReceive().RemoveRegistrationAsync(Arg.Any<CancellationToken>());
    }

    // R-AS-TXN §4.2 #3: initial save succeeds, enable fails, but the compensating rollback save
    // ALSO fails — the commit must NOT report a fully persisted outcome. It stays Saved=false (not
    // silently converted to "saved"), the autostart failure is still distinguishable, the in-memory
    // committed intent is reverted to false, and a later Stop persists false (not the stale true).
    [Fact]
    public async Task CommitSettings_EnableFails_RollbackSaveFails_NotReportedSaved_InMemoryFalse()
    {
        using var h = new Harness();
        h.Config.InitializeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ConfigurationInitializationStatus.Success));
        h.Autostart.SetEnabledAsync(true, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AutostartResult.Failure(
                "task registration failed (exit 1)", AutostartState.Absent)));

        // Reconcile performs no save for an off/empty doc, so within CommitSettingsAsync the save
        // order is: #1 initial (AutostartEnabled=true), #2 compensating rollback (false). The
        // rollback (#2) is rejected; the subsequent Stop save (#3) succeeds.
        var savedDocs = new List<AppSettings>();
        var saveCalls = 0;
        h.Config.SaveAsync(Arg.Do<AppSettings>(d =>
        {
            savedDocs.Add(d);
            saveCalls++;
            if (saveCalls == 2)
                throw new InvalidOperationException("rollback write failed");
        }), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var controller = h.CreateController();
        await controller.ReconcileAsync();

        var snapshot = CommittedSettingsSnapshot.From(new AppSettings
        {
            AutostartEnabled = true,
            CheckIntervalSeconds = 5,
            EnableToasts = false,
        });
        var result = await controller.CommitSettingsAsync(snapshot, @"C:\app.exe");

        // The rollback save failed: the commit is NOT reported as fully persisted.
        result.Saved.Should().BeFalse(
            "the final rollback save failed, so the commit must not report a persisted outcome");
        // The autostart failure is still carried, distinguishing this from a plain initial-save
        // failure (which carries no autostart outcome).
        result.Autostart.Should().NotBeNull("the autostart failure stays distinguishable");
        result.Autostart!.Value.Succeeded.Should().BeFalse();

        _ = h.Autostart.Received(1).SetEnabledAsync(true, Arg.Any<CancellationToken>());

        // The in-memory controller intent was reverted to false despite the rollback-save failure:
        // a follow-up Stop persists false, not the stale true.
        var stop = await controller.StopAsync();
        stop.RuntimeStopped.Should().BeTrue();
        savedDocs.Last().AutostartEnabled.Should().BeFalse(
            "the committed snapshot must keep AutostartEnabled=false even though the rollback save failed");
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
            controller.CurrentState.IsDegraded.Should().BeFalse(
                "an Error state is never degraded (R-F)");
            controller.CurrentState.Reason.Should().Contain("pin");
            _ = h.Monitor.DidNotReceive().TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
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
        committed.Saved.Should().BeTrue();

        saved.Should().NotBeNull();
        saved!.TargetProcessPath.Should().Be(otherPath);
        saved!.TargetHashSha256.Should().BeEmpty("a committed path change in the stopped state must clear the pin");
        saved!.IsMonitoringEnabled.Should().BeFalse();
    }

    // Deterministic clock for the E degraded-threshold tests.
    private sealed class ManualTimeProvider : TimeProvider
    {
        private TimeSpan _elapsed;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + _elapsed;

        public void Advance(TimeSpan t) => _elapsed += t;

        public override long GetTimestamp() => _elapsed.Ticks;

        // Make GetTimestamp's ticks the TimeSpan tick scale so the base (non-virtual)
        // GetElapsedTime(long) arithmetic is exact on every platform.
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    // Deterministic virtual clock for the v2.8 timing-phase tests (§5): the timestamp is manual
    // (like ManualTimeProvider), but CreateTimer queues virtual timers that fire only inside
    // Advance — so every session wait (Task.Delay(..., TimeProvider, ...)) resolves
    // deterministically with no real sleep participating in the timing semantics.
    private sealed class VirtualTimeProvider : TimeProvider
    {
        private long _nowTicks;
        private readonly object _gate = new();
        private readonly List<VirtualTimer> _timers = new();

        // Fires every virtual timer due within t, in due order, advancing the clock to each
        // timer's due time so timers queued by fired continuations chain correctly.
        public void Advance(TimeSpan t)
        {
            var targetTicks = _nowTicks + t.Ticks;
            while (true)
            {
                VirtualTimer? due = null;
                lock (_gate)
                {
                    foreach (var timer in _timers)
                    {
                        if (timer.DueTime.Ticks <= targetTicks && (due is null || timer.DueTime < due.DueTime))
                            due = timer;
                    }

                    if (due is null)
                    {
                        _nowTicks = targetTicks;
                        return;
                    }

                    _nowTicks = Math.Max(_nowTicks, due.DueTime.Ticks);
                    _timers.Remove(due);
                }

                due.Fire();
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new VirtualTimer(this, callback, state, dueTime);

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + new TimeSpan(Volatile.Read(ref _nowTicks));

        public override long GetTimestamp() => Volatile.Read(ref _nowTicks);

        // TimeSpan tick scale: the base (non-virtual) GetElapsedTime(long) arithmetic is exact.
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        private void RemoveTimer(VirtualTimer timer)
        {
            lock (_gate) _timers.Remove(timer);
        }

        private sealed class VirtualTimer : ITimer
        {
            private readonly VirtualTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private TimeSpan _dueTime;

            public VirtualTimer(VirtualTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
                // CreateTimer's due time is relative to the current virtual clock; store it as an
                // absolute value on the _nowTicks (TimeSpan tick) scale so Advance's due
                // comparisons stay consistent for timers created at any virtual moment.
                // Negative due times clamp to zero: the callback is due immediately.
                lock (owner._gate)
                {
                    _dueTime = new TimeSpan(owner._nowTicks + Math.Max(0, dueTime.Ticks));
                    owner._timers.Add(this);
                }
            }

            public TimeSpan DueTime => _dueTime;

            public void Fire() => _callback(_state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_owner._gate)
                {
                    _dueTime = new TimeSpan(_owner._nowTicks + Math.Max(0, dueTime.Ticks));
                    if (!_owner._timers.Contains(this))
                        _owner._timers.Add(this);
                }

                return true;
            }

            public void Dispose() => _owner.RemoveTimer(this);

            public ValueTask DisposeAsync()
            {
                _owner.RemoveTimer(this);
                return ValueTask.CompletedTask;
            }
        }
    }

    // Advances the virtual clock in PhasePoll-sized steps, yielding real time between steps so
    // the session loop's posted continuations run and queue the next virtual timers. Real time
    // only pumps thread-pool scheduling: no timing semantic depends on it, because the
    // orchestrator's checks and waits only progress inside Advance.
    private static async Task AdvanceUntilAsync(VirtualTimeProvider time, Func<bool> condition, string failureMessage, int maxSteps = 400)
    {
        for (var step = 0; step < maxSteps; step++)
        {
            if (condition())
                return;
            time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(1);
        }

        throw new Xunit.Sdk.XunitException($"Timed out waiting for: {failureMessage}");
    }

    [Fact]
    public async Task AlreadyRunning_StabilityBoundary_JustBelowIsUnstable_AtDurationConfirmsStable()
    {
        using var h = new Harness();
        h.Time = new VirtualTimeProvider();
        var time = (VirtualTimeProvider)h.Time;

        // The target is already Running when the session begins: the stability window runs for
        // the observed instance with no launch (v2.8 §3.1.2).
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromResult(new ProcessCheckResult(ProcessOutcome.Running, 42));
            });

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(maxRestarts: 1, startupTimeout: 1));
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.CheckCount >= 1, "the first check to run before virtual time advances");

        // Just below the configured duration (900 ms of 1000 ms): the window is incomplete,
        // no stability confirmation and no state change.
        await AdvanceUntilAsync(time, () => h.CheckCount >= 9, "the stability window to poll most of the duration");
        h.StableCount.Should().Be(0, "just below the configured duration the stability window is not complete");
        h.Statuses.Should().NotContain(GuardianStatus.Restarting);
        h.Statuses.Should().NotContain(GuardianStatus.Error);
        h.Monitor.DidNotReceive().TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());

        // A fresh positive Running observation at the full duration confirms stability.
        await AdvanceUntilAsync(time, () => h.StableCount >= 1, "the full continuous Running window to confirm stability");
        h.Monitor.DidNotReceive().TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());

        await controller.StopAsync();
    }

    [Fact]
    public async Task Initial_NeverAppears_FreeThenFirstAutomaticAllowed_ExhaustsAtLimit()
    {
        using var h = new Harness();
        h.Time = new VirtualTimeProvider();
        var time = (VirtualTimeProvider)h.Time;

        // The launch returns Started, but the process never becomes observable: CheckAsync
        // reliably reports NotRunning through the appearance deadline both times.
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning));
            });
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => { h.StartCallCount++; return ProcessStartResult.Started(); });

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(maxRestarts: 1, restartDelay: 1, startupTimeout: 1));
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.StartCallCount >= 1, "the free initial launch to be made");

        // Free initial attempt (uncharged): the appearance deadline expires, and the FIRST
        // automatic restart is allowed — the initial failure triggers it (v2.8 §3.1).
        await AdvanceUntilAsync(time, () => h.StartCallCount == 2, "the first automatic restart to launch after the free initial attempt");
        h.Statuses.Should().NotContain(GuardianStatus.Error, "the first automatic attempt is still within the budget");

        // The budgeted automatic attempt also fails to appear: the non-zero budget is
        // exhausted — Error immediately, no further launch (v2.8 §2.1).
        await AdvanceUntilAsync(time, () => controller.CurrentState.Status == GuardianStatus.Error,
            "the exhausted Max=1 budget to terminate the session with Error");

        h.Statuses.Should().Contain(GuardianStatus.Error);
        // Free initial launch + exactly one budgeted automatic attempt; no third TryStart.
        h.Monitor.Received(2).TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        // R-F settlement: the desired enabled=false was persisted best-effort on the fatal path.
        h.Config.ReceivedCalls().Should().Contain(c =>
            c.GetMethodInfo().Name == nameof(IConfigurationStore.SaveAsync),
            "budget exhaustion must persist the desired disabled state");
    }

    [Fact]
    public async Task Initial_ExitsJustBeforeStabilityBoundary_FirstAutomaticAllowed()
    {
        using var h = new Harness();
        h.Time = new VirtualTimeProvider();
        var time = (VirtualTimeProvider)h.Time;

        bool running = false;
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromResult(new ProcessCheckResult(
                    running ? ProcessOutcome.Running : ProcessOutcome.NotRunning, 42));
            });
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => { h.StartCallCount++; running = true; return ProcessStartResult.Started(); });

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(maxRestarts: 1, restartDelay: 1, startupTimeout: 1));
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.StartCallCount >= 1, "the free initial launch to be made");

        // The initial attempt appears and runs for part of the stability window, then exits
        // before the full duration: an unstable launch, still free (v2.8 §3.1) — nothing charged.
        await AdvanceUntilAsync(time, () => h.CheckCount >= 5, "several Running observations inside the stability window");
        running = false;

        // The first automatic restart is allowed by the untouched budget.
        await AdvanceUntilAsync(time, () => h.StartCallCount == 2, "the first automatic restart to launch after the unstable initial attempt");
        h.Statuses.Should().NotContain(GuardianStatus.Error, "the free initial attempt consumes nothing");
        h.Monitor.Received(2).TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());

        await controller.StopAsync();
    }

    [Fact]
    public async Task Max1_Automatic_ExitsBeforeStability_Error_NoSecondTryStart()
    {
        using var h = new Harness();
        h.Time = new VirtualTimeProvider();
        var time = (VirtualTimeProvider)h.Time;

        bool running = false;
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromResult(new ProcessCheckResult(
                    running ? ProcessOutcome.Running : ProcessOutcome.NotRunning, 42));
            });
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => { h.StartCallCount++; running = true; return ProcessStartResult.Started(); });

        var controller = h.CreateController();
        // CheckInterval far exceeds the one-second windows: the phases must run on their own
        // bounded cadence, independent of CheckIntervalSeconds (v2.8 §4.2).
        var start = await controller.StartAsync(h.Request(maxRestarts: 1, checkInterval: 30, restartDelay: 1, startupTimeout: 1));
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.StartCallCount >= 1, "the free initial launch to be made");

        // The initial attempt appears and completes the full stability window: budget reset.
        await AdvanceUntilAsync(time, () => h.StableCount >= 1, "the initial attempt's stability window to complete");

        // Crash: normal monitoring (the 60 s check interval resolves through virtual time) sees
        // NotRunning — exactly one budgeted automatic restart is allowed.
        running = false;
        await AdvanceUntilAsync(time, () => h.StartCallCount == 2, "the single budgeted automatic restart to launch");

        // The restarted process is positively observed once, then exits before the full
        // stability window: the budget is spent — Error, no second automatic TryStart.
        var checksAtRestart = h.CheckCount;
        await AdvanceUntilAsync(time, () => h.CheckCount >= checksAtRestart + 1, "the restarted process to be observed Running once");
        running = false;
        await AdvanceUntilAsync(time, () => controller.CurrentState.Status == GuardianStatus.Error,
            "the exhausted budget to terminate the session with Error");

        h.Statuses.Should().Contain(GuardianStatus.Error);
        // Free initial launch + exactly one budgeted automatic attempt; no third TryStart.
        h.Monitor.Received(2).TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        h.Config.ReceivedCalls().Should().Contain(c =>
            c.GetMethodInfo().Name == nameof(IConfigurationStore.SaveAsync),
            "budget exhaustion must persist the desired disabled state");
    }

    [Fact]
    public async Task TargetAppearsDuringRestartDelay_NoDuplicateStart()
    {
        using var h = new Harness();
        h.Time = new VirtualTimeProvider();
        var time = (VirtualTimeProvider)h.Time;

        // The free initial launch returns Started but the process never appears; the target
        // then appears on its own while the first restart delay is pending.
        bool running = false;
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromResult(new ProcessCheckResult(
                    running ? ProcessOutcome.Running : ProcessOutcome.NotRunning, 42));
            });
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => { h.StartCallCount++; return ProcessStartResult.Started(); });

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(maxRestarts: 1, restartDelay: 1, startupTimeout: 1));
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.StartCallCount >= 1, "the free initial launch to be made");
        await AdvanceUntilAsync(time, () => h.Statuses.Contains(GuardianStatus.Restarting),
            "the restart streak to begin after the initial appearance timeout");

        // The target appears on its own during the restart delay (v2.8 §3.5): after the delay,
        // the pre-launch recheck sees Running — no duplicate launch; the observed instance is
        // tracked for stability and replenishes the budget.
        running = true;
        await AdvanceUntilAsync(time, () => h.StableCount >= 1, "the observed instance's stability window to complete");

        h.Statuses.Should().Contain(GuardianStatus.Monitoring);
        h.Monitor.Received(1).TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        h.Statuses.Should().NotContain(GuardianStatus.Error);

        await controller.StopAsync();
    }

    [Fact]
    public async Task Unknown_DuringStartup_DoesNotTriggerRetryOrError()
    {
        using var h = new Harness();
        h.Time = new VirtualTimeProvider();
        var time = (VirtualTimeProvider)h.Time;

        // The launch returns Started but every observation afterwards stays indeterminate past
        // the appearance deadline: no retry, no Error, no duplicate launch (v2.8 §3.2). The
        // first cycle must reliably report NotRunning so the free initial launch is made.
        bool launched = false;
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromResult(launched
                    ? new ProcessCheckResult(ProcessOutcome.Unknown)
                    : new ProcessCheckResult(ProcessOutcome.NotRunning));
            });
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => { h.StartCallCount++; launched = true; return ProcessStartResult.Started(); });

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(maxRestarts: 1, restartDelay: 1, startupTimeout: 1));
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.StartCallCount >= 1, "the free initial launch to be made");

        // Let the appearance deadline elapse while the state stays Unknown: observation
        // continues degraded instead of failing the attempt.
        await AdvanceUntilAsync(time, () => h.CheckCount >= 12, "the appearance deadline to elapse while the state stays Unknown");

        h.Statuses.Should().NotContain(GuardianStatus.Error,
            "an indeterminate state never becomes a timeout failure (v2.8 §3.2)");
        h.Statuses.Should().NotContain(GuardianStatus.Restarting);
        h.Monitor.Received(1).TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());

        await controller.StopAsync();
    }

    [Fact]
    public async Task UnknownDuringStability_GapVoidsWindow_BudgetNotReplenished_ExhaustsAtLimit()
    {
        using var h = new Harness();
        h.Time = new VirtualTimeProvider();
        var time = (VirtualTimeProvider)h.Time;

        // Max=1: the free initial attempt stabilizes; the target crashes; the budgeted restart
        // is observed Running once, then an Unknown gap voids the interval (no failure, no
        // confirmation), Running resumes, and the process exits before the fresh full window
        // completes: the budget is not replenished — Error, no second automatic launch.
        bool running = false;
        int unknownGapRemaining = 0;
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                if (unknownGapRemaining > 0)
                {
                    unknownGapRemaining--;
                    return Task.FromResult(new ProcessCheckResult(ProcessOutcome.Unknown));
                }

                return Task.FromResult(new ProcessCheckResult(
                    running ? ProcessOutcome.Running : ProcessOutcome.NotRunning, 42));
            });
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => { h.StartCallCount++; running = true; return ProcessStartResult.Started(); });

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(maxRestarts: 1, restartDelay: 1, startupTimeout: 1));
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.StartCallCount >= 1, "the free initial launch to be made");
        await AdvanceUntilAsync(time, () => h.StableCount >= 1, "the initial attempt's stability window to complete");

        // Crash: the one budgeted automatic restart launches.
        running = false;
        await AdvanceUntilAsync(time, () => h.StartCallCount == 2, "the single budgeted automatic restart to launch");
        var firstRunningCheck = h.CheckCount;
        await AdvanceUntilAsync(time, () => h.CheckCount >= firstRunningCheck + 1,
            "the restarted process to be observed Running once");

        // One Unknown check inside the stability window: it voids the current interval —
        // neither a fall nor proof of stability (v2.8 §3.3).
        unknownGapRemaining = 1;
        var unknownCheck = h.CheckCount;
        await AdvanceUntilAsync(time, () => h.CheckCount >= unknownCheck + 1, "the Unknown gap check to run");
        h.Statuses.Should().NotContain(GuardianStatus.Error, "an Unknown gap never becomes a failure");

        // Running resumes; the fresh full window has not completed when the process exits.
        var freshWindowCheck = h.CheckCount;
        await AdvanceUntilAsync(time, () => h.CheckCount >= freshWindowCheck + 1, "Running to resume after the gap");
        running = false;

        await AdvanceUntilAsync(time, () => controller.CurrentState.Status == GuardianStatus.Error,
            "the unreplenished budget to terminate the session with Error");

        h.Statuses.Should().Contain(GuardianStatus.Error);
        // Free initial launch + exactly one budgeted automatic attempt; no second automatic launch.
        h.Monitor.Received(2).TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownDuringStability_FreshFullWindowAfterGap_ReplenishesBudget_LaterCrashAllowsOneMoreRestart()
    {
        using var h = new Harness();
        h.Time = new VirtualTimeProvider();
        var time = (VirtualTimeProvider)h.Time;

        // Max=1: the budgeted restart's stability window is voided by one Unknown gap, then a
        // FRESH full continuous Running window completes — the budget is replenished, and a
        // later crash allows one more automatic restart in the same session.
        bool running = false;
        int unknownGapRemaining = 0;
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                if (unknownGapRemaining > 0)
                {
                    unknownGapRemaining--;
                    return Task.FromResult(new ProcessCheckResult(ProcessOutcome.Unknown));
                }

                return Task.FromResult(new ProcessCheckResult(
                    running ? ProcessOutcome.Running : ProcessOutcome.NotRunning, 42));
            });
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => { h.StartCallCount++; running = true; return ProcessStartResult.Started(); });

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(maxRestarts: 1, restartDelay: 1, startupTimeout: 1));
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.StartCallCount >= 1, "the free initial launch to be made");
        await AdvanceUntilAsync(time, () => h.StableCount >= 1, "the initial attempt's stability window to complete");

        // Crash: the one budgeted automatic restart launches.
        running = false;
        await AdvanceUntilAsync(time, () => h.StartCallCount == 2, "the single budgeted automatic restart to launch");
        var firstRunningCheck = h.CheckCount;
        await AdvanceUntilAsync(time, () => h.CheckCount >= firstRunningCheck + 1,
            "the restarted process to be observed Running once");

        // One Unknown gap voids the interval; the fresh full window then completes.
        unknownGapRemaining = 1;
        var unknownCheck = h.CheckCount;
        await AdvanceUntilAsync(time, () => h.CheckCount >= unknownCheck + 1, "the Unknown gap check to run");
        await AdvanceUntilAsync(time, () => h.StableCount >= 2,
            "the fresh full stability window after the gap to replenish the budget");

        // Second crash: the replenished budget allows one more automatic restart.
        running = false;
        await AdvanceUntilAsync(time, () => h.StartCallCount == 3, "the later crash to allow one more automatic restart");

        h.Statuses.Should().NotContain(GuardianStatus.Error, "the fresh full window after the gap replenished the budget");
        h.Monitor.Received(3).TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());

        await controller.StopAsync();
    }

    [Fact]
    public async Task InvalidForm_StartRejected_NoSession_NoBudget()
    {
        using var h = new Harness();
        var controller = h.CreateController();

        // A relative path is form-invalid: the Start request is rejected before any session or
        // budget is engaged (E) — no hash read, no persistence as Error.
        var request = new StartRequest(
            CommittedSettingsSnapshot.From(new AppSettings { MaxRestartAttempts = 5, CheckIntervalSeconds = 1 }),
            "App\\target.exe");
        var result = await controller.StartAsync(request);

        result.Started.Should().BeFalse();
        result.Reason.Should().NotBeNullOrWhiteSpace();
        _ = h.Integrity.DidNotReceive().ComputeHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        _ = h.Config.DidNotReceive().SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_MissingTargetFile_Rejected_NoSession()
    {
        using var h = new Harness();
        // The file is unhashable at Start (clean missing): Start requires an existing readable
        // file, so the request is rejected and no session is engaged (E).
        h.Integrity.ComputeHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(TargetHashResult.Missing()));

        var controller = h.CreateController();
        var result = await controller.StartAsync(h.Request());

        result.Started.Should().BeFalse();
        _ = h.Monitor.DidNotReceive().CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reconcile_SavedInvalidPath_Error_NoSession()
    {
        using var h = new Harness();
        // Persisted enabled=true with a syntactically-invalid (relative) path: boot reconciliation
        // must report Error and never engage a session (E) — no silent monitoring attempt.
        h.Config.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AppSettings
            {
                IsMonitoringEnabled = true,
                TargetProcessPath = @"App\target.exe",
                TargetHashSha256 = "PIN",
            }));

        var controller = h.CreateController();
        await controller.ReconcileAsync();

        controller.CurrentState.Status.Should().Be(GuardianStatus.Error);
        controller.CurrentState.IsDegraded.Should().BeFalse(
            "an Error state is never degraded (R-F)");
        controller.CurrentState.Reason.Should().Contain("path");
        _ = h.Monitor.DidNotReceive().CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        _ = h.Monitor.DidNotReceive().TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProlongedUnknown_BeforeThreshold_NotDegraded_AfterThreshold_Degraded_NoError()
    {
        using var h = new Harness();
        var time = new ManualTimeProvider();
        h.Time = time;
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromResult(new ProcessCheckResult(ProcessOutcome.Unknown));
            });

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(checkInterval: 1));
        start.Started.Should().BeTrue();

        // Two Unknown cycles below the time threshold must NOT flag degraded (no single-glitch false alarm).
        await WaitUntilAsync(() => h.CheckCount >= 2, "two unknown cycles to run");
        controller.CurrentState.IsDegraded.Should().BeFalse("below the time threshold no degraded indicator");

        // Only after >= N checks AND >= T elapsed (poll-bound) does the persistent indicator appear.
        time.Advance(TimeSpan.FromSeconds(120));
        await WaitUntilAsync(() => h.CheckCount >= 3, "a third unknown cycle to run after time elapsed");
        await WaitUntilAsync(() => controller.CurrentState.IsDegraded, "degraded indicator after the time threshold");
        // A degraded active session must carry a stable non-empty reason, independent of toasts (R-F).
        controller.CurrentState.Reason.Should().NotBeNullOrWhiteSpace();
        controller.CurrentState.Reason.Should().Contain("cannot be verified");

        await controller.StopAsync();
        controller.CurrentState.Status.Should().NotBe(GuardianStatus.Error, "Unknown never escalates to Error");
        h.Statuses.Should().NotContain(GuardianStatus.Error);
    }

    [Fact]
    public async Task PersistSaveThrows_OnStart_NotStarted_NoSession()
    {
        using var h = new Harness();
        // A failing settings write must not start monitoring (F): the session is never engaged.
        h.Config.SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new IOException("disk failed"));
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.Running, 42)));

        var controller = h.CreateController();
        var result = await controller.StartAsync(h.Request());

        result.Started.Should().BeFalse("a failed persistence write must not start monitoring");
        result.Reason.Should().Contain("persist");
        _ = h.Monitor.DidNotReceive().CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistSaveThrows_OnStop_RuntimeStoppedTrue_SettingsSavedFalse()
    {
        using var h = new Harness();
        h.Config.SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.Running, 42)));

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request());
        start.Started.Should().BeTrue();
        await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Monitoring), "session to reach Monitoring");

        // From now on the store rejects writes: Stop must still guarantee RuntimeStopped
        // (the session is cancelled) while surfacing SettingsSaved=false (best-effort save).
        h.Config.SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new IOException("disk failed"));

        var stop = await controller.StopAsync();

        stop.RuntimeStopped.Should().BeTrue("Stop always guarantees the runtime session is over (F)");
        stop.SettingsSaved.Should().BeFalse("a failed final save is surfaced, not reported as success");
        controller.CurrentState.Status.Should().Be(GuardianStatus.Stopped);
        controller.CurrentState.SaveFailed.Should().BeTrue("the SaveFailed flag is surfaced on the state channel");
    }

    [Fact]
    public async Task PersistSaveThrows_OnFatalMismatch_ErrorStillPublished_WithSaveFailedFlag()
    {
        using var h = new Harness();
        // Pin at Start is "PIN"; at the launch re-verify the target reads "BB" -> fatal Mismatch.
        int hashCalls = 0;
        h.Integrity.ComputeHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(TargetHashResult.Computed(hashCalls++ == 0 ? "PIN" : "BB")));
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));

        var controller = h.CreateController();
        // Start's own persist must succeed first (a failed Start write is a separate test);
        // only the fatal-path Save(false) is forced to fail.
        var start = await controller.StartAsync(h.Request());
        start.Started.Should().BeTrue();

        // From now on the store rejects writes: the fatal Mismatch path cannot persist
        // enabled=false, but Error must still be published with the SaveFailed flag (F).
        h.Config.SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new IOException("disk failed"));

        await WaitUntilAsync(() => controller.CurrentState.Status == GuardianStatus.Error,
            "Error to be published after the fatal mismatch");

        controller.CurrentState.Status.Should().Be(GuardianStatus.Error);
        controller.CurrentState.SaveFailed.Should().BeTrue(
            "a failed Save(false) must not suppress the Error publication (F)");
        controller.CurrentState.IsDegraded.Should().BeFalse(
            "a persistence failure is SaveFailed, never IsDegraded (R-F)");
        _ = h.Monitor.DidNotReceive().TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FatalMismatch_ErrorPublishedOnlyAfterSessionTaskBySettleContinuation()
    {
        using var h = new Harness();
        // Pin at Start is "PIN"; at the launch re-verify the target reads "BB" -> fatal Mismatch.
        int hashCalls = 0;
        h.Integrity.ComputeHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(TargetHashResult.Computed(hashCalls++ == 0 ? "PIN" : "BB")));
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request());
        start.Started.Should().BeTrue();

        // Park the fatal disabled-save on a barrier. The persist runs in the outside-the-task
        // continuation (SettleSessionOutcomeAsync, R-F), which is reached only after the session
        // task has already returned its terminal outcome — so while the persist is parked, the
        // session has ended and Error has NOT yet been published.
        var persistedEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePersist = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Config.SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                persistedEntered.TrySetResult(true);
                return releasePersist.Task;
            });

        await WaitUntilAsync(() => persistedEntered.Task.IsCompleted,
            "the fatal disabled-save (outside the session task) to be entered");

        // The session task finished first (it must, for the continuation to be running its
        // persist), yet the Error is only published after that persist: it is not emitted inside
        // the session loop (R-F).
        controller.CurrentState.Status.Should().NotBe(GuardianStatus.Error,
            "Error is not published until the outside-the-task settle runs after the session task completes");

        releasePersist.TrySetResult(true);
        await WaitUntilAsync(() => controller.CurrentState.Status == GuardianStatus.Error,
            "Error to be published once the settle continuation completes");

        controller.CurrentState.Status.Should().Be(GuardianStatus.Error);
        controller.CurrentState.IsDegraded.Should().BeFalse("a fatal Error is never degraded (R-F)");
    }

    // R-F: every Stopped publication is IsDegraded=false; a failed save is surfaced ONLY through
    // SaveFailed, never by marking the state degraded. This test exercises the no-live-session
    // branch of StopAsync (a session that never ran, or already completed) with the save failing.
    [Fact]
    public async Task Stop_NoLiveSession_SaveFails_StoppedIsDegradedFalse_SaveFailedOnly()
    {
        using var h = new Harness();
        h.Config.SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new IOException("disk failed"));

        var controller = h.CreateController();
        var stop = await controller.StopAsync(); // no session ever started -> no-live-session branch

        stop.RuntimeStopped.Should().BeTrue();
        stop.SettingsSaved.Should().BeFalse();
        controller.CurrentState.Status.Should().Be(GuardianStatus.Stopped);
        controller.CurrentState.IsDegraded.Should().BeFalse(
            "Stopped is never degraded even when the disabled-state save fails (R-F)");
        controller.CurrentState.SaveFailed.Should().BeTrue(
            "the save failure is surfaced only through the independent SaveFailed flag");
    }

    // R-F: the live-session branch of StopAsync must publish IsDegraded=false when its final
    // save fails; the failure is a SaveFailed flag, never a degraded Stopped.
    [Fact]
    public async Task Stop_LiveSession_SaveFails_StoppedIsDegradedFalse_SaveFailedOnly()
    {
        using var h = new Harness();
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.Running, 42)));

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request());
        start.Started.Should().BeTrue();
        await WaitUntilAsync(() => controller.CurrentState.Status == GuardianStatus.Monitoring,
            "the session to reach Monitoring");

        h.Config.SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new IOException("disk failed"));

        var stop = await controller.StopAsync();

        stop.RuntimeStopped.Should().BeTrue("Stop always stops the runtime session (F)");
        stop.SettingsSaved.Should().BeFalse();
        controller.CurrentState.Status.Should().Be(GuardianStatus.Stopped);
        controller.CurrentState.IsDegraded.Should().BeFalse(
            "a failed final save during Stop is never a degraded Stopped (R-F)");
        controller.CurrentState.SaveFailed.Should().BeTrue(
            "the failure must be surfaced via SaveFailed, independently of IsDegraded");
    }

    // R-F settlement sync (4.2, Scenario A): Stop during the fatal settlement. The fatal
    // disabled-save is parked on a barrier while the settlement holds the operation lock; Stop
    // then linearizes after it. The settlement publishes its fatal Error first, Stop publishes
    // Stopped last, and no stale Error follows the Stop — the final state is a clean Stopped.
    [Fact]
    public async Task FatalSettlement_RaceStop_DuringSave_StopWins_NoStaleError()
    {
        using var h = new Harness();
        // Pin at Start is "PIN"; at the launch re-verify the target reads "BB" -> fatal Mismatch.
        int hashCalls = 0;
        h.Integrity.ComputeHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(TargetHashResult.Computed(hashCalls++ == 0 ? "PIN" : "BB")));
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));

        var published = new System.Collections.Concurrent.ConcurrentQueue<GuardianStatus>();
        var controller = h.CreateController();
        controller.StateChanged += (_, s) => published.Enqueue(s.Status);

        var start = await controller.StartAsync(h.Request());
        start.Started.Should().BeTrue();

        // Park the fatal disabled-save (the settlement's persist) on a barrier. Because the
        // settlement now holds the operation lock while the save is parked, Stop cannot run
        // concurrently with the settlement — it is deterministically linearized after it.
        var persistedEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePersist = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Config.SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                persistedEntered.TrySetResult(true);
                return releasePersist.Task;
            });

        await WaitUntilAsync(() => persistedEntered.Task.IsCompleted,
            "the fatal disabled-save (settlement continuation) to be entered");

        // Stop is now pending on the operation lock held by the parked settlement.
        var stopTask = controller.StopAsync();

        releasePersist.TrySetResult(true);

        var stop = await stopTask;
        stop.RuntimeStopped.Should().BeTrue();

        await WaitUntilAsync(() => controller.CurrentState.Status == GuardianStatus.Stopped,
            "the final state to be Stopped after the Stop linearizes after the settlement");
        controller.CurrentState.Status.Should().Be(GuardianStatus.Stopped);
        controller.CurrentState.IsDegraded.Should().BeFalse("a Stopped state is never degraded (R-F)");
        controller.CurrentState.SaveFailed.Should().BeFalse(
            "both the settlement save and the Stop final save succeeded in this run");

        published.Last().Should().Be(GuardianStatus.Stopped,
            "the final published state must be the Stop result — the stale fatal Error is never the last state");
    }

    // R-F settlement sync (4.2, Scenario B): Start during the fatal settlement. Start waits on the
    // operation lock, so it can never begin while the previous settlement is mid-flight; once the
    // settlement settles (persists disabled, publishes its fatal Error) Start re-persists
    // enabled=true and begins a fresh session. The old disabled write is overwritten by the new
    // session's own save and the stale Error precedes (never overwrites) the new session's state.
    [Fact]
    public async Task FatalSettlement_RaceStart_DuringSave_NewSessionStartsClean()
    {
        using var h = new Harness();
        int hashCalls = 0;
        h.Integrity.ComputeHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(TargetHashResult.Computed(hashCalls++ == 1 ? "BB" : "PIN")));
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));

        var published = new System.Collections.Concurrent.ConcurrentQueue<GuardianStatus>();
        var controller = h.CreateController();
        controller.StateChanged += (_, s) => published.Enqueue(s.Status);

        var start = await controller.StartAsync(h.Request());
        start.Started.Should().BeTrue();

        var persistedEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePersist = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var savedDocs = new List<AppSettings>();
        h.Config.SaveAsync(Arg.Do<AppSettings>(d => savedDocs.Add(d)), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                persistedEntered.TrySetResult(true);
                return releasePersist.Task;
            });

        await WaitUntilAsync(() => persistedEntered.Task.IsCompleted,
            "the fatal disabled-save (settlement continuation) to be entered");

        // A new Start is now pending on the operation lock: SessionActive is false (the old task
        // completed), so the request is valid, but it must not begin until the settlement settles.
        var startTask = controller.StartAsync(h.Request());

        releasePersist.TrySetResult(true);

        var result = await startTask;
        result.Started.Should().BeTrue("a new session may start once the previous settlement has settled");

        // The new session is active and its state is published LAST — the stale fatal Error from
        // the old settlement never overwrites it.
        await WaitUntilAsync(
            () => controller.CurrentState.Status is GuardianStatus.Waiting
                or GuardianStatus.Monitoring
                or GuardianStatus.Restarting,
            "the new session to be active");
        controller.CurrentState.IsDegraded.Should().BeFalse("the new session is not degraded");
        published.Last().Should().BeOneOf(
            new[] { GuardianStatus.Waiting, GuardianStatus.Monitoring, GuardianStatus.Restarting },
            "the stale fatal Error must never be the final published state after a new Start");

        // Settings: the old settlement's disabled write is overwritten by the new session's own
        // PersistEnabledAsync(true), so the last persisted state is enabled again — the old
        // settlement cannot disable the new session.
        savedDocs.Should().Contain(d => !d.IsMonitoringEnabled,
            "the settlement persisted IsMonitoringEnabled=false while parked");
        savedDocs.Last().IsMonitoringEnabled.Should().BeTrue(
            "the new session re-persists IsMonitoringEnabled=true after the old disabled write");
    }

    [Fact]
    public async Task IsDegraded_OnlyWhileActive_StoppedAndErrorAlwaysFalse_ReasonPresentWhenTrue()
    {
        using var h = new Harness();
        var time = new ManualTimeProvider();
        h.Time = time;
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromResult(new ProcessCheckResult(ProcessOutcome.Unknown));
            });

        var published = new ConcurrentQueue<(GuardianStatus Status, bool IsDegraded, string? Reason)>();
        var controller = h.CreateController();
        controller.StateChanged += (_, s) => published.Enqueue((s.Status, s.IsDegraded, s.Reason));

        var start = await controller.StartAsync(h.Request(checkInterval: 1));
        start.Started.Should().BeTrue();

        // Drive a live session into the degraded indicator (active Monitoring state), then stop it.
        // Two Unknown cycles must run BEFORE the time shift so FirstDegradedTimestamp is anchored
        // to the elapsed=0 moment (otherwise the 120s threshold is consumed by the anchor itself).
        await WaitUntilAsync(() => h.CheckCount >= 2, "two unknown cycles to anchor the degraded timestamp");
        time.Advance(TimeSpan.FromSeconds(120));
        await WaitUntilAsync(() => h.CheckCount >= 3, "a third unknown cycle to run after time elapsed");
        await WaitUntilAsync(() => controller.CurrentState.IsDegraded, "degraded indicator after the threshold");
        await controller.StopAsync();

        controller.CurrentState.Status.Should().Be(GuardianStatus.Stopped);
        controller.CurrentState.IsDegraded.Should().BeFalse("a clean stop is never degraded (R-F)");

        // Invariant scan over the whole run (R-F): every IsDegraded=true publication is an active
        // state carrying a non-empty reason; every Stopped/Error publication is IsDegraded=false.
        foreach (var (status, isDegraded, reason) in published)
        {
            if (isDegraded)
            {
                status.Should().BeOneOf(
                    new[] { GuardianStatus.Waiting, GuardianStatus.Monitoring, GuardianStatus.Restarting },
                    "IsDegraded=true is valid only while the session is active");
                reason.Should().NotBeNullOrWhiteSpace("a degraded state must carry a stable, non-empty reason");
            }
        }

        published.Where(p => p.Status is GuardianStatus.Stopped or GuardianStatus.Error)
            .Should().OnlyContain(p => !p.IsDegraded, "Stopped/Error are never degraded (R-F)");
    }

    [Fact]
    public async Task Reconcile_StaleEnabledTrue_StillValidTarget_ReengagesMonitoring()
    {
        using var h = new Harness();
        // Accepted no-durable-latch limitation (F), documented: a stale persisted enabled=true
        // with a still-valid target+pin re-engages monitoring at the next boot — that is the
        // normal resume path, not a bypass (the controller never auto-starts from the flag ALONE).
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
            "a still-valid retained target+pin with stale enabled=true resumes monitoring at boot");
        await controller.StopAsync();
    }

    [Fact]
    public async Task AutomaticRestart_FullStabilityWindow_ResetsBudget_LaterCrash_AllowsOneMoreRestart()
    {
        using var h = new Harness();
        // v2.8 stability semantics: an automatic restart is accepted as stable only after the
        // FULL positive-running stability window, and completing it resets the streak budget.
        // Max=1: free initial launch, one budgeted restart, a full stable window (reset), a
        // later crash, one more budgeted restart — repeated crashes separated by full stable
        // windows never consume the same old budget.
        bool running = false;
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromResult(new ProcessCheckResult(
                    running ? ProcessOutcome.Running : ProcessOutcome.NotRunning, 42));
            });
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => { h.StartCallCount++; running = true; return ProcessStartResult.Started(); });

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(maxRestarts: 1));
        start.Started.Should().BeTrue();

        // The free initial launch appears and completes the full stability window.
        await WaitUntilAsync(() => h.StableCount >= 1,
            "the initial launch's full stability window to complete");

        // First exit: the one budgeted restart (charged once) is allowed by the reset budget.
        running = false;
        await WaitUntilAsync(() => h.StartCallCount == 2, "the first budgeted restart to launch");

        // Let the restarted process complete the full stability window: the counter resets.
        await WaitUntilAsync(() => h.StableCount >= 2,
            "the restart's full stability window to complete and replenish the budget");

        // Second exit: the budget was replenished by the stable window, so one more automatic
        // restart is allowed in the same session (a first Running observation alone never
        // refunds, only the completed stability window does).
        running = false;
        await WaitUntilAsync(() => h.StartCallCount == 3,
            "the second exit to allow one more restart after the full stable window");

        h.Statuses.Should().NotContain(GuardianStatus.Error,
            "full stable windows replenish the streak budget (v2.8)");
        // Free initial launch + exactly one budgeted restart per crash after each stable window.
        h.Monitor.Received(3).TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        await controller.StopAsync();
    }

    [Fact]
    public async Task InitialRunningTarget_DoesNotConsumeBudget_FullStableWindow_Replenishes()
    {
        using var h = new Harness();
        // The target is already Running when the session begins: the stability window runs for
        // the observed instance with no TryStart (v2.8 §3.1.2) and nothing is consumed. Its
        // first later exit allows one automatic restart for Max=1; that restart completes
        // another full stability window, so a second exit is allowed one more restart (v2.8).
        bool running = true;
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromResult(new ProcessCheckResult(
                    running ? ProcessOutcome.Running : ProcessOutcome.NotRunning, 42));
            });
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => { h.StartCallCount++; running = true; return ProcessStartResult.Started(); });

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(maxRestarts: 1));
        start.Started.Should().BeTrue();

        // The target is observed Running without any launch; the entry stability window
        // (v2.8 §3.1.2) completes on the observed instance.
        await WaitUntilAsync(() => h.StableCount >= 1,
            "the entry stability window for the already-running target to complete");
        await WaitUntilAsync(() => h.CheckCount >= 2, "several Running observations with no launch");
        h.Monitor.DidNotReceive().TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());

        // First exit: the one allowed automatic restart for Max=1 (charged once).
        running = false;
        await WaitUntilAsync(() => h.StartCallCount == 1, "the first exit to allow one automatic restart");
        await WaitUntilAsync(() => h.StableCount >= 2,
            "the restart's full stability window to complete and replenish the budget");

        // Second exit: the restart completed the full stability window, so the counter was
        // reset and one more automatic restart is allowed in the same session.
        running = false;
        await WaitUntilAsync(() => h.StartCallCount == 2,
            "the second exit to allow one more restart after the full stable window");

        h.Statuses.Should().NotContain(GuardianStatus.Error,
            "a completed stability window replenishes the streak budget (v2.8)");
        h.Monitor.Received(2).TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        await controller.StopAsync();
    }

    [Fact]
    public async Task Max2_AllowsExactlyTwoAutomaticAttempts_ThenExhausts()
    {
        using var h = new Harness();
        // Every automatic attempt fails (genuine process-creation failure): Max=2 allows the
        // free initial launch plus exactly two budgeted attempts in the unstable streak, then
        // the budget gate terminates with Error before a third automatic launch (v2.8).
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => { h.StartCallCount++; return ProcessStartResult.Failed("launch failed"); });

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(maxRestarts: 2, restartDelay: 0));
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => controller.CurrentState.Status == GuardianStatus.Error,
            "the exhausted Max=2 budget to terminate the session with Error");

        h.Statuses.Should().Contain(GuardianStatus.Error);
        // Free initial launch + exactly two budgeted attempts; no fourth launch after exhaustion.
        h.Monitor.Received(3).TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        // R-F settlement: the desired enabled=false was persisted best-effort on the fatal path.
        h.Config.ReceivedCalls().Should().Contain(c =>
            c.GetMethodInfo().Name == nameof(IConfigurationStore.SaveAsync),
            "budget exhaustion must persist the desired disabled state (R-F)");
    }

    [Fact]
    public async Task Max0_BudgetNeverExhausts()
    {
        using var h = new Harness();
        // MaxRestartAttempts=0 disables the instability-streak budget: always-failing automatic
        // attempts never exhaust it and never publish Error, while other protections
        // (restart delay, typed outcomes) stay in force (v2.8).
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => { h.StartCallCount++; return ProcessStartResult.Failed("launch failed"); });

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(maxRestarts: 0, restartDelay: 0));
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.StartCallCount >= 5,
            "far more than the Max=1 budget of automatic attempts to run");

        h.Statuses.Should().NotContain(GuardianStatus.Error,
            "MaxRestartAttempts=0 means the per-session budget never exhausts");
        await controller.StopAsync();
    }

    [Fact]
    public async Task LaunchError_AdapterThrows_Uncharged_SessionSurvives()
    {
        using var h = new Harness();
        // Indeterminate adapter exception (v2.7 §2.3): the concrete adapter converts every
        // exception inside its launch boundary into a typed result, so an exception reaching
        // the orchestrator cannot be an observed genuine creation failure — it stays uncharged
        // (fail-safe: the budget is never spent on an unobserved attempt) and the session
        // survives with the restart delay between attempts.
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => { h.StartCallCount++; throw new InvalidOperationException("adapter fault"); });

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(maxRestarts: 1, restartDelay: 0));
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.StartCallCount >= 3,
            "the adapter exception to be retried without consuming the budget");

        h.Statuses.Should().NotContain(GuardianStatus.Error,
            "an indeterminate adapter exception must not spend the budget or exhaust it");
        await controller.StopAsync();
    }

    [Fact]
    public async Task Exhaustion_SaveThrows_ErrorStillPublished_WithSaveFailedFlag()
    {
        using var h = new Harness();
        // Every launch fails (NotStarted): the free initial + MaxRestartAttempts=1 budgeted attempt
        // exhaust the instability-streak budget, and the final Save(false) fails on the fatal path.
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(ProcessStartResult.Failed("launch failed"));

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(maxRestarts: 1));
        start.Started.Should().BeTrue();

        // From now on the store rejects writes: the fatal exhaustion path cannot persist
        // enabled=false, but Error must still be published with SaveFailed surfaced (F).
        h.Config.SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new IOException("disk failed"));

        await WaitUntilAsync(() => controller.CurrentState.Status == GuardianStatus.Error,
            "the consecutive-budget exhaustion path to publish Error");

        controller.CurrentState.Status.Should().Be(GuardianStatus.Error);
        controller.CurrentState.SaveFailed.Should().BeTrue(
            "a failed Save(false) on the exhaustion fatal path must surface SaveFailed (F)");
    }
}
