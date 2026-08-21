# ProcessGuardian — Development Context

**Last updated:** 2026-08-21

This is the handover document for continuing ProcessGuardian development in a new Copilot Chat/account.

Requirements belong in `ProcessGuardian_TZ.md`. Accepted architectural and behavioral decisions belong in `DECISIONS.md`. This document describes the current implementation state, verified behavior, remaining work, and practical handover context.

## 1. Current implementation status

### Completed and validated

#### Project foundation
- Real WinUI 3 project created from the Visual Studio WinUI template.
- Solution is `ProcessGuardian.slnx`.
- Current projects:
  - `src/ProcessGuardian.App`
  - `src/ProcessGuardian.Core`
  - `src/ProcessGuardian.Services`
  - `tests/ProcessGuardian.Tests`
- Core dependency direction is preserved.
- Application is configured as unpackaged.
- Windows App SDK target is `2.3.1`.
- Final MVP target is self-contained single-file `win-x64`.

#### Configuration subsystem
Implemented and tested:

- `AppSettings`;
- `AppState`;
- `IConfigService`;
- `ConfigService`;
- validation;
- schema version 1;
- missing/corrupt JSON handling;
- safe atomic save;
- `Microsoft.Windows.Storage.ApplicationData.GetForUnpackaged(...)`;
- per-user `settings.json`;
- composition-root initialization of configuration in `ProcessGuardian.App`.

Configuration defaults currently implemented and tested:

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

#### RingLogger
Implemented and tested:

- `IRingLogger`;
- `LogLevel`;
- `LogEntry`;
- bounded in-memory ring buffer;
- bounded on-disk `log.txt`;
- periodic flush;
- flush when buffer reaches capacity;
- shutdown flush;
- storage failure does not crash the application;
- concurrent writers;
- no parallel flush writers;
- protection against losing entries added during a flush;
- unit tests using existing storage mocks.

#### Process identity and monitoring controller
Implemented and tested:

- `IProcessManager`;
- `ProcessInfo`;
- `ProcessStartRequest`;
- `ProcessStartResult`;
- `ITimeProvider`;
- `ProcessIdentity`;
- `IProcessGuardianController`;
- `SystemProcessManager`;
- `SystemTimeProvider`;
- `ProcessGuardianController`;
- exact target EXE identification by full path;
- same-name different-path handling;
- conservative handling when executable-path information is unavailable;
- `UseShellExecute = false`;
- target directory as `WorkingDirectory`;
- command-line argument passing through `ProcessStartInfo.Arguments`;
- startup verification;
- bounded restart attempts;
- cancellation-safe restart delay and cooldown;
- single monitoring loop;
- single restart sequence;
- idempotent Start/Stop;
- AppState state transitions;
- IRingLogger integration.

The controller's monitoring schedule intentionally uses fixed-rate `ITimeProvider`-based scheduling rather than `PeriodicTimer`. This is an accepted architectural decision recorded in `DECISIONS.md`.

### Latest validated test/build state

The latest Step 5 validation reported:

- `66` tests passed;
- `0` tests failed;
- Debug Build succeeded.

The WinUI application was also successfully started during development; headless agent environments cannot visually verify the window, so local Visual Studio smoke testing remains the authoritative UI check.

Step 4 RingLogger was previously committed and pushed.

Step 5 ProcessGuardianController has been implemented and validated and was reported `READY FOR COMMIT`, but its commit/push status must be confirmed in Git before beginning the next stage.

## 2. Current solution shape

Expected current solution:

```text
ProcessGuardian.slnx

src/
├── ProcessGuardian.App
├── ProcessGuardian.Core
└── ProcessGuardian.Services

tests/
└── ProcessGuardian.Tests
```

Verify the actual solution and `.csproj` files before assuming exact contents.

Expected dependency direction:

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

No `ProcessGuardian.Utils` project is currently required.

## 3. Current technical baseline

- Windows 11.
- .NET 8 / C#.
- WinUI 3 / Windows App SDK 2.3.1.
- `net8.0-windows10.0.19041.0`.
- Unpackaged application.
- Self-contained publishing.
- Final single-file `win-x64` distribution.
- Standard-user execution.

Do not change these baseline decisions during unrelated feature work.

## 4. Important validated behavior

The following behavior is established and should not regress:

### Process identity
- Full executable path is authoritative.
- Process name alone is insufficient.
- Same-name different-path process is not the target.
- Inaccessible process-path information is treated conservatively and does not trigger an unsafe restart.

### Controller
- Process.Start success requires later verification.
- Verification uses the configured startup timeout.
- Verification polling is 250 ms.
- `MaxRestartAttempts = 0` disables automatic restart.
- Restart attempts are bounded.
- Cooldown is cancellation-safe.
- Only one monitoring loop exists.
- Only one restart sequence exists.
- Start/Stop is idempotent.
- Fixed-rate scheduling uses `ITimeProvider` and avoids cumulative drift.
- Process.Start is a direct short synchronous platform call; no unnecessary `Task.Run`.
- Monitoring and restart operations are cancellation-safe.

### Configuration
- `TargetProcessArguments` is currently a single nullable string.
- Configuration uses `ApplicationData.GetForUnpackaged`.
- `settings.json` is persisted under the ProcessGuardian user-local application-data directory.
- Persisted settings are separate from runtime-only AppState.

### Logging
- RingLogger is bounded in memory and on disk.
- Storage failure does not crash the application.
- Concurrent writers and flush races are handled.
- Flush-on-full is explicitly tested.

### UI/project baseline
- Real WinUI 3 application exists and runs locally.
- AppIcon/tray icon roles are separated.
- Final tray integration is still pending.

## 5. Current next stage

The next development stage is:

### Step 6 — Autostart and lifecycle foundation

Expected work:

1. implement/verify `IAutostartService`;
2. implement canonical HKCU Run support with `--background`;
3. keep `AutostartEnabled` independent from `MonitoringEnabled`;
4. implement/verify single-instance startup and activation;
5. define `--background` behavior;
6. integrate lifecycle composition carefully;
7. then implement Suspend/Resume in a later focused stage if that remains the cleanest dependency order.

Do not combine the entire remaining application into one broad refactor.

## 6. Remaining implementation work

After Step 6, remaining major areas include:

- `INotificationService` and Windows App Notifications;
- `IPowerEventService` and Suspend/Resume;
- complete single-instance activation integration if not fully closed in Step 6;
- tray icon and tray command integration;
- complete WinUI UI/ViewModel workflow;
- FileOpenPicker integration;
- final Release Build/Publish validation;
- clean Windows 11 VM verification;
- installer/shortcut validation.

## 7. Verification targets before release

Verify rather than assume:

- Windows App SDK 2.3.1 works with the current Visual Studio toolchain;
- application notifications work for unpackaged deployment;
- single-instance activation is correct;
- `--background` never unexpectedly shows the window;
- Suspend/Resume does not create duplicate controllers/loops;
- single-file first-run extraction works;
- all ICO assets are present and load from the published application;
- application works without Visual Studio;
- application works as a standard user;
- autostart survives reboot;
- installer and shortcuts work;
- complete UI workflow works;
- monitoring survives reboot and Resume as specified.

## 8. Git and handover

Each major stage follows:

```text
Plan
→ implementation
→ focused correction passes if needed
→ Build + Tests
→ Git review
→ user commit
→ user push
→ new Copilot thread
```

Do not assume a stage is committed just because the agent reported `READY FOR COMMIT`.

Before starting a new stage, verify the actual Git state and commit/push status.

## 9. Working rules for a new Copilot thread

At the beginning of a new stage:

1. read:
   - `.github/copilot-instructions.md`
   - `docs/ProcessGuardian_TZ.md`
   - `docs/DECISIONS.md`
   - `docs/DEVELOPMENT_CONTEXT.md`;
2. inspect the actual current solution and code;
3. do not resurrect historical defects unless they are present now;
4. start with `Plan` for non-trivial work;
5. implement with `Agent`;
6. use `Debugger` only for actual runtime/debug problems;
7. use `Git` only for final change review;
8. use `Profiler` only for real performance investigations;
9. do not allow the agent to commit or push automatically.

When the current stage is complete, update this document so the next Copilot thread has an accurate handover.
