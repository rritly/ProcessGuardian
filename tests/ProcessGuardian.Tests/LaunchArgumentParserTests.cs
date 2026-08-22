using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProcessGuardian.Services;

namespace ProcessGuardian.Tests
{
    [TestClass]
    public class LaunchArgumentParserTests
    {
        [TestMethod]
        public void IsBackgroundLaunch_DetectsSimpleFlag()
        {
            Assert.IsTrue(LaunchArgumentParser.IsBackgroundLaunch("--background"));
        }

        [TestMethod]
        public void IsBackgroundLaunch_DetectsFlagInArgs()
        {
            Assert.IsTrue(LaunchArgumentParser.IsBackgroundLaunch("\"C:\\Program Files\\App\\app.exe\" --background --other"));
        }

        [TestMethod]
        public void IsBackgroundLaunch_ReturnsFalseWhenNotPresent()
        {
            Assert.IsFalse(LaunchArgumentParser.IsBackgroundLaunch("--foreground"));
            Assert.IsFalse(LaunchArgumentParser.IsBackgroundLaunch(string.Empty));
            Assert.IsFalse(LaunchArgumentParser.IsBackgroundLaunch(null));
        }
    }
}
