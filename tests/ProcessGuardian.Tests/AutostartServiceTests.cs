using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProcessGuardian.Services;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ProcessGuardian.Tests
{
    [TestClass]
    public class AutostartServiceTests
    {
        private sealed class FakeRunKeyAccessor : IRunKeyAccessor
        {
            private readonly Dictionary<string, string> _values = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            public string? GetValue(string name) => _values.TryGetValue(name, out var v) ? v : null;
            public void SetValue(string name, string value) => _values[name] = value;
            public void DeleteValue(string name) => _values.Remove(name);
        }

        [TestMethod]
        public async Task EnableCreatesExpectedRunEntry()
        {
            var fake = new FakeRunKeyAccessor();
            var svc = new AutostartService(fake, () => "C:\\Program Files\\ProcessGuardian\\ProcessGuardian.exe");
            await svc.EnableAsync(true);
            var val = fake.GetValue(AppIdentity.Product);
            Assert.IsNotNull(val);
            Assert.IsTrue(val!.Contains("ProcessGuardian.exe"));
            Assert.IsTrue(val.Contains("--background"));
        }

        [TestMethod]
        public async Task DisableRemovesEntry()
        {
            var fake = new FakeRunKeyAccessor();
            fake.SetValue(AppIdentity.Product, "C:\\temp\\x.exe --background");
            var svc = new AutostartService(fake, () => "C:\\temp\\x.exe");
            await svc.EnableAsync(false);
            var val = fake.GetValue(AppIdentity.Product);
            Assert.IsNull(val);
        }

        [TestMethod]
        public async Task EnablingTwiceIsIdempotent()
        {
            var fake = new FakeRunKeyAccessor();
            var svc = new AutostartService(fake, () => "C:\\prog\\pg.exe");
            await svc.EnableAsync(true);
            var first = fake.GetValue(AppIdentity.Product);
            await svc.EnableAsync(true);
            var second = fake.GetValue(AppIdentity.Product);
            Assert.AreEqual(first, second);
        }

        [TestMethod]
        public async Task DisablingTwiceIsIdempotent()
        {
            var fake = new FakeRunKeyAccessor();
            var svc = new AutostartService(fake, () => "C:\\prog\\pg.exe");
            await svc.EnableAsync(false);
            await svc.EnableAsync(false);
            Assert.IsNull(fake.GetValue(AppIdentity.Product));
        }

        [TestMethod]
        public void IsEnabled_CorrectCommand_ReturnsTrue()
        {
            var fake = new FakeRunKeyAccessor();
            var svc = new AutostartService(fake, () => "C:\\prog\\pg.exe");
            fake.SetValue(AppIdentity.Product, "C:\\prog\\pg.exe --background");
            Assert.IsTrue(svc.IsEnabled());
        }

        [TestMethod]
        public void IsEnabled_MissingEntry_ReturnsFalse()
        {
            var fake = new FakeRunKeyAccessor();
            var svc = new AutostartService(fake, () => "C:\\prog\\pg.exe");
            Assert.IsFalse(svc.IsEnabled());
        }

        [TestMethod]
        public void IsEnabled_WrongExecutable_ReturnsFalse()
        {
            var fake = new FakeRunKeyAccessor();
            var svc = new AutostartService(fake, () => "C:\\prog\\pg.exe");
            fake.SetValue(AppIdentity.Product, "C:\\other\\otherapp.exe --background");
            Assert.IsFalse(svc.IsEnabled());
        }

        [TestMethod]
        public void IsEnabled_MissingBackgroundArg_ReturnsFalse()
        {
            var fake = new FakeRunKeyAccessor();
            var svc = new AutostartService(fake, () => "C:\\prog\\pg.exe");
            fake.SetValue(AppIdentity.Product, "C:\\prog\\pg.exe");
            Assert.IsFalse(svc.IsEnabled());
        }

        [TestMethod]
        public void IsEnabled_UnrelatedExeWithBackground_ReturnsFalse()
        {
            var fake = new FakeRunKeyAccessor();
            var svc = new AutostartService(fake, () => "C:\\prog\\pg.exe");
            fake.SetValue(AppIdentity.Product, "C:\\SomeOther\\other.exe --background");
            Assert.IsFalse(svc.IsEnabled());
        }
    }
}
