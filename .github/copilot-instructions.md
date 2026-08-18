# ProcessGuardian — GitHub Copilot project instructions

## Source of truth

The primary functional requirements are defined in:
`docs/ProcessGuardian_TZ.md`

Before implementing or changing functionality:
1. Read the relevant part of the technical specification.
2. Preserve the specified behavior unless the user explicitly asks to change it.
3. Do not invent product features that are not required by the specification.
4. If the existing code conflicts with the specification, prefer the specification unless the user explicitly directs otherwise.

## Project

ProcessGuardian is a Windows 11 desktop application:
- WinUI 3
- Windows App SDK
- .NET 8
- C#
- unpackaged deployment
- self-contained publish
- non-elevated application

The application monitors a configured target executable and restarts it when the target is no longer running.

## Architecture

Keep responsibilities separated according to the technical specification.

Main responsibilities:
- UI / MainWindow
- AppState
- ViewModels
- ProcessGuardianController
- AutostartService
- NotificationService
- ConfigService
- RingLogger
- supporting helpers / infrastructure

Do not put monitoring, process restart, autostart, configuration persistence, or notification business logic directly into MainWindow.

Prefer small, cohesive services with clear responsibilities.

## Target process identification

The configured executable path is authoritative.

Do not treat `Process.GetProcessesByName()` alone as proof that the configured target executable is running.

When checking whether the configured target is running:
- use the configured executable path as the primary identity;
- account for the fact that multiple processes can have the same executable name;
- handle cases where process information cannot be read because of permissions or the process exits during inspection;
- avoid falsely considering an unrelated executable with the same file name to be the monitored process.

The current product supports one configured target process. Do not implement multi-process monitoring unless explicitly requested.

## Process start and verification

`Process.Start()` is not by itself proof that the target application started successfully.

After starting the target:
1. wait for the configured startup verification period;
2. verify that the intended executable is actually running;
3. only then report restart success.

Startup failure, delayed startup, immediate process exit, and exceptions must be handled without crashing ProcessGuardian.

Use:
- full executable path;
- configured working directory;
- configured command-line arguments.

Do not leave ambiguous alternatives such as "use true or false depending on needs" in the implementation. Choose the behavior required by the concrete scenario and keep it consistent.

## Async and cancellation

- Use `async`/`await` for asynchronous work.
- Use `CancellationToken` for cancellable monitoring and delays where applicable.
- Do not use `Thread.Sleep()` in asynchronous services.
- Avoid blocking the UI thread.
- Ensure Stop cancels monitoring promptly and safely.
- Prevent overlapping monitoring loops or concurrent restart sequences for the same target.

## Initial delay / Resume

The configured initial delay is used after OS startup/autostart and after resume from sleep.

Monitoring must not accidentally create multiple timers/loops after repeated Resume events.

Suspend/Resume handling must be idempotent and cancellation-safe.

## Restart policy

Respect:
- `MaxRestartAttempts`
- `RestartDelaySeconds`
- `FailureCooldownSeconds`
- `InitialDelaySeconds`
- `CheckIntervalSeconds`
- `StartupVerificationTimeoutSeconds`

Every restart attempt must be logged.

## Tray icons

The three ICO files have distinct responsibilities:

- `Assets/AppIcon.ico` — application icon for the window, title bar, taskbar, Start menu, shortcuts and other system locations, except the notification tray.
- `Assets/TrayIconOn.ico` — tray icon when monitoring mode is active / Start.
- `Assets/TrayIconOff.ico` — tray icon when monitoring mode is stopped / Stop.

The tray icon must always reflect the current application monitoring state.

Do not use `AppIcon.ico` as the normal tray icon.

When the monitoring state changes, update the tray icon immediately and keep UI/AppState/tray state synchronized.

## Notifications

Use Windows App Notifications as specified by the technical specification.

The application must remain non-elevated.

If a toast notification cannot be displayed, use the specified tray fallback without crashing the application.

Notification failures must not terminate the monitoring controller.

## Autostart

The default autostart mechanism is the current-user Registry Run entry.

Autostart state and monitoring state are conceptually different:
- monitoring enabled/disabled controls whether ProcessGuardian actively monitors the target;
- autostart enabled/disabled controls whether ProcessGuardian itself launches automatically with Windows.

Do not silently enable or disable one merely because the other changes unless explicitly required by the technical specification or requested by the user.

Avoid creating duplicate autostart mechanisms.

## Configuration

Configuration is stored as JSON under the user's application data directory.

Requirements:
- validate settings before starting monitoring;
- handle missing configuration;
- handle malformed/corrupted JSON;
- maintain schema versioning;
- preserve reasonable defaults;
- do not crash the application because the configuration file is invalid.

Configuration writes should be safe against partial/corrupted writes where practical.

## Logging

RingLogger must be thread-safe.

Logging must never bring down the application.

Respect the configured ring buffer size and persist logs under `%APPDATA%\ProcessGuardian`.

Avoid unbounded log growth.

Log important state transitions, restart attempts, failures, exceptions, autostart changes, and lifecycle events.

Do not log secrets or unnecessary sensitive information.

## UI

The UI must remain responsive.

UI state must be derived from the shared AppState / ViewModel rather than duplicated in unrelated services.

When closing the main window:
- if monitoring is active, hide the window and keep the application running in the tray;
- full application exit is performed through the explicit Exit command.

## Single instance

ProcessGuardian is a single-instance application.

A second launch must not create a second monitoring controller or second tray icon.

Activation from a second launch, toast, or autostart should be routed to the already running instance according to the application lifecycle requirements.

## Error handling

Do not use broad silent `catch` blocks.

When an exception is expected and recoverable:
- handle it;
- log useful diagnostic information;
- keep the application running where appropriate.

Do not expose raw exception details to end users when a clear user-facing message can be provided instead.

## Testing

Important behavior should have automated tests where practical.

At minimum, preserve test coverage for:
- configuration validation;
- process identity checks;
- restart verification;
- cancellation/stop behavior;
- ring logging;
- autostart logic where testable;
- state transitions.

Do not remove tests merely to make a build pass.

## Build quality

The solution should build successfully in Release configuration.

Keep nullable reference type warnings and compiler warnings under control.

Do not introduce unnecessary NuGet dependencies.

Prefer the platform APIs already selected by the technical specification.

## Change discipline

Before making a substantial change:
1. inspect the existing implementation;
2. identify the affected services and dependencies;
3. make the smallest coherent change;
4. build and run relevant tests;
5. check for regressions in related behavior.

Do not rewrite working subsystems without a concrete reason.

When a design decision changes an established behavior or architecture, record it in:
`docs/DECISIONS.md`
