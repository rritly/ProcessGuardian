using CommunityToolkit.Mvvm.ComponentModel;

namespace ProcessGuardian.Domain.Entities;

public partial class AppSettings : ObservableObject
{
    // Upper bound for every numeric setting; also keeps values inside the 3-char UI boxes.
    private const int MaxNumericValue = 999;

    [ObservableProperty]
    private string _targetProcessPath = string.Empty;

    [ObservableProperty]
    private int _checkIntervalSeconds = 5;

    [ObservableProperty]
    private int _initialDelaySeconds = 0;

    [ObservableProperty]
    private int _maxRestartAttempts = 5;      // 0 = infinite

    [ObservableProperty]
    private int _restartDelaySeconds = 10;

    [ObservableProperty]
    private int _startupVerificationTimeoutSeconds = 5;

    [ObservableProperty]
    private bool _autostartEnabled = false;

    [ObservableProperty]
    private bool _isMonitoringEnabled = false; // Master switch

    [ObservableProperty]
    private bool _enableToasts = true;

    // Range clamps [min, MaxNumericValue]: out-of-range values from settings.json or the
    // UI would hot-loop, restart-spam, or overflow the 3-digit mini boxes. UI + JSON alike.
    partial void OnCheckIntervalSecondsChanged(int value)
    {
        if (value < 1)
            CheckIntervalSeconds = 1;
        else if (value > MaxNumericValue)
            CheckIntervalSeconds = MaxNumericValue;
    }

    partial void OnInitialDelaySecondsChanged(int value)
    {
        if (value < 0)
            InitialDelaySeconds = 0;
        else if (value > MaxNumericValue)
            InitialDelaySeconds = MaxNumericValue;
    }

    partial void OnMaxRestartAttemptsChanged(int value)
    {
        if (value < 0)
            MaxRestartAttempts = 0;
        else if (value > MaxNumericValue)
            MaxRestartAttempts = MaxNumericValue;
    }

    partial void OnRestartDelaySecondsChanged(int value)
    {
        if (value < 0)
            RestartDelaySeconds = 0;
        else if (value > MaxNumericValue)
            RestartDelaySeconds = MaxNumericValue;
    }

    partial void OnStartupVerificationTimeoutSecondsChanged(int value)
    {
        if (value < 1)
            StartupVerificationTimeoutSeconds = 1;
        else if (value > MaxNumericValue)
            StartupVerificationTimeoutSeconds = MaxNumericValue;
    }
}
