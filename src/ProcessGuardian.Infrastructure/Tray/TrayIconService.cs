using System.Drawing;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.Logging;
using ProcessGuardian.Domain;
using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Domain.Interfaces;

namespace ProcessGuardian.Infrastructure.Tray;

public sealed class TrayIconService : ITrayService
{
    private readonly TaskbarIcon _icon;
    private readonly Icon _icoOn;
    private readonly Icon _icoOff;
    private readonly Icon _icoError;
    // True when the icon was created from an embedded resource and therefore must be
    // disposed. Fallback icons are shared SystemIcons instances owned by the framework.
    private readonly bool _icoOnOwned;
    private readonly bool _icoOffOwned;
    private readonly bool _icoErrorOwned;
    private readonly ILogger<TrayIconService> _logger;
    private readonly TrayMenuPopup _menu;
    private bool _isMonitoring;

    public event EventHandler? ShowRequested;
    public event EventHandler? StartRequested;
    public event EventHandler? StopRequested;
    public event EventHandler? ExitRequested;

    public TrayIconService(ILogger<TrayIconService> logger)
    {
        _logger = logger;

        // Embedded tray icons carry fixed LogicalNames (Infrastructure.csproj) that are
        // independent of the edition's assembly name — do NOT derive them from AppEdition.
        (_icoOn, _icoOnOwned) = LoadIconSafe("ProcessGuardian.Assets.TrayIconOn.ico");
        (_icoOff, _icoOffOwned) = LoadIconSafe("ProcessGuardian.Assets.TrayIconOff.ico");
        (_icoError, _icoErrorOwned) = LoadIconSafe("ProcessGuardian.Assets.TrayIconError.ico");

        _icon = new TaskbarIcon
        {
            Icon = _icoOff,
            ToolTipText = $"{AppEdition.Name} - Stopped",
            Visibility = Visibility.Visible,
        };

        _icon.TrayMouseDoubleClick += (_, __) => ShowRequested?.Invoke(this, EventArgs.Empty);

        _menu = new TrayMenuPopup();
        _menu.ShowClicked += (_, __) => ShowRequested?.Invoke(this, EventArgs.Empty);
        _menu.ExitClicked += (_, __) => ExitRequested?.Invoke(this, EventArgs.Empty);
        _menu.StartStopClicked += (_, __) =>
        {
            if (_isMonitoring)
                StopRequested?.Invoke(this, EventArgs.Empty);
            else
                StartRequested?.Invoke(this, EventArgs.Empty);
        };
        _icon.TrayRightMouseUp += (_, __) => _menu.Open();

        // Initial state: monitoring is off on a cold start.
        ApplyStatus(GuardianStatus.Stopped);
    }

    private (Icon Icon, bool Owned) LoadIconSafe(string resourceName)
    {
        try
        {
            using var stream = typeof(TrayIconService).Assembly.GetManifestResourceStream(resourceName);
            if (stream is not null)
                return (new Icon(stream), true);

            _logger.LogWarning("Embedded icon resource {Resource} not found, using fallback", resourceName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load embedded icon {Resource}, using fallback", resourceName);
        }

        try
        {
            return (System.Drawing.SystemIcons.Application, false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SystemIcons.Application unavailable, using WinLogo fallback");
            return (System.Drawing.SystemIcons.WinLogo, false);
        }
    }

    public void Initialize() { /* Already initialized in ctor */ }

    public void SetStatus(GuardianStatus status)
    {
        try
        {
            _icon.Icon = status switch
            {
                GuardianStatus.Monitoring or GuardianStatus.Waiting or GuardianStatus.Restarting => _icoOn,
                GuardianStatus.Error => _icoError,
                _ => _icoOff,
            };
            _icon.ToolTipText = $"{AppEdition.Name} - {status}";

            ApplyStatus(status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set tray icon status");
        }
    }

    private void ApplyStatus(GuardianStatus status)
    {
        _isMonitoring = status is GuardianStatus.Monitoring or GuardianStatus.Waiting or GuardianStatus.Restarting;
        _menu.SetSecondLabel(_isMonitoring ? "Disable" : "Start monitoring");
    }

    public void Dispose()
    {
        try
        {
            _menu.Close();
            _icon.Dispose();
            if (_icoOnOwned) _icoOn.Dispose();
            if (_icoOffOwned) _icoOff.Dispose();
            if (_icoErrorOwned) _icoError.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing tray icon resources");
        }
    }
}
