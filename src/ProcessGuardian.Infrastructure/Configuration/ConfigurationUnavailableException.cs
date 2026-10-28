namespace ProcessGuardian.Infrastructure.Configuration;

/// <summary>
/// Dedicated exception for the unsafe-location config state (reparse indirection,
/// unverifiable parent container, a quarantine that never converges, or an
/// unreadable owner/DACL). In this state the store refuses all read/write I/O and
/// no settings object is returned. The boot path must route this to the ordinary
/// Error/refusal indicator (never the fatal startup MessageBox).
/// </summary>
public sealed class ConfigurationUnavailableException : Exception
{
    public ConfigurationUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }

    /// <summary>
    /// User-facing guidance for manual administrator recovery.
    /// </summary>
    public string? Guidance { get; }

    public ConfigurationUnavailableException(string message, string guidance, Exception? inner = null)
        : base(message, inner)
    {
        Guidance = guidance;
    }
}
