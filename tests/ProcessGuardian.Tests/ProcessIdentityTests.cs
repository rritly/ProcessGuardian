using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProcessGuardian.Core;
using System.Collections.Generic;

namespace ProcessGuardian.Tests
{
    [TestClass]
    public class ProcessIdentityTests
    {
        [TestMethod]
        public void TargetProcessFound_ByFullPath()
        {
            var target = "C:\\Apps\\MyApp\\app.exe";
            var candidates = new List<ProcessInfo>
            {
                new ProcessInfo { ProcessId = 1, ProcessName = "app", ExecutablePath = "C:\\Apps\\MyApp\\app.exe", IsPathAvailable = true }
            };

            var result = ProcessIdentity.Inspect(candidates, target);
            Assert.AreEqual(ProcessInspectionOutcome.TargetFound, result);
        }

        [TestMethod]
        public void SameNameWrongPath_IsNotTarget()
        {
            var target = "C:\\Apps\\MyApp\\app.exe";
            var candidates = new List<ProcessInfo>
            {
                new ProcessInfo { ProcessId = 2, ProcessName = "app", ExecutablePath = "C:\\Other\\app.exe", IsPathAvailable = true }
            };

            var result = ProcessIdentity.Inspect(candidates, target);
            Assert.AreEqual(ProcessInspectionOutcome.SameNameDifferentPath, result);
        }

        [TestMethod]
        public void ProcessInformationUnavailable_IsHandled()
        {
            var target = "C:\\Apps\\MyApp\\app.exe";
            var candidates = new List<ProcessInfo>
            {
                new ProcessInfo { ProcessId = 3, ProcessName = "app", ExecutablePath = null, IsPathAvailable = false }
            };

            var result = ProcessIdentity.Inspect(candidates, target);
            Assert.AreEqual(ProcessInspectionOutcome.ProcessInformationUnavailable, result);
        }

        [TestMethod]
        public void NormalizePath_HandlesTrailingSeparator()
        {
            var target = "C:\\Apps\\MyApp\\app.exe\\";
            var candidates = new List<ProcessInfo>
            {
                new ProcessInfo { ProcessId = 4, ProcessName = "app", ExecutablePath = "C:\\Apps\\MyApp\\app.exe", IsPathAvailable = true }
            };

            var result = ProcessIdentity.Inspect(candidates, target);
            Assert.AreEqual(ProcessInspectionOutcome.TargetFound, result);
        }
    }
}
