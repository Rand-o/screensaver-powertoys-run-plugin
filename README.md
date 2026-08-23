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

- "Display logon screen on resume" is honored: that behavior is implemented
  by the screensaver itself (it reads `ScreenSaverIsSecure` and locks the
  workstation on wake), so any screensaver that supports it — all built-in
  Windows 11 screensavers do — behaves exactly as under the system idle
  trigger. The plugin never calls `LockWorkStation()` itself.
- Targets `net9.0-windows`; requires a .NET 9-era PowerToys (0.9x).
