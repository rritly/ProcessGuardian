<a id="readme-top"></a>

<div align="center">

# ProcessGuardian

A lightweight Windows background utility that watches a user-specified executable and
restarts it automatically when it crashes or disappears.

It comes in handy for programs that have no "run at Windows startup" option of their
own, for programs that crash periodically and need to be brought back up automatically,
or for testing. The app also collects event logs you can inspect at any time — handy
for seeing exactly what the guardian did and when.

Built with WPF on .NET 9 using Clean Architecture
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
| Admin | Release-Elevated | `ProcessGuardianAdmin.exe` | `requireAdministrator` | `%ProgramData%\ProcessGuardianAdmin\settings.json` (Administrators/SYSTEM-only ACL) | Scheduled task (logon, highest privileges, silent) |

Both editions run side by side: separate executable identity, single-instance mutex, settings, logs, autostart entry and tray icon.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

### Built With

* [.NET 9](https://dotnet.microsoft.com/) — `net9.0-windows10.0.17763.0`, WPF
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
| `ERROR` | red | Error | Target missing/unreadable or the restart budget is exhausted; monitoring is disabled. |

Exhausted restart budget (unless *Max restart attempts* = 0) puts the guardian into ERROR and
disables monitoring; pressing **Start monitoring** again re-pins the hash and resets the budget.

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
* **Binary changed or missing** → the launch is blocked, a red *Target Changed* toast is
  shown, and monitoring is disabled. Enabling monitoring again (Start) is the explicit
  re-confirmation and re-pins the hash.
* **Target unreadable at Start** → Start aborts fail-closed (*Target Unreadable*): no
  monitoring without a valid pin, and the previous pin is kept.

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
| Settings | `%LocalAppData%\ProcessGuardian\settings.json` | `%ProgramData%\ProcessGuardianAdmin\settings.json` (ACL: Administrators/SYSTEM only) |
| Log file | `%LocalAppData%\ProcessGuardian\logs.log` | `%LocalAppData%\ProcessGuardianAdmin\logs.log` |
| Crash diagnostics | same folder (`crash.log`, `shutdown-error.log`) | same folder |

Log files are size-capped (512 KB) with one archive generation (`logs.1.log`), so disk usage
stays bounded. Deleting `settings.json` while the app runs is safe: it continues with defaults
and recreates the file on the next save.

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
| 7 | Delete target .exe while monitoring | Status ERROR, no crash, log shows file not found |
| 8 | Change check interval while monitoring | New interval applied within one cycle (≤ old interval seconds) |
| 9 | Delete `settings.json` while app running | App continues with defaults, recreates file on next save |
| 10 | Run without any `Assets/` folder next to the exe | Tray shows embedded icons normally — icons are embedded in the assemblies, no external files needed |
| 11 | Uncheck toast-notifications, kill the target while monitoring | Recovery still happens, no toast cards appear |

<p align="right">(<a href="#readme-top">back to top</a>)</p>

<!-- BUILD & PUBLISH -->

## Build & Publish

### Prerequisites

* **To run:** Windows 10 1809+ / Windows Server 2019+, x64. The published builds are
  self-contained — no .NET runtime needs to be installed.
* **To build:** .NET SDK 9.0 (the repository pins `9.0.100` with `rollForward: latestFeature`)
  with the Windows Desktop workload.

### Installation

**Ready-made single-file builds** — run from the repository root (Windows PowerShell):

```
powershell -NoProfile -ExecutionPolicy Bypass -File .\build\publish.ps1
```

Flags to build a single profile (no arguments → both editions):

- `-SelfContained` → `publish/self-contained` (Release → `ProcessGuardian.exe`)
- `-Admin` → `publish/self-contained-admin` (Release-Elevated → `ProcessGuardianAdmin.exe`)

Both editions are also built and tested in CI (`Release` and `Release-Elevated` solution configurations).

**From source:**

```
dotnet build .\ProcessGuardian.sln
dotnet test .\ProcessGuardian.sln
```

Debug builds:

* `src\ProcessGuardian.Wpf\bin\Debug\net9.0-windows10.0.17763.0\ProcessGuardian.exe` (regular)
* `src\ProcessGuardian.Wpf\bin\Release-Elevated\net9.0-windows10.0.17763.0\ProcessGuardianAdmin.exe`
  (admin — only the `Release-Elevated` configuration exists for the admin edition)

Output sizes (self-contained single exe):

| Edition | Output size | SPEC goal |
|---------|-------------|-----------|
| ProcessGuardian | ≈ 78 MB | NFR-06 ≤ 55 MB (not met) |
| ProcessGuardianAdmin | ≈ 78 MB | NFR-06 ≤ 55 MB (not met) |

Reason: the `net9.0-windows10.0.17763.0` TFM pulls in the Windows SDK bindings, and WPF does not trim well in single-file mode.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

<!-- LIMITATIONS -->

## Limitations

- Distribution size NFRs (NFR-05/NFR-06) are not yet met (see the size table above).
- Power events (SPEC §2.7) are not implemented (`[MAY]`).

<p align="right">(<a href="#readme-top">back to top</a>)</p>

<!-- MARKDOWN LINKS & IMAGES -->
[product-screenshot]: assets/mainWindow.png
