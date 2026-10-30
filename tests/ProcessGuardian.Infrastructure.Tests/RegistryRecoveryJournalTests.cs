using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using FluentAssertions;
using ProcessGuardian.Infrastructure.Recovery;
using ProcessGuardian.Infrastructure.Security;
using Xunit;

namespace ProcessGuardian.Infrastructure.Tests;

// Focused R-A tests for the HKLM recovery journal. The ACL/owner decision logic is exercised
// through fabricated RegistrySecurity instances (same trust rules as A), the missing-key read
// path is verified against the live read-only HKLM lookup, and the write-rights constant is
// pinned. Live create/ACL/owner correctness is NOT claimed here — that is the manual elevated
// checklist (CI runs non-elevated and cannot prove a real DACL/owner).
public class RegistryRecoveryJournalTests
{
    private static readonly SecurityIdentifier AdminsSid =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemSid =
        new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier UsersSid =
        new(WellKnownSidType.BuiltinUsersSid, null);

    [Fact]
    public void CreateProtectedRegistrySecurity_SetsTrustedOwner_ProtectedDacl_FullControlOnlyForAdminsAndSystem()
    {
        var security = ElevatedSecurity.CreateProtectedRegistrySecurity();

        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        owner.Should().Be(AdminsSid, "the creating user's SID is not a trusted owner for the journal key");

        security.AreAccessRulesProtected.Should().BeTrue("inheritance disabled, protected DACL");

        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<RegistryAccessRule>().ToList();
        rules.Should().HaveCount(2);
        rules.Should().OnlyContain(r => r.AccessControlType == AccessControlType.Allow
                                         && r.RegistryRights.HasFlag(RegistryRights.FullControl));
        rules.Should().Contain(r => r.IdentityReference.Equals(AdminsSid));
        rules.Should().Contain(r => r.IdentityReference.Equals(SystemSid));
    }

    [Fact]
    public void Classify_ProtectedDescriptor_Trusted()
    {
        ElevatedSecurity.ClassifyRegistrySecurity(
            ElevatedSecurity.CreateProtectedRegistrySecurity()).Should().Be(ProtectionClassification.Trusted);
    }

    [Fact]
    public void Classify_ForeignOwner_EvenWithStrongDacl_Untrusted()
    {
        var security = ElevatedSecurity.CreateProtectedRegistrySecurity();
        security.SetOwner(WindowsIdentity.GetCurrent().User!);

        // A strong DACL whose owner is the creating user's SID is quarantined, not loaded (A).
        ElevatedSecurity.ClassifyRegistrySecurity(security).Should().Be(ProtectionClassification.Untrusted);
    }

    [Fact]
    public void Classify_ForeignAllowAce_Untrusted()
    {
        var security = ElevatedSecurity.CreateProtectedRegistrySecurity();
        security.AddAccessRule(new RegistryAccessRule(
            UsersSid, RegistryRights.ReadKey, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));

        ElevatedSecurity.ClassifyRegistrySecurity(security).Should().Be(ProtectionClassification.Untrusted);
    }

    [Fact]
    public void Classify_UnexpectedDenyAce_Untrusted()
    {
        var security = ElevatedSecurity.CreateProtectedRegistrySecurity();
        security.AddAccessRule(new RegistryAccessRule(
            UsersSid, RegistryRights.ReadKey, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));

        ElevatedSecurity.ClassifyRegistrySecurity(security).Should().Be(ProtectionClassification.Untrusted);
    }

    [Fact]
    public void Classify_InheritanceEnabled_Untrusted()
    {
        var security = ElevatedSecurity.CreateProtectedRegistrySecurity();
        security.SetAccessRuleProtection(false, false);

        ElevatedSecurity.ClassifyRegistrySecurity(security).Should().Be(ProtectionClassification.Untrusted);
    }

    [Fact]
    public void WriteRights_IncludeSetValueAndSecurityRead_DoNotRequestChangePermissions()
    {
        var rights = RegistryRecoveryJournal.WriteRights;
        rights.Should().HaveFlag(RegistryRights.SetValue, "existing-key writes (SetValue/DeleteValue) need KEY_SET_VALUE");
        rights.Should().HaveFlag(RegistryRights.ReadPermissions, "owner/DACL read-back needs READ_CONTROL");
        rights.Should().HaveFlag(RegistryRights.QueryValues, "reading the current State needs KEY_QUERY_VALUE");
        rights.Should().NotHaveFlag(RegistryRights.ChangePermissions, "ChangePermissions is not a substitute for SetValue (R-A)");
    }

    [Fact]
    public async Task ReadAsync_MissingKey_ReturnsNone_NotRefused()
    {
        // Guard: the recovery key only ever exists after a live (usually elevated) write, and its
        // trust then needs an ACL read. On a clean runner the key is absent, which is the case this
        // test covers — if a key is present, it was created by the manual elevated checklist and the
        // real-ACL behavior is verified there, not asserted blindly from a non-elevated context.
        using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (var existing = baseKey.OpenSubKey(@"SOFTWARE\ProcessGuardianAdmin\Recovery"))
        {
            if (existing is not null)
                return;
        }

        var journal = new RegistryRecoveryJournal();
        var state = await journal.ReadAsync();

        state.Should().Be(RecoveryState.None, "absent journal key = complete (plan) must not be a refusal");
    }
}
