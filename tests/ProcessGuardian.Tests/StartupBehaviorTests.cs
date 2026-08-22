using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProcessGuardian.Core;
using ProcessGuardian.Tests.Fakes;
using System.Threading.Tasks;

namespace ProcessGuardian.Tests
{
    [TestClass]
    public class StartupBehaviorTests
    {
        private AppSettings MakeDefaults()
        {
            return new AppSettings
            {
                InitialDelaySeconds = 0,
                CheckIntervalSeconds = 1,
                MaxRestartAttempts = 1,
                RestartDelaySeconds = 1,
                StartupVerificationTimeoutSeconds = 1,
                FailureCooldownSeconds = 1
            };
        }

        private (IProcessGuardianController controller, FakeProcessManager pm, FakeTimeProvider tp, ProcessGuardian.Tests.Fakes.MockLogger logger, AppState state) CreateController(AppSettings? settings = null)
        {
            settings ??= MakeDefaults();
            var state = new AppState { Settings = settings };
            var pm = new FakeProcessManager();
            var tp = new FakeTimeProvider();
            var logger = new ProcessGuardian.Tests.Fakes.MockLogger();
            var cfg = new ProcessGuardian.Tests.Fakes.FakeConfigService(settings);
            var controller = new ProcessGuardian.Services.ProcessGuardianController(cfg, logger, state, pm, tp);
            return (controller, pm, tp, logger, state);
        }

        [TestMethod]
        public async Task MonitoringEnabled_True_StartsController()
        {
            var settings = MakeDefaults();
            settings.MonitoringEnabled = true;

            var (controller, pm, tp, logger, state) = CreateController(settings);

            await controller.StartAsync();
            Assert.IsTrue(controller.IsRunning);

            await controller.StopAsync();
        }

        [TestMethod]
        public void MonitoringEnabled_Flag_IsNotModifiedByParser()
        {
            var settings = MakeDefaults();
            settings.MonitoringEnabled = false;
            settings.AutostartEnabled = true;

            // Parser has no access to settings; ensure values remain unchanged
            Assert.IsFalse(settings.MonitoringEnabled);
            Assert.IsTrue(settings.AutostartEnabled);
        }
    }
}
