using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ProcessGuardian.Domain.Exceptions;
using ProcessGuardian.Infrastructure.Configuration;

namespace ProcessGuardian.Infrastructure.Security;

/// <summary>Result of an owner/DACL/reparse evaluation of one config/log object.</summary>
public enum ProtectionClassification
{
    /// <summary>Owner is exactly Administrators or SYSTEM, hardened DACL, no reparse.</summary>
    Trusted,
    /// <summary>Weak owner/DACL (foreign owner, missing rights, unexpected deny/foreign allow) — quarantine-and-recreate.</summary>
    Untrusted,
    /// <summary>Reparse indirection or unreadable security — refuse all I/O for this object/path.</summary>
    UnsafeLocation,
}

/// <summary>
/// The elevated edition's object-protection engine. Confines the raw Win32 security
/// interop (create-exclusive directory, born-hardened descriptors with a trusted
/// owner, SeRestorePrivilege scoping, the handle-pinned parent-container repair, and
/// the normalized known-template DACL check).
///
/// The decision logic is unit-testable through fabricated <see cref="FileSystemSecurity"/>
/// instances; the live-ACL behavior is validated by the manual elevated checklist (CI
/// runs non-elevated and cannot prove a real DACL/owner).
/// </summary>
public static class ElevatedSecurity
{
    public static readonly SecurityIdentifier AdminsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    public static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);

    // --- Descriptor builders -------------------------------------------------

    /// <summary>Born-hardened directory descriptor: inheritance disabled, Administrators +
    /// SYSTEM FullControl, owner = Administrators. Never left to the creator's default ACL.</summary>
    public static DirectorySecurity CreateProtectedDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        AddHardenedRules(security, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        security.SetOwner(AdminsSid);
        return security;
    }

    /// <summary>Born-hardened file descriptor (model (a)): explicit DACL + trusted owner at creation.</summary>
    public static FileSecurity CreateProtectedFileSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        AddHardenedRules(security, InheritanceFlags.None);
        security.SetOwner(AdminsSid);
        return security;
    }

    private static void AddHardenedRules(FileSystemSecurity security, InheritanceFlags inheritance)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            AdminsSid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            SystemSid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
    }

    public static bool IsTrustedOwner(SecurityIdentifier? owner) =>
        owner is not null && (owner.Equals(AdminsSid) || owner.Equals(SystemSid));

    // --- Normalized known-template check (not a universal effective-access analyzer) --

    /// <summary>
    /// Verifies three facts directly: (1) required Administrators/SYSTEM allow rights are
    /// present, (2) no foreign allow permission for any other SID, (3) no unexpected deny
    /// ACEs. Inheritance must be disabled. A foreign owner never passes.
    /// </summary>
    public static ProtectionClassification ClassifyFileSystemSecurity(FileSystemSecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !IsTrustedOwner(owner))
            return ProtectionClassification.Untrusted;

        if (!security.AreAccessRulesProtected)
            return ProtectionClassification.Untrusted;

        // Dereferenced SIDs: the caller must be able to map them.
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                            .Cast<FileSystemAccessRule>().ToList();

        var adminsAllow = false;
        var systemAllow = false;
        var foreignAllow = false;
        var hasDeny = false;

        foreach (var rule in rules)
        {
            if (rule.AccessControlType == AccessControlType.Deny)
            {
                // A deny ACE does not grant anything, but an unexpected deny can shadow
                // the required rights or alter the expected ACL shape — never trusted.
                hasDeny = true;
                continue;
            }

            var sid = rule.IdentityReference as SecurityIdentifier;
            var isAdmins = sid is not null && sid.Equals(AdminsSid);
            var isSystem = sid is not null && sid.Equals(SystemSid);

            if (!isAdmins && !isSystem)
                foreignAllow = true;

            if (isAdmins && rule.FileSystemRights.HasFlag(FileSystemRights.FullControl))
                adminsAllow = true;
            if (isSystem && rule.FileSystemRights.HasFlag(FileSystemRights.FullControl))
                systemAllow = true;
        }

        if (!adminsAllow || !systemAllow || foreignAllow || hasDeny)
            return ProtectionClassification.Untrusted;

        return ProtectionClassification.Trusted;
    }

    /// <summary>
    /// Classifies an existing object on disk. A reparse (or an unreadable owner/DACL)
    /// is <see cref="ProtectionClassification.UnsafeLocation"/>; a weak owner/DACL is
    /// <see cref="ProtectionClassification.Untrusted"/> (quarantine-and-recreate).
    /// </summary>
    public static ProtectionClassification ClassifyPath(string path, bool isDirectory)
    {
        if (HasReparsePoint(path))
            return ProtectionClassification.UnsafeLocation;

        FileSystemSecurity? security;
        try
        {
            security = isDirectory
                ? new DirectoryInfo(path).GetAccessControl()
                : new FileInfo(path).GetAccessControl();
        }
        catch (UnauthorizedAccessException)
        {
            // READ_CONTROL denied → fail closed, never "unknown → proceed".
            return ProtectionClassification.UnsafeLocation;
        }
        catch (IOException)
        {
            return ProtectionClassification.UnsafeLocation;
        }

        return ClassifyFileSystemSecurity(security);
    }

    public static bool HasReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false; // a missing object is not "a reparse"; existence is decided elsewhere
        }
    }

    // --- Create-exclusive ----------------------------------------------------

    /// <summary>
    /// Creates a directory with the protected descriptor supplied AT CREATION (not applied
    /// afterwards) and a trusted owner. If the directory already exists this is a collision,
    /// not a silent success (<c>alreadyExists=true</c>).
    /// </summary>
    public static bool TryCreateDirectoryExclusive(string path, DirectorySecurity security, out bool alreadyExists)
    {
        var binary = security.GetSecurityDescriptorBinaryForm();
        var sa = new Native.SECURITY_ATTRIBUTES
        {
            nLength = (uint)Marshal.SizeOf<Native.SECURITY_ATTRIBUTES>(),
        };
        sa.lpSecurityDescriptor = Marshal.AllocHGlobal(binary.Length);
        try
        {
            Marshal.Copy(binary, 0, sa.lpSecurityDescriptor, binary.Length);
            using (EnableRestorePrivilege())
            {
                if (Native.CreateDirectoryW(path, ref sa))
                {
                    alreadyExists = false;
                    return true;
                }
            }
            var error = Marshal.GetLastWin32Error();
            if (error == Native.ERROR_ALREADY_EXISTS)
            {
                alreadyExists = true;
                return false;
            }
            throw new Win32Exception(error);
        }
        finally
        {
            Marshal.FreeHGlobal(sa.lpSecurityDescriptor);
        }
    }

    /// <summary>
    /// Creates a file with an explicit protected descriptor and trusted owner via native
    /// CreateFileW with a SECURITY_ATTRIBUTES descriptor supplied AT CREATION, fail-closed
    /// on an existing path (CREATE_NEW) — model (a): no window in which the file exists under
    /// the creator's default ACL. The returned stream owns the handle; callers write and dispose.
    /// </summary>
    public static FileStream OpenCreateProtectedFile(string path, FileSecurity security)
    {
        var binary = security.GetSecurityDescriptorBinaryForm();
        var descriptor = Marshal.AllocHGlobal(binary.Length);
        var attributes = new Native.SECURITY_ATTRIBUTES
        {
            nLength = (uint)Marshal.SizeOf<Native.SECURITY_ATTRIBUTES>(),
            lpSecurityDescriptor = descriptor,
        };
        Marshal.Copy(binary, 0, descriptor, binary.Length);
        var attributesPtr = Marshal.AllocHGlobal(Marshal.SizeOf(attributes));
        var privilege = EnableRestorePrivilege();
        try
        {
            Marshal.StructureToPtr(attributes, attributesPtr, false);
            var handle = Native.CreateFileW(
                path,
                Native.GENERIC_WRITE,
                Native.FILE_SHARE_NONE,
                attributesPtr,
                Native.CREATE_NEW,
                Native.FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return new FileStream(handle, FileAccess.Write);
        }
        finally
        {
            privilege?.Dispose();
            Marshal.FreeHGlobal(attributesPtr);
            Marshal.FreeHGlobal(descriptor);
        }
    }

    // --- Privilege scoping ---------------------------------------------------

    /// <summary>
    /// Enables SeRestorePrivilege for the scope of the returned handle (disable on Dispose).
    /// Returns null when the privilege is not present (non-admin) — callers must then keep
    /// failing closed via post-create verification.
    /// </summary>
    public static IDisposable? EnableRestorePrivilege()
    {
        if (!Native.OpenProcessToken(Native.GetCurrentProcess(), Native.TOKEN_QUERY | Native.TOKEN_ADJUST_PRIVILEGES, out var tokenHandle))
            return null;
        if (!Native.LookupPrivilegeValueW(null, Native.SE_RESTORE_NAME, out var luid))
            return null;
        var tp = new Native.TOKEN_PRIVILEGES { PrivilegeCount = 1 };
        tp.Privileges.Luid = luid;
        tp.Privileges.Attributes = Native.SE_PRIVILEGE_ENABLED;
        if (!Native.AdjustTokenPrivileges(tokenHandle, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
            return null;
        // A "success" that silently did not enable the privilege (ERROR_NOT_ALL_PRIVILEGES_SET)
        // is indistinguishable here; post-create verification is the backstop.
        return new RestorePrivilegeScope(tokenHandle, luid);
    }

    private sealed class RestorePrivilegeScope : IDisposable
    {
        private readonly IntPtr _token;
        private readonly Native.LUID _luid;

        public RestorePrivilegeScope(IntPtr token, Native.LUID luid)
        {
            _token = token;
            _luid = luid;
        }

        public void Dispose()
        {
            var tp = new Native.TOKEN_PRIVILEGES { PrivilegeCount = 1 };
            tp.Privileges.Luid = _luid;
            tp.Privileges.Attributes = 0;
            _ = Native.AdjustTokenPrivileges(_token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
    }

    // --- Parent-container bootstrap (handle-pinned) --------------------------

    /// <summary>
    /// Ensures the elevated ProgramData root is born-protected (clean first run) or
    /// verified/repaired through the handle-pinned protocol (existing legacy parent).
    /// Fails closed (refuses initialization) when identity cannot be pinned, the owner/DACL
    /// cannot be verified or repaired, or a reparse/ambiguous ancestor would divert the path.
    /// Never repairs, renames, or deletes any child while it runs.
    /// </summary>
    public static void EnsureParentContainer(string parentPath)
    {
        var full = Path.GetFullPath(parentPath);
        VerifyAncestorChain(full); // reparse/ambiguity anywhere above -> refuse before any child I/O

        if (!Directory.Exists(full))
        {
            // Clean first run: create-exclusive with the protected descriptor + trusted owner.
            CreateDirectoryExclusive(full);
            return;
        }

        using var handle = OpenParentHandlePinned(full); // fail closed if it cannot be pinned
        VerifyHandleIdentity(handle, full); // volume/path identity still resolves at the expected path

        // Owner/DACL read is by name, but the pinned handle precludes delete/rename of the
        // same object, so the follow-up read refers to the very object we pinned.
        var current = ClassifyPath(full, isDirectory: true);
        if (current == ProtectionClassification.Trusted)
            return;

        if (current == ProtectionClassification.UnsafeLocation)
            throw Refuse(full, $"parent container is a reparse point or its security cannot be read");

        // Legacy parent with a weak owner/DACL: the only allowed in-place repair is through
        // this non-replaceable handle. Never via SeTakeOwnershipPrivilege on a child.
        ApplyProtectedDescriptorToHandle(handle);

        var after = ClassifyPath(full, isDirectory: true);
        if (after != ProtectionClassification.Trusted)
            throw Refuse(full, "parent container repair did not converge to a trusted owner/DACL");
    }

    private static void CreateDirectoryExclusive(string full)
    {
        if (HasReparsePoint(full))
            throw Refuse(full, "the parent container path must not be a reparse point");

        var security = CreateProtectedDirectorySecurity();
        if (TryCreateDirectoryExclusive(full, security, out var collision))
        {
            // Mandatory post-create verification: a created-but-not-verified object is unusable.
            if (ClassifyPath(full, isDirectory: true) != ProtectionClassification.Trusted)
                throw Refuse(full, "parent container did not verify as trusted after creation");
            return;
        }
        if (collision)
            throw Refuse(full, "the parent container already exists and cannot be verified as trusted");
        throw Refuse(full, "parent container creation failed");
    }

    private static SafeFileHandle OpenParentHandlePinned(string full)
    {
        var handle = Native.CreateFileW(
            full,
            Native.GENERIC_READ,
            Native.FILE_SHARE_READ, // no write and no delete/rename while the repair runs
            IntPtr.Zero,
            Native.OPEN_EXISTING,
            Native.FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);
        if (handle.IsInvalid)
            throw Refuse(full, $"cannot pin the parent container handle (Win32 error {Marshal.GetLastWin32Error()})");
        return handle;
    }

    private static void VerifyHandleIdentity(SafeFileHandle handle, string expectedPath)
    {
        if (!Native.GetFileInformationByHandle(handle, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        if ((info.dwFileAttributes & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
            throw Refuse(expectedPath, "the parent container is a reparse point");

        var sb = new StringBuilder(512);
        uint length = Native.GetFinalPathNameByHandleW(handle, sb, (uint)sb.Capacity, 0);
        if (length == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (length > sb.Capacity)
        {
            sb.Capacity = (int)length;
            length = Native.GetFinalPathNameByHandleW(handle, sb, (uint)sb.Capacity, 0);
        }
        var resolved = sb.ToString();
        if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal))
            resolved = resolved.Substring(4);

        if (!string.Equals(resolved.TrimEnd('\\'), Path.GetFullPath(expectedPath).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw Refuse(expectedPath, $"path identity mismatch (resolved to '{resolved}')");
    }

    private static void ApplyProtectedDescriptorToHandle(SafeFileHandle handle)
    {
        var security = CreateProtectedDirectorySecurity();
        var binary = security.GetSecurityDescriptorBinaryForm();
        var buffer = Marshal.AllocHGlobal(binary.Length);
        try
        {
            Marshal.Copy(binary, 0, buffer, binary.Length);
            if (!Native.GetSecurityDescriptorOwner(buffer, out var ownerPtr, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!Native.GetSecurityDescriptorDacl(buffer, out var daclPresent, out var daclPtr, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!daclPresent)
                throw Refuse(null, "protected security descriptor unexpectedly has no DACL");

            using (EnableRestorePrivilege())
            {
                uint rc = Native.SetSecurityInfo(
                    handle.DangerousGetHandle(),
                    Native.SE_FILE_OBJECT,
                    Native.OWNER_SECURITY_INFORMATION | Native.DACL_SECURITY_INFORMATION | Native.PROTECTED_DACL_SECURITY_INFORMATION,
                    ownerPtr,
                    IntPtr.Zero,
                    daclPtr,
                    IntPtr.Zero);
                if (rc != 0)
                    throw new Win32Exception((int)rc);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void VerifyAncestorChain(string full)
    {
        var current = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(current))
            throw Refuse(full, "the parent container has no parent directory");
        while (!string.IsNullOrEmpty(current))
        {
            if (!string.Equals(Path.GetPathRoot(current), current, StringComparison.OrdinalIgnoreCase))
            {
                // Any segment that could divert the derived path and is a reparse/link is unsafe.
                if (HasReparsePoint(current))
                    throw Refuse(full, $"path segment '{current}' is a reparse point (could divert the app root)");
            }
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent;
        }
    }

    private static ConfigurationUnavailableException Refuse(string? path, string reason)
    {
        var suffix = path is null ? string.Empty : $" ({path})";
        return new ConfigurationUnavailableException(
            $"Elevated configuration is unavailable{suffix}: {reason}.",
            "An administrator must inspect and repair the elevated configuration under " +
            "%ProgramData%\\ProcessGuardianAdmin (owner must be BUILTIN\\Administrators or SYSTEM; " +
            "no reparse points) or the recovery state at HKLM\\SOFTWARE\\ProcessGuardianAdmin\\Recovery, " +
            "then start the application again.");
    }

    // --- Native interop ------------------------------------------------------

    private static class Native
    {
        public const uint ERROR_ALREADY_EXISTS = 183;
        public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
        public const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        public const uint FILE_SHARE_READ = 0x1;
        public const uint FILE_SHARE_NONE = 0x0;
        public const uint OPEN_EXISTING = 3;
        public const uint CREATE_NEW = 1;
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;

        public const uint OWNER_SECURITY_INFORMATION = 0x1;
        public const uint DACL_SECURITY_INFORMATION = 0x4;
        public const uint PROTECTED_DACL_SECURITY_INFORMATION = 0x80000000;
        public const uint SE_FILE_OBJECT = 1;

        public const uint SE_PRIVILEGE_ENABLED = 0x2;
        public const uint TOKEN_QUERY = 0x8;
        public const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
        public const string SE_RESTORE_NAME = "SeRestorePrivilege";

        [StructLayout(LayoutKind.Sequential)]
        public struct SECURITY_ATTRIBUTES
        {
            public uint nLength;
            public IntPtr lpSecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID_AND_ATTRIBUTES
        {
            public LUID Luid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID_AND_ATTRIBUTES Privileges;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BY_HANDLE_FILE_INFORMATION
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint dwVolumeSerialNumber;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint nNumberOfLinks;
            public uint nFileIndexHigh;
            public uint nFileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CreateDirectoryW(string lpPathName, ref SECURITY_ATTRIBUTES lpSecurityAttributes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool LookupPrivilegeValueW(string? lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges, ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool GetSecurityDescriptorOwner(IntPtr pSecurityDescriptor, out IntPtr pOwner, out bool lpbOwnerDefaulted);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool GetSecurityDescriptorDacl(IntPtr pSecurityDescriptor, out bool lpbDaclPresent, out IntPtr pDacl, out bool lpbDaclDefaulted);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint SetSecurityInfo(
            IntPtr handle, uint objectType, uint securityInformation,
            IntPtr psidOwner, IntPtr psidGroup, IntPtr pDacl, IntPtr pSacl);
    }
}
