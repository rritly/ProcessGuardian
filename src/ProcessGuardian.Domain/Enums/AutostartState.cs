namespace ProcessGuardian.Domain.Enums;

/// <summary>
/// Typed result of reading the autostart registration. Replaces the ambiguous bool of the
/// legacy <c>IsEnabledAsync</c> so the controller can distinguish a reliably registered and
/// correctly-configured autostart, a reliably absent/detached one, a registration that exists
/// but does not match the expected configuration, and a state that could not be read reliably.
/// A read/access failure is <see cref="Unknown"/>, never <see cref="Absent"/>: the recovery and
/// sync policies must not treat an unreadable state as "off" (that would let a destructive
/// disable or a false "verified off" run on bad data).
/// </summary>
public enum AutostartState
{
    /// <summary>The registration state could not be read reliably (query/access/parse failure).</summary>
    Unknown,

    /// <summary>Reliably absent: no registration exists.</summary>
    Absent,

    /// <summary>A registration exists but is disabled (scheduled task present but not enabled).</summary>
    Disabled,

    /// <summary>Registered, enabled, and matches the expected configuration (exe, --background,
    /// logon trigger, run level, interactive logon).</summary>
    Enabled,

    /// <summary>A registration exists (enabled or disabled) but does not match the expected parameters.</summary>
    Misconfigured,
}
