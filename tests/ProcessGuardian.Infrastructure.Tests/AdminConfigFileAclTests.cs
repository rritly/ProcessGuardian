using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProcessGuardian.Infrastructure.Configuration;
using Xunit;

namespace ProcessGuardian.Infrastructure.Tests;

// The admin edition secures its settings file, not just its directory: inheritance
// is stripped and only Administrators/SYSTEM retain access. Applied to throwaway
// paths so the tests run without elevation; access is restored for cleanup.
public class AdminConfigFileAclTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public AdminConfigFileAclTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ProcessGuardian.Tests", Guid.NewGuid().ToString("N"), "fileacl");
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "settings.json");
        File.WriteAllText(_file, "{}");
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_file))
                new FileInfo(_file).GetAccessControl().SetAccessRuleProtection(false, true);
            var dirInfo = new DirectoryInfo(_dir);
            if (dirInfo.Exists)
                dirInfo.GetAccessControl().SetAccessRuleProtection(false, true);
            Directory.Delete(Path.GetDirectoryName(_dir)!, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; never throw from Dispose
        }
    }

    [Fact]
    public void HardenFileForAdministratorsOnly_DisablesInheritance_AndGrantsAdminsAndSystem()
    {
        JsonConfigurationStore.HardenFileForAdministratorsOnly(_file);

        var security = new FileInfo(_file).GetAccessControl();
        security.AreAccessRulesProtected.Should().BeTrue("inherited user rights must be cut off");

        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier));
        rules.Count.Should().Be(2, "exactly Administrators and SYSTEM rules remain");
        rules.Cast<FileSystemAccessRule>().Should().OnlyContain(r =>
            r.AccessControlType == AccessControlType.Allow &&
            r.FileSystemRights.HasFlag(FileSystemRights.FullControl));
    }

    [Fact]
    public void CreateHardenedDirectorySecurity_DirectoryIsBornProtected_NoInheritedRules()
    {
        var dir = Path.Combine(_dir, "born-hardened");

        new DirectoryInfo(dir).Create(JsonConfigurationStore.CreateHardenedDirectorySecurity());

        var security = new DirectoryInfo(dir).GetAccessControl();
        security.AreAccessRulesProtected.Should().BeTrue(
            "the directory must never exist with a user-writable inherited ACL");

        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier));
        rules.Count.Should().Be(2, "exactly Administrators and SYSTEM rules");
        rules.Cast<FileSystemAccessRule>().Should().OnlyContain(r => r.AccessControlType == AccessControlType.Allow);
    }
}
