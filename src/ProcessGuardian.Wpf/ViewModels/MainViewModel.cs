using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ProcessGuardian.Application.Services;
using ProcessGuardian.Domain.Entities;
using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Domain.Exceptions;
using ProcessGuardian.Domain.Interfaces;
using ProcessGuardian.Infrastructure.Logging;

namespace ProcessGuardian.Wpf.ViewModels;

/// <summary>
/// UI projection of the controller-owned monitoring state. All persistence — the
/// debounced save, Start/Stop, and the exit path — flows through
/// <see cref="IMonitoringController"/>; this ViewModel never writes the store or the
/// autostart service directly (C+D). The target pin is computed and stored by the
/// controller, not here.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IMonitoringController _controller;
    private readonly INotificationService _notifications;
    private readonly ILogger<MainViewModel> _logger;
    private readonly InMemoryLogSink _logSink;

    // Track previous settings to unsubscribe from PropertyChanged on replacement.
    private AppSettings? _previousSettings;

    [ObservableProperty]
    private AppSettings _settings = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private GuardianStatus _status = GuardianStatus.Stopped;

    [ObservableProperty]
    private bool _isDegraded;

    [ObservableProperty]
    private string _logText = string.Empty;

    // Persistent config-state indicators (A). Non-empty ConfigurationError means the store
    // refused (unsafe location); Start/monitoring must stay disabled. Non-empty ResetNotice
    // reports that untrusted settings were quarantined and replaced with fresh defaults.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _configurationError = string.Empty;

    [ObservableProperty]
    private string _resetNotice = string.Empty;

    // Autostart/recovery error surfaced to the user when a settings commit's autostart step
    // failed or a recovery resolution (A/B) could not be completed. The checkbox never reports
    // "on" while registration is absent; this message explains why explicitly.
    [ObservableProperty]
    private string _autostartError = string.Empty;

    // Checkbox display state: the verified ACTUAL registration (checked only when the controller
    // confirmed an Enabled registration). The persisted intent lives in Settings.AutostartEnabled;
    // this property decouples them so a failed enable never leaves a checked box (2.5).
    [ObservableProperty]
    private bool _autostartEffective;

    // False while a reset notice is pending: ordinary autostart edits are locked until the user
    // resolves the recovery explicitly (2.1).
    [ObservableProperty]
    private bool _isAutostartEditable = true;

    // Guards programmatic AutostartEffective updates so they never look like a user toggle
    // (a programmatic set must not re-schedule a save and loop on an error state).
    private bool _updatingAutostartEffective;

    // Serializes the two recovery resolutions so Keep/Remove cannot run concurrently.
    private bool _resolutionInProgress;

    private CancellationTokenSource? _saveCts;

    // Busy gate for Start/Stop, shared by the window buttons and the tray menu: held
    // until the controller settles Status, so re-clicks are ignored, not queued. The
    // controller serializes internally; this gate is a UX convenience, not the only guard.
    private bool _startStopInProgress;

    private enum PendingOperation { None, Start, Stop }

    private PendingOperation _pendingOperation = PendingOperation.None;
    private TaskCompletionSource<bool>? _statusReady;

    // Failsafe: if Status never settles (controller stuck or errored), the gate is
    // released after this timeout so the user can retry.
    private static readonly TimeSpan BusyFailsafeTimeout = TimeSpan.FromSeconds(10);

    // Set while the initial load assigns Settings: the subscription is wired, but the
    // debounced save must not rewrite settings.json right after it was loaded.
    private bool _suppressSave;

    public MainViewModel(
        IMonitoringController controller,
        INotificationService notifications,
        ILogger<MainViewModel> logger,
        InMemoryLogSink logSink)
    {
        _controller = controller;
        _notifications = notifications;
        _logger = logger;
        _logSink = logSink;

        // Project the controller state channel onto the bindings (C+D). The tray keeps
        // its own subscription through IStatusReporter.
        _controller.StateChanged += (s, state) =>
            OnDispatcher(() =>
            {
                Status = state.Status;
                IsDegraded = state.IsDegraded;
                if (state.SaveFailed)
                    _logger.LogError("Monitoring state: save failed ({Reason})", state.Reason);
            });

        // Autostart actual-state channel: the controller is the single writer; the checkbox and
        // the startup-sync error are driven from the verified registration state (2.4/2.5).
        _controller.AutostartStateChanged += (_, __) => OnDispatcher(ApplyAutostartStateFromController);

        // Update log text when new entries arrive
        _logSink.LogUpdated += (_, __) =>
            OnDispatcher(() => LogText = _logSink.GetRecentLogs(200));

        // Sync current state from the controller so a late-opened window is not stale.
        Status = _controller.CurrentState.Status;
        IsDegraded = _controller.CurrentState.IsDegraded;
        LogText = _logSink.GetRecentLogs(200);

        _ = LoadSettingsAsync();
    }

    private static void OnDispatcher(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null)
            dispatcher.BeginInvoke(action);
        else
            action();
    }

    // Generated partial hook (CommunityToolkit.Mvvm): completes the in-flight
    // readiness wait once the controller settles the expected status.
    partial void OnStatusChanged(GuardianStatus value)
    {
        if (_statusReady is null)
            return;

        if (IsSettled(value))
            _statusReady.TrySetResult(true);
    }

    // True when the controller settled the status expected for the in-flight operation:
    // Start -> Monitoring/Waiting/Restarting/Error; Stop -> Stopped/Error.
    private bool IsSettled(GuardianStatus value)
    {
        return _pendingOperation switch
        {
            PendingOperation.Start => value is GuardianStatus.Monitoring
                or GuardianStatus.Waiting
                or GuardianStatus.Restarting
                or GuardianStatus.Error,
            PendingOperation.Stop => value is GuardianStatus.Stopped or GuardianStatus.Error,
            _ => false,
        };
    }

    // Generated by the source generator; fires AFTER the property value changes.
    // Use _previousSettings to unsubscribe from the old value.
    partial void OnSettingsChanged(AppSettings value)
    {
        if (_previousSettings is not null)
            _previousSettings.PropertyChanged -= OnSettingsPropertyChanged;
        _previousSettings = value;
        value.PropertyChanged += OnSettingsPropertyChanged;
        if (!_suppressSave)
            ScheduleSave();
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            var loaded = await _controller.GetCurrentSettingsAsync();
            _suppressSave = true;
            try
            {
                Settings = loaded; // subscription is wired, but no debounced save on load
            }
            finally
            {
                _suppressSave = false;
            }
        }
        catch (ConfigurationUnavailableException ex)
        {
            // A refused store is an ordinary Error path, never a fatal crash: the user is
            // pointed to recovery and monitoring must not start. The window projects the same
            // Error state as the tray (Status=Error, IsDegraded=false) so a refusal is never
            // shown as a plain STOPPED window (R-A).
            _logger.LogError(ex, "Configuration store refused on load");
            ConfigurationError = ex.Guidance ?? "Configuration is unavailable; apply the guidance and restart.";
            Status = GuardianStatus.Error;
            IsDegraded = false;
            // Install a fallback settings instance for bindings ONLY — never schedule a normal
            // persistence debounce against a refused store (R-A). No CommitSettingsAsync /
            // PersistEnabledAsync / store write may run until initialization succeeds.
            _suppressSave = true;
            try
            {
                Settings = new AppSettings();
            }
            finally
            {
                _suppressSave = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings on startup");
            _suppressSave = true;
            try
            {
                Settings = new AppSettings();
            }
            finally
            {
                _suppressSave = false;
            }
        }
    }

    /// <summary>
    /// Applies the non-fatal store-initialization outcome to the persistent indicators
    /// (called by the boot path after <see cref="IConfigurationStore.InitializeAsync"/>).
    /// </summary>
    public void ApplyInitializationStatus(ConfigurationInitializationStatus status)
    {
        switch (status)
        {
            case ConfigurationInitializationStatus.Refused:
                ConfigurationError =
                    "The elevated configuration is unavailable. An administrator must inspect and repair the " +
                    "elevated configuration (owner BUILTIN\\Administrators or SYSTEM; no reparse points) or the " +
                    "recovery state at HKLM\\SOFTWARE\\ProcessGuardianAdmin\\Recovery, then start the application again.";
                // One coherent ordinary Error state in both the window and the tray: the refusal is an
                // Error, not a plain stopped monitoring session (R-A). IsDegraded is meaningful only in
                // active session states, so a refusal leaves it false.
                Status = GuardianStatus.Error;
                IsDegraded = false;
                break;
            case ConfigurationInitializationStatus.ResetNoticePending:
                ResetNotice =
                    "Settings were reset to safe defaults: the previous configuration was untrusted and has been " +
                    "quarantined. Monitoring is off and the target pin is cleared — start monitoring once to " +
                    "re-select and re-pin the target, then choose how to resolve autostart below.";
                // Until the user resolves the recovery explicitly, autostart edits are locked and the
                // checkbox must not show a verified state (2.1/2.5).
                IsAutostartEditable = false;
                _updatingAutostartEffective = true;
                try
                {
                    AutostartEffective = false;
                }
                finally
                {
                    _updatingAutostartEffective = false;
                }
                NotifyRecoveryCanExecuteChanged();
                break;
        }
    }

    // Exposes the two explicit recovery resolutions. Both require a pending notice; option A
    // (keep autostart) additionally requires a recorded pin so the user must first re-select,
    // re-pin and save the target through the controller.
    private bool CanKeepAutostart =>
        !_resolutionInProgress
        && !string.IsNullOrEmpty(ResetNotice)
        && _controller.HasRecordedPin;

    private bool CanRemoveAutostart =>
        !_resolutionInProgress && !string.IsNullOrEmpty(ResetNotice);

    // The checkbox toggles the DESIRED intent; the controller verifies the actual registration
    // and corrects AutostartEffective on the result / autostart-state channel. A programmatic
    // update (guarded by _updatingAutostartEffective) never re-enters this path.
    partial void OnAutostartEffectiveChanged(bool value)
    {
        if (_updatingAutostartEffective)
            return;
        _suppressSave = true;
        try
        {
            Settings.AutostartEnabled = value;
        }
        finally
        {
            _suppressSave = false;
        }
        _ = CommitNowAsync();
    }

    // Applies the controller's verified autostart state to the checkbox and surfaces a
    // startup-sync failure. Commit-time autostart errors are surfaced by CommitAsync directly
    // (no duplicate toast here); this handler toasts only for the reconciliation-time error.
    private void ApplyAutostartStateFromController()
    {
        if (_controller.AutostartActualState is { } state)
        {
            _updatingAutostartEffective = true;
            try
            {
                AutostartEffective = state == AutostartState.Enabled;
            }
            finally
            {
                _updatingAutostartEffective = false;
            }
        }

        if (!string.IsNullOrEmpty(_controller.AutostartSyncError))
        {
            AutostartError = _controller.AutostartSyncError!;
            _logger.LogWarning("Autostart sync error surfaced: {Error}", _controller.AutostartSyncError);
            if (Settings.EnableToasts)
                _ = _notifications.ShowAsync("Autostart Error", AutostartError);
        }
        else if (_controller.AutostartActualState is not null)
        {
            // A newer, successfully synchronized state supersedes a stale sync error: a one-off
            // startup failure must not linger once the registration is verified again.
            AutostartError = string.Empty;
        }
    }

    [RelayCommand(CanExecute = nameof(CanKeepAutostart))]
    private async Task KeepAutostart()
    {
        if (!BeginResolution())
            return;
        try
        {
            AutostartError = string.Empty;
            var result = await _controller.ResolveRecoveryAsync(
                RecoveryResolutionChoice.KeepAutostart,
                CommitSnapshot(),
                Settings.TargetProcessPath);
            await HandleRecoveryResultAsync(RecoveryResolutionChoice.KeepAutostart, result);
        }
        catch (Exception ex)
        {
            AutostartError = $"Keep-autostart failed: {ex.Message}";
            _logger.LogError(ex, "Keep-autostart resolution failed");
            if (Settings.EnableToasts)
                _ = _notifications.ShowAsync("Recovery Not Complete", AutostartError);
        }
        finally
        {
            EndResolution();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveAutostart))]
    private async Task RemoveAutostart()
    {
        if (!BeginResolution())
            return;
        try
        {
            AutostartError = string.Empty;
            var result = await _controller.ResolveRecoveryAsync(
                RecoveryResolutionChoice.RemoveAutostart,
                CommitSnapshot());
            await HandleRecoveryResultAsync(RecoveryResolutionChoice.RemoveAutostart, result);
        }
        catch (Exception ex)
        {
            AutostartError = $"Remove-autostart failed: {ex.Message}";
            _logger.LogError(ex, "Remove-autostart resolution failed");
            if (Settings.EnableToasts)
                _ = _notifications.ShowAsync("Recovery Not Complete", AutostartError);
        }
        finally
        {
            EndResolution();
        }
    }

    // Claims the recovery gate; returns false when a resolution is already in flight.
    private bool BeginResolution()
    {
        if (_resolutionInProgress)
            return false;
        _resolutionInProgress = true;
        NotifyRecoveryCanExecuteChanged();
        return true;
    }

    private void EndResolution()
    {
        _resolutionInProgress = false;
        NotifyRecoveryCanExecuteChanged();
    }

    private void NotifyRecoveryCanExecuteChanged()
    {
        KeepAutostartCommand.NotifyCanExecuteChanged();
        RemoveAutostartCommand.NotifyCanExecuteChanged();
    }

    private async Task HandleRecoveryResultAsync(RecoveryResolutionChoice choice, RecoveryResolutionResult result)
    {
        NotifyRecoveryCanExecuteChanged();
        if (result.Resolved)
        {
            // A fully-verified resolution clears the notice and unlocks autostart editing; the
            // recovery is complete only after the operations AND the journal clear succeeded.
            ResetNotice = string.Empty;
            AutostartError = string.Empty;
            IsAutostartEditable = true;
            var (title, body) = choice == RecoveryResolutionChoice.KeepAutostart
                ? ("Autostart Kept",
                   "The scheduled task is enabled and the recovery is complete. The app will start " +
                   "automatically at the next logon.")
                : ("Autostart Removed",
                   "The scheduled task was removed and the recovery is complete. The app will not " +
                   "start automatically at the next logon.");
            _logger.LogInformation("Recovery resolution ({Variant}) completed; reset notice cleared", choice);
            NotifyRecoveryCanExecuteChanged();
            if (Settings.EnableToasts)
                _ = _notifications.ShowAsync(title, body);
        }
        else
        {
            // The notice stays pending; the user can retry the same or the other option.
            AutostartError = result.ErrorMessage ?? "The recovery could not be completed; try again.";
            _logger.LogWarning("Recovery not completed ({Variant}): {Error}", choice, AutostartError);
            NotifyRecoveryCanExecuteChanged();
            if (Settings.EnableToasts)
                _ = _notifications.ShowAsync("Recovery Not Complete", AutostartError);
        }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressSave)
            return;
        ScheduleSave();
    }

    private CommittedSettingsSnapshot CommitSnapshot() =>
        CommittedSettingsSnapshot.From(Settings);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        if (!BeginOperation(PendingOperation.Start))
            return;
        try
        {
            // The controller validates the path, computes the pin, persists canonical path
            // + pin + enabled together, and only then starts the session (C+D/K).
            var request = new StartRequest(CommitSnapshot(), Settings.TargetProcessPath);
            var result = await _controller.StartAsync(request);
            if (!result.Started)
            {
                _logger.LogWarning("Start rejected: {Reason}", result.Reason);
                if (Settings.EnableToasts)
                    _ = _notifications.ShowAsync("Monitoring Not Started", result.Reason ?? string.Empty);
                return; // fail-closed: no session, no loop
            }

            _logger.LogInformation("Start Monitoring: path={Path}", Settings.TargetProcessPath);
            if (Settings.EnableToasts)
                _ = _notifications.ShowAsync("Monitoring Started", string.Empty);
            await WaitUntilReadyAsync();
            // A Start records the target pin, which may enable the keep-autostart recovery option.
            NotifyRecoveryCanExecuteChanged();
        }
        finally
        {
            EndOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task Stop()
    {
        if (!BeginOperation(PendingOperation.Stop))
            return;
        try
        {
            _logger.LogInformation("Stop Monitoring: path={Path}", Settings.TargetProcessPath);
            if (Settings.EnableToasts)
                _ = _notifications.ShowAsync("Monitoring Stopped", string.Empty);
            var result = await _controller.StopAsync(CommitSnapshot());
            if (result.RuntimeStopped && !result.SettingsSaved)
            {
                // No durably persisted off-state; the runtime is stopped but the UI must
                // not present an unqualified success (C+D/F).
                _logger.LogError(result.PersistenceError, "Stop: runtime stopped but enabled=false was not persisted");
            }
            await WaitUntilReadyAsync();
        }
        finally
        {
            EndOperation();
        }
    }

    // Claims the busy gate. Returns false when an operation is already in flight, so a
    // repeated click during a not-yet-ready operation is ignored rather than queued.
    private bool BeginOperation(PendingOperation op)
    {
        if (_startStopInProgress)
            return false;
        _startStopInProgress = true;
        _pendingOperation = op;
        _statusReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        NotifyStartStopCanExecuteChanged();
        return true;
    }

    private void EndOperation()
    {
        _startStopInProgress = false;
        _pendingOperation = PendingOperation.None;
        _statusReady = null;
        NotifyStartStopCanExecuteChanged();
    }

    private void NotifyStartStopCanExecuteChanged()
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    // Waits asynchronously until the controller settles Status for the accepted
    // operation, or until the failsafe timeout releases the busy gate.
    private async Task WaitUntilReadyAsync()
    {
        // Status may already be settled; check first so we do not wait for a change
        // that will never arrive.
        if (IsSettled(Status))
            return;

        var ready = _statusReady!.Task;
        var timeout = Task.Delay(BusyFailsafeTimeout);
        if (await Task.WhenAny(ready, timeout) == timeout)
        {
            _logger.LogWarning("Start/Stop readiness not reached within {Timeout}; releasing busy gate", BusyFailsafeTimeout);
        }
    }

    private bool CanStart => !_startStopInProgress
        && string.IsNullOrEmpty(ConfigurationError)
        && Status is GuardianStatus.Stopped or GuardianStatus.Error;
    private bool CanStop => !_startStopInProgress && Status is GuardianStatus.Monitoring or GuardianStatus.Waiting or GuardianStatus.Restarting;

    [RelayCommand]
    private void BrowseExecutable()
    {
        try
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Executable files (*.exe)|*.exe",
                Title = "Select target executable"
            };
            if (dialog.ShowDialog() == true)
            {
                Settings.TargetProcessPath = dialog.FileName;
                // A committed path change in the stopped state clears the pin through the
                // controller; keystrokes (uncommitted draft) do not.
                _ = CommitNowAsync();
                // A path re-selection clears the recorded pin, possibly disabling keep-autostart.
                NotifyRecoveryCanExecuteChanged();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BrowseExecutable dialog failed");
        }
    }

    [RelayCommand]
    private void OpenLogs()
    {
        try
        {
            if (!File.Exists(_logSink.LogFilePath))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_logSink.LogFilePath)!);
                    File.WriteAllText(_logSink.LogFilePath, string.Empty);
                }
                catch
                {
                    // Creating the file is best-effort; Process.Start below may still work.
                }
            }
            Process.Start(new ProcessStartInfo { FileName = _logSink.LogFilePath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open log file {Path}", _logSink.LogFilePath);
        }
    }

    [RelayCommand]
    private void ClearLogs()
    {
        try
        {
            _logSink.Clear();
            LogText = _logSink.GetRecentLogs(200);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear logs");
        }
    }

    // Debounced persist: commit 500 ms after the last settings change, through the
    // controller (single writer). The controller rejects a target-path change while a
    // session is active, so a late debounced save can never retarget a running session.
    private void ScheduleSave()
    {
        _saveCts?.Cancel();
        _saveCts = new CancellationTokenSource();
        var token = _saveCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, token);
                await CommitNowAsync();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background commit failed");
            }
        }, token);
    }

    // Cancel the pending debounce and commit immediately so the controller persists the
    // latest committed fields; the returned task is awaited to keep the busy gate held.
    private Task CommitNowAsync()
    {
        _saveCts?.Cancel();
        _saveCts = new CancellationTokenSource();
        return Task.Run(CommitAsync);
    }

    private async Task CommitAsync()
    {
        try
        {
            var result = await _controller.CommitSettingsAsync(CommitSnapshot(), Settings.TargetProcessPath);
            if (!result.Saved)
            {
                // The persistence step itself failed; the failure is already surfaced through the
                // controller's StateChanged (SaveFailed). Log it here too.
                _logger.LogError("Settings commit: settings save failed");
            }

            if (result.Autostart is { Succeeded: false } autostart)
            {
                // A failed autostart step is never masked by a successful settings save: surface
                // the explicit error so the checkbox never reads "on" without real registration.
                AutostartError = autostart.ErrorMessage ?? "Autostart update failed";
                _logger.LogError("Settings commit: autostart failed: {Error}", AutostartError);
                if (autostart.ActualState != AutostartState.Enabled)
                {
                    // Revert the displayed + persisted intent to the verified actual state so the
                    // checkbox never reads "on" and saved settings stay consistent — programmatic,
                    // so no debounce/save loop is scheduled (2.5).
                    _updatingAutostartEffective = true;
                    try
                    {
                        AutostartEffective = false;
                    }
                    finally
                    {
                        _updatingAutostartEffective = false;
                    }
                    _suppressSave = true;
                    try
                    {
                        if (Settings.AutostartEnabled)
                            Settings.AutostartEnabled = false;
                    }
                    finally
                    {
                        _suppressSave = false;
                    }
                }
                if (Settings.EnableToasts)
                    _ = _notifications.ShowAsync("Autostart Error", AutostartError);
            }
            else if (result.Autostart is { Succeeded: true } autostartOk)
            {
                // A verified autostart success clears any earlier stale error and confirms the
                // checkbox against the actual registration state.
                AutostartError = string.Empty;
                _updatingAutostartEffective = true;
                try
                {
                    AutostartEffective = autostartOk.ActualState == AutostartState.Enabled;
                }
                finally
                {
                    _updatingAutostartEffective = false;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Settings commit failed");
        }
    }

    // Stop monitoring and persist before the process exits; on a user Exit the target is
    // stopped through the controller (which also removes the autostart registration
    // following the persisted AutostartEnabled flag).
    public async Task StopMonitoringAndSaveAsync()
    {
        try
        {
            _saveCts?.Cancel();
            await _controller.StopAsync(CommitSnapshot());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop and persist monitoring on exit");
        }
    }

    // WPF never disposes the DataContext; App.xaml.cs must call this explicitly on shutdown.
    public async Task DisposeAsync()
    {
        _saveCts?.Cancel();
        if (_previousSettings is not null)
            _previousSettings.PropertyChanged -= OnSettingsPropertyChanged;
        try
        {
            _logSink.Flush();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Log flush on dispose failed");
        }
        await Task.CompletedTask;
    }
}
