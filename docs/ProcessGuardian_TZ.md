# Technical Specification: ProcessGuardian for Windows 11

## 1. Purpose and MVP scope

ProcessGuardian is a Windows 11 desktop application built with WinUI 3, Windows App SDK, and .NET 8.

MVP capabilities:

- monitor one user-selected EXE;
- identify the exact configured executable by full path, not by name alone;
- perform a bounded number of restart attempts;
- verify actual startup after each attempt;
- operate in the system tray;
- persist settings and diagnostic logs;
- support per-user autostart;
- support Suspend/Resume;
- run without administrator privileges.

Out of scope for MVP:

- multiple monitored targets;
- PID-based targeting;
- another-user process execution;
- Windows Service/kernel watchdog;
- remote management;
- cross-device synchronization;
- complex Task Scheduler workflows;
- additional user/session process filters.

## 2. Platform and deployment

- Windows 11.
- .NET 8 / C#.
- Target framework: `net8.0-windows10.0.19041.0`.
- WinUI 3 / Windows App SDK.
- Windows App SDK version: `2.3.1`.
- Unpackaged application.
- Self-contained deployment.
- Final MVP distribution: single-file `win-x64`.
- Standard user execution; no MVP elevation.

The final Publish configuration belongs in the project/publish profile and must be validated separately from ordinary Debug/Release Build.

## 3. Solution responsibilities

The current solution is organized as:

- `ProcessGuardian.App` — WinUI UI, lifecycle, activation, tray integration, and composition root.
- `ProcessGuardian.Core` — models, runtime state, pure logic, and service contracts.
- `ProcessGuardian.Services` — monitoring and Windows/platform-specific services.
- `ProcessGuardian.Tests` — automated tests and test doubles.

No `ProcessGuardian.Utils` project is required by the current solution. Create such a project only if a future concrete requirement justifies it.

Dependency direction:

```text
ProcessGuardian.App
    -> ProcessGuardian.Core
    -> ProcessGuardian.Services

ProcessGuardian.Services
    -> ProcessGuardian.Core

ProcessGuardian.Tests
    -> ProcessGuardian.Core
    -> ProcessGuardian.Services
```

`ProcessGuardian.Core` must not depend on WinUI, Windows App SDK, or `System.Diagnostics.Process`.

Business logic must not be placed in `MainWindow`.

## 4. Target process identity

`TargetProcessPath` is the canonical identity.

`TargetProcessName` is a candidate lookup aid only and is not sufficient to identify the target.

The process inspection pipeline must:

1. derive/find candidate processes by name;
2. inspect their executable paths where accessible;
3. handle `Win32Exception`, `UnauthorizedAccessException`, `InvalidOperationException`, `NotSupportedException`, and process termination races;
4. normalize Windows paths;
5. compare executable paths case-insensitively;
6. classify the result as target found, target not found, same-name different-path, or process-information-unavailable.

If process identity cannot be determined safely, automatic restart must not be performed for that monitoring cycle.

A same-name executable in another directory is not the target.

## 5. Monitoring loop and lifecycle

The current controller uses a fixed-rate schedule based on `ITimeProvider.UtcNow` rather than `PeriodicTimer`.

The schedule is anchored to a starting timestamp and advances by `CheckIntervalSeconds`. It must:

- avoid cumulative drift;
- never overlap monitoring checks;
- have at most one active monitoring loop;
- support prompt cancellation;
- allow `StopAsync` to await loop termination.

Do not replace the current fixed-rate scheduling with a different timer mechanism unless a new architectural decision is explicitly accepted.

Logical states include:

- `Stopped`
- `WaitingInitialDelay`
- `Monitoring`
- `Restarting`
- `Error`
- `Cooldown`

`InitialDelaySeconds` is applied when monitoring actually starts. Future Suspend/Resume integration must apply the delay after Resume only when monitoring had been active before Suspend, without creating a second loop or a second independent delay.

Suspend must prevent restart activity. Resume must not be treated as proof that the target stopped.

## 6. Restart policy

For each recovery sequence:

1. determine that the configured target is absent with sufficient confidence;
2. if `MaxRestartAttempts == 0`, do not attempt automatic restart;
3. perform at most `MaxRestartAttempts`;
4. start with the configured full `TargetProcessPath`;
5. set the target directory as `WorkingDirectory`;
6. pass the configured command-line argument string through `ProcessStartInfo.Arguments`;
7. wait up to `StartupVerificationTimeoutSeconds`;
8. verify that the intended executable path actually appeared;
9. on success, reset the current attempt counter and return to `Monitoring`;
10. after all attempts fail, enter `Error`/`Cooldown` according to the established state machine.

`Process.Start()` success alone is never considered successful recovery.

`Process.Start` is a short platform operation. Cancellation does not cancel the call itself; cancellation applies to subsequent verification, retry delay, cooldown, and monitoring operations.

## 7. Configuration

Current persisted settings:

```json
{
  "SchemaVersion": 1,
  "TargetProcessPath": "C:\Path\To\App.exe",
  "TargetProcessName": "App.exe",
  "TargetProcessArguments": "--example value",
  "InitialDelaySeconds": 40,
  "CheckIntervalSeconds": 20,
  "MaxRestartAttempts": 4,
  "RestartDelaySeconds": 3,
  "StartupVerificationTimeoutSeconds": 10,
  "FailureCooldownSeconds": 90,
  "EnableLogging": true,
  "LogBufferSize": 500,
  "AutostartEnabled": true,
  "MonitoringEnabled": false
}
```

`TargetProcessArguments` is currently stored as a single command-line string and passed through `ProcessStartInfo.Arguments`.

The configuration subsystem must:

- load or create defaults;
- validate settings;
- handle missing/corrupt JSON;
- support schema version handling;
- provide safe defaults;
- save safely and atomically;
- use the unpackaged `ApplicationData` storage mechanism established by the implementation.

Current defaults are those defined and tested by the implementation:

- `SchemaVersion = 1`
- `TargetProcessPath = null`
- `TargetProcessName = null`
- `TargetProcessArguments = null`
- `InitialDelaySeconds = 40`
- `CheckIntervalSeconds = 20`
- `MaxRestartAttempts = 4`
- `RestartDelaySeconds = 3`
- `StartupVerificationTimeoutSeconds = 10`
- `FailureCooldownSeconds = 90`
- `EnableLogging = true`
- `LogBufferSize = 500`
- `AutostartEnabled = true`
- `MonitoringEnabled = false`

Do not change defaults or property semantics without an explicit requirement/decision.

## 8. Autostart

Canonical MVP mechanism:

`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`

with `--background`.

`AutostartEnabled` and `MonitoringEnabled` are independent:

- `AutostartEnabled` controls whether ProcessGuardian launches at logon.
- `MonitoringEnabled` controls whether monitoring is active.

Changing one must not silently change the other.

The MVP does not require Startup-folder duplication. Task Scheduler is outside the MVP baseline.

## 9. Notifications

Use Windows App Notifications / `AppNotificationManager` as specified by the implementation target.

At minimum provide notifications for:

- successful recovery;
- exhausted restart attempts;
- significant monitoring errors.

Notification failure must never stop the controller. Use the defined tray fallback when necessary.

## 10. Suspend / Resume

Use the Windows power-event mechanism chosen for the implementation.

Required behavior:

- Suspend prevents restart activity and cancels/pauses pending waits as appropriate.
- Resume re-establishes monitoring only if monitoring was active before Suspend.
- Resume applies `InitialDelaySeconds`.
- Repeated Resume events are idempotent.
- Resume does not create another monitoring loop.

## 11. Single-instance and startup

Use Windows App SDK application-instance management.

Expected behavior:

- the application is single-instance;
- a second launch activates the existing instance and exits;
- a second instance must not create another controller or tray icon;
- `--background` loads configuration and starts monitoring only when `MonitoringEnabled` is true;
- `--background` does not automatically show the main window.

The precise AppInstance integration is part of the lifecycle implementation stage.

## 12. UI

The main window must expose:

- target EXE path and Browse;
- command-line arguments;
- initial/check/restart/verification/cooldown timings;
- maximum restart attempts;
- logging and buffer size;
- Autostart;
- Start / Stop;
- Minimize to tray;
- Exit.

Status must include text, not color alone.

UI state must be derived from `AppState`/ViewModel.

When monitoring is active, closing the main window hides it and leaves the process running in the tray. Explicit Exit shuts down the application.

## 13. FileOpenPicker

Only `.exe` files may be selected.

For WinUI 3 desktop:

1. obtain the window HWND;
2. initialize the picker with that HWND;
3. show the picker;
4. store the selected absolute path;
5. derive the process name;
6. validate the selected file;
7. update configuration/application state.

Use the current Windows App SDK/WinUI mechanism for HWND initialization.

## 14. Tray and assets

Required assets:

```text
Assets/
├── AppIcon.ico
├── TrayIconOn.ico
└── TrayIconOff.ico
```

Semantics:

- `AppIcon.ico` — application surfaces such as window, taskbar, Start menu, and shortcuts.
- `TrayIconOn.ico` — monitoring active.
- `TrayIconOff.ico` — monitoring stopped/unavailable.

`AppIcon.ico` is not the normal tray icon.

All required assets must be included in the project and final publish output. Runtime loading must not depend on the current working directory.

## 15. Logging and storage

`RingLogger` must:

- be thread-safe;
- have a bounded in-memory ring buffer;
- keep the on-disk log bounded;
- use per-user application data storage;
- flush periodically, when the buffer is full, and at shutdown;
- preserve entries added during concurrent flush;
- prevent parallel flush writers;
- retain unsaved entries after a storage failure;
- never crash the application because logging failed;
- avoid recursive self-logging.

Log meaningful lifecycle, state, restart, cooldown, autostart, power-event, configuration, notification, and error events.

Do not log secrets or unnecessary sensitive information.

## 16. Publishing and installation

Final MVP deployment:

- unpackaged;
- self-contained;
- single-file;
- `win-x64`.

The single-file package may extract required Windows App SDK components at runtime. Validate first-run behavior on a clean Windows 11 VM without Visual Studio.

Recommended installation location:

`%LOCALAPPDATA%\ProcessGuardian\`

The base installation must not require administrator privileges.

## 17. Security and resilience

- do not use `Process.Kill()` in the normal recovery flow;
- prefer direct process execution over command shells;
- revalidate the configured executable path;
- handle disappearing files and race conditions;
- treat inaccessible process information conservatively;
- do not store secrets in configuration;
- bound restarts with attempts and cooldown;
- do not add elevation to the MVP.

## 18. Testing requirements

Automated tests must cover at least:

### Configuration
- defaults;
- missing file;
- corrupt JSON;
- schema handling;
- invalid path/extension;
- invalid ranges;
- save/load roundtrip;
- storage/write failures;
- cancellation where applicable.

### Process monitoring
- exact target path;
- same-name executable in another directory;
- inaccessible executable path;
- target missing;
- immediate exit;
- delayed startup;
- startup verification timeout;
- external target appearance during recovery.

### Restart
- first/later successful recovery;
- all attempts fail;
- `MaxRestartAttempts = 0`;
- cooldown;
- no parallel restart sequences.

### Controller lifecycle
- one monitoring loop;
- idempotent Start/Stop;
- cancellation during initial delay, verification, retry delay, and cooldown;
- fixed-rate scheduling without cumulative drift;
- correct AppState transitions;
- logging of critical recovery events.

### Integration and later lifecycle work
- single-instance;
- `--background`;
- autostart;
- Suspend/Resume;
- tray On/Off icons;
- notification fallback;
- clean publish/run;
- reboot and Resume behavior.

## 19. MVP acceptance criteria

The MVP is ready when:

- Release Build and final Publish succeed;
- the exact target EXE is identified by full path;
- restart attempts are bounded and verified;
- there are no duplicate monitoring loops or restart sequences;
- settings persist safely;
- autostart and `--background` work correctly;
- Suspend/Resume does not create duplicate monitoring;
- tray and notifications work;
- logging failures cannot stop monitoring;
- installation and normal operation work without administrator privileges;
- the single-file `win-x64` published application runs correctly on a clean Windows 11 system.
