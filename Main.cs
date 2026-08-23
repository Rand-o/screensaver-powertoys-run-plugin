using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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

                Process screensaver = Process.Start(new ProcessStartInfo
                {
                    FileName = scrPath,
                    Arguments = "/s",
                    UseShellExecute = true
                });

                if (screensaver == null)
                {
                    _context.API.ShowMsg("Screensaver", "Failed to start the screensaver.");
                    return false;
                }

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
    }
}
