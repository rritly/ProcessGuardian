using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using ProcessGuardian.Infrastructure.Configuration;

namespace ProcessGuardian.Infrastructure.Recovery;

/// <summary>
/// HKLM-backed recovery/notice journal. Fixed key: <c>HKLM\SOFTWARE\ProcessGuardianAdmin\Recovery</c>,
/// value <c>State</c> (<c>REG_SZ</c>) = <c>RecoveryInProgress</c> or <c>ResetNoticePending</c>;
/// absent means complete. The key is created with a restrictive ACL (owner BUILTIN\Administrators
/// or LOCAL SYSTEM, inheritance disabled, FullControl only for those two SIDs); a pre-existing
/// weak or unverifiable key is refused, never silently accepted.
///
/// This runs only in the elevated edition; CI exercises the logic through the in-memory fake,
/// and the live ACL is covered by the manual elevated checklist.
/// </summary>
public sealed class RegistryRecoveryJournal : IElevatedRecoveryJournal
{
    private const string RootPath = @"SOFTWARE\ProcessGuardianAdmin\Recovery";
    private const string StateValue = "State";

    private static readonly SecurityIdentifier AdminsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);

    public Task<RecoveryState> ReadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(RootPath, RegistryKeyPermissionCheck.ReadSubTree, RegistryRights.ReadKey | RegistryRights.QueryValues)
            ?? throw Refuse($"the recovery key '{RootDisplayPath()}' cannot be opened (access denied or missing while a state was expected)");
        VerifyKeySecurity(key);
        return Task.FromResult(ReadState(key));
    }

    public Task WriteAsync(RecoveryState state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = OpenOrCreateKey(baseKey);
        VerifyKeySecurity(key);
        if (state == RecoveryState.None)
        {
            if (key.GetValue(StateValue, null) is not null)
                key.DeleteValue(StateValue, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(StateValue, state.ToString(), RegistryValueKind.String);
        }
        return Task.CompletedTask;
    }

    private static RegistryKey OpenOrCreateKey(RegistryKey baseKey)
    {
        // An existing key must be verified as-is; a weak pre-existing key is a refusal,
        // never a silent accept or an in-place ACL overwrite.
        var existing = baseKey.OpenSubKey(RootPath, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ReadKey | RegistryRights.ChangePermissions);
        if (existing is not null)
            return existing;

        var created = baseKey.CreateSubKey(RootPath, RegistryKeyPermissionCheck.ReadWriteSubTree, CreateProtectedRegistrySecurity());
        if (created is null)
            throw Refuse($"the recovery key '{RootDisplayPath()}' could not be created");
        return created;
    }

    private static RecoveryState ReadState(RegistryKey key)
    {
        var raw = key.GetValue(StateValue, null) as string;
        if (raw is null)
            return RecoveryState.None;
        return Enum.TryParse<RecoveryState>(raw, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw Refuse($"the recovery key '{RootDisplayPath()}' holds an unrecognized State value '{raw}'");
    }

    private static void VerifyKeySecurity(RegistryKey key)
    {
        RegistrySecurity security;
        try
        {
            security = key.GetAccessControl();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw Refuse($"the recovery key '{RootDisplayPath()}' cannot be read (READ_CONTROL denied)", ex);
        }

        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || (!owner.Equals(AdminsSid) && !owner.Equals(SystemSid)))
            throw Refuse($"the recovery key '{RootDisplayPath()}' has an untrusted owner");

        if (!security.AreAccessRulesProtected)
            throw Refuse($"the recovery key '{RootDisplayPath()}' is not inheritance-protected");

        var adminsFull = false;
        var systemFull = false;
        var foreignAllow = false;
        var denyPresent = false;
        foreach (RegistryAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Deny)
            {
                denyPresent = true;
                continue;
            }
            var sid = rule.IdentityReference as SecurityIdentifier;
            if (sid is null)
                throw Refuse($"the recovery key '{RootDisplayPath()}' has an unmappable ACE");

            var isAdmins = sid.Equals(AdminsSid);
            var isSystem = sid.Equals(SystemSid);
            if (!isAdmins && !isSystem)
                foreignAllow = true;
            if (isAdmins && rule.RegistryRights.HasFlag(RegistryRights.FullControl))
                adminsFull = true;
            if (isSystem && rule.RegistryRights.HasFlag(RegistryRights.FullControl))
                systemFull = true;
        }

        if (!adminsFull || !systemFull || foreignAllow || denyPresent)
            throw Refuse($"the recovery key '{RootDisplayPath()}' has an unexpected DACL shape");
    }

    private static RegistrySecurity CreateProtectedRegistrySecurity()
    {
        var security = new RegistrySecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new RegistryAccessRule(
            AdminsSid, RegistryRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new RegistryAccessRule(
            SystemSid, RegistryRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static string RootDisplayPath() => @"HKLM\" + RootPath;

    private static ConfigurationUnavailableException Refuse(string message, Exception? inner = null)
    {
        return new ConfigurationUnavailableException(
            $"Recovery journal unavailable: {message}.",
            "An administrator must repair or delete the value under HKLM\\SOFTWARE\\ProcessGuardianAdmin\\Recovery " +
            "(owner must be BUILTIN\\Administrators or SYSTEM, inheritance disabled, FullControl for those two SIDs only), " +
            "then start the application again.",
            inner);
    }
}
