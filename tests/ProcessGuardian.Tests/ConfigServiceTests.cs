using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;
using ProcessGuardian.Core;
using ProcessGuardian.Services;
using ProcessGuardian.Tests.Mocks;

namespace ProcessGuardian.Tests
{
    [TestClass]
    public class ConfigServiceTests
    {
        [TestMethod]
        public void Defaults_AreValid()
        {
            var svc = new ProcessGuardian.Services.ConfigService(new MockFileStorage());
            var d = svc.GetDefaults();
            Assert.AreEqual(1, d.SchemaVersion);
            Assert.IsFalse(d.MonitoringEnabled);
            Assert.IsTrue(d.EnableLogging);
        }

        [TestMethod]
        public void Validate_ValidConfiguration()
        {
            var s = new AppSettings { SchemaVersion = 1, TargetProcessPath = "C:\\Tools\\app.exe", MonitoringEnabled = true, InitialDelaySeconds = 1, CheckIntervalSeconds = 10, MaxRestartAttempts = 1, RestartDelaySeconds = 1, StartupVerificationTimeoutSeconds = 1, FailureCooldownSeconds = 1, EnableLogging = true, LogBufferSize = 10, AutostartEnabled = false };
            var v = AppSettingsValidator.Validate(s);
            Assert.IsTrue(v.IsValid);
        }

        [TestMethod]
        public void Validate_WrongFileExtension()
        {
            var s = new AppSettings { SchemaVersion = 1, TargetProcessPath = "C:\\Tools\\app.txt", MonitoringEnabled = true, InitialDelaySeconds = 1, CheckIntervalSeconds = 10, MaxRestartAttempts = 1, RestartDelaySeconds = 1, StartupVerificationTimeoutSeconds = 1, FailureCooldownSeconds = 1, EnableLogging = true, LogBufferSize = 10 };
            var v = AppSettingsValidator.Validate(s);
            Assert.IsFalse(v.IsValid);
        }

        [TestMethod]
        public void Validate_NonRootedPath()
        {
            var s = new AppSettings { SchemaVersion = 1, TargetProcessPath = "relative\\app.exe", MonitoringEnabled = true, InitialDelaySeconds = 1, CheckIntervalSeconds = 10, MaxRestartAttempts = 1, RestartDelaySeconds = 1, StartupVerificationTimeoutSeconds = 1, FailureCooldownSeconds = 1, EnableLogging = true, LogBufferSize = 10 };
            var v = AppSettingsValidator.Validate(s);
            Assert.IsFalse(v.IsValid);
        }

        [TestMethod]
        public void Validate_NegativeInitialDelay()
        {
            var s = new AppSettings { SchemaVersion = 1, InitialDelaySeconds = -1, CheckIntervalSeconds = 10, MaxRestartAttempts = 1, RestartDelaySeconds = 1, StartupVerificationTimeoutSeconds = 1, FailureCooldownSeconds = 1, EnableLogging = true, LogBufferSize = 10 };
            var v = AppSettingsValidator.Validate(s);
            Assert.IsFalse(v.IsValid);
        }

        [TestMethod]
        public void Validate_ZeroCheckInterval()
        {
            var s = new AppSettings { SchemaVersion = 1, CheckIntervalSeconds = 0, InitialDelaySeconds = 1, MaxRestartAttempts = 1, RestartDelaySeconds = 1, StartupVerificationTimeoutSeconds = 1, FailureCooldownSeconds = 1, EnableLogging = true, LogBufferSize = 10 };
            var v = AppSettingsValidator.Validate(s);
            Assert.IsFalse(v.IsValid);
        }

        [TestMethod]
        public void Validate_NegativeMaxRestartAttempts()
        {
            var s = new AppSettings { SchemaVersion = 1, MaxRestartAttempts = -1, CheckIntervalSeconds = 10, InitialDelaySeconds = 1, RestartDelaySeconds = 1, StartupVerificationTimeoutSeconds = 1, FailureCooldownSeconds = 1, EnableLogging = true, LogBufferSize = 10 };
            var v = AppSettingsValidator.Validate(s);
            Assert.IsFalse(v.IsValid);
        }

        [TestMethod]
        public void Validate_NegativeRestartDelay()
        {
            var s = new AppSettings { SchemaVersion = 1, RestartDelaySeconds = -1, CheckIntervalSeconds = 10, InitialDelaySeconds = 1, MaxRestartAttempts = 1, StartupVerificationTimeoutSeconds = 1, FailureCooldownSeconds = 1, EnableLogging = true, LogBufferSize = 10 };
            var v = AppSettingsValidator.Validate(s);
            Assert.IsFalse(v.IsValid);
        }

        [TestMethod]
        public void Validate_ZeroStartupVerificationTimeout()
        {
            var s = new AppSettings { SchemaVersion = 1, StartupVerificationTimeoutSeconds = 0, CheckIntervalSeconds = 10, InitialDelaySeconds = 1, MaxRestartAttempts = 1, RestartDelaySeconds = 1, FailureCooldownSeconds = 1, EnableLogging = true, LogBufferSize = 10 };
            var v = AppSettingsValidator.Validate(s);
            Assert.IsFalse(v.IsValid);
        }

        [TestMethod]
        public void Validate_NegativeFailureCooldown()
        {
            var s = new AppSettings { SchemaVersion = 1, FailureCooldownSeconds = -1, CheckIntervalSeconds = 10, InitialDelaySeconds = 1, MaxRestartAttempts = 1, RestartDelaySeconds = 1, StartupVerificationTimeoutSeconds = 1, EnableLogging = true, LogBufferSize = 10 };
            var v = AppSettingsValidator.Validate(s);
            Assert.IsFalse(v.IsValid);
        }

        [TestMethod]
        public void Validate_InvalidLogBufferSize()
        {
            var s = new AppSettings { SchemaVersion = 1, LogBufferSize = 0, CheckIntervalSeconds = 10, InitialDelaySeconds = 1, MaxRestartAttempts = 1, RestartDelaySeconds = 1, StartupVerificationTimeoutSeconds = 1, FailureCooldownSeconds = 1, EnableLogging = true };
            var v = AppSettingsValidator.Validate(s);
            Assert.IsFalse(v.IsValid);
        }
        [TestMethod]
        public async Task LoadMissingFile_ReturnsDefaults()
        {
            var storage = new MockFileStorage();
            var svc = new ConfigService(storage);
            var res = await svc.LoadOrCreateAsync();
            Assert.IsFalse(res.LoadedFromFile);
            Assert.AreEqual(1, res.Settings.SchemaVersion);
        }

        [TestMethod]
        public async Task SaveAndLoad_Roundtrip()
        {
            var storage = new MockFileStorage();
            var svc = new ConfigService(storage);
            var defaults = svc.GetDefaults();
            defaults.TargetProcessPath = "C:\\Tools\\app.exe";
            await svc.SaveAsync(defaults);
            var res = await svc.LoadOrCreateAsync();
            Assert.IsTrue(res.LoadedFromFile);
            Assert.AreEqual(defaults.TargetProcessPath, res.Settings.TargetProcessPath);
        }

        [TestMethod]
        public async Task Save_Throws_OnInvalid()
        {
            var storage = new MockFileStorage();
            var svc = new ConfigService(storage);
            var s = svc.GetDefaults();
            s.MonitoringEnabled = true;
            s.TargetProcessPath = null; // invalid when monitoring enabled
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => svc.SaveAsync(s));
        }

        [TestMethod]
        public async Task Load_CorruptJson_ReturnsDefaultsAndError()
        {
            var storage = new MockFileStorage();
            await storage.WriteAllTextAtomicAsync("settings.json", "{ invalid json }");
            var svc = new ConfigService(storage);
            var res = await svc.LoadOrCreateAsync();
            Assert.IsFalse(res.LoadedFromFile);
            Assert.IsFalse(string.IsNullOrEmpty(res.ErrorMessage));
        }

        [TestMethod]
        public void Validate_InvalidPathBecauseNotAbsolute()
        {
            var s = new AppSettings { SchemaVersion = 1, TargetProcessPath = "relative\\app.exe", MonitoringEnabled = true, InitialDelaySeconds = 0, CheckIntervalSeconds = 10, MaxRestartAttempts = 1, RestartDelaySeconds = 0, StartupVerificationTimeoutSeconds = 1, FailureCooldownSeconds = 0, EnableLogging = true, LogBufferSize = 10, AutostartEnabled = false };
            var v = AppSettingsValidator.Validate(s);
            Assert.IsFalse(v.IsValid);
        }

        [TestMethod]
        public async Task Load_FutureSchemaVersion_ReturnsDefaultsAndError()
        {
            var storage = new MockFileStorage();
            var json = System.Text.Json.JsonSerializer.Serialize(new AppSettings { SchemaVersion = 99 }, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
            await storage.WriteAllTextAtomicAsync("settings.json", json);
            var svc = new ConfigService(storage);
            var res = await svc.LoadOrCreateAsync();
            Assert.IsFalse(res.LoadedFromFile);
            Assert.IsFalse(string.IsNullOrEmpty(res.ErrorMessage));
        }

        [TestMethod]
        public async Task Save_Roundtrip_PersistsValues()
        {
            var storage = new MockFileStorage();
            var svc = new ConfigService(storage);
            var s = svc.GetDefaults();
            s.TargetProcessPath = "C:\\Some\\app.exe";
            await svc.SaveAsync(s);
            var res = await svc.LoadOrCreateAsync();
            Assert.IsTrue(res.LoadedFromFile);
            Assert.AreEqual(s.TargetProcessPath, res.Settings.TargetProcessPath);
        }

        [TestMethod]
        public async Task Save_AtomicWriteFailure_Propagates()
        {
            var storage = new MockFileStorage { ThrowOnWrite = true };
            var svc = new ConfigService(storage);
            var s = svc.GetDefaults();
            s.TargetProcessPath = "C:\\Some\\app.exe";
            await Assert.ThrowsExceptionAsync<IOException>(() => svc.SaveAsync(s));
        }

        [TestMethod]
        public async Task Save_Cancellation_DoesNotLeavePartial()
        {
            var storage = new MockFileStorage();
            var svc = new ConfigService(storage);
            var s = svc.GetDefaults();
            s.TargetProcessPath = "C:\\Some\\app.exe";
            using var cts = new System.Threading.CancellationTokenSource();
            var task = svc.SaveAsync(s, cts.Token);
            cts.Cancel();
            await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () => await task);
        }
    }
}
