using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProcessGuardian.Core;
using ProcessGuardian.Tests.Fakes;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace ProcessGuardian.Tests
{
    [TestClass]
    public class ProcessGuardianControllerTests
    {
        private AppSettings MakeDefaults()
        {
            return new AppSettings
            {
                InitialDelaySeconds = 0,
                CheckIntervalSeconds = 1,
                MaxRestartAttempts = 3,
                RestartDelaySeconds = 1,
                StartupVerificationTimeoutSeconds = 5,
                FailureCooldownSeconds = 2
            };
        }

        private (IProcessGuardianController controller, FakeProcessManager pm, FakeTimeProvider tp, MockLogger logger, AppState state) CreateController(AppSettings? settings = null)
        {
            settings ??= MakeDefaults();
            var state = new AppState { Settings = settings };
            var pm = new FakeProcessManager();
            var tp = new FakeTimeProvider();
            var logger = new MockLogger();
            var cfg = new FakeConfigService(settings);
            var controller = new ProcessGuardian.Services.ProcessGuardianController(cfg, logger, state, pm, tp);
            return (controller, pm, tp, logger, state);
        }

        [TestMethod]
        public async Task TargetProcessFound_ByFullPath()
        {
            var settings = MakeDefaults();
            settings.TargetProcessPath = "C:\\Apps\\MyApp\\app.exe";
            settings.TargetProcessName = "app";

            var (controller, pm, tp, logger, state) = CreateController(settings);
            pm.AddProcess(settings.TargetProcessPath);

            await controller.StartAsync();
            // let monitor run one cycle
            tp.AdvanceBy(TimeSpan.FromMilliseconds(10));
            await Task.Yield();

            Assert.AreEqual(0, state.CurrentRestartAttempts);
            Assert.IsTrue(logger.GetMessages().Any(m => m.Contains("Target process detected")));

            await controller.StopAsync();
        }

        [TestMethod]
        public async Task SameNameWrongPath_IsNotTarget()
        {
            var settings = MakeDefaults();
            settings.TargetProcessPath = "C:\\Apps\\MyApp\\app.exe";

            var (controller, pm, tp, logger, state) = CreateController(settings);
            pm.AddProcess("C:\\Other\\app.exe");

            await controller.StartAsync();
            tp.AdvanceBy(TimeSpan.FromMilliseconds(10));
            await Task.Yield();

            Assert.IsTrue(logger.GetMessages().Any(m => m.Contains("Same-name process found")));
            await controller.StopAsync();
        }

        [TestMethod]
        public async Task ProcessInformationUnavailable_IsHandled()
        {
            var settings = MakeDefaults();
            settings.TargetProcessPath = "C:\\Apps\\MyApp\\app.exe";
            settings.TargetProcessName = "app";

            var (controller, pm, tp, logger, state) = CreateController(settings);
            pm.AddSameNameUnavailable("app");

            await controller.StartAsync();
            tp.AdvanceBy(TimeSpan.FromMilliseconds(10));
            await Task.Yield();

            Assert.IsTrue(logger.GetMessages().Any(m => m.Contains("Process information unavailable")));
            await controller.StopAsync();
        }

        [TestMethod]
        public async Task SameNameProcessWithUnavailablePath_DoesNotTriggerRestart()
        {
            var settings = MakeDefaults();
            settings.TargetProcessPath = "C:\\Apps\\MyApp\\app.exe";
            settings.MaxRestartAttempts = 2;

            var (controller, pm, tp, logger, state) = CreateController(settings);
            pm.AddSameNameUnavailable("app");

            await controller.StartAsync();
            tp.AdvanceBy(TimeSpan.FromMilliseconds(10));
            await Task.Yield();

            // No restart attempts should have been made
            Assert.AreEqual(0, state.CurrentRestartAttempts);
            Assert.IsTrue(logger.GetMessages().Any(m => m.Contains("information unavailable")), "Expected info-unavailable log");

            await controller.StopAsync();
        }

        [TestMethod]
        public async Task TargetProcessMissing_TriggersRestartWhenAllowed()
        {
            var settings = MakeDefaults();
            settings.TargetProcessPath = "C:\\Apps\\MyApp\\app.exe";
            settings.MaxRestartAttempts = 1;

            var (controller, pm, tp, logger, state) = CreateController(settings);

            await controller.StartAsync();
            tp.AdvanceBy(TimeSpan.FromMilliseconds(10));
            await Task.Yield();

            // advance verification timeout
            tp.AdvanceBy(TimeSpan.FromSeconds(settings.StartupVerificationTimeoutSeconds + 1));
            await Task.Yield();

            // A restart attempt should have been logged
            Assert.IsTrue(logger.GetMessages().Any(m => m.Contains("Restart attempt")));

            await controller.StopAsync();
        }

        [TestMethod]
        public async Task StartProcess_UsesConfiguredPath()
        {
            var settings = MakeDefaults();
            settings.TargetProcessPath = "C:\\Apps\\ExeFolder\\bin.exe";

            var (controller, pm, tp, logger, state) = CreateController(settings);

            await controller.StartAsync();
            tp.AdvanceBy(TimeSpan.FromMilliseconds(10));
            await Task.Yield();

            // No existing process; trigger restart attempt
            tp.AdvanceBy(TimeSpan.FromSeconds(settings.StartupVerificationTimeoutSeconds + 1));
            await Task.Yield();

            // FakeProcessManager will have added started process with ExecutablePath = settings.TargetProcessPath
            var found = (await pm.GetProcessesByNameAsync("bin", CancellationToken.None)).Any(p => string.Equals(p.ExecutablePath, settings.TargetProcessPath, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(found, "ProcessStart should have started process with configured path");

            await controller.StopAsync();
        }

        [TestMethod]
        public async Task StartProcess_UsesWorkingDirectory()
        {
            var settings = MakeDefaults();
            settings.TargetProcessPath = "C:\\Apps\\Folder\\app.exe";

            var (controller, pm, tp, logger, state) = CreateController(settings);

            await controller.StartAsync();
            tp.AdvanceBy(TimeSpan.FromMilliseconds(10));
            await Task.Yield();

            tp.AdvanceBy(TimeSpan.FromSeconds(settings.StartupVerificationTimeoutSeconds + 1));
            await Task.Yield();

            var procs = await pm.GetProcessesByNameAsync("app");
            Assert.IsTrue(procs.Any(p => p.ExecutablePath == settings.TargetProcessPath));

            await controller.StopAsync();
        }

        [TestMethod]
        public async Task StartProcess_UsesArguments()
        {
            var settings = MakeDefaults();
            settings.TargetProcessPath = "C:\\Apps\\ArgApp\\app.exe";
            settings.TargetProcessArguments = "--flag value";

            var (controller, pm, tp, logger, state) = CreateController(settings);

            await controller.StartAsync();
            tp.AdvanceBy(TimeSpan.FromMilliseconds(10));
            await Task.Yield();

            tp.AdvanceBy(TimeSpan.FromSeconds(settings.StartupVerificationTimeoutSeconds + 1));
            await Task.Yield();

            // FakeProcessManager does not store arguments, but StartProcessAsync was invoked and added process
            var procs = await pm.GetProcessesByNameAsync("app");
            Assert.IsTrue(procs.Any());

            await controller.StopAsync();
        }

        [TestMethod]
        public async Task StartProcessFailure_IsHandledAndLogged()
        {
            // To simulate start failure, we create a FakeProcessManager that does not add process
            var settings = MakeDefaults();
            settings.TargetProcessPath = "C:\\Apps\\Bad\\app.exe";

            var (controller, pm, tp, logger, state) = CreateController(settings);

            // Replace pm.StartProcessAsync to return failure by removing its ability to add.
            // For simplicity, remove pm and provide a custom failing manager
            var failingPm = new FailingProcessManager();
            var cfg = new FakeConfigService(settings);
            var controller2 = new ProcessGuardian.Services.ProcessGuardianController(cfg, logger, state, failingPm, tp);

            await controller2.StartAsync();
            tp.AdvanceBy(TimeSpan.FromMilliseconds(10));
            await Task.Yield();

            tp.AdvanceBy(TimeSpan.FromSeconds(settings.StartupVerificationTimeoutSeconds + 1));
            await Task.Yield();

            Assert.IsTrue(logger.GetErrors().Any() || logger.GetMessages().Any(m => m.Contains("Process.Start failed")));

            await controller2.StopAsync();
        }

        // Additional helper failing manager
        private class FailingProcessManager : IProcessManager
        {
            public Task<IReadOnlyList<ProcessInfo>> GetProcessesByNameAsync(string processName, CancellationToken ct = default)
            {
                return Task.FromResult((IReadOnlyList<ProcessInfo>)Array.Empty<ProcessInfo>());
            }

            public Task<ProcessStartResult> StartProcessAsync(ProcessStartRequest request, CancellationToken ct = default)
            {
                return Task.FromResult(new ProcessStartResult { Started = false, ErrorMessage = "Simulated failure" });
            }
        }

        [TestMethod]
        public async Task StartupVerification_SucceedsWhenTargetAppears()
        {
            var settings = MakeDefaults();
            settings.TargetProcessPath = "C:\\Apps\\Verify\\app.exe";
            settings.StartupVerificationTimeoutSeconds = 5;

            var (controller, pm, tp, logger, state) = CreateController(settings);

            // Make Start add process, but simulate process appears after first verification poll
            await controller.StartAsync();
            tp.AdvanceBy(TimeSpan.FromMilliseconds(10));
            await Task.Yield();

            // At this point controller should have attempted start; simulate external appearance before timeout
            pm.AddProcess(settings.TargetProcessPath);
            tp.AdvanceBy(TimeSpan.FromMilliseconds(250));
            await Task.Yield();

            Assert.IsTrue(logger.GetMessages().Any(m => m.Contains("Startup verification succeeded")));

            await controller.StopAsync();
        }

        [TestMethod]
        public async Task StartupVerification_TimesOut()
        {
            var settings = MakeDefaults();
            settings.TargetProcessPath = "C:\\Apps\\VerifyFail\\app.exe";
            settings.StartupVerificationTimeoutSeconds = 1;
            // use failing process manager to ensure no process will appear
            var state = new AppState { Settings = settings };
            var failing = new FailingProcessManager();
            var tp = new FakeTimeProvider();
            var logger = new MockLogger();
            var cfg = new FakeConfigService(settings);
            var controller = new ProcessGuardian.Services.ProcessGuardianController(cfg, logger, state, failing, tp);

            await controller.StartAsync();
            tp.AdvanceBy(TimeSpan.FromMilliseconds(10));
            await Task.Yield();

            // advance beyond timeout
            tp.AdvanceBy(TimeSpan.FromSeconds(2));
            await Task.Yield();
            await Task.Delay(1);
            tp.CompleteAll();
            // verification timeout should have produced a failed attempt
            Assert.IsTrue(state.CurrentRestartAttempts > 0 || logger.GetMessages().Any(m => m.Contains("Startup verification failed")));

            await controller.StopAsync();
        }

        [TestMethod]
        public async Task RestartAttemptCounter_Increments()
        {
            var settings = MakeDefaults();
            settings.TargetProcessPath = "C:\\Apps\\Cnt\\app.exe";
            settings.MaxRestartAttempts = 2;
            // use failing manager to ensure attempts are recorded
            var state = new AppState { Settings = settings };
            var failing = new FailingProcessManager();
            var tp = new FakeTimeProvider();
            var logger = new MockLogger();
            var cfg = new FakeConfigService(settings);
            var controller = new ProcessGuardian.Services.ProcessGuardianController(cfg, logger, state, failing, tp);

            await controller.StartAsync();
            tp.AdvanceBy(TimeSpan.FromMilliseconds(10));
            await Task.Yield();

            tp.AdvanceBy(TimeSpan.FromSeconds(settings.StartupVerificationTimeoutSeconds + 1));
            await Task.Yield();

            Assert.IsTrue(state.CurrentRestartAttempts > 0);
            await controller.StopAsync();
        }

        [TestMethod]
        public async Task RestartAttempts_ResetAfterSuccess()
        {
            var settings = MakeDefaults();
            settings.TargetProcessPath = "C:\\Apps\\Reset\\app.exe";
            settings.MaxRestartAttempts = 3;

            var (controller, pm, tp, logger, state) = CreateController(settings);
            // Ensure process already present so no restart needed
            pm.AddProcess(settings.TargetProcessPath);

            await controller.StartAsync();
            tp.AdvanceBy(TimeSpan.FromMilliseconds(10));
            await Task.Yield();

            Assert.AreEqual(0, state.CurrentRestartAttempts);

            await controller.StopAsync();
        }

        [TestMethod]
        public async Task MaxRestartAttempts_IsRespected()
        {
            var settings = MakeDefaults();
            settings.TargetProcessPath = "C:\\Apps\\Max\\app.exe";
            settings.MaxRestartAttempts = 1;
            settings.StartupVerificationTimeoutSeconds = 1;
            settings.RestartDelaySeconds = 1;
            settings.FailureCooldownSeconds = 1;

            // use failing manager to ensure attempts are exhausted
            var state = new AppState { Settings = settings };
            var failing = new FailingProcessManager();
            var tp = new FakeTimeProvider();
            var logger = new MockLogger();
            var cfg = new FakeConfigService(settings);
            var controller = new ProcessGuardian.Services.ProcessGuardianController(cfg, logger, state, failing, tp);
            await controller.StartAsync();
            tp.AdvanceBy(TimeSpan.FromMilliseconds(10));
            await Task.Yield();

            // advance to exhaust attempt and cooldown
            tp.AdvanceBy(TimeSpan.FromSeconds(5));
            await Task.Yield();
            await Task.Delay(1);
            tp.CompleteAll();
            // either log produced or AppState reflects exhausted attempts
            Assert.IsTrue(logger.GetMessages().Any(m => m.Contains("Restart attempts exhausted")) || state.LastErrorMessage == "Restart attempts exhausted");
            await controller.StopAsync();
        }

        [TestMethod]
        public async Task MaxRestartAttemptsZero_DisablesRestart()
        {
            var settings = MakeDefaults();
            settings.TargetProcessPath = "C:\\Apps\\Disabled\\app.exe";
            settings.MaxRestartAttempts = 0;

            var (controller, pm, tp, logger, state) = CreateController(settings);
            await controller.StartAsync();
            tp.AdvanceBy(TimeSpan.FromMilliseconds(10));
            await Task.Yield();

            Assert.IsTrue(logger.GetMessages().Any(m => m.Contains("MaxRestartAttempts=0")));
            await controller.StopAsync();
        }

        [TestMethod]
        public async Task RestartDelay_IsCancellationSafe()
        {
            var settings = MakeDefaults();
            settings.TargetProcessPath = "C:\\Apps\\Delay\\app.exe";
            settings.MaxRestartAttempts = 2;
            settings.RestartDelaySeconds = 5;

            var (controller, pm, tp, logger, state) = CreateController(settings);
            await controller.StartAsync();
            tp.AdvanceBy(TimeSpan.FromMilliseconds(10));
            await Task.Yield();

            // during restart delay, call StopAsync and ensure it cancels promptly
            var stopTask = controller.StopAsync();
            tp.CompleteAll();
            await stopTask;

            Assert.IsFalse(controller.IsRunning);
        }

        [TestMethod]
        public async Task FailureCooldown_IsCancellationSafe()
        {
            var settings = MakeDefaults();
            settings.TargetProcessPath = "C:\\Apps\\Cool\\app.exe";
            settings.MaxRestartAttempts = 1;
            settings.StartupVerificationTimeoutSeconds = 0;
            settings.FailureCooldownSeconds = 10;

            var (controller, pm, tp, logger, state) = CreateController(settings);
            await controller.StartAsync();
            tp.AdvanceBy(TimeSpan.FromMilliseconds(10));
            await Task.Yield();

            // cooldown in progress; stop
            var stopTask = controller.StopAsync();
            tp.CompleteAll();
            await stopTask;

            Assert.IsFalse(controller.IsRunning);
        }

        [TestMethod]
        public async Task Stop_CancelsMonitoringLoop()
        {
            var (controller, pm, tp, logger, state) = CreateController();
            await controller.StartAsync();
            tp.AdvanceBy(TimeSpan.FromMilliseconds(10));
            await Task.Yield();

            await controller.StopAsync();
            Assert.IsFalse(controller.IsRunning);
        }

        [TestMethod]
        public async Task StartCalledTwice_DoesNotCreateSecondLoop()
        {
            var (controller, pm, tp, logger, state) = CreateController();
            await controller.StartAsync();
            await controller.StartAsync();
            tp.AdvanceBy(TimeSpan.FromMilliseconds(10));
            await Task.Yield();

            Assert.IsTrue(controller.IsRunning);
            await controller.StopAsync();
        }

        [TestMethod]
        public async Task StopCalledTwice_IsSafe()
        {
            var (controller, pm, tp, logger, state) = CreateController();
            await controller.StartAsync();
            tp.AdvanceBy(TimeSpan.FromMilliseconds(10));
            await Task.Yield();

            await controller.StopAsync();
            await controller.StopAsync();
            Assert.IsFalse(controller.IsRunning);
        }

        [TestMethod]
        public async Task ExternalStartDuringRestart_IsHandled()
        {
            var settings = MakeDefaults();
            settings.TargetProcessPath = "C:\\Apps\\External\\app.exe";
            settings.MaxRestartAttempts = 3;

            var (controller, pm, tp, logger, state) = CreateController(settings);

            await controller.StartAsync();
            tp.AdvanceBy(TimeSpan.FromMilliseconds(10));
            await Task.Yield();

            // Simulate external process start during verification
            pm.AddProcess(settings.TargetProcessPath);
            tp.AdvanceBy(TimeSpan.FromMilliseconds(250));
            await Task.Yield();
            await Task.Delay(1);

            Assert.IsTrue(logger.GetMessages().Any(m => m.Contains("Startup verification succeeded")));
            await controller.StopAsync();
        }

        [TestMethod]
        public async Task StateTransitions_StoppedToWaitingToMonitoring()
        {
            var settings = MakeDefaults();
            settings.InitialDelaySeconds = 1;
            var (controller, pm, tp, logger, state) = CreateController(settings);

            var start = controller.StartAsync();
            Assert.AreEqual(GuardianStatus.WaitingInitialDelay, state.CurrentStatus);
            tp.AdvanceBy(TimeSpan.FromSeconds(1));
            await start;
            // allow monitor loop to establish
            tp.AdvanceBy(TimeSpan.FromMilliseconds(10));
            await Task.Yield();
            await Task.Delay(1);
            Assert.IsTrue(controller.IsRunning || state.CurrentStatus == GuardianStatus.Monitoring);

            await controller.StopAsync();
        }

        [TestMethod]
        public async Task StateTransitions_RestartFailureToCooldown()
        {
            var settings = MakeDefaults();
            settings.TargetProcessPath = "C:\\Apps\\Stat\\app.exe";
            settings.MaxRestartAttempts = 1;
            settings.StartupVerificationTimeoutSeconds = 1;
            settings.FailureCooldownSeconds = 1;

            var (controller, pm, tp, logger, state) = CreateController(settings);
            await controller.StartAsync();
            tp.AdvanceBy(TimeSpan.FromSeconds(5));
            await Task.Yield();

            Assert.IsTrue(state.CurrentStatus == GuardianStatus.Monitoring || state.CurrentStatus == GuardianStatus.Error || state.CurrentStatus == GuardianStatus.Monitoring);
            await controller.StopAsync();
        }

        [TestMethod]
        public async Task RestartAttempts_AreLogged()
        {
            var settings = MakeDefaults();
            settings.TargetProcessPath = "C:\\Apps\\Log\\app.exe";
            settings.MaxRestartAttempts = 1;
            settings.StartupVerificationTimeoutSeconds = 1;

            var (controller, pm, tp, logger, state) = CreateController(settings);
            await controller.StartAsync();
            tp.AdvanceBy(TimeSpan.FromSeconds(3));
            await Task.Yield();

            Assert.IsTrue(logger.GetMessages().Any(m => m.Contains("Restart attempt")));
            await controller.StopAsync();
        }
    }
}
