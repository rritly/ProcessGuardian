namespace ProcessGuardian.Domain;

// Product identity for the two editions built from one codebase. The regular
// build compiles the non-elevated edition; the 'Release-Elevated' configuration
// (ELEVATED_EDITION define) compiles the admin edition: its own executable name,
// requireAdministrator manifest and isolated identity so both editions can run
// side by side (separate mutex, settings, logs, autostart entry, tray).
public static class AppEdition
{
#if ELEVATED_EDITION
    public const string Name = "ProcessGuardianAdmin";
    // Runtime (not const) on purpose: a const false would make the elevated
    // code paths unreachable code (CS0162) under TreatWarningsAsErrors.
    public static readonly bool IsElevated = true;
#else
    public const string Name = "ProcessGuardian";
    public static readonly bool IsElevated = false;
#endif

    public const string DataDirName = Name;
    public const string MutexName = Name + "_SingleInstance";
    public const string ShowEventName = Name + "_ShowRequested";
    public const string AutostartValueName = Name;
    public const string ScheduledTaskName = Name;
    public const string WindowTitle = Name;
}
