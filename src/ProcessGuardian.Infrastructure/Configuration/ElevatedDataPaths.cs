using ProcessGuardian.Domain;

namespace ProcessGuardian.Infrastructure.Configuration;

/// <summary>
/// The single derivation of the elevated edition's protected data root, shared by every
/// consumer that must touch that tree (the JSON configuration store and the Task Scheduler
/// XML stager). The root is <c>%ProgramData%\{AppEdition.DataDirName}</c>; child directories
/// (e.g. <c>config</c>) are appended by their owners and are never re-derived independently.
/// The root itself is protected/verified by <see cref="ElevatedSecurity.EnsureParentContainer"/>
/// during elevated configuration initialization.
/// </summary>
internal static class ElevatedDataPaths
{
    /// <summary>The protected elevated data root: <c>%ProgramData%\{AppEdition.DataDirName}</c>.</summary>
    public static string ElevatedRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        AppEdition.DataDirName);
}
