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

        // Advances the frozen clock only forward, to an absolute timestamp on the _elapsed
        // tick scale (used by timer callbacks below; a later manual Advance must never be
        // undone by an earlier-scheduled timer firing).
        private void AdvanceTo(long ticks)
        {
            if (ticks > _elapsed.Ticks)
                _elapsed = new TimeSpan(ticks);
        }

        public override long GetTimestamp() => _elapsed.Ticks;

        // Make GetTimestamp's ticks the TimeSpan tick scale so the base (non-virtual)
        // GetElapsedTime(long) arithmetic is exact on every platform.
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        // Timer-based waits (Task.Delay(..., this provider, ...)) must advance the frozen
        // clock: when the underlying real timer fires, the clock first jumps to the timer's
        // absolute due time and only then the callback runs — otherwise a deadline-based wait
        // would spin forever on a clock that never moves between manual Advance calls.
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new RealTimeAdvancingTimer(this, callback, state, dueTime, period);

        private sealed class RealTimeAdvancingTimer : ITimer
        {
            private readonly ManualTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private readonly long _dueTicks; // absolute, on the _elapsed tick scale
            private System.Threading.Timer? _timer;

            public RealTimeAdvancingTimer(ManualTimeProvider owner, TimerCallback callback,
                object? state, TimeSpan dueTime, TimeSpan period)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
                _dueTicks = owner.GetTimestamp() + Math.Max(0, dueTime.Ticks);
                if (dueTime == Timeout.InfiniteTimeSpan)
                    return;
                _timer = new System.Threading.Timer(_ => Fire(), null, dueTime, period);
            }

            private void Fire()
            {
                _owner.AdvanceTo(_dueTicks);
                _callback(_state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _timer?.Change(dueTime, period);
                return true;
            }

            public void Dispose() => _timer?.Dispose();

            public ValueTask DisposeAsync()
            {
                _timer?.Dispose();
                return ValueTask.CompletedTask;
            }
        }
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
        {            var targetTicks = _nowTicks + t.Ticks;
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

        // Advances the raw clock WITHOUT firing queued timers. Models a synchronous blocking
        // operation (a non-zero-length TryStart) that consumes wall-clock time: no scheduled
        // wait can observe the intermediate moments and no queued callback fires during the
        // blocking span, so the post-operation timestamp is deterministic.
        public void AdvanceClockOnly(TimeSpan t)
        {
            lock (_gate)
                _nowTicks += t.Ticks;
        }

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
        // CheckInterval (3 s) far exceeds the one-second windows but stays within the virtual
        // stepping budget: recovery decisions happen on normal ticks (v2.8.1 §B.1), while the
        // appearance/stability phases observe on their own bounded cadence.
        var start = await controller.StartAsync(h.Request(maxRestarts: 1, checkInterval: 3, restartDelay: 1, startupTimeout: 1));
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.StartCallCount >= 1, "the free initial launch to be made");

        // The initial attempt appears and completes the full stability window: budget reset.
        await AdvanceUntilAsync(time, () => h.StableCount >= 1, "the initial attempt's stability window to complete");

        // Crash: normal monitoring (the 3 s check interval resolves through virtual time) sees
        // NotRunning on a normal tick — exactly one budgeted automatic restart is allowed.
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

        long startTicks = time.GetTimestamp();

        // The free initial launch returns Started but the process never appears; the target
        // then appears on its own while the first restart delay is still pending.
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
        // RestartDelaySeconds (3 s) is armed by the appearance-timeout observation at t=1 s
        // (the free launch never appeared): the deadline is t=4 s. The target appears on its
        // own while the delay is still pending.
        var start = await controller.StartAsync(h.Request(maxRestarts: 1, checkInterval: 1, restartDelay: 3, startupTimeout: 1));
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.StartCallCount >= 1, "the free initial launch to be made");
        await AdvanceUntilAsync(time, () => h.Statuses.Contains(GuardianStatus.Restarting),
            "the restart streak to begin after the initial appearance timeout");

        // The target appears on its own while the restart delay is pending (v2.8 §3.5): the
        // fresh observation sees Running — the pending restart and its delay reference are
        // cancelled (§2.3 G); the observed instance is tracked for stability and replenishes
        // the budget.
        running = true;
        await AdvanceUntilAsync(time, () => h.StableCount >= 1, "the observed instance's stability window to complete");

        // The delay deadline elapses afterwards: the tick's fresh observation is Running —
        // still no duplicate start (v2.8 §5.12).
        await AdvanceUntilAsync(time,
            () => time.GetTimestamp() - startTicks >= TimeSpan.FromSeconds(4).Ticks,
            "normal ticks past the deadline to run");
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

    // §4.2/§4.3: the terminal Error is published BEFORE the disabled-save completes. The save
    // runs in the outside-the-task continuation (SettleSessionOutcomeAsync, R-F), which is
    // reached only after the session task has returned its terminal outcome — but the Error is
    // already on the state channel while the persist is still parked on this barrier.
    [Fact]
    public async Task FatalMismatch_ErrorPublishedBeforeParkedDisabledSaveCompletes()
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

        // Park the fatal disabled-save on a barrier. The publish now happens inside the
        // outside-the-task continuation (SettleSessionOutcomeAsync, R-F) BEFORE the persist,
        // so while the save is parked the Error must already be on the state channel — the
        // first Error rendering never waits for disk I/O (§4.2).
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

        // The session task has finished and the Error was published before the parked save:
        // the terminal state never waits for the completion of the persistence write.
        controller.CurrentState.Status.Should().Be(GuardianStatus.Error,
            "the terminal Error is published before the disabled-save completes (§4.2)");
        controller.CurrentState.Reason.Should().Contain("no longer matches the recorded pin",
            "the error card receives its final reason on the first display");
        controller.CurrentState.SaveFailed.Should().BeFalse(
            "the save has not completed yet, so no SaveFailed flag is known yet");

        releasePersist.TrySetResult(true);
        await WaitUntilAsync(() => h.Config.ReceivedCalls().Any(c =>
                c.GetMethodInfo().Name == nameof(IConfigurationStore.SaveAsync)),
            "the disabled-save to complete after the Error was already published");

        controller.CurrentState.Status.Should().Be(GuardianStatus.Error,
            "the successful persistence never changes the already published terminal reason");
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

    // ---------------------------------------------------------------------
    // v2.8.1 (R-AS-RESTART-CADENCE) required deterministic tests
    // ---------------------------------------------------------------------

    // Required test #1/#8: a startup-verification timeout longer than the check interval must
    // never hold the supervision cadence — the loss and the free launch happen on the first
    // normal tick, checks keep flowing through the whole appearance window, and the next
    // launch fires on the first eligible normal tick after the FULL RestartDelaySeconds wait
    // measured from the timeout observation that established the failed startup (§2.3 C) —
    // never after the verification timeout itself, never blocked by it.
    [Fact]
    public async Task StartupTimeout_LongerThanCheckInterval_RestartDecidedOnNormalCadence_ChecksContinue()
    {
        using var h = new Harness();
        h.Time = new VirtualTimeProvider();
        var time = (VirtualTimeProvider)h.Time;

        long startTicks = time.GetTimestamp();
        long secondLaunchTicks = 0;
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning));
            });
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.StartCallCount++;
                if (h.StartCallCount == 2)
                    secondLaunchTicks = time.GetTimestamp();
                return ProcessStartResult.Started();
            });

        var controller = h.CreateController();
        // Verification timeout (5 s) far exceeds the check interval (1 s).
        var start = await controller.StartAsync(h.Request(maxRestarts: 1, checkInterval: 1, restartDelay: 1, startupTimeout: 5));
        start.Started.Should().BeTrue();

        // The loss is detected and the free launch happens on the first normal tick, without
        // any virtual waiting and without the verification timeout participating.
        await WaitUntilAsync(() => h.StartCallCount >= 1, "the free launch on the first normal tick");

        // Checks keep flowing through the whole 5 s appearance window (not blocked by it).
        await AdvanceUntilAsync(time, () => h.CheckCount >= 4 && h.StartCallCount == 1,
            "normal checks to continue while the appearance window is pending");

        // The budgeted restart fires on the first eligible normal tick after the FULL
        // RestartDelaySeconds (1 s) measured from the timeout observation at t=5 s (§2.3 C):
        // the failed startup is established by that observation, so the wait starts there —
        // the launch fires at ~t=6 s, and no earlier (the deadline is the detection time plus
        // the delay, evaluated on normal ticks only).
        await AdvanceUntilAsync(time, () => h.StartCallCount == 2, "the restart to launch on a normal tick");
        var launchElapsedMs = (secondLaunchTicks - startTicks) / TimeSpan.TicksPerMillisecond;
        launchElapsedMs.Should().BeGreaterThanOrEqualTo(6000,
            "no launch before the full RestartDelaySeconds measured from the timeout observation (§2.3 C)");
        launchElapsedMs.Should().BeLessThanOrEqualTo(6100,
            "the launch fires on the first normal tick at/after the deadline, with no extra delay");
        h.CheckCount.Should().BeGreaterThanOrEqualTo(5, "checks kept flowing through the appearance window");

        // The restarted attempt also never appears: the budget exhausts with Error.
        await AdvanceUntilAsync(time, () => controller.CurrentState.Status == GuardianStatus.Error,
            "the exhausted budget to terminate the session with Error");
        h.Monitor.Received(2).TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
    }

    // §3.4 #1/#2/#5: an automatic attempt produces "Process Recovered" on its FIRST positive
    // Running observation, BEFORE the stability interval has elapsed; repeated Running
    // observations produce no duplicates; a subsequent automatic attempt produces its own
    // one-time notification after its own first Running observation.
    [Fact]
    public async Task RecoveryToast_SentOnFirstRunningObservation_AtMostOnce()
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
        var start = await controller.StartAsync(h.Request(maxRestarts: 1, checkInterval: 1, restartDelay: 1, startupTimeout: 2));
        start.Started.Should().BeTrue();

        // The free initial attempt stabilizes: no toast — a Started result alone never notifies.
        await WaitUntilAsync(() => h.StartCallCount >= 1, "the free initial launch to be made");
        await AdvanceUntilAsync(time, () => h.StableCount >= 1, "the initial stability window to complete");
        h.ToastTitles.Should().BeEmpty("Process.Start returning Started never notifies (§3.3)");

        // Crash: the budgeted automatic restart launches. No notification yet.
        running = false;
        await AdvanceUntilAsync(time, () => h.StartCallCount == 2, "the budgeted automatic restart to launch");
        h.ToastTitles.Should().BeEmpty("no notification before the first positive Running observation");

        // FIRST positive Running observation of the attempt: the toast fires immediately,
        // BEFORE the stability window has completed.
        var checksAtLaunch = h.CheckCount;
        await AdvanceUntilAsync(time, () => h.CheckCount >= checksAtLaunch + 1,
            "the restarted process to be observed Running once");
        await WaitUntilAsync(() => h.ToastTitles.Contains("Process Recovered"),
            "the recovery toast on the first positive Running observation (§3.1)");
        h.ToastTitles.Count(t => t == "Process Recovered").Should().Be(1,
            "the toast fires on the FIRST Running observation");
        h.StableCount.Should().Be(1, "the stability window has NOT completed yet — the notification does not wait for it");

        // Repeated Running observations produce no duplicate notification; the full stability
        // window then completes and resets the budget (§3.2) — the notification accounting is
        // independent of the stability accounting.
        await AdvanceUntilAsync(time, () => h.StableCount >= 2, "the restart's stability window to complete");
        h.ToastTitles.Count(t => t == "Process Recovered").Should().Be(1,
            "repeated Running observations never duplicate the notification (§3.3)");

        // Crash again: the replenished budget earns one more restart, whose own first Running
        // observation produces its own one-time notification (§3.4 #5).
        running = false;
        await AdvanceUntilAsync(time, () => h.StartCallCount == 3, "the third launch after the budget was replenished");
        var checksAfterThirdLaunch = h.CheckCount;
        await AdvanceUntilAsync(time, () => h.CheckCount >= checksAfterThirdLaunch + 1,
            "the third attempt to be observed Running once");
        h.ToastTitles.Count(t => t == "Process Recovered").Should().Be(2,
            "each automatic attempt produces its own one-time notification");

        // The third attempt exits before its stability window completes: the budget is not
        // reset again — Error, no third notification.
        running = false;
        await AdvanceUntilAsync(time, () => controller.CurrentState.Status == GuardianStatus.Error,
            "the unreplenished budget to terminate the session with Error");
        h.ToastTitles.Count(t => t == "Process Recovered").Should().Be(2,
            "no third notification (at most once per attempt)");
        h.Monitor.Received(3).TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
    }

    // §3.4 #3: the free initial launch never produces the automatic-recovery notification —
    // not on its first Running observation, not on repeated ones, not after the stability
    // window completes.
    [Fact]
    public async Task RecoveryToast_FreeInitialLaunch_NeverNotifies()
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
        var start = await controller.StartAsync(h.Request(maxRestarts: 1, checkInterval: 1, restartDelay: 1, startupTimeout: 2));
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.StartCallCount >= 1, "the free initial launch to be made");
        await AdvanceUntilAsync(time, () => h.StableCount >= 1, "the initial stability window to complete");

        // Repeated Running observations well past the stability window: still no notification.
        await AdvanceUntilAsync(time, () => h.CheckCount >= 12, "several more Running observations to pass");
        h.ToastTitles.Should().BeEmpty("the free initial launch never produces the recovery notification (§3.3)");
        h.Monitor.Received(1).TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());

        await controller.StopAsync();
    }

    // §3.4 #4: a process that produces the notification on its first Running observation and
    // then exits BEFORE the stability window completes does NOT reset the restart budget —
    // the notification reports the appearance, not the stability qualification (§3.2).
    [Fact]
    public async Task RecoveryToast_ExitsBeforeStabilityCompletes_BudgetNotReset()
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
        var start = await controller.StartAsync(h.Request(maxRestarts: 1, checkInterval: 1, restartDelay: 1, startupTimeout: 2));
        start.Started.Should().BeTrue();

        // The free initial attempt stabilizes: no toast, budget not consumed.
        await WaitUntilAsync(() => h.StartCallCount >= 1, "the free initial launch to be made");
        await AdvanceUntilAsync(time, () => h.StableCount >= 1, "the initial stability window to complete");

        // Crash: the budgeted restart launches and is observed Running once — the toast fires
        // immediately on that first observation.
        running = false;
        await AdvanceUntilAsync(time, () => h.StartCallCount == 2, "the budgeted restart to launch");
        var checksAtLaunch = h.CheckCount;
        await AdvanceUntilAsync(time, () => h.CheckCount >= checksAtLaunch + 1,
            "the restarted process to be observed Running once");
        await WaitUntilAsync(() => h.ToastTitles.Contains("Process Recovered"),
            "the toast on the first positive Running observation");
        h.StableCount.Should().Be(1, "only the initial attempt's window completed so far");

        // The process exits BEFORE the stability window completes: the notification was
        // already sent, but the budget is NOT reset — the unreplenished budget terminates
        // the session with Error.
        running = false;
        await AdvanceUntilAsync(time, () => controller.CurrentState.Status == GuardianStatus.Error,
            "the unreplenished budget to terminate the session with Error");
        h.StableCount.Should().Be(1,
            "the stability window never completed, so the budget was never reset (§3.2)");
        h.ToastTitles.Count(t => t == "Process Recovered").Should().Be(1,
            "the notification reports the Running appearance, not the stability qualification (§3.2)");
        h.Monitor.Received(2).TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
    }

    // §3.4 #7: a failing toast delivery must never block supervision nor crash the session —
    // the delivery task is observed and its failure is logged, the checks keep flowing. The
    // notification is requested on the first Running observation (§3.1).
    [Fact]
    public async Task RecoveryToast_DeliveryFailure_DoesNotBlockMonitoring()
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
        h.Notifications.ShowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("toast bus down")));

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(maxRestarts: 1, checkInterval: 1, restartDelay: 1, startupTimeout: 2));
        start.Started.Should().BeTrue();

        // The initial attempt stabilizes, then the crash triggers the budgeted restart.
        await WaitUntilAsync(() => h.StartCallCount >= 1, "the free initial launch to be made");
        await AdvanceUntilAsync(time, () => h.StableCount >= 1, "the initial stability window to complete");
        running = false;
        await AdvanceUntilAsync(time, () => h.StartCallCount == 2, "the automatic restart to launch");

        // The first positive Running observation requests the notification (the failing
        // delivery is swallowed); the session keeps monitoring on subsequent ticks and the
        // stability window still completes normally.
        var checksAtLaunch = h.CheckCount;
        await AdvanceUntilAsync(time, () => h.CheckCount >= checksAtLaunch + 1,
            "the restarted process to be observed Running once (the failing delivery is requested)");
        h.ToastTitles.Should().Contain("Process Recovered",
            "the notification is requested on the first Running observation, before the stability window completes");
        await AdvanceUntilAsync(time, () => h.StableCount >= 2, "the restart's stability window to complete");
        var checksAfterStability = h.CheckCount;
        await AdvanceUntilAsync(time, () => h.CheckCount >= checksAfterStability + 2,
            "the monitoring to keep flowing after the failed toast delivery");
        controller.CurrentState.Status.Should().Be(GuardianStatus.Monitoring,
            "a toast delivery failure never blocks or terminates the session (§3.4 #7)");
        controller.CurrentState.IsDegraded.Should().BeFalse();
        h.CheckCount.Should().BeGreaterThanOrEqualTo(checksAfterStability + 2,
            "normal checks continued after the failed delivery");
    }

    // §3.4 #6: an attempt that never reaches a positive Running observation never produces
    // the recovery notification — the pending flag is cleared when the attempt times out.
    [Fact]
    public async Task RecoveryToast_NeverWhenAttemptNeverReachesRunning()
    {
        using var h = new Harness();
        h.Time = new VirtualTimeProvider();
        var time = (VirtualTimeProvider)h.Time;

        // Both launches return Started, but the process never becomes observable: both
        // attempts die at the appearance deadline and the Max=1 budget exhausts.
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning));
            });
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => { h.StartCallCount++; return ProcessStartResult.Started(); });

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(maxRestarts: 1, checkInterval: 1, restartDelay: 1, startupTimeout: 1));
        start.Started.Should().BeTrue();

        await AdvanceUntilAsync(time, () => controller.CurrentState.Status == GuardianStatus.Error,
            "the exhausted budget to terminate the session with Error");

        // No "Process Recovered" was ever requested: neither attempt reached a positive
        // Running observation (§3.3). The exhaustion toast is a separate notification.
        h.ToastTitles.Should().NotContain("Process Recovered",
            "an attempt that never reached Running never notifies (§3.4 #6)");
        h.ToastTitles.Should().Contain("Recovery Failed",
            "the budget exhaustion is a separate failure notification");
        h.Monitor.Received(2).TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
    }

    // Required test #6: RestartDelaySeconds longer than the check interval — no early launch,
    // normal checks keep flowing during the delay, and the launch fires on the first eligible
    // normal tick at/after the deadline.
    [Fact]
    public async Task RestartDelay_LongerThanCheckInterval_NoEarlyLaunch_LaunchOnFirstEligibleTick()
    {
        using var h = new Harness();
        h.Time = new VirtualTimeProvider();
        var time = (VirtualTimeProvider)h.Time;

        long startTicks = time.GetTimestamp();
        long secondLaunchTicks = 0;
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning));
            });
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.StartCallCount++;
                if (h.StartCallCount == 2)
                    secondLaunchTicks = time.GetTimestamp();
                return ProcessStartResult.Failed("launch failed");
            });

        var controller = h.CreateController();
        // Check interval 1 s, restart delay 2 s, unlimited budget (two launches asserted).
        var start = await controller.StartAsync(h.Request(maxRestarts: 0, checkInterval: 1, restartDelay: 2, startupTimeout: 1));
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.StartCallCount >= 1, "the free launch at the first tick");

        // The budgeted attempt waits for the deadline: the tick at 1000 ms finds the target
        // still NotRunning but the 2 s deadline not elapsed — no early launch.
        await AdvanceUntilAsync(time, () => h.StartCallCount == 2,
            "the restart to launch on the first eligible normal tick");
        var launchElapsedMs = (secondLaunchTicks - startTicks) / TimeSpan.TicksPerMillisecond;
        launchElapsedMs.Should().Be(2000,
            "no launch before the RestartDelaySeconds deadline; the first eligible normal tick fires at the deadline");
        h.CheckCount.Should().BeGreaterThanOrEqualTo(2, "checks kept flowing between the deadline and the launch");
        await controller.StopAsync();
    }

    // Required test #7: RestartDelaySeconds shorter than the check interval — the launch
    // happens on the next normal tick after the deadline, without any inline delay pulling it
    // off the tick cadence (the old inline wait would have launched at ~1 s, off-cadence).
    [Fact]
    public async Task RestartDelay_ShorterThanCheckInterval_LaunchOnNextNormalTick_NoExtraDelay()
    {
        using var h = new Harness();
        h.Time = new VirtualTimeProvider();
        var time = (VirtualTimeProvider)h.Time;

        long startTicks = time.GetTimestamp();
        long secondLaunchTicks = 0;
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning));
            });
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.StartCallCount++;
                if (h.StartCallCount == 2)
                    secondLaunchTicks = time.GetTimestamp();
                return ProcessStartResult.Failed("launch failed");
            });

        var controller = h.CreateController();
        // Check interval 3 s, restart delay 1 s, unlimited budget (two launches asserted).
        var start = await controller.StartAsync(h.Request(maxRestarts: 0, checkInterval: 3, restartDelay: 1, startupTimeout: 1));
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.StartCallCount >= 1, "the free launch at the first tick");

        // The deadline (1 s) elapses before the next normal tick (3 s): the launch rides the
        // tick cadence — no inline 1 s wait would pull it off-cadence to ~1 s, and no extra
        // delay pushes it past the tick.
        await AdvanceUntilAsync(time, () => h.StartCallCount == 2,
            "the restart to launch on the next normal tick");
        var launchElapsedMs = (secondLaunchTicks - startTicks) / TimeSpan.TicksPerMillisecond;
        launchElapsedMs.Should().Be(3000,
            "the launch fires exactly on the next normal tick after the deadline elapsed");
        h.CheckCount.Should().BeGreaterThanOrEqualTo(2, "the tick's check and the launch happen on the same cycle");
        await controller.StopAsync();
    }

    // Required test #10: the check schedule is deadline-anchored — a check that consumes
    // measurable time must not push every subsequent tick a full extra interval into the
    // future; each serviced tick advances the schedule by exactly one interval.
    [Fact]
    public async Task CheckExecutionTime_DoesNotAddExtraInterval_ScheduleStaysDeadlineAnchored()
    {
        using var h = new Harness();
        h.Time = new VirtualTimeProvider();
        var time = (VirtualTimeProvider)h.Time;

        var checkTicks = new long[4];
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                if (h.CheckCount <= 4)
                    Volatile.Write(ref checkTicks[h.CheckCount - 1], time.GetTimestamp());
                // Simulate a check that consumes 400 ms of (virtual) time.
                time.Advance(TimeSpan.FromMilliseconds(400));
                return Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning));
            });
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => { h.StartCallCount++; return ProcessStartResult.Failed("launch failed"); });

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(maxRestarts: 0, checkInterval: 1, restartDelay: 5));
        start.Started.Should().BeTrue();

        await AdvanceUntilAsync(time, () => h.CheckCount >= 4, "four ticks to run");
        await controller.StopAsync();

        // With deadline anchoring the ticks land at ~0/1000/2000/3000 ms (the 400 ms check time
        // is absorbed by the anchored schedule; small sub-slice jitter is possible on the first
        // tick, whose anchor and record can straddle a 100 ms virtual step). A schedule that
        // waited a full interval after each check would land at 0/1400/2800/4200 ms instead —
        // each gap must stay within one interval, never interval + check time.
        for (var i = 1; i < 4; i++)
        {
            var gapMs = (Volatile.Read(ref checkTicks[i]) - Volatile.Read(ref checkTicks[i - 1]))
                / TimeSpan.TicksPerMillisecond;
            gapMs.Should().BeLessThanOrEqualTo(1000,
                $"tick {i + 1} must not be pushed past one interval after the previous tick (no drift)");
            gapMs.Should().BeGreaterThanOrEqualTo(900,
                $"tick {i + 1} must not fire early (anchored to the tick deadline)");
        }
    }

    // ---------------------------------------------------------------------
    // R-AS-EXIT-ANCHORED-DELAY required deterministic tests: RestartDelaySeconds
    // is a FULL wait measured from the moment the supervisor DETECTS that the
    // previously running target has exited (post-observation timestamp, §2.2) —
    // regardless of how long the process had run.
    // ---------------------------------------------------------------------

    // Required test #1: a long-running target (it ran far longer than the configured
    // RestartDelaySeconds) must still wait the FULL delay from the moment its exit is
    // detected — the run time must NOT cause an immediate retry.
    [Fact]
    public async Task RestartDelay_LongRunningTarget_WaitsFullDelayFromDetection()
    {
        using var h = new Harness();
        h.Time = new VirtualTimeProvider();
        var time = (VirtualTimeProvider)h.Time;

        long startTicks = time.GetTimestamp();
        long firstNotRunningTicks = 0;
        long secondLaunchTicks = 0;
        bool running = false;
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                var result = new ProcessCheckResult(
                    running ? ProcessOutcome.Running : ProcessOutcome.NotRunning, 42);
                if (!running && firstNotRunningTicks == 0)
                    firstNotRunningTicks = time.GetTimestamp();
                return Task.FromResult(result);
            });
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.StartCallCount++;
                if (h.StartCallCount == 2)
                    secondLaunchTicks = time.GetTimestamp();
                running = true;
                return ProcessStartResult.Started();
            });

        var controller = h.CreateController();
        // RestartDelaySeconds (2 s) is far SHORTER than the run time below; unlimited budget.
        var start = await controller.StartAsync(h.Request(maxRestarts: 0, checkInterval: 1, restartDelay: 2, startupTimeout: 1));
        start.Started.Should().BeTrue();

        // The free launch happens at t=0 and completes the full stability window.
        await WaitUntilAsync(() => h.StartCallCount >= 1, "the free launch to be made");
        await AdvanceUntilAsync(time, () => h.StableCount >= 1, "the initial stability window to complete");

        // The target runs until t=5 s — far longer than the 2 s RestartDelaySeconds — then
        // exits. The run time must NOT cause an immediate retry: the full 2 s wait is
        // measured from the DETECTION of the exit.
        await AdvanceUntilAsync(time,
            () => time.GetTimestamp() - startTicks >= TimeSpan.FromSeconds(5).Ticks,
            "the target to run until t=5 s");
        running = false;

        // The exit is detected on the next normal tick (~t=6 s); the restart then waits the
        // full RestartDelaySeconds (2 s) from that detection: no launch at the detection tick
        // or during the delay, and the launch at the first eligible normal tick at/after the
        // deadline. Repeated NotRunning observations between detection and the deadline must
        // NOT extend the original delay (§2.3 F).
        await AdvanceUntilAsync(time, () => h.StartCallCount == 2,
            "the restart to launch after the full delay from the detected exit");
        var detectionElapsedMs = (secondLaunchTicks - firstNotRunningTicks) / TimeSpan.TicksPerMillisecond;
        detectionElapsedMs.Should().BeGreaterThanOrEqualTo(2000,
            "the full RestartDelaySeconds is measured from the detected exit (§2.2), even though the process ran far longer than the delay");
        detectionElapsedMs.Should().BeLessThanOrEqualTo(3100,
            "the launch fires on the first eligible normal tick at/after the deadline — no indefinite postponement by repeated NotRunning observations");
        h.Monitor.Received(2).TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        h.Statuses.Should().NotContain(GuardianStatus.Error, "the unlimited budget never exhausts");

        await controller.StopAsync();
    }

    // Required test #2: a short-running target (it exits shortly after launch) follows the
    // same rule — the full RestartDelaySeconds is measured from the detected exit.
    [Fact]
    public async Task RestartDelay_ShortRunningTarget_WaitsFullDelayFromDetection()
    {
        using var h = new Harness();
        h.Time = new VirtualTimeProvider();
        var time = (VirtualTimeProvider)h.Time;

        long startTicks = time.GetTimestamp();
        long firstNotRunningTicks = 0;
        long secondLaunchTicks = 0;
        bool running = false;
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                var result = new ProcessCheckResult(
                    running ? ProcessOutcome.Running : ProcessOutcome.NotRunning, 42);
                if (!running && firstNotRunningTicks == 0)
                    firstNotRunningTicks = time.GetTimestamp();
                return Task.FromResult(result);
            });
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.StartCallCount++;
                if (h.StartCallCount == 2)
                    secondLaunchTicks = time.GetTimestamp();
                running = true;
                return ProcessStartResult.Started();
            });

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(maxRestarts: 0, checkInterval: 1, restartDelay: 2, startupTimeout: 1));
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.StartCallCount >= 1, "the free launch to be made");
        await AdvanceUntilAsync(time, () => h.StableCount >= 1, "the initial stability window to complete");

        // The target exits shortly after its launch (t≈1.2 s, well inside the 2 s delay) and
        // the exit is detected on the next normal tick: the full delay is measured from that
        // detection.
        await AdvanceUntilAsync(time,
            () => time.GetTimestamp() - startTicks >= TimeSpan.FromSeconds(1).Ticks,
            "the target to run until t≈1.2 s");
        running = false;

        await AdvanceUntilAsync(time, () => h.StartCallCount == 2,
            "the restart to launch after the full delay from the detected exit");
        var detectionElapsedMs = (secondLaunchTicks - firstNotRunningTicks) / TimeSpan.TicksPerMillisecond;
        detectionElapsedMs.Should().BeGreaterThanOrEqualTo(2000,
            "the full RestartDelaySeconds is measured from the detected exit of the short-running target");
        detectionElapsedMs.Should().BeLessThanOrEqualTo(3100,
            "the launch fires on the first eligible normal tick at/after the deadline");
        h.Monitor.Received(2).TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());

        await controller.StopAsync();
    }

    // Required test #5: Running reappearing while a restart is pending cancels the pending
    // restart and clears its delay reference — no duplicate launch is performed.
    [Fact]
    public async Task RestartDelay_RunningReappears_CancelsPendingRestart_NoDuplicateLaunch()
    {
        using var h = new Harness();
        h.Time = new VirtualTimeProvider();
        var time = (VirtualTimeProvider)h.Time;

        long startTicks = time.GetTimestamp();
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
        var start = await controller.StartAsync(h.Request(maxRestarts: 0, checkInterval: 1, restartDelay: 2, startupTimeout: 1));
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.StartCallCount >= 1, "the free launch to be made");
        await AdvanceUntilAsync(time, () => h.StableCount >= 1, "the initial stability window to complete");

        // Exit: the restart condition is established (the delay is armed at the detection).
        running = false;
        await AdvanceUntilAsync(time, () => h.CheckCount >= 4, "the exit to be detected");
        h.StartCallCount.Should().Be(1, "no launch while the restart delay is pending");

        // Running reappears on its own while the restart is pending: the pending restart is
        // cancelled and its delay reference is cleared (§2.3 G) — no duplicate launch.
        running = true;
        await AdvanceUntilAsync(time,
            () => time.GetTimestamp() - startTicks >= TimeSpan.FromSeconds(4).Ticks,
            "normal ticks to run past the would-be restart deadline");
        h.StartCallCount.Should().Be(1, "a pending restart cancelled by Running never launches");
        h.Statuses.Should().NotContain(GuardianStatus.Error);
        h.Monitor.Received(1).TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());

        await controller.StopAsync();
    }

    // Required test #6: after a pending restart was cancelled by Running reappearing, a NEW
    // exit starts a FRESH full delay from its own detection timestamp — the old reference
    // must not survive.
    [Fact]
    public async Task RestartDelay_SubsequentExit_StartsFreshDelayFromNewDetection()
    {
        using var h = new Harness();
        h.Time = new VirtualTimeProvider();
        var time = (VirtualTimeProvider)h.Time;

        long startTicks = time.GetTimestamp();
        long firstNotRunningTicks = 0;
        long secondLaunchTicks = 0;
        bool running = false;
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                var result = new ProcessCheckResult(
                    running ? ProcessOutcome.Running : ProcessOutcome.NotRunning, 42);
                if (!running && firstNotRunningTicks == 0)
                    firstNotRunningTicks = time.GetTimestamp();
                return Task.FromResult(result);
            });
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.StartCallCount++;
                if (h.StartCallCount == 2)
                    secondLaunchTicks = time.GetTimestamp();
                running = true;
                return ProcessStartResult.Started();
            });

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(maxRestarts: 0, checkInterval: 1, restartDelay: 2, startupTimeout: 1));
        start.Started.Should().BeTrue();

        await WaitUntilAsync(() => h.StartCallCount >= 1, "the free launch to be made");
        await AdvanceUntilAsync(time, () => h.StableCount >= 1, "the initial stability window to complete");

        // First exit: the restart is armed at its detection (t≈2 s)...
        running = false;
        await AdvanceUntilAsync(time, () => h.CheckCount >= 4, "the first exit to be detected");

        // ...but Running reappears before the delay elapses: the pending restart and its
        // delay reference are cancelled (§2.3 G).
        running = true;
        await AdvanceUntilAsync(time,
            () => time.GetTimestamp() - startTicks >= TimeSpan.FromSeconds(3).Ticks,
            "the target to reappear and run");

        // Second exit: a FRESH full delay starts from the NEW detection. A stale reference
        // from the first exit would allow the launch at the old deadline (detection1 + 2 s)
        // — one full delay after the second detection is required instead.
        running = false;
        firstNotRunningTicks = 0; // re-capture the SECOND detection in the stub
        await AdvanceUntilAsync(time, () => h.StartCallCount == 2,
            "the restart to launch after the fresh full delay from the second detection");

        var detectionElapsedMs = (secondLaunchTicks - firstNotRunningTicks) / TimeSpan.TicksPerMillisecond;
        detectionElapsedMs.Should().BeGreaterThanOrEqualTo(2000,
            "the second exit starts a fresh full RestartDelaySeconds from its own detection (§2.3 G)");
        detectionElapsedMs.Should().BeLessThanOrEqualTo(3100,
            "the launch fires on the first eligible normal tick at/after the fresh deadline");
        h.Monitor.Received(2).TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());

        await controller.StopAsync();
    }

    // Test D: a genuine ProcessStartStatus.Failed charges the automatic budget exactly once,
    // updates the actual-attempt timestamp at the Failed boundary, and spaces the next attempt
    // by RestartDelaySeconds from that attempt — with no double charge on later processing.
    [Fact]
    public async Task FailedLaunch_ChargesOnce_UpdatesAttemptTimestamp_SpacesNextAttempt()
    {
        using var h = new Harness();
        h.Time = new VirtualTimeProvider();
        var time = (VirtualTimeProvider)h.Time;

        var launchTicks = new long[3];
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning));
            });
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.StartCallCount++;
                launchTicks[h.StartCallCount - 1] = time.GetTimestamp();
                return ProcessStartResult.Failed("launch failed");
            });

        var controller = h.CreateController();
        // Max=2: the free launch (uncharged) plus two budgeted attempts; delay 2 s between
        // actual attempts, check interval 1 s.
        var start = await controller.StartAsync(h.Request(maxRestarts: 2, checkInterval: 1, restartDelay: 2, startupTimeout: 1));
        start.Started.Should().BeTrue();

        // The free launch fails at t=0 (uncharged); the first budgeted attempt fires at the
        // 2 s deadline; the second at the next 2 s deadline — each spaced from the previous
        // actual attempt.
        await AdvanceUntilAsync(time, () => h.StartCallCount >= 1, "the free launch to fail at the first tick");
        await AdvanceUntilAsync(time, () => h.StartCallCount == 2, "the first budgeted attempt at the 2 s deadline");
        await AdvanceUntilAsync(time, () => h.StartCallCount == 3, "the second budgeted attempt at the next 2 s deadline");

        var firstGapMs = (launchTicks[1] - launchTicks[0]) / TimeSpan.TicksPerMillisecond;
        var secondGapMs = (launchTicks[2] - launchTicks[1]) / TimeSpan.TicksPerMillisecond;
        // The recorded TryStart timestamp may trail its anchoring tick by up to one virtual
        // step, which can push the deadline past the next tick boundary: the gap is at least
        // the full RestartDelaySeconds measured from the previous actual attempt, and at most
        // the deadline plus one normal tick — never an early launch and never a delay re-armed
        // by an observation event.
        firstGapMs.Should().BeGreaterThanOrEqualTo(2000,
            "the next attempt is never earlier than RestartDelaySeconds after the previous actual attempt");
        firstGapMs.Should().BeLessThanOrEqualTo(3100,
            "the launch fires on the first eligible normal tick — at most one tick past the deadline");
        secondGapMs.Should().BeGreaterThanOrEqualTo(2000,
            "the spacing deadline is re-armed only at the actual Failed boundary");
        secondGapMs.Should().BeLessThanOrEqualTo(3100,
            "no extra delay beyond the first eligible normal tick");

        // Exactly two budgeted attempts were charged (one each): the budget exhausts — Error,
        // no third budgeted TryStart (no double charge on later state processing).
        await AdvanceUntilAsync(time, () => controller.CurrentState.Status == GuardianStatus.Error,
            "the exhausted budget to terminate the session with Error");
        h.Monitor.Received(3).TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
    }

    // Test E: attempts blocked before the OS process-creation boundary (TryStart Missing or
    // ReadError, indeterminate adapter exception) never charge the budget, never update the
    // actual-attempt timestamp and never create an artificial RestartDelaySeconds delay — the
    // session keeps retrying on the normal cadence and recovers when an attempt succeeds.
    [Fact]
    public async Task BlockedLaunches_NoCharge_NoAttemptTimestamp_NoArtificialDelay()
    {
        using var h = new Harness();
        h.Time = new VirtualTimeProvider();
        var time = (VirtualTimeProvider)h.Time;

        long startTicks = time.GetTimestamp();
        var launchTicks = new long[4];
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning));
            });
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.StartCallCount++;
                launchTicks[h.StartCallCount - 1] = time.GetTimestamp();
                return h.StartCallCount switch
                {
                    1 => ProcessStartResult.Missing(),
                    2 => ProcessStartResult.ReadError(),
                    3 => throw new InvalidOperationException("indeterminate launch failure"),
                    _ => ProcessStartResult.Started(),
                };
            });

        var controller = h.CreateController();
        // A huge RestartDelaySeconds (10 s) would block any attempt that wrongly armed the
        // spacing clock on a blocked/indeterminate outcome; blocked attempts must instead
        // retry on every normal tick (1 s). Max=1: had any blocked attempt charged, the
        // budget gate would have terminated the session before the fourth (free) attempt.
        var start = await controller.StartAsync(h.Request(maxRestarts: 1, checkInterval: 1, restartDelay: 10, startupTimeout: 1));
        start.Started.Should().BeTrue();

        await AdvanceUntilAsync(time, () => h.StartCallCount == 4,
            "the blocked attempts to retry on the normal cadence and the fourth (free) attempt to succeed");

        // Every blocked attempt retried on the very next normal tick — no RestartDelaySeconds
        // (10 s) gap was created by any blocked/indeterminate outcome. The recorded TryStart
        // timestamp may trail its anchoring tick by up to one virtual step (the racy first-tick
        // anchor, same as the drift test), so the gap band is one check interval ± one step —
        // never the 10 s artificial delay and never a zero-gap burst.
        for (var i = 1; i < 4; i++)
        {
            var gapMs = (launchTicks[i] - launchTicks[i - 1]) / TimeSpan.TicksPerMillisecond;
            gapMs.Should().BeGreaterThanOrEqualTo(900,
                $"blocked attempt {i + 1} must not fire early (tick-cadence retries)");
            gapMs.Should().BeLessThanOrEqualTo(1100,
                $"blocked attempt {i + 1} must retry on the next normal tick, without an artificial restart delay");
        }

        // The fourth attempt's appearance window begins (the process never appears): no
        // duplicate launch while it is pending, and no Error — blocked attempts never charged.
        await AdvanceUntilAsync(time,
            () => time.GetTimestamp() - startTicks >= TimeSpan.FromSeconds(5).Ticks,
            "the appearance window to observe the never-appearing attempt");
        h.StartCallCount.Should().Be(4, "no duplicate launch while the appearance window is pending");
        h.Statuses.Should().NotContain(GuardianStatus.Error, "blocked attempts never charge the budget");

        await controller.StopAsync();
    }

    // ---------------------------------------------------------------------
    // R-AS-RECOVERY-CORRECTNESS required deterministic tests: the genuine
    // TryStart failure spaces the next attempt from the COMPLETED failed
    // attempt, the deadline is derived each cycle from the CURRENT committed
    // RestartDelaySeconds, and the terminal Error is published immediately
    // without an intermediate Restarting.
    // ---------------------------------------------------------------------

    // Test A (§2.3 C/§2.5 #7): RestartDelaySeconds = 10; the first TryStart takes 2 virtual
    // seconds and returns Started, but the process never appears. The failed startup is
    // established by the timeout observation at t=3 s (the 2 s blocking TryStart pushed the
    // attempt's own appearance deadline to t=2 s + 1 s), and the FULL delay starts from THAT
    // observation — the next attempt fires at t≈13 s, never at the attempt's completion time
    // plus the delay (t=12 s) and never without the delay at all.
    [Fact]
    public async Task RestartDelay_FailedStartup_ArmsFullDelayFromTimeoutObservation()
    {
        using var h = new Harness();
        h.Time = new VirtualTimeProvider();
        var time = (VirtualTimeProvider)h.Time;

        long startTicks = time.GetTimestamp();
        var launchTicks = new long[2];
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning));
            });
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.StartCallCount++;
                if (h.StartCallCount == 1)
                {
                    // The first (free) creation attempt consumes 2 virtual seconds of blocking
                    // TryStart — no queued wait may fire during the blocking span.
                    time.AdvanceClockOnly(TimeSpan.FromSeconds(2));
                }
                launchTicks[h.StartCallCount - 1] = time.GetTimestamp();
                return ProcessStartResult.Started();
            });

        var controller = h.CreateController();
        // Check interval 1 s, restart delay 10 s, unlimited budget; the process never appears.
        var start = await controller.StartAsync(h.Request(maxRestarts: 0, checkInterval: 1, restartDelay: 10, startupTimeout: 1));
        start.Started.Should().BeTrue();

        await AdvanceUntilAsync(time, () => h.StartCallCount == 2,
            "the restart to launch after the full spacing interval");

        // The first attempt's completion was captured AFTER TryStart returned: t=2 s, with at
        // most one scheduling step of jitter if the launch fired on the test's second Advance
        // step.
        var firstCompletionMs = (launchTicks[0] - startTicks) / TimeSpan.TicksPerMillisecond;
        firstCompletionMs.Should().BeGreaterThanOrEqualTo(2000,
            "the attempt timestamp is captured after the 2 s TryStart returned");
        firstCompletionMs.Should().BeLessThanOrEqualTo(2100,
            "the free launch happens on the first eligible tick, with at most one Advance step of jitter");

        // The failed startup is established by the timeout observation (t≈3 s = the 2 s
        // blocking TryStart + the 1 s appearance deadline); the FULL RestartDelaySeconds (10 s)
        // starts from THAT observation: the next attempt fires at t≈13-14 s — never at t=12 s
        // (the completion-anchored interpretation) and never earlier.
        var secondLaunchMs = (launchTicks[1] - startTicks) / TimeSpan.TicksPerMillisecond;
        secondLaunchMs.Should().BeGreaterThanOrEqualTo(13000,
            "the full delay is measured from the timeout observation that established the failed startup (§2.3 C), not from the TryStart completion");
        secondLaunchMs.Should().BeLessThanOrEqualTo(14100,
            "the launch fires on the first eligible normal tick at/after the deadline");
        await controller.StopAsync();
    }

    // Required test #3: a slow CheckAsync must not consume the restart delay — the delay
    // starts at the post-observation timestamp (after the check returned its result), not at
    // the pre-check tick timestamp (§2.2).
    [Fact]
    public async Task RestartDelay_SlowCheckAsync_ArmsDelayAfterResultReturned()
    {
        using var h = new Harness();
        h.Time = new VirtualTimeProvider();
        var time = (VirtualTimeProvider)h.Time;

        long startTicks = time.GetTimestamp();
        long restartLaunchTicks = 0;
        bool running = true;
        var slowed = false;
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                var pre = time.GetTimestamp();
                // The first check at/after the t=3 s tick is SLOW: it consumes 2 virtual
                // seconds of blocking time and then reports the exit. The pre-check tick
                // timestamp is t=3 s; the post-observation timestamp is t=5 s (§2.2).
                if (running && !slowed && pre >= TimeSpan.FromSeconds(3).Ticks)
                {
                    slowed = true;
                    running = false; // the slow check IS the exit detection
                    time.AdvanceClockOnly(TimeSpan.FromSeconds(2));
                    return Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning));
                }
                var outcome = running ? ProcessOutcome.Running : ProcessOutcome.NotRunning;
                return Task.FromResult(new ProcessCheckResult(outcome, 42));
            });
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.StartCallCount++;
                if (h.StartCallCount == 1)
                    restartLaunchTicks = time.GetTimestamp();
                return ProcessStartResult.Started();
            });

        var controller = h.CreateController();
        // Check interval 3 s (ticks at 0/3/6/9 s), stability 1 s (confirmed by a phase poll at
        // t=1 s), restart delay 2 s, unlimited budget.
        var start = await controller.StartAsync(h.Request(maxRestarts: 0, checkInterval: 3, restartDelay: 2, startupTimeout: 1));
        start.Started.Should().BeTrue();

        // Virtual timers fire only inside Advance, so the stability window is confirmed by
        // advancing the virtual clock (never by a real-time wait).
        await AdvanceUntilAsync(time, () => h.StableCount >= 1, "the initial stability window to complete");

        // The exit is detected by the SLOW check at the tick t=3 s that completes at t=5 s. If
        // the delay were armed with the PRE-check timestamp (t=3 s), the deadline (5 s) would
        // allow the launch on the tick at t=6 s; armed with the post-observation timestamp
        // (t=5 s, §2.2), the deadline is 7 s and the launch happens on the tick at t=9 s.
        await AdvanceUntilAsync(time, () => h.StartCallCount == 1,
            "the restart to launch after the full delay from the post-observation timestamp");
        var launchElapsedMs = (restartLaunchTicks - startTicks) / TimeSpan.TicksPerMillisecond;
        launchElapsedMs.Should().BeGreaterThanOrEqualTo(9000,
            "time spent inside CheckAsync must not consume the delay: the full wait starts at the post-observation timestamp (§2.2) — a pre-check anchoring would launch at t=6 s");
        launchElapsedMs.Should().BeLessThanOrEqualTo(9200,
            "the launch fires on the first eligible normal tick at/after the post-observation deadline");
        await controller.StopAsync();
    }

    // Required test #9: the free initial launch is never delayed by RestartDelaySeconds when
    // the target is initially absent — even a huge configured delay must not hold the first
    // launch (InitialDelaySeconds continues to govern it as before).
    [Fact]
    public async Task RestartDelay_InitialFreeLaunch_NotDelayedByRestartDelay()
    {
        using var h = new Harness();
        h.Time = new VirtualTimeProvider();
        var time = (VirtualTimeProvider)h.Time;

        long startTicks = time.GetTimestamp();
        long firstLaunchTicks = 0;
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning));
            });
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.StartCallCount++;
                if (h.StartCallCount == 1)
                    firstLaunchTicks = time.GetTimestamp();
                return ProcessStartResult.Started();
            });

        var controller = h.CreateController();
        // A huge RestartDelaySeconds must not delay the free initial launch.
        var start = await controller.StartAsync(h.Request(maxRestarts: 0, checkInterval: 1, restartDelay: 999, startupTimeout: 999));
        start.Started.Should().BeTrue();

        await AdvanceUntilAsync(time, () => h.StartCallCount == 1, "the free initial launch to fire");
        var launchElapsedMs = (firstLaunchTicks - startTicks) / TimeSpan.TicksPerMillisecond;
        launchElapsedMs.Should().BeLessThanOrEqualTo(1100,
            "the free initial launch fires on the first normal tick (at most one scheduling step of jitter) — the huge RestartDelaySeconds never delays it (§2.3 A)");
        await controller.StopAsync();
    }

    // Required test #11: an Unknown observation neither launches nor falsely establishes a
    // new absence timestamp — an already-armed restart delay is preserved across Unknown
    // cycles (§2.3 H).
    [Fact]
    public async Task RestartDelay_Unknown_DoesNotLaunch_DoesNotRearmDelay()
    {
        using var h = new Harness();
        h.Time = new VirtualTimeProvider();
        var time = (VirtualTimeProvider)h.Time;

        long startTicks = time.GetTimestamp();
        long secondLaunchTicks = 0;
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                // Tick t=0: NotRunning → the free launch fails and arms the delay (t≈0).
                // Ticks t=1 s and t=2 s: Unknown (must not re-arm, must not launch).
                // Tick t=3 s: NotRunning → the launch is allowed by the ORIGINAL arm time.
                return Task.FromResult(new ProcessCheckResult(
                    h.CheckCount is 2 or 3 ? ProcessOutcome.Unknown : ProcessOutcome.NotRunning));
            });
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.StartCallCount++;
                if (h.StartCallCount == 2)
                    secondLaunchTicks = time.GetTimestamp();
                return ProcessStartResult.Failed("launch failed");
            });

        var controller = h.CreateController();
        var start = await controller.StartAsync(h.Request(maxRestarts: 0, checkInterval: 1, restartDelay: 2, startupTimeout: 1));
        start.Started.Should().BeTrue();

        await AdvanceUntilAsync(time, () => h.StartCallCount == 2,
            "the restart to launch after the full delay from the original detection");
        var launchElapsedMs = (secondLaunchTicks - startTicks) / TimeSpan.TicksPerMillisecond;
        launchElapsedMs.Should().BeGreaterThanOrEqualTo(3000,
            "no launch on the Unknown ticks and none before the full delay from the original detection");
        launchElapsedMs.Should().BeLessThanOrEqualTo(3100,
            "an Unknown observation never re-arms the delay: the ORIGINAL detection timestamp governs the deadline (§2.3 F/H)");
        h.Monitor.Received(2).TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        await controller.StopAsync();
    }

    // Test B (§8): TryStart returns Failed after 2 virtual seconds; the budget is charged
    // exactly once and the next attempt is spaced a full RestartDelaySeconds from the Failed
    // return, until the exhausted budget terminates the session with Error.
    [Fact]
    public async Task FailedLaunch_NonZeroTryStart_SpacesFromFailedReturn_ChargedOnce_TerminalError()
    {
        using var h = new Harness();
        h.Time = new VirtualTimeProvider();
        var time = (VirtualTimeProvider)h.Time;

        long startTicks = time.GetTimestamp();
        var launchTicks = new long[3];
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning));
            });
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.StartCallCount++;
                // Every actual creation attempt consumes 2 virtual seconds of blocking
                // TryStart — no queued wait may fire during the blocking span.
                time.AdvanceClockOnly(TimeSpan.FromSeconds(2));
                launchTicks[h.StartCallCount - 1] = time.GetTimestamp();
                return ProcessStartResult.Failed("launch failed");
            });

        var controller = h.CreateController();
        // Max=2: the free launch (uncharged) plus two budgeted attempts; delay 10 s between
        // actual attempts, check interval 1 s.
        var start = await controller.StartAsync(h.Request(maxRestarts: 2, checkInterval: 1, restartDelay: 10, startupTimeout: 1));
        start.Started.Should().BeTrue();

        await AdvanceUntilAsync(time, () => h.StartCallCount == 2, "the first budgeted attempt");
        await AdvanceUntilAsync(time, () => h.StartCallCount == 3, "the second budgeted attempt");

        // launchTicks record each attempt's COMPLETION (captured after the 2 s blocking
        // TryStart returned): the completion-to-completion gap equals RestartDelaySeconds
        // (10 s, from the previous completion) plus the next attempt's own 2 s TryStart,
        // with at most one tick of jitter. A pre-TryStart anchor would show a 10 s gap.
        var firstGapMs = (launchTicks[1] - launchTicks[0]) / TimeSpan.TicksPerMillisecond;
        var secondGapMs = (launchTicks[2] - launchTicks[1]) / TimeSpan.TicksPerMillisecond;
        firstGapMs.Should().BeGreaterThanOrEqualTo(12000,
            "the next attempt is never earlier than RestartDelaySeconds after the Failed attempt returned (§3.1)");
        firstGapMs.Should().BeLessThanOrEqualTo(13000, "the launch fires on the first eligible normal tick");
        secondGapMs.Should().BeGreaterThanOrEqualTo(12000,
            "the spacing reference is re-anchored at the completed Failed attempt");
        secondGapMs.Should().BeLessThanOrEqualTo(13000, "no extra delay beyond the first eligible normal tick");

        // The budget was charged exactly once per budgeted attempt: after the second one the
        // budget is exhausted and the session terminates with Error (no fourth TryStart).
        await AdvanceUntilAsync(time, () => controller.CurrentState.Status == GuardianStatus.Error,
            "the exhausted budget to terminate the session with Error");
        h.Monitor.Received(3).TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
    }

    // Test C (§3.3/§8): the deadline is derived each cycle from the CURRENT committed
    // RestartDelaySeconds — decreasing it mid-session shortens the remaining interval (a
    // launch the old 10 s value would have held until t=10 fires at t=6); increasing it
    // afterwards extends the interval (the next attempt waits for the new 20 s deadline).
    // The change never allows a launch earlier than the newly computed deadline.
    [Fact]
    public async Task RestartDelay_ChangeMidSession_DeadlineDerivedFromCurrentCommittedValue()
    {
        using var h = new Harness();
        h.Time = new VirtualTimeProvider();
        var time = (VirtualTimeProvider)h.Time;

        long startTicks = time.GetTimestamp();
        var launchTicks = new long[3];
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning));
            });
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.StartCallCount++;
                launchTicks[h.StartCallCount - 1] = time.GetTimestamp();
                return ProcessStartResult.Failed("launch failed");
            });

        var controller = h.CreateController();
        // Max=0 (unlimited): the mid-session change alone must control the cadence.
        var start = await controller.StartAsync(h.Request(maxRestarts: 0, checkInterval: 1, restartDelay: 10, startupTimeout: 1));
        start.Started.Should().BeTrue();

        await AdvanceUntilAsync(time, () => h.StartCallCount >= 1, "the free launch to fail at the first tick");

        // Decrease at t=5 s: 10 -> 2 s. The new deadline (0 + 2 s) is already elapsed, so the
        // launch is allowed on the first normal tick after the change (t=6 s) — the old 10 s
        // deadline would have held it until t=10 s.
        await AdvanceUntilAsync(time,
            () => time.GetTimestamp() - startTicks >= TimeSpan.FromSeconds(5).Ticks,
            "virtual time to reach t=5 s");
        var decrease = await controller.CommitSettingsAsync(
            CommittedSettingsSnapshot.From(new AppSettings
            {
                MaxRestartAttempts = 0,
                CheckIntervalSeconds = 1,
                RestartDelaySeconds = 2,
                StartupVerificationTimeoutSeconds = 1,
            }), h.TargetPath);
        decrease.Saved.Should().BeTrue();

        await AdvanceUntilAsync(time, () => h.StartCallCount == 2, "the restart on the first tick after the decrease");
        var afterDecreaseMs = (launchTicks[1] - startTicks) / TimeSpan.TicksPerMillisecond;
        afterDecreaseMs.Should().BeGreaterThanOrEqualTo(6000,
            "the launch is allowed only on a normal tick (the change was committed at t=5 s)");
        afterDecreaseMs.Should().BeLessThanOrEqualTo(6100,
            "the decreased setting shortens the interval: the launch fires at the first normal tick (t=6 s), not at the old t=10 s deadline");

        // Increase at t=7 s: 2 -> 20 s. The previous attempt completed at t=6 s; the new
        // deadline (6 + 20 = 26 s) extends the wait — no launch before it despite the old
        // 2 s interval having elapsed many times over.
        await AdvanceUntilAsync(time,
            () => time.GetTimestamp() - startTicks >= TimeSpan.FromSeconds(7).Ticks,
            "virtual time to reach t=7 s");
        var increase = await controller.CommitSettingsAsync(
            CommittedSettingsSnapshot.From(new AppSettings
            {
                MaxRestartAttempts = 0,
                CheckIntervalSeconds = 1,
                RestartDelaySeconds = 20,
                StartupVerificationTimeoutSeconds = 1,
            }), h.TargetPath);
        increase.Saved.Should().BeTrue();

        await AdvanceUntilAsync(time,
            () => time.GetTimestamp() - startTicks >= TimeSpan.FromSeconds(25).Ticks,
            "checks to keep flowing while the extended interval is pending");
        h.StartCallCount.Should().Be(2, "no launch before the newly computed (extended) deadline");

        await AdvanceUntilAsync(time, () => h.StartCallCount == 3, "the restart at the extended deadline");
        var afterIncreaseMs = (launchTicks[2] - launchTicks[1]) / TimeSpan.TicksPerMillisecond;
        afterIncreaseMs.Should().BeGreaterThanOrEqualTo(20000,
            "the increased setting extends the interval from the previous completed attempt (§3.3)");
        afterIncreaseMs.Should().BeLessThanOrEqualTo(21100,
            "the launch fires on the first eligible normal tick at the extended deadline");

        await controller.StopAsync();
    }

    // Test D (§4.4/§8): budget exhaustion — no intermediate Restarting is published inside the
    // terminal sequence, the Error carries its final reason on the FIRST display (before the
    // disabled-save completes), and a successful save preserves the reason with SaveFailed=false.
    [Fact]
    public async Task Exhaustion_ErrorWithoutIntermediateRestarting_PublishedBeforeParkedSave()
    {
        using var h = new Harness();
        h.Time = new VirtualTimeProvider();
        var time = (VirtualTimeProvider)h.Time;

        // The launches succeed but the process never appears: both attempts die at the
        // appearance deadline, and the second one exhausts the Max=1 budget.
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => { h.StartCallCount++; return ProcessStartResult.Started(); });

        var published = new ConcurrentQueue<GuardianStatus>();
        var controller = h.CreateController();
        controller.StateChanged += (_, s) => published.Enqueue(s.Status);

        // Save #1 (Start's enabled=true persist) succeeds; the fatal settlement save parks.
        var persistedEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePersist = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveCalls = 0;
        h.Config.SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref saveCalls) == 2)
                {
                    persistedEntered.TrySetResult(true);
                    return releasePersist.Task;
                }
                return Task.CompletedTask;
            });

        var start = await controller.StartAsync(h.Request(maxRestarts: 1, checkInterval: 1, restartDelay: 1, startupTimeout: 1));
        start.Started.Should().BeTrue();

        await AdvanceUntilAsync(time, () => h.StartCallCount == 2,
            "the single budgeted restart to launch after the free attempt's appearance timeout");

        // The budgeted attempt also never appears: the budget is exhausted on that decision
        // cycle — the terminal Error is published with no additional Restarting state between
        // the previous legitimate publications and the Error. The virtual clock must advance
        // for the session loop to run the exhausting cycle (virtual timers fire only in
        // Advance), hence AdvanceUntilAsync rather than WaitUntilAsync.
        await AdvanceUntilAsync(time, () => persistedEntered.Task.IsCompleted,
            "the fatal settlement save to be entered (the session task has already returned)");

        // While the save is parked, the Error is ALREADY published with its final reason:
        // the first display never waits for the persistence I/O (§4.2/§4.3).
        controller.CurrentState.Status.Should().Be(GuardianStatus.Error,
            "the terminal Error is published before the disabled-save completes (§4.2)");
        controller.CurrentState.Reason.Should().Contain("exhausted",
            "the error card receives its final reason on the first display");
        controller.CurrentState.SaveFailed.Should().BeFalse("the parked save has not completed yet");

        // Exactly one Restarting was published in the whole trace — the legitimate one for the
        // budgeted attempt while the budget still allowed it; the exhausted decision cycle
        // itself published no Restarting before the Error (§4.2).
        var statuses = published.ToArray();
        statuses.Count(s => s == GuardianStatus.Restarting).Should().Be(1,
            "only the budgeted attempt's own Restarting is published; the terminal cycle skips it");
        statuses.Last().Should().Be(GuardianStatus.Error,
            "the terminal Error is the last published state while the save is parked");

        var reasonWhileParked = controller.CurrentState.Reason;
        releasePersist.TrySetResult(true);
        await WaitUntilAsync(() => controller.CurrentState.SaveFailed is false
            && h.Config.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IConfigurationStore.SaveAsync)),
            "the parked disabled-save to complete");

        // The successful save never changes the already published terminal reason.
        controller.CurrentState.Status.Should().Be(GuardianStatus.Error);
        controller.CurrentState.Reason.Should().Be(reasonWhileParked,
            "a successful save preserves the original Error reason (§4.3)");
        controller.CurrentState.SaveFailed.Should().BeFalse("the save completed successfully");
    }

    // Test E (§4.3/§8): Stop during the terminal (budget-exhausted) settlement wins — the
    // settlement holds the operation lock while its save is parked, the Stop linearizes after
    // it, and the stale Error is never the final published state.
    [Fact]
    public async Task Exhaustion_RaceStop_DuringTerminalSettlement_StopWins_NoStaleError()
    {
        using var h = new Harness();
        h.Time = new VirtualTimeProvider();
        var time = (VirtualTimeProvider)h.Time;

        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => { h.StartCallCount++; return ProcessStartResult.Failed("launch failed"); });

        var published = new ConcurrentQueue<GuardianStatus>();
        var controller = h.CreateController();
        controller.StateChanged += (_, s) => published.Enqueue(s.Status);

        // Save #1 (Start's enabled=true persist) succeeds; the fatal settlement save parks.
        var persistedEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePersist = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveCalls = 0;
        h.Config.SaveAsync(Arg.Any<AppSettings>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref saveCalls) == 2)
                {
                    persistedEntered.TrySetResult(true);
                    return releasePersist.Task;
                }
                return Task.CompletedTask;
            });

        var start = await controller.StartAsync(h.Request(maxRestarts: 1, checkInterval: 1, restartDelay: 1, startupTimeout: 1));
        start.Started.Should().BeTrue();

        // Free launch Failed at t=0 (uncharged); the budgeted attempt fails at t=1 and exhausts.
        await AdvanceUntilAsync(time, () => persistedEntered.Task.IsCompleted,
            "the terminal settlement to publish the Error and park its save");

        controller.CurrentState.Status.Should().Be(GuardianStatus.Error,
            "the Error is published before the parked settlement save");

        // Stop is pending on the operation lock held by the parked settlement.
        var stopTask = controller.StopAsync();
        releasePersist.TrySetResult(true);

        var stop = await stopTask;
        stop.RuntimeStopped.Should().BeTrue();

        await WaitUntilAsync(() => controller.CurrentState.Status == GuardianStatus.Stopped,
            "the final state to be Stopped after the Stop linearizes after the settlement");
        controller.CurrentState.Status.Should().Be(GuardianStatus.Stopped);
        published.Last().Should().Be(GuardianStatus.Stopped,
            "the stale terminal Error is never the final published state after a Stop");
    }

    // Test E (§4.3/§8): Start during the terminal (budget-exhausted) settlement begins a fresh
    // session only after the settlement settles, and the stale Error never overwrites the new
    // session's state; the new session re-persists IsMonitoringEnabled=true.
    [Fact]
    public async Task Exhaustion_RaceStart_DuringTerminalSettlement_NewSessionStartsClean()
    {
        using var h = new Harness();
        h.Time = new VirtualTimeProvider();
        var time = (VirtualTimeProvider)h.Time;

        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => { h.StartCallCount++; return ProcessStartResult.Failed("launch failed"); });

        var published = new ConcurrentQueue<GuardianStatus>();
        var controller = h.CreateController();
        controller.StateChanged += (_, s) => published.Enqueue(s.Status);

        var persistedEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePersist = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var savedDocs = new List<AppSettings>();
        var saveCalls = 0;
        h.Config.SaveAsync(Arg.Do<AppSettings>(d => savedDocs.Add(d)), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref saveCalls) == 2)
                {
                    persistedEntered.TrySetResult(true);
                    return releasePersist.Task;
                }
                return Task.CompletedTask;
            });

        var start = await controller.StartAsync(h.Request(maxRestarts: 1, checkInterval: 1, restartDelay: 1, startupTimeout: 1));
        start.Started.Should().BeTrue();

        await AdvanceUntilAsync(time, () => persistedEntered.Task.IsCompleted,
            "the terminal settlement to publish the Error and park its save");

        // A new Start is pending on the operation lock held by the parked settlement.
        var startTask = controller.StartAsync(h.Request(maxRestarts: 1, checkInterval: 1, restartDelay: 1, startupTimeout: 1));
        releasePersist.TrySetResult(true);

        var result = await startTask;
        result.Started.Should().BeTrue("a new session may start once the previous settlement has settled");

        await WaitUntilAsync(
            () => controller.CurrentState.Status is GuardianStatus.Waiting
                or GuardianStatus.Monitoring
                or GuardianStatus.Restarting,
            "the new session to be active");
        published.Last().Should().BeOneOf(
            new[] { GuardianStatus.Waiting, GuardianStatus.Monitoring, GuardianStatus.Restarting },
            "the stale terminal Error from the old settlement must never be the final published state");
        savedDocs.Last().IsMonitoringEnabled.Should().BeTrue(
            "the new session re-persists IsMonitoringEnabled=true after the old disabled write");
    }

    // §4.4: a reliable NotRunning with the budget still permitting the restart still publishes
    // the user-visible Restarting before the launch — the immediate-Error change only removes
    // the intermediate state for already-terminal cycles, never the normal restart feedback.
    // Publish coalesces CONSECUTIVE identical states, so two back-to-back restarts on an
    // always-NotRunning trace may surface as one Restarting state; the assertion therefore
    // checks that a Restarting was visibly published BEFORE each budgeted launch, captured
    // inside the TryStart stub at the launch boundary itself.
    [Fact]
    public async Task Loss_WithBudgetRemaining_PublishesRestarting_BeforeLaunch()
    {
        using var h = new Harness();
        h.Time = new VirtualTimeProvider();
        var time = (VirtualTimeProvider)h.Time;

        var restartingBeforeBudgetedLaunch = new bool[3];
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning));
            });
        h.Monitor.TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.StartCallCount++;
                restartingBeforeBudgetedLaunch[h.StartCallCount - 1] =
                    h.Statuses.Contains(GuardianStatus.Restarting);
                return ProcessStartResult.Failed("launch failed");
            });

        var published = new ConcurrentQueue<GuardianStatus>();
        var controller = h.CreateController();
        controller.StateChanged += (_, s) => published.Enqueue(s.Status);

        // Max=2: the free launch is uncharged; each of the two budgeted attempts is preceded
        // by a user-visible Restarting and charged once.
        var start = await controller.StartAsync(h.Request(maxRestarts: 2, checkInterval: 1, restartDelay: 1, startupTimeout: 1));
        start.Started.Should().BeTrue();

        await AdvanceUntilAsync(time, () => h.StartCallCount == 2, "the first budgeted attempt");
        await AdvanceUntilAsync(time, () => h.StartCallCount == 3, "the second budgeted attempt");

        // The free launch never shows Restarting; every budgeted launch does.
        restartingBeforeBudgetedLaunch[0].Should().BeFalse(
            "the free initial launch is never announced as a restart");
        restartingBeforeBudgetedLaunch[1].Should().BeTrue(
            "the first budgeted restart publishes Restarting before the launch (§4.4)");
        restartingBeforeBudgetedLaunch[2].Should().BeTrue(
            "the second budgeted restart publishes Restarting before the launch (§4.4)");

        // The exhausted budget terminates the session with Error; no third budgeted launch.
        await AdvanceUntilAsync(time, () => controller.CurrentState.Status == GuardianStatus.Error,
            "the exhausted budget to terminate the session with Error");
        published.Last().Should().Be(GuardianStatus.Error, "the exhausted budget terminates the session");
        h.Monitor.Received(3).TryStart(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
    }
}
