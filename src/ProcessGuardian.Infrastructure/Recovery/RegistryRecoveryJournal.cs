using System.Security.AccessControl;
using Microsoft.Win32;
using ProcessGuardian.Domain.Exceptions;
using ProcessGuardian.Infrastructure.Security;

namespace ProcessGuardian.Infrastructure.Recovery;

/// <summary>
/// HKLM-backed recovery/notice journal. Fixed key: <c>HKLM\SOFTWARE\ProcessGuardianAdmin\Recovery</c>,
/// value <c>State</c> (<c>REG_SZ</c>) = <c>RecoveryInProgress</c> or <c>ResetNoticePending</c>;
/// absent means complete. The key is created with a restrictive descriptor (owner BUILTIN\Administrators
/// or LOCAL SYSTEM, inheritance disabled, FullControl only for those two SIDs) and verified
/// immediately after creation with the same trust rules as A
/// (<see cref="ElevatedSecurity.ClassifyRegistrySecurity"/>); a pre-existing weak or unverifiable key
/// is refused, never silently accepted or repaired in place.
///
/// This runs only in the elevated edition; CI exercises the decision logic through fabricated
/// <see cref="RegistrySecurity"/> instances plus the absent-key read path, and the live ACL is
/// covered by the manual elevated checklist.
/// </summary>
public sealed class RegistryRecoveryJournal : IElevatedRecoveryJournal
{
    private const string RootPath = @"SOFTWARE\ProcessGuardianAdmin\Recovery";
    private const string StateValue = "State";

    /// <summary>
    /// Minimum rights to open an existing key for a write: <c>SetValue</c> covers both
    /// <c>SetValue</c> and <c>DeleteValue</c> (<c>RegDeleteValue</c> requires KEY_SET_VALUE),
    /// <c>ReadPermissions</c> (READ_CONTROL) covers the owner/DACL verification, and
    /// <c>QueryValues</c> covers reading the current <c>State</c> before clearing it.
    /// <c>ChangePermissions</c> is deliberately not requested — it is not a substitute for
    /// <c>SetValue</c>.
    /// </summary>
    internal const RegistryRights WriteRights =
        RegistryRights.QueryValues | RegistryRights.ReadPermissions | RegistryRights.SetValue;

    public Task<RecoveryState> ReadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(RootPath, RegistryKeyPermissionCheck.ReadSubTree,
            RegistryRights.ReadKey | RegistryRights.QueryValues);
        if (key is null)
        {
            // "Absent key = complete" (the plan's absent = complete state). A missing key is not an
            // ACL failure — it is the normal clean first run, so it reports None and initialization
            // proceeds to the normal create/verify path. An existing-but-inaccessible key throws
            // above and is a refusal. The journal never bypasses the independent parent/config
            // protection checks (it is one stage of InitializeElevatedAsync, not a trust gate).
            return Task.FromResult(RecoveryState.None);
        }
        VerifyKeySecurity(key, "read");
        return Task.FromResult(ReadState(key));
    }

    public Task WriteAsync(RecoveryState state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = OpenOrCreateKey(baseKey);
        // Post-create verification happens before any State write: a created key whose owner is the
        // caller's user SID (or whose descriptor did not take) is refused, never used.
        VerifyKeySecurity(key, "write");
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
        // An existing key must be verified as-is; a weak pre-existing key is a refusal, never a
        // silent accept or an in-place ACL overwrite. Open with the minimum write rights so the
        // later SetValue/DeleteValue/GetValue succeed on a trusted key.
        var existing = baseKey.OpenSubKey(RootPath, RegistryKeyPermissionCheck.ReadWriteSubTree, WriteRights);
        if (existing is not null)
            return existing;

        // Create with a descriptor that sets the trusted owner at creation. Setting a non-caller
        // owner may require SeRestorePrivilege; enable it only for this create and revert it
        // immediately (the scoped handle disables it in Dispose). SeTakeOwnershipPrivilege is never
        // used against an existing key or an untrusted config object. If the privilege is not held,
        // post-create verification is the fail-closed backstop (a user-owned key never passes).
        RegistryKey? created;
        using (ElevatedSecurity.EnableRestorePrivilege())
        {
            created = baseKey.CreateSubKey(RootPath, RegistryKeyPermissionCheck.ReadWriteSubTree,
                ElevatedSecurity.CreateProtectedRegistrySecurity());
        }
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

    private static void VerifyKeySecurity(RegistryKey key, string operation)
    {
        RegistrySecurity security;
        try
        {
            security = key.GetAccessControl();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // READ_CONTROL denied → fail closed, never "unknown → proceed".
            throw Refuse($"the recovery key '{RootDisplayPath()}' cannot be read for {operation} (READ_CONTROL denied)", ex);
        }

        // Same trust rules as A: a caller/user-owned key is never trusted.
        if (ElevatedSecurity.ClassifyRegistrySecurity(security) != ProtectionClassification.Trusted)
            throw Refuse($"the recovery key '{RootDisplayPath()}' is not trusted (foreign owner or unexpected DACL)");
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
