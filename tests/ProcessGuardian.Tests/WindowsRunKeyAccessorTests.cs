using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProcessGuardian.Services;
using System.Reflection;

namespace ProcessGuardian.Tests
{
    [TestClass]
    public class WindowsRunKeyAccessorTests
    {
        [TestMethod]
        public void RunKeyPath_IsCorrect()
        {
            // Use reflection to validate the private const string RunKeyPath value so tests catch accidental escaping errors
            var field = typeof(WindowsRunKeyAccessor).GetField("RunKeyPath", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, "RunKeyPath field should exist");
            var val = field!.GetValue(null) as string;
            Assert.AreEqual("Software\\Microsoft\\Windows\\CurrentVersion\\Run", val);
        }
    }
}
