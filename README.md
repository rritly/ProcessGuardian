<a id="readme-top"></a>

<div align="center">

# ProcessGuardian

A lightweight Windows background utility that watches a user-specified executable and
restarts it automatically when it crashes or disappears.

It comes in handy for programs that have no "run at Windows startup" option of their
own, for programs that crash periodically and need to be brought back up automatically,
or for testing. The app also collects event logs you can inspect at any time — handy
for seeing exactly what the guardian did and when.

Built with WPF on .NET 10 using Clean Architecture
(Domain → Application → Infrastructure → Presentation).

[About](#about-the-project) &middot; [Usage](#usage) &middot; [Build &amp; Publish](#build--publish)

</div>

<!-- TABLE OF CONTENTS -->
<details>
  <summary>Table of Contents</summary>
  <ol>
    <li><a href="#about-the-project">About The Project</a></li>
    <li><a href="#built-with">Built With</a></li>
    <li><a href="#usage">Usage</a>
      <ul>
        <li><a href="#running-the-app">Running the App</a></li>
        <li><a href="#main-window">Main Window</a></li>
        <li><a href="#status-badge">Status Badge</a></li>
        <li><a href="#tray">Tray</a></li>
        <li><a href="#toast-cards">Toast Cards</a></li>
        <li><a href="#target-integrity-pin">Target Integrity Pin</a></li>
        <li><a href="#autostart">Autostart</a></li>
        <li><a href="#data-files">Data Files</a></li>
      </ul>
    </li>
    <li><a href="#manual-acceptance-tests">Manual Acceptance Tests</a></li>
    <li><a href="#build--publish">Build &amp; Publish</a>
      <ul>
        <li><a href="#prerequisites">Prerequisites</a></li>
        <li><a href="#installation">Installation</a></li>
      </ul>
    </li>
    <li><a href="#limitations">Limitations</a></li>
    <li><a href="#license">License</a></li>
  </ol>
</details>

<!-- ABOUT THE PROJECT -->

## About The Project

[![Main window][product-screenshot]](assets/mainWindow.png)

ProcessGuardian runs in the system tray and periodically checks that a user-selected
executable is alive. If the target process is not found (crashed, killed, never started),
the guardian launches it again and verifies that it came up within the configured
verification window. Everything is configured from a single window; the app otherwise
lives in the notification-area tray.

Key behaviors:

- **Crash recovery** — the target is polled on a fixed interval and relaunched when it disappears.
- **Binary integrity pin** — the target's SHA-256 hash is recorded when monitoring starts; recovery
  relaunches are verified against that pin, so a swapped or updated binary is never silently executed.
- **Two editions from one codebase** — a regular per-user edition and an elevated admin edition that
  can also monitor targets requiring administrator rights.
- **Fully offline** — self-contained single-file builds; no installer, no external assets
  (tray icons are embedded in the assemblies).

The admin edition can monitor and restart elevated targets (its own checks run with the
same rights); the regular edition cannot.

| Edition | Build configuration | Executable | App manifest | Config file | Autostart mechanism |
|---------|--------------------|------------|--------------|-------------|---------------------|
| Regular | Release | `ProcessGuardian.exe` | default (`asInvoker`) | `%LocalAppData%\ProcessGuardian\settings.json` | HKCU `...\CurrentVersion\Run` |
| Admin | Release-Elevated | `ProcessGuardianAdmin.exe` | `requireAdministrator` | `%ProgramData%\ProcessGuardianAdmin\config\settings.json` (Administrators/SYSTEM-only ACL) | Scheduled task (logon, highest privileges, silent) |

Both editions run side by side: separate executable identity, single-instance mutex, settings, logs, autostart entry and tray icon.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

### Built With

* [.NET 10](https://dotnet.microsoft.com/) — `net10.0-windows10.0.17763.0`, WPF
* [CommunityToolkit.Mvvm](https://www.nuget.org/packages/CommunityToolkit.Mvvm) — MVVM source generators
* [Microsoft.Extensions.Hosting](https://www.nuget.org/packages/Microsoft.Extensions.Hosting) — generic host, DI, `BackgroundService`
* [Serilog](https://serilog.net/) — file logging
* [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon) — system tray icon

<p align="right">(<a href="#readme-top">back to top</a>)</p>

<!-- USAGE -->

## Usage

The repository builds two executables (editions) from the same sources:

* **`ProcessGuardian.exe`** — the regular edition. Runs with the current user's rights and is
  ready for production scenarios where the monitored target does not require administrator rights.
* **`ProcessGuardianAdmin.exe`** — the admin edition, always launched with elevation
  (`requireAdministrator`). Its checks run with administrator rights, so it can also monitor
  and restart elevated targets; its settings file lives in ProgramData with an
  Administrators-only ACL, and its autostart uses a scheduled task. Both editions can run at
  the same time — see the editions table in [About The Project](#about-the-project).

### Running the App

* **Self-contained:** run the exe — a single standalone file, no runtime or external assets needed.
  Native libraries are extracted to `%TEMP%` on first launch, so the first start is slightly slower.
* `--background` — start in the tray without a window (this is how autostart launches the app).
* Closing the window (×) hides it to the tray; the process keeps running and monitoring continues.
* Exit via the **Exit** item in the tray menu.
* Launching the exe a second time activates/shows the window of the already-running instance;
  the new process exits immediately (per-edition single instance, so both editions can run at once).

<p align="right">(<a href="#readme-top">back to top</a>)</p>

### Main Window

![Main window][product-screenshot]

The window consists of three cards.

**Card 1 — Settings**

| Element | Description |
|---------|-------------|
| **Target** + text field | Absolute path to the monitored `.exe`. Editable directly. |
| **＋** (button right of the field) | Opens a file dialog (*Executable files (*.exe)*) and fills the Target field with the selected file. |
| **Check interval (s)** | How often the target process is polled. Range 1–999, default 5. |
| **Verify timeout (s)** | Window for startup verification after a recovery launch. Range 1–999, default 5. |
| **Max restart attempts** | Recovery budget. Range 0–999 (0 = infinite), default 5. |
| **Initial delay (s)** | Pause after Start before the first check. Range 0–999, default 0. |
| **Restart delay (s)** | Delay between recovery attempts. Range 0–999, default 10. |
| **Autostart with Windows** | Checkbox, off by default. See [Autostart](#autostart). |

All numeric boxes accept at most 3 digits; out-of-range values (from the UI or `settings.json`)
are clamped to their range on save. Changes are persisted with a 500 ms debounce.

**Card 2 — Monitoring**

| Element | Description |
|---------|-------------|
| **Start monitoring** | Visible when the status is STOPPED or ERROR. Computes and pins the target's SHA-256 hash, then enables monitoring. Re-clicks while the operation is in flight are ignored. |
| **Disable** | Visible while monitoring is active (badge MONITORING). Stops monitoring and the recovery cycle. |
| **toast-notifications** | Checkbox, on by default. Master switch for the toast cards. |
| **status** badge | Color-coded status, see [Status Badge](#status-badge). |

**Card 3 — Log**

| Element | Description |
|---------|-------------|
| Trash icon button (tooltip **Clear Logs**) | Clears the in-window log and truncates the log file. |
| Book icon button (tooltip **Open Logs**) | Opens the log file in the associated viewer (creates it first if missing). |
| Log area | Read-only, shows the last 200 log entries and auto-updates. |

<p align="right">(<a href="#readme-top">back to top</a>)</p>

### Status Badge

The badge shows one of three states; the tray tooltip shows the exact internal status.

| Badge | Color | Internal statuses | Meaning |
|-------|-------|-------------------|---------|
| `STOPPED` | gray | Stopped | Monitoring is off. |
| `MONITORING` | green | Monitoring, Waiting, Restarting | Polling the target; Waiting = initial delay in progress; Restarting = recovery launch is pending/scheduled. |
| `ERROR` | red | Error | Configuration refused/unavailable, a target-pin mismatch, or the restart budget exhausted; monitoring is disabled. |

Exhausted restart budget (unless *Max restart attempts* = 0) puts the guardian into ERROR and
disables monitoring; pressing **Start monitoring** again re-pins the hash and resets the budget.

A **DEGRADED** indicator appears next to the badge while monitoring is active when the guardian
cannot reliably determine the target's state — e.g. a poll returns `Unknown`, or the target is
transiently missing/unreadable at a relaunch. Degraded is *not* an error: the session keeps
polling, the target stays selected and pinned, and no restart budget is spent. The indicator
turns on only after a sustained condition (two consecutive degraded cycles over a 120-second
window) and its tooltip always shows the explanatory reason, regardless of the toast setting. A
missing or unreadable target at *initial Start* is different: the Start request is rejected
outright — no session and no launch.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

### Tray

The tray icon is always visible; its state mirrors the guardian status:

| Icon | Statuses |
|------|----------|
| Off | Stopped |
| On | Waiting / Monitoring / Restarting |
| Error | Error |

The tooltip is `<Edition name> - <Status>` (e.g. `ProcessGuardian - Monitoring`).

Interactions:

* **Left double-click** — show the main window.
* **Right-click** — open the tray menu.
* **Escape** or clicking elsewhere — close the menu; a repeated right-click toggles it.

Tray menu items:

| Item | Description |
|------|-------------|
| **Show `<Edition name>`** | Shows and activates the main window. |
| **Start monitoring / Disable** | Dynamic item: the same action as the window button — *Start monitoring* when stopped, *Disable* when active. |
| **Exit `<Edition name>`** | Stops monitoring, persists settings and exits the process. |

The menu appears at the cursor (growing upward), is clamped to the work area of the monitor
under the cursor (correct on secondary monitors and mixed-DPI setups), and its width adapts
to the longest label of the edition.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

### Toast Cards

Toast cards are custom overlay notifications (not Windows toasts) shown at the bottom-right
of the primary monitor's work area, above the taskbar. They never steal focus and are not
listed in Alt-Tab.

Behavior:

* Up to **4 cards** stacked; when a 5th arrives, the oldest is evicted immediately.
* Each card auto-hides after **5 seconds**; clicking a card dismisses it instantly.
* Fade-in/fade-out animations; the icon is the application icon; text color indicates the type.
* All cards are suppressed while **toast-notifications** is unchecked.

Toast card types:

| Card | Color | Shown when |
|------|-------|------------|
| **Monitoring Started** | white | Monitoring was enabled (Start). |
| **Monitoring Stopped** | white | Monitoring was disabled (Disable). |
| **Target Unreadable** | white | Start was aborted: the target file could not be read/hashed. Monitoring stays off, the previous pin is kept. |
| **Process Recovered** | green | A recovery launch succeeded and the process is running. |
| **Recovery Failed** | red | The restart budget is exhausted; monitoring is disabled. |
| **Target Changed** | red | The target binary's hash no longer matches the pin; the recovery relaunch was blocked and monitoring is disabled. Press Start again to re-confirm the binary. |

<p align="right">(<a href="#readme-top">back to top</a>)</p>

### Target Integrity Pin

On every **Start monitoring** the SHA-256 hash of the target executable is recorded
("pinned"). Every recovery relaunch is verified against this pin before the process
is started:

* **Binary unchanged** → the target is relaunched normally.
* **Binary changed** → the launch is blocked, a red *Target Changed* toast is shown, and
  monitoring is disabled. Enabling monitoring again (Start) is the explicit re-confirmation
  and re-pins the hash.
* **Target unreadable at Start** → Start aborts fail-closed (*Target Unreadable*): no
  monitoring without a valid pin, and the previous pin is kept.

A transparently missing or unreadable target *during an active session* is **not** a pin
mismatch: it is treated as degraded (see [Status Badge](#status-badge)) — the target stays
selected and pinned, no restart budget is spent, and monitoring is not disabled.

This prevents a crashed-but-replaced binary (e.g. by malware) from being silently relaunched.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

### Autostart

| Edition | Mechanism | Details |
|---------|-----------|---------|
| Regular | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `<Edition name>` | `"<path to exe>" --background` |
| Admin | Scheduled task `<Edition name>` | logon trigger, highest privileges, runs `"<path to exe>" --background` (silent) |

The **Autostart with Windows** checkbox is persisted in `settings.json`, which is the source
of truth: it is re-applied on startup and re-removed on a user Exit, so the checkbox state
survives an Exit.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

### Data Files

| Data | Regular edition | Admin edition |
|------|-----------------|---------------|
| Settings | `%LocalAppData%\ProcessGuardian\settings.json` | `%ProgramData%\ProcessGuardianAdmin\config\settings.json` (ACL: Administrators/SYSTEM only) |
| Log file | `%LocalAppData%\ProcessGuardian\logs.log` | `%LocalAppData%\ProcessGuardianAdmin\logs.log` |
| Crash diagnostics | same folder (`crash.log`, `shutdown-error.log`) | same folder |

Log files are size-capped (512 KB) with one archive generation (`logs.1.log`), so disk usage
stays bounded. Deleting `settings.json` while the app runs is safe: it continues with defaults
and recreates the file on the next save.

#### Recovery journal (elevated edition)

The elevated edition keeps a crash-safe recovery journal at `HKLM\SOFTWARE\ProcessGuardianAdmin\Recovery`.
On a clean first run the key is absent, which means no recovery is in progress and normal
autostart sync proceeds. If the journal indicates a quarantined configuration, the app stays
stopped and shows an explicit *recovery* choice (Keep / Remove the existing autostart task)
instead of touching the registration on its own. If the elevated configuration is refused as
unsafe, the app reports an actionable Error: an administrator must repair the owner/DACL of
`%ProgramData%\ProcessGuardianAdmin` (owner `BUILTIN\Administrators` or `SYSTEM`, no reparse
points) or the recovery state under `HKLM\SOFTWARE\ProcessGuardianAdmin\Recovery`. Do not relax
ACLs or grant broad permissions to "fix" a refusal — that would defeat the tamper protection.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

<!-- MANUAL ACCEPTANCE TESTS -->

## Manual Acceptance Tests

Run manually by a person after a build (no automated pass marks here):

| # | Scenario | Expected Result |
|---|----------|-----------------|
| 1 | Launch app, set target to `calc.exe`, click Start | Status → MONITORING, tray icon green |
| 2 | Close window (×) | Window hides, tray icon remains, monitoring continues |
| 3 | Kill target process | Process restarts, toast appears, log updated |
| 4 | Set max attempts = 1, kill target twice | After 2nd failure: status ERROR, monitoring disabled, tray red |
| 5 | Launch second instance | First instance window activates (or tray stays), second exits immediately |
| 6 | Enable autostart, restart Windows *(optional — requires a Windows reboot)* | App starts in background (no window), tray visible, monitoring active if enabled |
| 7 | Delete the target .exe while monitoring | Transient *Missing*: no ERROR, no crash, the target stays selected/pinned and no restart budget is spent; after two degraded cycles (~120 s) the DEGRADED indicator shows with its reason in the tooltip. Restoring the file resumes normal monitoring. |
| 8 | Change check interval while monitoring | New interval applied within one cycle (≤ old interval seconds) |
| 9 | Delete `settings.json` while app running | App continues with defaults, recreates file on next save |
| 10 | Run without any `Assets/` folder next to the exe | Tray shows embedded icons normally — icons are embedded in the assemblies, no external files needed |
| 11 | Uncheck toast-notifications, kill the target while monitoring | Recovery still happens, no toast cards appear |

<p align="right">(<a href="#readme-top">back to top</a>)</p>

<!-- BUILD & PUBLISH -->

## Build & Publish

### Prerequisites

* **To run:** Windows 11, or Windows 10 **LTSC/Enterprise** editions only (the `.NET 10` runtime supports Windows 10 1607/1809/21H2 only for those channel/edition combinations — not Home/Pro), plus Windows Server 2016+; x64. The published builds are self-contained — no .NET runtime needs to be installed.
* **To build:** .NET SDK 10.0 (the repository pins `10.0.100` with `rollForward: latestFeature`) with the Windows Desktop workload.

### Installation

**Ready-made single-file builds** — run from the repository root (Windows PowerShell):

```
powershell -NoProfile -ExecutionPolicy Bypass -File .\build\publish.ps1
```

Flags to build a single profile (no arguments → both editions):

- `-SelfContained` → `publish/self-contained` (Release → `ProcessGuardian.exe`)
- `-Admin` → `publish/self-contained-admin` (Release-Elevated → `ProcessGuardianAdmin.exe`)

Both editions are also built and tested in CI (`Release` and `Release-Elevated` solution configurations).

Automated CI builds and runs the unit/integration tests for both editions, but it cannot
exercise real Windows ACL/DACL protection, the elevated *scheduled task* autostart, or the HKLM
recovery journal — those security-sensitive behaviors depend on an elevated session and must be
validated with the separate manual elevated checklist (running the admin exe elevated). Passing
CI alone does not certify real ACL validation on any given machine.

**From source:**

```
dotnet build .\ProcessGuardian.sln
dotnet test .\ProcessGuardian.sln
```

Debug builds:

* `src\ProcessGuardian.Wpf\bin\Debug\net10.0-windows10.0.17763.0\ProcessGuardian.exe` (regular)
* `src\ProcessGuardian.Wpf\bin\Release-Elevated\net10.0-windows10.0.17763.0\ProcessGuardianAdmin.exe`
  (admin — only the `Release-Elevated` configuration exists for the admin edition)

Output sizes (self-contained single exe):

| Edition | Output size |
|---------|-------------|
| ProcessGuardian | ≈ 78 MB |
| ProcessGuardianAdmin | ≈ 78 MB |

Reason: the `net10.0-windows10.0.17763.0` TFM pulls in the Windows SDK bindings, and WPF does not trim well in single-file mode.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

<!-- LIMITATIONS -->

## Limitations

- Distribution size (≈ 78 MB per edition) is above the original design goal (see the size table above).
- Power events (e.g. reacting to system sleep/resume) are not implemented.

<!-- LICENSE -->

## License

Distributed under the MIT License. See `LICENSE` for more information.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

<!-- MARKDOWN LINKS & IMAGES -->
[product-screenshot]: assets/mainWindow.png

---

<sub>**Keywords:** process guardian, process watchdog, process monitor, auto restart, keep process alive, crash recovery, restart on crash, windows autostart, system tray utility, toast notifications, sha-256 integrity, wpf, dotnet, c#, open source, application watchdog, process watcher, application monitor, background service, dependency injection, mvvm toolkit, generic host, serilog, notifyicon, clean architecture, single instance, single-file publish, self-contained exe, scheduled task, .net 10, windows 10, windows 11</sub>

