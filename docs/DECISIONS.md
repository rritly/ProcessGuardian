# ProcessGuardian — Architecture Decision Log

This file records important project decisions that affect architecture, behavior, deployment, or compatibility.

It is not a task list and does not replace the technical specification.

New decisions should be added only when a meaningful architectural or behavioral choice is made.

## Decision 001 — WinUI 3 / Windows App SDK

**Status:** Accepted

**Decision**

ProcessGuardian is implemented as a Windows 11 desktop application using:
- WinUI 3
- Windows App SDK
- .NET 8
- C#

**Reason**

This is the technology stack defined by the technical specification and provides the required desktop UI, lifecycle, notifications and Windows integration.

## Decision 002 — Unpackaged application

**Status:** Accepted

**Decision**

ProcessGuardian is distributed as an unpackaged Windows App SDK application with self-contained publishing.

**Reason**

The application is intended to run for the current user without requiring a traditional MSIX deployment.

**Consequence**

Deployment must include all dependencies required by the chosen Windows App SDK deployment model.

## Decision 003 — Non-elevated application

**Status:** Accepted

**Decision**

The normal ProcessGuardian process runs with standard user privileges and does not request elevation.

**Reason**

The application should be usable without administrator rights and must support notification and per-user autostart scenarios.

**Consequence**

Features that genuinely require elevation, such as optional advanced Task Scheduler scenarios, must be isolated and explicitly handled rather than making the whole application elevated.

## Decision 004 — One monitored target process

**Status:** Accepted

**Decision**

The initial product version monitors one configured target executable.

**Reason**

The current technical specification defines a single target. Support for multiple targets is future work.

## Decision 005 — Executable path is the primary process identity

**Status:** Accepted

**Decision**

The configured full executable path is the authoritative identity of the target process.

**Reason**

Executable names are not unique. A check based only on `Process.GetProcessesByName()` can report an unrelated process as the target.

**Consequence**

Process existence checks must verify the intended executable rather than relying solely on the process name.

## Decision 006 — Process.Start requires startup verification

**Status:** Accepted

**Decision**

A successful call to `Process.Start()` is not treated as proof that the monitored process successfully started.

**Reason**

The target can start and immediately terminate, fail during initialization, or become discoverable only after a short delay.

**Consequence**

After each restart attempt, ProcessGuardian waits for the configured verification interval and checks whether the intended process is actually running.

## Decision 007 — Single monitoring loop

**Status:** Accepted

**Decision**

There must be at most one active monitoring loop for the target process.

**Reason**

Multiple concurrent loops could trigger duplicate restart attempts, conflicting state changes, duplicated notifications, and incorrect attempt counters.

**Consequence**

Start/Stop and Resume handling must be cancellation-safe and idempotent.

## Decision 008 — Tray icon represents monitoring state

**Status:** Accepted

**Decision**

The notification-area icon is state-dependent:
- `Assets/TrayIconOn.ico` — monitoring active / Start;
- `Assets/TrayIconOff.ico` — monitoring stopped / Stop.

**Reason**

The tray icon provides an immediate visual indication of the application's monitoring mode.

**Consequence**

Tray icon changes must be synchronized with the authoritative application state and must not rely on independent UI flags.

## Decision 009 — App icon is separate from tray icons

**Status:** Accepted

**Decision**

`Assets/AppIcon.ico` is the main application icon.

It is used for the window, title bar, taskbar, Start menu, shortcuts and other system locations where the application icon is required.

It is not used as the normal notification-area icon.

## Decision 010 — Per-user autostart

**Status:** Accepted

**Decision**

The default autostart mechanism is the current user's Registry Run entry.

**Reason**

It works without administrator privileges and fits the application's per-user, non-elevated execution model.

**Consequence**

The implementation must avoid creating duplicate autostart entries through multiple mechanisms unless explicitly requested.

## Decision 011 — Per-user configuration and logs

**Status:** Accepted

**Decision**

Configuration and logs are stored under the current user's application data directory.

**Reason**

The application should not require write access to protected installation directories.

**Consequence**

Configuration and log services must handle missing directories and malformed configuration safely.

## Decision 012 — No separate architecture document initially

**Status:** Accepted

**Decision**

`docs/ProcessGuardian_TZ.md` is the requirements document. This file records decisions that affect implementation.

A separate `ARCHITECTURE.md` is not required at the initial development stage.

**Reason**

The technical specification already defines the initial component architecture. Creating a second architecture document before implementation would duplicate information and create another document that can become inconsistent with the code.

**Consequence**

Create `ARCHITECTURE.md` only when the implemented architecture becomes sufficiently complex that a standalone architecture overview provides value.
