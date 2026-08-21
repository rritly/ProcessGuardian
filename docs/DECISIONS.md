# ProcessGuardian — Architecture Decision Log

This file contains only accepted decisions that affect architecture, behavior, deployment, or compatibility. Current implementation status belongs in `docs/DEVELOPMENT_CONTEXT.md`.

## Decision 001 — WinUI 3 / Windows App SDK

**Status:** Accepted

ProcessGuardian uses WinUI 3 / Windows App SDK with .NET 8 / C# for Windows 11.

## Decision 002 — Unpackaged self-contained deployment

**Status:** Accepted

The application uses unpackaged Windows App SDK deployment with self-contained publishing.

The final MVP distribution is a single-file `win-x64` executable. Windows App SDK dependencies may be extracted at runtime according to the supported single-file deployment model.

## Decision 003 — Non-elevated MVP

**Status:** Accepted

The normal application runs with standard user privileges. MVP functionality must not require or automatically request elevation.

## Decision 004 — One monitored target

**Status:** Accepted

The MVP monitors one configured executable.

Multiple targets, PID-based targeting, and extended process/session filters are out of scope.

## Decision 005 — Full executable path is process identity

**Status:** Accepted

`TargetProcessPath` is authoritative.

`Process.GetProcessesByName()` is only a preliminary candidate filter and cannot by itself establish target identity.

If process identity cannot be determined safely because executable-path information is unavailable, the controller must not launch a second instance in that monitoring cycle.

## Decision 006 — Restart requires verification

**Status:** Accepted

`Process.Start()` success is not restart success.

After each restart attempt, the intended executable must be verified within `StartupVerificationTimeoutSeconds`.

## Decision 007 — Single monitoring loop and restart sequence

**Status:** Accepted

At most one monitoring loop and one restart sequence may be active.

Start/Stop and future Suspend/Resume integration must be idempotent and cancellation-safe.

## Decision 008 — Fixed-rate controller scheduling

**Status:** Accepted

The controller uses a fixed-rate schedule based on `ITimeProvider.UtcNow` rather than `PeriodicTimer`.

The schedule is anchored to a start timestamp and advances by `CheckIntervalSeconds`, preventing cumulative drift while remaining deterministic for unit tests.

Do not replace this mechanism with another timer implementation without an explicit architectural decision.

## Decision 009 — State-driven tray icons

**Status:** Accepted

`TrayIconOn.ico` represents active monitoring.

`TrayIconOff.ico` represents stopped/unavailable monitoring.

Tray state follows the authoritative application state.

## Decision 010 — Separate application icon

**Status:** Accepted

`AppIcon.ico` is the application icon for the window, taskbar, Start menu, shortcuts, and other application surfaces.

It is not the normal tray icon.

## Decision 011 — HKCU Run is canonical MVP autostart

**Status:** Accepted

Use:

`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`

with `--background` as the canonical MVP autostart mechanism.

The Startup folder is not a required second mechanism. Task Scheduler is outside the MVP baseline.

## Decision 012 — Autostart and monitoring are independent

**Status:** Accepted

`AutostartEnabled` controls whether ProcessGuardian starts at user logon.

`MonitoringEnabled` controls whether the monitoring controller runs.

Neither setting may silently change the other.

## Decision 013 — Command-line arguments are stored as a single string

**Status:** Accepted

The current configuration model stores `TargetProcessArguments` as a single nullable command-line string.

The controller passes this value through `ProcessStartInfo.Arguments`.

This decision matches the current implemented configuration model. If structured argument storage is introduced later, it must be an explicit schema/architecture change.

## Decision 014 — Current Windows App SDK target

**Status:** Accepted

The current project targets Windows App SDK `2.3.1`.

Upgrades must be deliberate and compatibility-tested rather than introduced during unrelated feature work.

## Decision 015 — Per-user configuration and logs

**Status:** Accepted

Configuration and diagnostic logs are stored in the current user's application-data area.

The implementation uses `Microsoft.Windows.Storage.ApplicationData.GetForUnpackaged(...)` for unpackaged application data.

## Decision 016 — Documentation responsibilities

**Status:** Accepted

- `.github/copilot-instructions.md` — permanent Copilot workflow and coding constraints.
- `docs/ProcessGuardian_TZ.md` — product requirements and acceptance criteria.
- `docs/DECISIONS.md` — accepted architectural and behavioral decisions.
- `docs/DEVELOPMENT_CONTEXT.md` — current implementation status, verified behavior, historical findings, risks, and current priorities.

Avoid duplicating large amounts of implementation status across these documents.

## Decision 017 — No separate ARCHITECTURE.md for now

**Status:** Accepted

A separate architecture document is not currently needed because the specification, decision log, and code structure adequately describe the system.

Create `ARCHITECTURE.md` only if the implemented system becomes substantially more complex and such a document adds clear value.
