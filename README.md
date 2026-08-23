# Screensaver — PowerToys Run plugin

A minimal community plugin for [PowerToys Run](https://learn.microsoft.com/windows/powertoys/run)
that starts the Windows system screensaver.

Type `scr` in PowerToys Run, press Enter, and the screensaver configured in
Windows Settings launches — exactly the way Windows' own idle trigger does.

## How it works

- Action keyword: `scr`
- Reads `SCRNSAVE.EXE` from `HKCU\Control Panel\Desktop`
- Launches `<SCRNSAVE.EXE> /s` (the same command line Windows uses on idle)
- If no screensaver is configured (or the file is missing), shows a
  notification and leaves Run open

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

## Notes

- The workstation locks on resume: after the screensaver starts, the plugin
  waits for it to exit and then calls `LockWorkStation()` (user32). A
  programmatically-started screensaver does not reliably produce the "display
  logon screen on resume" lock on its own, so the plugin locks explicitly.
  This locks unconditionally (it does not depend on the `ScreenSaverIsSecure`
  registry toggle).
- Targets `net9.0-windows`; requires a .NET 9-era PowerToys (0.9x).
