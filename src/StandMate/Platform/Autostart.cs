using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace StandMate.Platform
{
    /// <summary>"Start with Windows" via the per-user Run key (no admin rights needed).</summary>
    internal static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string ValueName = "StandMate";

        public static string Command => $"\"{Application.ExecutablePath}\" --background";

        public static bool IsEnabled
        {
            get
            {
                try
                {
                    using (var run = Registry.CurrentUser.OpenSubKey(RunKey))
                        if (run?.GetValue(ValueName) == null) return false;
                    // Task Manager's "Disable" writes a flag here (first byte odd = disabled).
                    using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey))
                        if (approved?.GetValue(ValueName) is byte[] b && b.Length > 0 && (b[0] & 1) == 1) return false;
                    return true;
                }
                catch (Exception) { return false; }
            }
        }

        public static void Set(bool enabled)
        {
            try
            {
                using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (enabled) run.SetValue(ValueName, Command);
                    else run.DeleteValue(ValueName, false);
                }
                using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
                    approved?.DeleteValue(ValueName, false); // clear a Task Manager "disabled" flag
            }
            catch (Exception) { /* best effort */ }
        }
    }
}
