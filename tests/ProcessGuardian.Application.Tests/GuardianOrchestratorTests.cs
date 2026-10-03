using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ProcessGuardian.Application.Services;
using ProcessGuardian.Domain.Entities;
using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Domain.Interfaces;
using ProcessGuardian.Domain.ValueObjects;
using Xunit;

namespace ProcessGuardian.Application.Tests;

public class GuardianOrchestratorTests
{
    private const int TimeoutMs = 10000;

    private sealed class Harness : IDisposable
    {
        // A real on-disk .exe is required: ProcessTarget.TryCreate (FR-16) rejects
        // paths whose file does not exist before any monitor call happens.
        public string TargetPath { get; }

        public AppSettings Settings { get; }

        public IProcessMonitor Monitor { get; } = Substitute.For<IProcessMonitor>();
        public IConfigurationStore Config { get; } = Substitute.For<IConfigurationStore>();
        public INotificationService Notifications { get; } = Substitute.For<INotificationService>();
        public IStatusReporter Status { get; } = Substitute.For<IStatusReporter>();
        public ITargetIntegrityService Integrity { get; } = Substitute.For<ITargetIntegrityService>();

        public ConcurrentBag<GuardianStatus> Statuses { get; } = new();
        public ConcurrentBag<string> ToastTitles { get; } = new();
        public int LoadCount { get; set; }
        public int CheckCount { get; set; }

        public Harness()
        {
            var dir = Path.Combine(Path.GetTempPath(), "ProcessGuardian.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            TargetPath = Path.Combine(dir, "target.exe");
            File.WriteAllText(TargetPath, string.Empty);

            Settings = new AppSettings
            {
                IsMonitoringEnabled = true,
                TargetProcessPath = TargetPath,
                InitialDelaySeconds = 0,
                CheckIntervalSeconds = 1,
                RestartDelaySeconds = 1,
                StartupVerificationTimeoutSeconds = 1,
                MaxRestartAttempts = 5,
            };

            Config.LoadAsync(Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    LoadCount++;
                    return Task.FromResult(Settings);
                });

            Status.Report(Arg.Do<GuardianStatus>(s => Statuses.Add(s)));

            Notifications.ShowAsync(
                    Arg.Do<string>(t => ToastTitles.Add(t)),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
        }

        public GuardianOrchestrator CreateOrchestrator() =>
            new(Monitor, Config, Notifications, NullLogger<GuardianOrchestrator>.Instance, Status, Integrity);

        public static async Task RunAsync(GuardianOrchestrator orchestrator, CancellationTokenSource cts, Func<Task> body)
        {
            await orchestrator.StartAsync(cts.Token);
            try
            {
                await body();
            }
            finally
            {
                cts.Cancel();
                await orchestrator.StopAsync(CancellationToken.None);
                orchestrator.Dispose();
            }
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

    [Fact]
    public async Task TargetRunning_NoRestart_StatusMonitoring()
    {
        var h = new Harness();
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.Running, 42)));

        using var cts = new CancellationTokenSource();
        var orchestrator = h.CreateOrchestrator();
        await Harness.RunAsync(orchestrator, cts, async () =>
        {
            await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Monitoring),
                "Status Monitoring to be reported");
        });

        await h.Monitor.DidNotReceive().StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        h.Statuses.Should().Contain(GuardianStatus.Monitoring);
    }

    [Fact]
    public async Task TargetMissing_RestartTriggered_RecoveryToast_StatusMonitoring()
    {
        var h = new Harness();
        // Target is running initially, then goes missing: the recovery is a real
        // budgeted restart (not the free initial launch), so a Recovered toast fires.
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

        using var cts = new CancellationTokenSource();
        var orchestrator = h.CreateOrchestrator();
        await Harness.RunAsync(orchestrator, cts, async () =>
        {
            // Let the first check see the target running (monitoring established).
            await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Monitoring) && checkCount >= 1,
                "Initial Monitoring status");
            running = false;
            await WaitUntilAsync(() => h.ToastTitles.Any(t => t.Contains("Recovered")),
                "Recovered toast to be shown");
        });

        await h.Monitor.Received(1).StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        await h.Monitor.Received(1).VerifyStartupAsync(Arg.Any<ProcessTarget>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        h.Statuses.Should().Contain(GuardianStatus.Monitoring);
        h.Statuses.Should().NotContain(GuardianStatus.Restarting);
        h.ToastTitles.Should().Contain(t => t.Contains("Recovered"));
    }

    [Fact]
    public async Task Exhaustion_SetsError_DisablesMonitoring_NoAutoRestart()
    {
        var h = new Harness();
        h.Settings.MaxRestartAttempts = 3;
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));
        h.Monitor.StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        using var cts = new CancellationTokenSource();
        var orchestrator = h.CreateOrchestrator();
        await Harness.RunAsync(orchestrator, cts, async () =>
        {
            await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Error),
                "Status Error to be reported");
            await WaitUntilAsync(() => h.ToastTitles.Any(t => t.Contains("Failed") || t.Contains("attempt")),
                "Failed/exhaustion toast to be shown");
            await WaitUntilAsync(() => h.Config.ReceivedCalls().Any(c =>
                c.GetMethodInfo().Name == nameof(IConfigurationStore.SaveAsync)),
                "SaveAsync to be called to persist disabled state");
        });

        h.Statuses.Should().Contain(GuardianStatus.Error);
        h.ToastTitles.Should().Contain(t => t.Contains("Failed") || t.Contains("attempt"));
        h.Settings.IsMonitoringEnabled.Should().BeFalse();
        // First Start is the free initial launch; then Max=3 budgeted attempts, then exhaustion.
        await h.Monitor.Received(4).StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NewStartAfterError_RestoresFullAttemptBudget()
    {
        var h = new Harness();
        h.Settings.MaxRestartAttempts = 1;
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));
        h.Monitor.StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        using var cts = new CancellationTokenSource();
        var orchestrator = h.CreateOrchestrator();
        await Harness.RunAsync(orchestrator, cts, async () =>
        {
            // First session: one free initial launch + one budgeted attempt (Max=1), then Error.
            await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Error) && !h.Settings.IsMonitoringEnabled,
                "First session to exhaust and disable monitoring");
            await h.Monitor.Received(2).StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());

            // User Start again: full budget restored -> one free launch + one attempt (Max=1).
            // Let the exhausted cycle exit fully so the next Start grants a free launch.
            await Task.Delay(1000);
            h.Monitor.ClearReceivedCalls();
            h.Settings.IsMonitoringEnabled = true;
            await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Error) && !h.Settings.IsMonitoringEnabled,
                "Second session to exhaust and disable monitoring");

            await h.Monitor.Received(2).StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task FirstStart_ConsumesNoAttempts_StatusMonitoring_NoExhaustion()
    {
        var h = new Harness();
        h.Settings.MaxRestartAttempts = 1;
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));
        h.Monitor.StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        h.Monitor.VerifyStartupAsync(Arg.Any<ProcessTarget>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        using var cts = new CancellationTokenSource();
        var orchestrator = h.CreateOrchestrator();
        await Harness.RunAsync(orchestrator, cts, async () =>
        {
            await WaitUntilAsync(() => h.Monitor.ReceivedCalls().Any(c =>
                    c.GetMethodInfo().Name == nameof(IProcessMonitor.StartAsync)),
                "First StartAsync to be called");
            await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Monitoring),
                "Monitoring status after initial launch");
        });

        await h.Monitor.Received(1).StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        h.Settings.IsMonitoringEnabled.Should().BeTrue();
        h.Statuses.Should().NotContain(GuardianStatus.Error);
    }

    [Fact]
    public async Task FirstStart_Free_ThenBudgetConsumed_Error()
    {
        var h = new Harness();
        h.Settings.MaxRestartAttempts = 1;
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));
        h.Monitor.StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        using var cts = new CancellationTokenSource();
        var orchestrator = h.CreateOrchestrator();
        await Harness.RunAsync(orchestrator, cts, async () =>
        {
            await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Error) && !h.Settings.IsMonitoringEnabled,
                "Exhaustion after free initial launch plus one budgeted attempt");
        });

        // Exactly one free initial launch + one budgeted attempt (Max=1), then Error; no third StartAsync.
        await h.Monitor.Received(2).StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        h.Statuses.Should().Contain(GuardianStatus.Error);
    }

    [Fact]
    public async Task ProtectedProcess_TargetNotRunning_RestartTriggered()
    {
        var h = new Harness();
        // Target is running initially, then goes missing: recovery is a real budgeted
        // restart (not the free initial launch), so a Recovered toast fires.
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

        using var cts = new CancellationTokenSource();
        var orchestrator = h.CreateOrchestrator();
        await Harness.RunAsync(orchestrator, cts, async () =>
        {
            await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Monitoring) && checkCount >= 1,
                "Initial Monitoring status");
            running = false;
            await WaitUntilAsync(() => h.ToastTitles.Any(t => t.Contains("Recovered")),
                "Recovered toast to be shown");
        });

        await h.Monitor.Received(1).StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SettingsChangeDuringCycle_NewSettingsPickedUpNextIteration()
    {
        var h = new Harness();
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                if (h.CheckCount == 1)
                {
                    h.Settings.IsMonitoringEnabled = false;
                }
                return Task.FromResult(new ProcessCheckResult(ProcessOutcome.Running));
            });

        using var cts = new CancellationTokenSource();
        var orchestrator = h.CreateOrchestrator();
        await Harness.RunAsync(orchestrator, cts, async () =>
        {
            await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Stopped),
                "Status Stopped to be reported after monitoring disabled");
        });

        h.LoadCount.Should().BeGreaterThan(1);
        h.Statuses.Should().Contain(GuardianStatus.Stopped);
    }

    [Fact]
    public async Task StartAsyncThrows_LoopStaysAlive()
    {
        var h = new Harness();
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning));
            });
        h.Monitor.StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<bool>(new InvalidOperationException("boom")));

        using var cts = new CancellationTokenSource();
        var orchestrator = h.CreateOrchestrator();
        await Harness.RunAsync(orchestrator, cts, async () =>
        {
            await WaitUntilAsync(() => h.CheckCount >= 3,
                "CheckAsync to be called multiple times after StartAsync threw");
        });

        h.CheckCount.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task ShowAsyncThrows_RestartStillSucceeds_StatusMonitoring()
    {
        var h = new Harness();
        bool started = false;
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(started ? ProcessOutcome.Running : ProcessOutcome.NotRunning)));
        h.Monitor.StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => { started = true; return Task.FromResult(true); });
        h.Monitor.VerifyStartupAsync(Arg.Any<ProcessTarget>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        h.Notifications.ClearReceivedCalls();
        h.Notifications.ShowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("toast fail")));

        using var cts = new CancellationTokenSource();
        var orchestrator = h.CreateOrchestrator();
        await Harness.RunAsync(orchestrator, cts, async () =>
        {
            await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Monitoring)
                && h.Monitor.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IProcessMonitor.StartAsync)),
                "Monitoring status after restart despite toast failure");
        });

        await h.Monitor.Received(1).StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        h.Statuses.Should().Contain(GuardianStatus.Monitoring);
    }

    [Fact]
    public async Task HugeCheckInterval_DoesNotBusyLoop()
    {
        var h = new Harness();
        // Upper bound is intentionally unclamped at the entity level. Before the wait cap,
        // the int cast overflowed and CheckAsync was invoked thousands of times per second.
        h.Settings.CheckIntervalSeconds = int.MaxValue;
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromResult(new ProcessCheckResult(ProcessOutcome.Running, 42));
            });

        using var cts = new CancellationTokenSource();
        var orchestrator = h.CreateOrchestrator();
        await Harness.RunAsync(orchestrator, cts, async () =>
        {
            await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Monitoring),
                "Monitoring status after the first check");
            await Task.Delay(700);
            h.CheckCount.Should().Be(1);
        });

        h.CheckCount.Should().Be(1);
    }

    [Fact]
    public async Task CheckAsyncThrows_StatusError_LoopContinues()
    {
        var h = new Harness();
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.CheckCount++;
                return Task.FromException<ProcessCheckResult>(new InvalidOperationException("check boom"));
            });

        using var cts = new CancellationTokenSource();
        var orchestrator = h.CreateOrchestrator();
        await Harness.RunAsync(orchestrator, cts, async () =>
        {
            await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Error),
                "Status Error to be reported");
            await WaitUntilAsync(() => h.CheckCount >= 3,
                "CheckAsync to be called again after error");
        });

        h.Statuses.Should().Contain(GuardianStatus.Error);
        h.CheckCount.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task HashMismatch_LaunchBlocked_MonitoringDisabled_TargetChangedToast()
    {
        var h = new Harness();
        h.Settings.TargetHashSha256 = "AA11";
        h.Integrity.ComputeHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("BB"));
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));
        h.Monitor.StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        h.Monitor.VerifyStartupAsync(Arg.Any<ProcessTarget>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        using var cts = new CancellationTokenSource();
        var orchestrator = h.CreateOrchestrator();
        await Harness.RunAsync(orchestrator, cts, async () =>
        {
            await WaitUntilAsync(() => h.ToastTitles.Contains("Target Changed"),
                "Target Changed toast to be shown");
            await WaitUntilAsync(() => h.Config.ReceivedCalls().Any(c =>
                c.GetMethodInfo().Name == nameof(IConfigurationStore.SaveAsync)),
                "SaveAsync to persist the disabled state");
        });

        await h.Monitor.DidNotReceive().StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        h.Settings.IsMonitoringEnabled.Should().BeFalse();
        h.Statuses.Should().Contain(GuardianStatus.Error);
        h.ToastTitles.Should().Contain("Target Changed");
    }

    [Fact]
    public async Task HashReadFailure_BlocksLaunch_FailClosed()
    {
        var h = new Harness();
        h.Settings.TargetHashSha256 = "AA";
        h.Integrity.ComputeHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));

        using var cts = new CancellationTokenSource();
        var orchestrator = h.CreateOrchestrator();
        await Harness.RunAsync(orchestrator, cts, async () =>
        {
            await WaitUntilAsync(() => h.ToastTitles.Contains("Target Changed"),
                "Target Changed toast after unreadable hash");
        });

        await h.Monitor.DidNotReceive().StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        h.Settings.IsMonitoringEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task HashMatches_LaunchProceeds_StatusMonitoring()
    {
        var h = new Harness();
        h.Settings.TargetHashSha256 = "AA";
        h.Integrity.ComputeHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("aa"));
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));
        h.Monitor.StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        h.Monitor.VerifyStartupAsync(Arg.Any<ProcessTarget>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        using var cts = new CancellationTokenSource();
        var orchestrator = h.CreateOrchestrator();
        await Harness.RunAsync(orchestrator, cts, async () =>
        {
            await WaitUntilAsync(() => h.Monitor.ReceivedCalls().Any(c =>
                    c.GetMethodInfo().Name == nameof(IProcessMonitor.StartAsync)),
                "StartAsync to be called when the hash matches");
        });

        await h.Integrity.Received().ComputeHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await h.Monitor.Received(1).StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
        h.Statuses.Should().Contain(GuardianStatus.Monitoring);
    }

    [Fact]
    public async Task EmptyHash_CheckSkipped_LaunchProceeds()
    {
        var h = new Harness();
        h.Monitor.CheckAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ProcessCheckResult(ProcessOutcome.NotRunning)));
        h.Monitor.StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        h.Monitor.VerifyStartupAsync(Arg.Any<ProcessTarget>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        using var cts = new CancellationTokenSource();
        var orchestrator = h.CreateOrchestrator();
        await Harness.RunAsync(orchestrator, cts, async () =>
        {
            await WaitUntilAsync(() => h.Statuses.Contains(GuardianStatus.Monitoring),
                "Monitoring status after initial launch without a pin");
        });

        await h.Integrity.DidNotReceive().ComputeHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await h.Monitor.Received(1).StartAsync(Arg.Any<ProcessTarget>(), Arg.Any<CancellationToken>());
    }
}
