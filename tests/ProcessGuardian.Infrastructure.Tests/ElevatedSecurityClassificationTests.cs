using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;
using ProcessGuardian.Infrastructure.Security;
using Xunit;

namespace ProcessGuardian.Infrastructure.Tests;

// Decision-logic for the elevated protection check, exercised through fabricated security
// descriptors (never applied to disk, so no elevation is required). These verify the
// normalized known-template check: three facts directly — required Admins/SYSTEM rights
// present, no foreign allow permission, no unexpected deny ACE — plus a trusted owner.
public class ElevatedSecurityClassificationTests
{
    private static readonly SecurityIdentifier AdminsSid =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemSid =
        new(WellKnownSidType.LocalSystemSid, null);

    private static DirectorySecurity HardenedDirectory()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(
            AdminsSid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            SystemSid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        security.SetOwner(AdminsSid);
        return security;
    }

    [Fact]
    public void HardenedDacl_TrustedAdministratorsOwner_IsTrusted()
    {
        ElevatedSecurity.ClassifyFileSystemSecurity(HardenedDirectory())
            .Should().Be(ProtectionClassification.Trusted);
    }

    [Fact]
    public void HardenedDacl_SystemOwner_IsTrusted()
    {
        var security = HardenedDirectory();
        security.SetOwner(SystemSid);
        ElevatedSecurity.ClassifyFileSystemSecurity(security)
            .Should().Be(ProtectionClassification.Trusted);
    }

    [Fact]
    public void StrongDacl_ForeignCreatingUserOwner_IsUntrusted()
    {
        // The original defect: the DACL is hardened but the owner was left as the creating
        // user SID. A foreign owner must never be accepted as trusted.
        var security = HardenedDirectory();
        security.SetOwner(WindowsIdentity.GetCurrent().User!);

        ElevatedSecurity.ClassifyFileSystemSecurity(security)
            .Should().Be(ProtectionClassification.Untrusted);
    }

    [Fact]
    public void InheritedDacl_NotProtected_IsUntrusted()
    {
        var security = HardenedDirectory();
        security.SetAccessRuleProtection(false, true);

        ElevatedSecurity.ClassifyFileSystemSecurity(security)
            .Should().Be(ProtectionClassification.Untrusted);
    }

    [Fact]
    public void ForeignAllowAce_IsUntrusted()
    {
        var security = HardenedDirectory();
        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.Read,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));

        ElevatedSecurity.ClassifyFileSystemSecurity(security)
            .Should().Be(ProtectionClassification.Untrusted);
    }

    [Fact]
    public void UnexpectedDenyAce_IsUntrusted()
    {
        var security = HardenedDirectory();
        security.AddAccessRule(new FileSystemAccessRule(
            AdminsSid, FileSystemRights.Delete,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));

        ElevatedSecurity.ClassifyFileSystemSecurity(security)
            .Should().Be(ProtectionClassification.Untrusted);
    }

    [Fact]
    public void MissingAdministratorsOrSystemRights_IsUntrusted()
    {
        var security = HardenedDirectory();
        security.RemoveAccessRule(new FileSystemAccessRule(
            SystemSid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));

        ElevatedSecurity.ClassifyFileSystemSecurity(security)
            .Should().Be(ProtectionClassification.Untrusted);
    }
}
