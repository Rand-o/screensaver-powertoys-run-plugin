using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
                    SubTitle = "Launch the system screensaver (locks on resume)",
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
                Task.Run(() => WaitForFullscreenWindow(sw, scrPath, pid, registryMs, processMs));

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

        // Polls for a visible, near-fullscreen window owned by the screensaver
        // process, then writes a one-line timing record. Never throws.
        private static void WaitForFullscreenWindow(Stopwatch sw, string scrPath, int pid, long registryMs, long processMs)
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

            WriteLog(scrPath, pid, registryMs, processMs, windowMs);
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
        //   registry=+1ms process=+42ms window=+2310ms
        // "process" = time to create the OS process; "window" = time until the
        // fullscreen window is visible. A large gap between the two means the
        // delay is inside the screensaver itself (asset/GPU init), not in launch.
        private static void WriteLog(string scrPath, int pid, long registryMs, long processMs, long windowMs)
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
                    $"registry=+{registryMs}ms process=+{processMs}ms window={window}";
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch
            {
                // Diagnostics must never break the plugin.
            }
        }
    }
}
