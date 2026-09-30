# ProcessGuardian

A lightweight Windows background utility that monitors a user-specified executable and restarts it if it crashes or disappears. WPF on .NET 9, Clean Architecture (Domain → Application → Infrastructure → Presentation).

## Build & Publish

Run from the repository root (Windows PowerShell):

```
powershell -NoProfile -ExecutionPolicy Bypass -File .\build\publish.ps1
```

The script builds both profiles. Use flags to build a single profile:

- no arguments → both profiles
- `-FrameworkDependent` → `publish/framework-dependent`
- `-SelfContained` → `publish/self-contained`

Actual output sizes from Increment 5.1 (not yet meeting SPEC size goals):

| Profile | Output size | SPEC goal |
|---------|-------------|-----------|
| framework-dependent | ≈ 26 MB | NFR-05 ≤ 8 MB (not met) |
| self-contained (single exe) | ≈ 78 MB | NFR-06 ≤ 55 MB (not met) |

Reason in one sentence: the `net9.0-windows10.0.17763.0` TFM pulls in the Windows SDK bindings, and WPF does not trim well in single-file mode.

## Installation / Run

- **Framework-dependent:** install the .NET 9 Desktop Runtime, then run `ProcessGuardian.exe`.
- **Self-contained:** run `ProcessGuardian.exe` — a single standalone file, no runtime or external assets needed. Native libraries are extracted to `%TEMP%` on first launch, so the first start is slightly slower.
- `--background` — start in the tray without a window (used for Windows autostart).
- Closing the window (×) hides to tray; it does not exit the process.
- Exit via the **Exit** item in the tray menu.
- Launching the exe a second time activates/shows the window of the already-running instance; the new process exits immediately.

## Usage

- **Browse…** → absolute path to the target `.exe`.
- Arguments, check interval, max attempts (0 = infinite), cooldown.
- **Start / Stop Monitoring**.
- **Autostart with Windows** → `HKCU\...\Run`, value `"<path>" --background`.
- Settings: `%LocalAppData%\ProcessGuardian\settings.json`
- File logs (if enabled): `%LocalAppData%\ProcessGuardian\logs\`

## Tray

- **Off** — stopped
- **On** — Waiting/Monitoring
- **Error** — Error
- Menu: Show, Start, Stop, Exit
- Double-click — show window

## Manual Acceptance Tests

The 11 scenarios from SPEC §9.3. The run is performed manually by a person after a build (no automated pass marks here).

| # | Scenario | Expected Result |
|---|----------|-----------------|
| 1 | Launch app, set target to `calc.exe`, click Start | Status → Monitoring, tray icon green |
| 2 | Close window (×) | Window hides, tray icon remains, monitoring continues |
| 3 | Kill target process | Process restarts, toast appears, log updated |
| 4 | Set max attempts = 1, kill target twice | After 2nd failure: status Error, cooldown starts, tray red |
| 5 | Launch second instance | First instance window activates (or tray stays), second exits immediately |
| 6 | Enable autostart, restart Windows *(optional — requires a Windows reboot)* | App starts in background (no window), tray visible, monitoring active if enabled |
| 7 | Delete target .exe while monitoring | Status Error, no crash, log shows file not found |
| 8 | Change check interval while monitoring | New interval applied within one cycle (≤ old interval seconds) |
| 9 | Delete `settings.json` while app running | App continues with defaults, recreates file on next save |
| 10 | Run without any `Assets/` folder next to the exe | Tray shows embedded icons normally — icons are embedded in the assemblies, no external files needed |
| 11 | Run on machine without Toast support *(optional — depends on environment)* | Monitoring works normally, no toasts, no crash |

## Limitations

- Distribution size NFRs (NFR-05/NFR-06) are not yet met.
- Power events (SPEC §2.7) are not implemented (`[MAY]`).
