# Screensaver — PowerToys Run plugin

A minimal community plugin for [PowerToys Run](https://learn.microsoft.com/windows/powertoys/run)
that starts the Windows system screensaver.

Type `scr` in PowerToys Run, press Enter, and the screensaver configured in
Windows Settings launches — exactly the way Windows' own idle trigger does.
Before starting, it silently turns off Caffeine and PowerToys Awake so they
cannot dismiss the screensaver.

## How it works

- Action keyword: `scr`
- Silently deactivates Caffeine and PowerToys Awake (see below)
- Reads `SCRNSAVE.EXE` from `HKCU\Control Panel\Desktop`
- Launches `<SCRNSAVE.EXE> /s` (the same command line Windows uses on idle)
- If no screensaver is configured (or the file is missing), shows a
  notification and leaves Run open

## Keep-awake tools are switched off first (silently)

Caffeine (Zhorn) and PowerToys Awake keep the display awake via
`SetThreadExecutionState`, which cancels a screensaver a moment after it
appears — so with `caff on` active the screensaver never survives. `scr` does
the equivalent of `caff off` before launching, using the exact same mechanism
as the [caffeine-plugin](../caffeine-plugin):

| Tool | What `scr` does |
| --- | --- |
| Caffeine | relaunches `%USERPROFILE%\Music\caffeine64.exe` with `-appoff` (only when an instance is running; it stays in the tray, inactive) |
| PowerToys Awake | writes `mode: 0` (PASSIVE) into the Awake module's settings file — the channel PowerToys' own AwakeService watches; no process is started or closed |

**No popup, no notification, no message** — unlike `caff off`, nothing is shown
to the user. Successes and failures only go to the log (see *Diagnose where the
time goes*), and the screensaver starts regardless of whether the switch-off
worked.

When Awake has to change from an active mode, the plugin waits 300 ms for the
running `PowerToys.Awake.exe` to apply the settings file (~25 ms throttle) before
launching the screensaver; that is the only added latency, and it is skipped
when nothing was active.

The switch-off is **not undone** afterwards: Caffeine and Awake stay off, just
like after `caff off`. Re-enable them with `caff on` / `caff awake on`.

## Building (Linux cross-compile)

Prerequisite: .NET 9 SDK (no Windows machine needed; the project uses
`EnableWindowsTargeting`).

```bash
./build.sh
```

Builds Release for x64 (the distributed artifact) and ARM64 (compile-check
only), then produces:

- `dist/Screensaver/` — the install folder
- `dist/Screensaver.zip` — the same, zipped

## Installing (Windows 11)

1. Copy `dist/Screensaver/` to:
   `%LOCALAPPDATA%\Microsoft\PowerToys\PowerToys Run\Plugins\Screensaver\`
2. Restart PowerToys (tray icon → quit, relaunch) so it rescans plugins.
3. Type `scr` in PowerToys Run → "Start screensaver" → Enter.

## Launch latency

The visible delay = **process creation** + **the screensaver's own init until
its first frame**. Both are variable on Windows:

- **Windows Defender real-time protection** scans the executable when the
  process is created. A cached scan result is ~free; a fresh scan adds
  hundreds of ms to seconds, depending on CPU load. This is the usual suspect
  for "sometimes 5 s" launches.
- **Cold vs. warm page cache** — the first launch after boot reads the `.scr`
  and its dependent DLLs from disk; later launches read them from RAM.
- **The screensaver itself** — asset-heavy (3D) screensavers load textures,
  models or videos and create a D3D device before showing a frame. That can
  take 2–5 s and varies with CPU/GPU load. No launcher can speed this up; only
  a lighter screensaver can.

### Make it as fast as possible

1. **Exclude the screensaver from Defender** (run elevated PowerShell, with the
   actual path from `HKCU\Control Panel\Desktop` → `SCRNSAVE.EXE`):

   ```powershell
   Add-MpPreference -ExclusionPath "C:\Windows\System32\bubbles.scr"
   ```

   Built-in screensavers live in `C:\Windows\System32`; third-party ones are
   usually in `C:\Program Files\...` or a user folder. Verify with
   `Get-MpPreference | Select-Object -ExpandProperty ExclusionPath`.

2. **Pick a lightweight screensaver** if the log below shows the delay is
   inside the screensaver (see next section).

The plugin itself already uses the fastest launch path available: a direct
`CreateProcess` (no ShellExecute round-trip through Explorer).

### Diagnose where the time goes

Every launch appends one line to `%TEMP%\screensaver-plugin\launch.log`:

```
2026-01-01 12:00:00.123 scr="C:\Windows\System32\Mystify.scr" pid=1234 registry=+1ms keepawake=+350ms(caffeine:off,awake:off) process=+392ms window=+2310ms
```

- `keepawake` — what the silent `caff off` did: `caffeine:off|not-running|not-found|error`,
  `awake:off|already-off|not-installed|error`. Values other than `off`/
  `not-running`/`already-off` mean the tool could not be deactivated and may
  interrupt the screensaver.
- `process` — time until the OS process exists (Defender/cold-cache/shell
  effects show up here).
- `window` — time until the screensaver's fullscreen window is visible.
- A small `process` but a large `window` (e.g. `process=+40ms window=+3800ms`)
  means the delay is the screensaver's own initialization → switch to a
  lighter screensaver. A large `process` means the OS/AV side → apply the
  Defender exclusion.

Trigger a few launches on a "slow" occasion and a "fast" one and compare the
lines.

## Notes

- The workstation locks on resume: after the screensaver starts, the plugin
  waits for it to exit and then calls `LockWorkStation()` (user32). A
  programmatically-started screensaver does not reliably produce the "display
  logon screen on resume" lock on its own, so the plugin locks explicitly.
  This locks unconditionally (it does not depend on the `ScreenSaverIsSecure`
  registry toggle).
- Targets `net9.0-windows`; requires a .NET 9-era PowerToys (0.9x).
