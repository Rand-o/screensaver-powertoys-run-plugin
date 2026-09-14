using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Wox.Plugin;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Screensaver
{
    public class Main : IPlugin
    {
        public static string PluginID => "5A6E5384F5BF4932AD3E29442A4EC976";

        // user32!LockWorkStation — locks the workstation (equivalent to Win+L).
        // Callable from any thread of a process on the interactive desktop.
        [DllImport("user32.dll", EntryPoint = "LockWorkStation", SetLastError = true)]
        private static extern bool LockWorkStation();

        // --- Diagnostics: detect when the screensaver's fullscreen window appears ---

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        // --- Keep-awake tools (Caffeine / PowerToys Awake) ---

        // Awake module settings-file modes (PowerToys AwakeMode enum).
        private const int AwakeModePassive = 0;

        // Grace period for the running PowerToys.Awake instance to pick up the
        // settings file (it watches it with a ~25 ms throttle) before the
        // screensaver starts — otherwise its SetThreadExecutionState state can
        // still dismiss the screensaver a moment after it appears.
        private const int AwakeApplyGraceMs = 300;

        private static readonly string LogDir = Path.Combine(Path.GetTempPath(), "screensaver-plugin");
        private static readonly string LogPath = Path.Combine(LogDir, "launch.log");

        private PluginInitContext _context;

        public string Name => "Screensaver";

        public string Description => "Start the Windows screensaver";

        public void Init(PluginInitContext context)
        {
            _context = context;
        }

        public List<Result> Query(Query query)
        {
            return new List<Result>
            {
                new Result
                {
                    Title = "Start screensaver",
                    SubTitle = "Launch the system screensaver (silently turns off Caffeine/Awake, locks on resume)",
                    IcoPath = "Images/icon.png",
                    Score = 100,
                    Action = _ => StartScreensaver()
                }
            };
        }

        private bool StartScreensaver()
        {
            var sw = Stopwatch.StartNew();
            try
            {
                string scrPath;
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                {
                    scrPath = key?.GetValue("SCRNSAVE.EXE") as string;
                }

                if (string.IsNullOrWhiteSpace(scrPath) || !File.Exists(scrPath))
                {
                    _context.API.ShowMsg("Screensaver", "No screensaver is configured in Windows settings.");
                    return false;
                }

                long registryMs = sw.ElapsedMilliseconds;

                // Caffeine and PowerToys Awake keep the display awake by holding
                // SetThreadExecutionState, which dismisses a running screensaver.
                // Switch both off first — silently, exactly like "caff off" from the
                // Caffeine plugin but without any notification.
                string keepAwake = DisableKeepAwakeSilently();
                long keepAwakeMs = sw.ElapsedMilliseconds;

                // UseShellExecute = false → direct CreateProcess. The shell path
                // (ShellExecuteEx) adds COM init + file-association lookup, and its
                // latency varies with Explorer's state; CreateProcess is the fast,
                // stable path. WorkingDirectory is set explicitly because some
                // screensavers load assets relative to their own folder.
                Process screensaver = Process.Start(new ProcessStartInfo
                {
                    FileName = scrPath,
                    Arguments = "/s",
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(scrPath)
                });

                if (screensaver == null)
                {
                    _context.API.ShowMsg("Screensaver", "Failed to start the screensaver.");
                    return false;
                }

                long processMs = sw.ElapsedMilliseconds;
                int pid = screensaver.Id;

                // Diagnostics: time how long until the screensaver's fullscreen
                // window actually appears, then append a one-line timing record to
                // %TEMP%\screensaver-plugin\launch.log.
                Task.Run(() => WaitForFullscreenWindow(sw, scrPath, pid, registryMs, keepAwakeMs, keepAwake, processMs));

                // A screensaver started programmatically does not reliably produce the
                // "display logon screen on resume" lock (that is only guaranteed for the
                // system idle trigger). Lock the workstation explicitly once the
                // screensaver exits, i.e. when the user resumes.
                Task.Run(async () =>
                {
                    try
                    {
                        await screensaver.WaitForExitAsync();
                        LockWorkStation();
                    }
                    catch (Exception e)
                    {
                        Log.Exception("Failed to lock workstation after screensaver exit", e, GetType());
                    }
                });

                return true;
            }
            catch (Exception e)
            {
                Log.Exception("Failed to start screensaver", e, GetType());
                _context.API.ShowMsg("Screensaver", e.Message);
                return false;
            }
        }

        // --- Silent keep-awake shutdown (same mechanism as "caff off") ---
        //
        // Caffeine (Zhorn) and PowerToys Awake both keep the display awake through
        // SetThreadExecutionState, which cancels the screensaver as soon as it
        // appears. Both are deactivated here with the exact same mechanism the
        // Caffeine plugin uses for "caff off":
        //
        //   * Caffeine  -> relaunch caffeine64.exe with -appoff (deactivates the
        //                  running instance; the app stays in the tray, inactive)
        //   * Awake     -> write mode 0 (PASSIVE) into the Awake module's settings
        //                  file, the channel PowerToys' own AwakeService watches
        //
        // It is deliberately silent: no ShowMsg, no notification, no result —
        // successes and failures only go to the log. Whatever happens the
        // screensaver is started anyway.
        //
        // Returns a short machine-readable summary for the launch log.
        private static string DisableKeepAwakeSilently()
        {
            var notes = new List<string>();

            bool needsGrace;
            try
            {
                notes.Add(TryDeactivateCaffeine());
            }
            catch (Exception e)
            {
                Log.Exception("Failed to deactivate Caffeine", e, typeof(Main));
                notes.Add("caffeine:error");
            }

            try
            {
                notes.Add(TryAwakeOff(out needsGrace));
            }
            catch (Exception e)
            {
                Log.Exception("Failed to turn PowerToys Awake off", e, typeof(Main));
                needsGrace = false;
                notes.Add("awake:error");
            }

            if (needsGrace)
            {
                Thread.Sleep(AwakeApplyGraceMs);
            }

            return string.Join(",", notes);
        }

        private static string TryDeactivateCaffeine()
        {
            Process[] procs = Process.GetProcessesByName("caffeine64");
            try
            {
                if (procs.Length == 0)
                {
                    return "caffeine:not-running";
                }
            }
            finally
            {
                foreach (Process p in procs)
                {
                    p.Dispose();
                }
            }

            // Same location the Caffeine plugin uses.
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                "caffeine64.exe");
            if (!File.Exists(path))
            {
                Log.Warn($"caffeine64.exe not found in {Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)}", typeof(Main));
                return "caffeine:not-found";
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "-appoff",
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(path)
                });
                return "caffeine:off";
            }
            catch (Exception e)
            {
                Log.Exception("Failed to deactivate Caffeine", e, typeof(Main));
                return "caffeine:error";
            }
        }

        // Awake "off" (mode 0 / PASSIVE). Never closes an Awake process — a running
        // instance only switches to its inactive state. needsGrace is true when the
        // mode actually changed away from an active one, i.e. the running instance
        // needs a moment to apply the file before the screensaver starts.
        private static string TryAwakeOff(out bool needsGrace)
        {
            needsGrace = false;

            string path = AwakeSettingsPath;
            if (!Directory.Exists(Path.GetDirectoryName(path)))
            {
                return "awake:not-installed";
            }

            int previousMode = ReadAwakeMode(path);
            if (previousMode == AwakeModePassive)
            {
                // Already inactive — nothing to write and nothing to wait for.
                return "awake:already-off";
            }

            if (!UpdateAwakeSettings(path, p => p.Mode = AwakeModePassive))
            {
                return "awake:error";
            }

            needsGrace = true; // the mode was active before this write
            return "awake:off";
        }

        // --- PowerToys Awake settings file ---
        //
        // Same channel PowerToys' own AwakeService uses (verified against the
        // PowerToys v0.100.x source): the runner keeps a windowless
        // PowerToys.Awake.exe instance running that watches this file with a
        // ~25 ms throttle and applies whatever mode it holds.

        // Module settings file, per PowerToys version:
        //   v0.100.x+ : %LOCALAPPDATA%\Microsoft\PowerToys\Awake\settings.json
        //   older     : %LOCALAPPDATA%\PowerToys\settings\Awake.json
        private static string AwakeSettingsPath
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string newPath = Path.Combine(local, "Microsoft", "PowerToys", "Awake", "settings.json");
                if (File.Exists(newPath))
                {
                    return newPath;
                }

                string oldPath = Path.Combine(local, "PowerToys", "settings", "Awake.json");
                if (File.Exists(oldPath))
                {
                    return oldPath;
                }

                return newPath; // default for current PowerToys
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        // Mirrors the Awake settings schema written by PowerToys (Settings.UI.Library AwakeSettings).
        private sealed class AwakeSettingsFile
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = "Awake";

            [JsonPropertyName("version")]
            public string Version { get; set; } = "1.0.0";

            [JsonPropertyName("properties")]
            public AwakeSettingsProperties Properties { get; set; } = new();
        }

        private sealed class AwakeSettingsProperties
        {
            [JsonPropertyName("keepDisplayOn")]
            public bool KeepDisplayOn { get; set; }

            [JsonPropertyName("mode")]
            public int Mode { get; set; }

            [JsonPropertyName("intervalHours")]
            public uint IntervalHours { get; set; }

            [JsonPropertyName("intervalMinutes")]
            public uint IntervalMinutes { get; set; } = 1;

            [JsonPropertyName("expirationDateTime")]
            public DateTimeOffset ExpirationDateTime { get; set; } = DateTimeOffset.Now;

            [JsonPropertyName("customTrayTimes")]
            public Dictionary<string, uint> CustomTrayTimes { get; set; } = new();
        }

        // Current mode in the settings file, or -1 when there is no readable file
        // (nothing active in that case).
        private static int ReadAwakeMode(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return AwakeModePassive;
                }

                AwakeSettingsFile loaded = JsonSerializer.Deserialize<AwakeSettingsFile>(File.ReadAllText(path));
                return loaded?.Properties?.Mode ?? AwakeModePassive;
            }
            catch (Exception e)
            {
                Log.Exception("Failed to read Awake settings file", e, typeof(Main));
                return -1;
            }
        }

        // Reads the existing settings file (preserving keepDisplayOn / customTrayTimes),
        // applies the mutation and writes it back. Returns false when the folder does
        // not exist or the read/write failed. Never throws.
        private static bool UpdateAwakeSettings(string path, Action<AwakeSettingsProperties> mutate)
        {
            try
            {
                AwakeSettingsFile settings = new();
                if (File.Exists(path))
                {
                    try
                    {
                        AwakeSettingsFile loaded = JsonSerializer.Deserialize<AwakeSettingsFile>(File.ReadAllText(path));
                        if (loaded?.Properties != null)
                        {
                            settings = loaded;
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Exception("Failed to parse Awake settings file; using defaults", e, typeof(Main));
                    }
                }

                mutate(settings.Properties);
                File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
                return true;
            }
            catch (Exception e)
            {
                Log.Exception("Failed to write Awake settings file", e, typeof(Main));
                return false;
            }
        }

        // Polls for a visible, near-fullscreen window owned by the screensaver
        // process, then writes a one-line timing record. Never throws.
        private static void WaitForFullscreenWindow(Stopwatch sw, string scrPath, int pid, long registryMs, long keepAwakeMs, string keepAwake, long processMs)
        {
            const int pollIntervalMs = 15;
            const int timeoutMs = 30000;
            long windowMs = -1;

            try
            {
                int screenW = GetSystemMetrics(SM_CXSCREEN);
                int screenH = GetSystemMetrics(SM_CYSCREEN);
                long deadline = sw.ElapsedMilliseconds + timeoutMs;

                while (sw.ElapsedMilliseconds < deadline)
                {
                    if (FindFullscreenWindow(pid, screenW, screenH))
                    {
                        windowMs = sw.ElapsedMilliseconds;
                        break;
                    }

                    Thread.Sleep(pollIntervalMs);
                }
            }
            catch
            {
                // Diagnostics must never break the plugin.
            }

            WriteLog(scrPath, pid, registryMs, keepAwakeMs, keepAwake, processMs, windowMs);
        }

        private static bool FindFullscreenWindow(int pid, int screenW, int screenH)
        {
            bool found = false;
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd))
                {
                    return true;
                }

                GetWindowThreadProcessId(hWnd, out uint procId);
                if (procId != (uint)pid)
                {
                    return true;
                }

                if (!GetWindowRect(hWnd, out RECT r))
                {
                    return true;
                }

                // Fullscreen: covers at least 80% of the primary screen in both
                // dimensions (tolerates borders/taskbar offsets).
                if (r.Right - r.Left >= screenW * 0.8 && r.Bottom - r.Top >= screenH * 0.8)
                {
                    found = true;
                    return false; // stop enumerating
                }

                return true;
            }, IntPtr.Zero);
            return found;
        }

        // Example line:
        // 2026-01-01 12:00:00.123 scr="C:\Windows\System32\Mystify.scr" pid=1234
        //   registry=+1ms keepawake=+350ms(caffeine:off,awake:off) process=+392ms window=+2310ms
        // "process" = time to create the OS process; "window" = time until the
        // fullscreen window is visible. A large gap between the two means the
        // delay is inside the screensaver itself (asset/GPU init), not in launch.
        private static void WriteLog(string scrPath, int pid, long registryMs, long keepAwakeMs, string keepAwake, long processMs, long windowMs)
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 256 * 1024)
                {
                    File.Delete(LogPath);
                }

                string window = windowMs >= 0 ? $"+{windowMs}ms" : "timeout(30s)";
                string line =
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} scr=\"{scrPath}\" pid={pid} " +
                    $"registry=+{registryMs}ms keepawake=+{keepAwakeMs}ms({keepAwake}) " +
                    $"process=+{processMs}ms window={window}";
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch
            {
                // Diagnostics must never break the plugin.
            }
        }
    }
}
