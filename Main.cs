using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Wox.Plugin;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Screensaver
{
    public class Main : IPlugin
    {
        public static string PluginID => "5A6E5384F5BF4932AD3E29442A4EC976";

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
                    SubTitle = "Launch the system screensaver",
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

                Process.Start(new ProcessStartInfo
                {
                    FileName = scrPath,
                    Arguments = "/s",
                    UseShellExecute = true
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
