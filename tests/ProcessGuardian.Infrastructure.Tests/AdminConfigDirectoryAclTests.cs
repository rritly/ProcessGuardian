using System.IO;
using System.Security.AccessControl;
using FluentAssertions;
using ProcessGuardian.Infrastructure.Configuration;
using Xunit;

namespace ProcessGuardian.Infrastructure.Tests;

// The admin edition hardens its settings directory (inheritance stripped,
// Administrators/SYSTEM only). Applied to a throwaway directory so the test
// runs without elevation; access is restored afterwards for cleanup.
public class AdminConfigDirectoryAclTests : IDisposable
{
    private readonly string _dir;

    public AdminConfigDirectoryAclTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ProcessGuardian.Tests", Guid.NewGuid().ToString("N"), "admincfg");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            var info = new DirectoryInfo(_dir);
            if (info.Exists)
            {
                info.GetAccessControl().SetAccessRuleProtection(false, true);
                info.Refresh();
                Directory.Delete(Path.GetDirectoryName(_dir)!, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; never throw from Dispose
        }
    }

    [Fact]
    public void HardenForAdministratorsOnly_DisablesInheritance_AndGrantsAdminsAndSystem()
    {
        JsonConfigurationStore.HardenForAdministratorsOnly(_dir);

        var security = new DirectoryInfo(_dir).GetAccessControl();
        security.AreAccessRulesProtected.Should().BeTrue("inherited user rights must be cut off");

        var rules = security.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier));
        rules.Count.Should().Be(2, "exactly Administrators and SYSTEM rules remain");
        rules.Cast<FileSystemAccessRule>().Should().OnlyContain(r => r.AccessControlType == AccessControlType.Allow);
    }
}
