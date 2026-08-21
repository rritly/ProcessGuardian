# ProcessGuardian — GitHub Copilot Project Instructions

## Project context

For substantial work, read these project documents:

1. `docs/ProcessGuardian_TZ.md` — product requirements and acceptance criteria.
2. `docs/DECISIONS.md` — accepted architectural and behavioral decisions.
3. `docs/DEVELOPMENT_CONTEXT.md` — current implementation status, verified behavior, open work, and priorities.

Use the following precedence:

1. The user's explicit current request.
2. The actual current code, solution, and project configuration.
3. `docs/ProcessGuardian_TZ.md`.
4. `docs/DECISIONS.md`.
5. `docs/DEVELOPMENT_CONTEXT.md`.
6. Existing behavior that is not contradicted by the sources above.

If code and documentation appear inconsistent, inspect the actual implementation and the relevant documents before making changes. Do not silently restore obsolete behavior.

## Platform and project architecture

ProcessGuardian is a Windows 11 desktop application using:

- WinUI 3
- Windows App SDK 2.3.1
- .NET 8 / C#
- unpackaged deployment
- self-contained publishing
- final single-file `win-x64` distribution
- standard user privileges

Current solution responsibilities:

- `ProcessGuardian.App` — WinUI UI, application lifecycle, activation, tray integration, and composition root.
- `ProcessGuardian.Core` — models, runtime state, pure logic, and service contracts. It must remain independent of WinUI, Windows App SDK, and `System.Diagnostics.Process`.
- `ProcessGuardian.Services` — monitoring and Windows/platform-specific services.
- `ProcessGuardian.Tests` — automated tests and test doubles.
- `ProcessGuardian.Utils` — use only if such a project actually exists in the current solution; do not create it solely because an older document mentioned it.

Keep business logic out of `MainWindow`. Preserve the established dependency direction:

- App -> Core
- App -> Services
- Services -> Core
- Tests -> Core/Services

Do not introduce circular dependencies.

## Process identity and recovery

`TargetProcessPath` is the authoritative identity of the monitored executable.

`Process.GetProcessesByName()` may be used only as a preliminary candidate filter. Never treat a matching process name alone as proof that the configured target is running.

When inspecting candidates:

- compare normalized Windows executable paths case-insensitively;
- handle `Win32Exception`, `UnauthorizedAccessException`, `InvalidOperationException`, `NotSupportedException`, and processes that terminate during inspection;
- distinguish target found, target not found, same-name different-path, and process-information-unavailable cases;
- if process identity cannot be determined safely, do not launch a second instance merely because the target could not be confirmed.

After `Process.Start()`, recovery is not considered successful until the intended executable is verified within `StartupVerificationTimeoutSeconds`.

Use:

- the configured full executable path as `FileName`;
- the target directory as `WorkingDirectory`;
- the configured command-line argument string through `ProcessStartInfo.Arguments`.

Do not use `cmd.exe`, PowerShell, or `Process.Kill()` in the normal recovery flow.

## Monitoring loop and lifecycle

The current monitoring implementation intentionally uses a fixed-rate schedule anchored to `ITimeProvider.UtcNow` rather than `PeriodicTimer`. Preserve this design unless a future decision explicitly changes it.

The monitoring schedule must:

- respect `CheckIntervalSeconds`;
- avoid cumulative drift;
- never run overlapping monitoring checks;
- have at most one active monitoring loop;
- be cancellation-safe;
- stop promptly.

`StartAsync` must be idempotent. `StopAsync` must cancel and await the active loop and must not leave background tasks running.

`InitialDelaySeconds` is applied by the controller when monitoring actually starts. Future lifecycle integrations must not apply a second independent initial delay.

Suspend/Resume integration is a later stage. Resume must not create a second monitoring loop and must not be interpreted as proof that the target stopped.

Use `async`/`await` and `CancellationToken`. Do not use `Thread.Sleep()` in asynchronous services.

## Restart policy

Respect:

- `InitialDelaySeconds`
- `CheckIntervalSeconds`
- `MaxRestartAttempts`
- `RestartDelaySeconds`
- `StartupVerificationTimeoutSeconds`
- `FailureCooldownSeconds`

Semantics already established:

- `MaxRestartAttempts = 0` disables automatic restart.
- Increment the attempt counter before each actual start attempt.
- Do not exceed the configured maximum.
- A successfully verified restart resets the current attempt counter.
- Exhausted attempts lead to `Error`/`Cooldown` according to the established controller state machine.
- Cooldown is cancellation-safe.
- There must never be more than one restart sequence at a time.

Startup verification uses a cancellation-aware polling loop with a 250 ms polling interval and the configured startup verification timeout.

## State and configuration

`AppState` is the authoritative runtime state.

Persisted configuration belongs in `AppSettings`. Runtime-only state must not be serialized into `settings.json`.

Do not create duplicate sources of truth for:

- `MonitoringEnabled`
- current monitoring status
- restart attempt counter
- error state
- monitoring loop state

The configuration subsystem uses `Microsoft.Windows.Storage.ApplicationData.GetForUnpackaged(...)` for user-local storage.

Do not revert to the old UWP `Windows.Storage.ApplicationData.Current` API.

## Logging

`IRingLogger` is defined in Core and `RingLogger` is implemented in Services.

The logger must:

- be thread-safe;
- keep the in-memory ring bounded by `LogBufferSize`;
- keep the on-disk log bounded by `LogBufferSize` records;
- flush periodically, when the buffer is full, and on shutdown;
- preserve new entries that arrive during a flush;
- avoid parallel flush writers;
- retain buffered entries after write failure so that a later flush can retry;
- never crash the application because logging failed;
- never recursively log its own internal failure through itself.

Do not add another logging framework unless explicitly requested.

## Autostart, single-instance, and notifications

Canonical MVP autostart:

`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`

with `--background`.

`AutostartEnabled` and `MonitoringEnabled` are independent. Do not silently change one when changing the other. Do not add duplicate Startup-folder or Task Scheduler mechanisms to the MVP.

The application is single-instance. A second launch must activate the existing instance rather than create another controller or tray icon. `--background` must not automatically show the main window.

Use Windows App Notifications as specified by the TZ. Notification failures must not stop monitoring; use the defined tray fallback.

## Tray and assets

Required assets:

- `Assets/AppIcon.ico`
- `Assets/TrayIconOn.ico`
- `Assets/TrayIconOff.ico`

Semantics:

- `AppIcon.ico` — application surfaces such as the window, taskbar, Start menu, and shortcuts.
- `TrayIconOn.ico` — monitoring active.
- `TrayIconOff.ico` — monitoring stopped/unavailable.

`AppIcon.ico` is not the normal tray icon. Tray state must follow the authoritative application state.

Runtime resource loading must not depend on the current working directory.

## Testing and change discipline

Every substantial change follows this cycle:

1. inspect the current implementation, interfaces, and tests;
2. compare with the TZ and decisions;
3. create a focused implementation plan when the change is non-trivial;
4. implement the smallest coherent change;
5. build and run relevant tests;
6. inspect regressions;
7. perform a Git review;
8. the user performs the final commit and push.

Do not perform `git commit`, `git push`, or `git branch` unless the user explicitly requests it.

Do not rewrite working subsystems without a concrete reason.

Do not change solution structure, target frameworks, package versions, or publish/deployment settings merely to simplify a task.

Before changing an accepted architectural or behavioral decision, stop and identify the conflict rather than silently overriding it.

## Documentation maintenance

Update `docs/DEVELOPMENT_CONTEXT.md` when implementation status materially changes.

Add a new entry to `docs/DECISIONS.md` only when a meaningful architectural or behavioral decision is accepted.

Keep responsibilities separated:

- `ProcessGuardian_TZ.md` — requirements and acceptance criteria.
- `DECISIONS.md` — accepted architectural/behavioral decisions.
- `DEVELOPMENT_CONTEXT.md` — current implementation status, verified behavior, open work, and handover context.

Do not duplicate large amounts of content between these documents.
