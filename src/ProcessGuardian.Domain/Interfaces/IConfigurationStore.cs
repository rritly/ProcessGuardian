using ProcessGuardian.Domain.Entities;

namespace ProcessGuardian.Domain.Interfaces;

/// <summary>
/// Outcome of <see cref="IConfigurationStore.InitializeAsync"/>.
/// </summary>
public enum ConfigurationInitializationStatus
{
    /// <summary>Configuration was loaded as-is (trusted) and the store is ready.</summary>
    Success,

    /// <summary>
    /// The previous (untrusted) configuration was quarantined and fresh defaults were
    /// written. A persistent reset notice is pending until the user explicitly resolves it.
    /// </summary>
    ResetNoticePending,

    /// <summary>
    /// The store refused to read/write (unsafe location, unverifiable parent, or a
    /// quarantine that never converged). No settings object can be obtained; the
    /// boot path must enter the ordinary Error mode and must not start monitoring.
    /// </summary>
    Refused,
}

public interface IConfigurationStore
{
    /// <summary>
    /// Performs all non-fatal initialization (directory/ACL/quarantine work). The
    /// elevated edition runs the quarantine-and-recreate protocol here. Safe to call
    /// multiple times; after a <see cref="ConfigurationInitializationStatus.Refused"/>
    /// result the store is unusable and <see cref="LoadAsync"/>/<see cref="SaveAsync"/>
    /// throw a dedicated exception.
    /// </summary>
    Task<ConfigurationInitializationStatus> InitializeAsync(CancellationToken ct = default);

    Task<AppSettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}
