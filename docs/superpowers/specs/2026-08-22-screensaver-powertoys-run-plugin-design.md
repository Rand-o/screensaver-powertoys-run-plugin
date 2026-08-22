# Screensaver PowerToys Run Plugin — Design

**Date:** 2026-08-22
**Status:** Approved
**Plugin ID:** `5A6E5384F5BF4932AD3E29442A4EC976`

## Overview

A minimal community plugin for PowerToys Run that starts the Windows system
screensaver. Typing the action keyword `scr` shows a single result; pressing
Enter launches the screensaver configured in Windows settings, exactly the way
Windows' own idle trigger does.

## Goals

- One result, one action: start the system screensaver.
- Preserve system screensaver behavior, including the
  "display logon screen on resume" setting (`ScreenSaverIsSecure`).
- Buildable on Linux (cross-compile to Windows) with no Windows machine in the
  loop.
- Installable by copying one folder into the PowerToys Run user plugin
  directory.

## Non-goals

- No settings panel / `ISettingProvider` (no configurable `.scr` path).
- No context menus, i18n, or delayed execution.
- No unit test project (required only for plugins merged into
  Microsoft/PowerToys).
- No changes to any screensaver itself (e.g. AquariumSaver).

## Behavior

### Query

- Action keyword: `scr` (`IsGlobal: false`).
- `Query()` always returns exactly one `Result`, regardless of the text typed
  after the keyword:
  - `Title`: "Start screensaver"
  - `SubTitle`: "Launch the system screensaver"
  - `IcoPath`: `Images/icon.png`
  - `Score`: 100
- No scoring or filtering logic.

### Action (Enter pressed)

1. Read `SCRNSAVE.EXE` from `HKCU\Control Panel\Desktop` (the path of the
   screensaver configured in Windows Settings).
2. Launch it with argument `/s` via
   `Process.Start(new ProcessStartInfo { FileName = scrPath, Arguments = "/s", UseShellExecute = true })`.
3. Return `true` from the action (PowerToys Run hides).

Launching `<SCRNSAVE.EXE> /s` is byte-for-byte what Windows' own idle trigger
does. The "display logon screen on resume" behavior is implemented by the
screensaver itself (it reads `ScreenSaverIsSecure` and calls
`LockWorkStation()` on wake), so it is preserved automatically for any
screensaver that implements it (all built-in Windows 11 screensavers do).
The plugin does not need to — and must not — call `LockWorkStation()` itself.

### Error handling

| Condition | Handling |
| --- | --- |
| `SCRNSAVE.EXE` missing or empty | `ShowMsg` notification: "No screensaver is configured in Windows settings." Action returns `false` (Run stays open). |
| File no longer exists on disk | Same as above (message includes the configured path). |
| `Process.Start` throws | `Log.Exception` + `ShowMsg` with the error message. Action returns `false`. |

Notifications use `PluginInitContext.API.ShowMsg(title, message)`, captured in
`Init()`. Logging uses the `Wox.Plugin.Logger.Log` static class.

## Project layout

```
screensaver-plugin/
├── Community.PowerToys.Run.Plugin.Screensaver.csproj
├── Main.cs                  # the entire plugin (~80 lines)
├── plugin.json
├── Images/icon.png          # generated, theme-neutral, 256x256
├── build.sh                 # build + assemble dist/
├── README.md                # build & install instructions
└── docs/superpowers/specs/  # this design doc
```

- Project/assembly name: `Community.PowerToys.Run.Plugin.Screensaver`
  (community naming convention).
- Namespace: `Community.PowerToys.Run.Plugin.Screensaver`.
- No "PowerToys" prefix on internal entities (per official checklist).

## Key technical decisions (verified against sources)

### Target framework and build

- TFM: `net9.0-windows`, `Platforms: x64;ARM64`, `PlatformTarget: $(Platform)`.
- `UseWPF=true` (required by the plugin API surface),
  `EnableWindowsTargeting=true` (enables building on Linux).
- .NET 9 SDK installed on the Linux build box via the official
  `dotnet-install.sh` script (user-local install, no root).

### Dependencies

- Single NuGet dependency:
  `Community.PowerToys.Run.Plugin.Dependencies` version `0.97.0`.
- Verified by inspecting the nupkg: it bundles x64 + ARM64 copies of
  `Wox.Plugin.dll`, `Wox.Infrastructure.dll`, `PowerToys.Common.UI.dll`,
  `PowerToys.ManagedCommon.dll`, `PowerToys.Settings.UI.Lib.dll`
  (net9.0 builds from PowerToys source) and copies them to the build output
  via its `.targets` file.
- In current PowerToys, `Wox.Plugin.dll` contains all required API types:
  `IPlugin`, `Query`, `Result`, `PluginInitContext`, `IPublicAPI`, `Log`
  (verified via the DeepWiki page for the current source tree and by
  inspecting the DLL metadata). No separate `Wox.dll` / `PowerToys.Run.dll`
  references are needed.
- No third-party dependencies → `DynamicLoading: false`; no
  `DynamicPlugin.props` import.

### Main class

```csharp
public class Main : IPlugin
{
    public static string PluginID => "5A6E5384F5BF4932AD3E29442A4EC976";

    private PluginInitContext _context;

    public string Name => "Screensaver";
    public string Description => "Start the Windows screensaver";

    public void Init(PluginInitContext context) { _context = context; }

    public List<Result> Query(Query query) { /* single Result, see Behavior */ }

    private bool StartScreensaver() { /* registry read + Process.Start */ }
}
```

- The class name `Main` is what the host discovers (convention).
- `PluginID` must equal the `ID` in `plugin.json`.

### plugin.json

Current official format (per
`doc/devdocs/modules/launcher/new-plugin-checklist.md`), plus legacy
`AssemblyPath`/`IcoPath` fields for older PowerToys hosts (unknown fields are
ignored by the deserializer):

```json
{
  "ID": "5A6E5384F5BF4932AD3E29442A4EC976",
  "Name": "Screensaver",
  "Type": "Plugin",
  "Description": "Start the Windows screensaver",
  "Author": "admin",
  "Version": "1.0.0",
  "Language": "csharp",
  "Website": "https://aka.ms/powertoys",
  "ActionKeyword": "scr",
  "IsGlobal": false,
  "ExecuteFileName": "Community.PowerToys.Run.Plugin.Screensaver.dll",
  "AssemblyPath": "Community.PowerToys.Run.Plugin.Screensaver.dll",
  "IcoPathDark": "Images/icon.png",
  "IcoPathLight": "Images/icon.png",
  "IcoPath": "Images/icon.png",
  "DynamicLoading": false
}
```

### Icon

- One generated 256×256 PNG (`Images/icon.png`), theme-neutral (works in both
  dark and light themes), generated with Python (Pillow) at implementation
  time. Motif: a monitor screen with a moon/stars or "Zz" sleep symbol.
- Referenced by `IcoPathDark`, `IcoPathLight`, `IcoPath`, and the `Result`.

## Build process (Linux)

1. `build.sh` (or manual):
   ```bash
   dotnet build Community.PowerToys.Run.Plugin.Screensaver.csproj -c Release -p:Platform=x64
   dotnet build Community.PowerToys.Run.Plugin.Screensaver.csproj -c Release -p:Platform=ARM64
   ```
2. `build.sh` assembles the install folder:
   - `dist/Screensaver/` ← plugin DLL, `plugin.json`, `Images/`, and the
     bundled API DLLs (already copied to the build output by the NuGet
     package's targets).
   - `dist/Screensaver.zip` ← zipped folder for easy transfer.
3. The x64 build is the primary artifact (target machine is x64 Windows 11);
   the ARM64 build is produced for completeness at no extra cost.

## Installation (Windows 11)

1. Copy `dist/Screensaver/` to
   `%LOCALAPPDATA%\Microsoft\PowerToys\PowerToys Run\Plugins\Screensaver\`
   (exact path verified from `src/modules/launcher/Wox.Plugin/Constant.cs`:
   `DataDirectory = %LOCALAPPDATA%\Microsoft\PowerToys\PowerToys Run`,
   `PluginsDirectory = DataDirectory\Plugins`).
2. Restart PowerToys (tray icon → quit, relaunch) so `PluginManager` rescans.
3. Type `scr` in PowerToys Run → "Start screensaver" appears → Enter →
   screensaver starts → wake → logon screen (per the system setting).

## Testing

- **Linux (automated):** `dotnet build` succeeds for both x64 and ARM64.
  Compiling against the real `Wox.Plugin.dll` is the main API-compatibility
  check.
- **Windows (manual checklist):**
  1. Plugin appears in PowerToys Run settings (Run → plugins).
  2. `scr` shows the single result with icon.
  3. Enter starts the configured screensaver; Run window hides.
  4. Moving the mouse resumes; logon screen appears (system setting honored).
  5. No-screensaver path: temporarily set `SCRNSAVE.EXE` to an empty/bogus
     value → notification shown, Run stays open. Restore afterwards.
  6. Logs at
     `%LOCALAPPDATA%\Microsoft\PowerToys\PowerToys Run\Logs\<version>\`
     contain no unexpected errors.

## Risks and mitigations

| Risk | Mitigation |
| --- | --- |
| Installed PowerToys is older than the net9.0-era API (e.g. .NET 8-era 0.8x) | Plugin would fail to load. Swap in an older `Community.PowerToys.Run.Plugin.Dependencies` version (0.8x) with matching TFM. User to report PowerToys version if the plugin doesn't load. |
| `Wox.Plugin.dll` API drift between the bundled 0.97.0 DLLs and the installed host | The host resolves shared assemblies from its own process first; bundled DLLs are a fallback. The plugin uses only the long-stable core surface (`IPlugin`, `Query`, `Result`, `ShowMsg`). |
| Machine-wide PowerToys install | User plugin directory still works (per-user `Plugins` folder is always scanned). |
| WPF build on Linux edge cases | `EnableWindowsTargeting=true` is the documented mechanism; a plain class library with no XAML compiles cleanly. If it fails, fallback: build on the Windows machine (project is portable). |

## Out of scope (future ideas)

- Secure-resume support in AquariumSaver itself (separate project).
- Settings panel to override the `.scr` path.
- GitHub release packaging / winget distribution.
