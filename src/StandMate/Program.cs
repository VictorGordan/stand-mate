using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using StandMate.Core;

namespace StandMate
{
    internal static class Program
    {
        /// <summary>Also referenced by the installer (AppMutex) so upgrades/uninstalls can close the app.</summary>
        public const string MutexName = "StandMate.SingleInstance";
        private const string ShowEventName = "StandMate.ShowSettings";

        /// <summary>
        /// Flags:
        ///   --background  start silently in the tray (used by "Start with Windows")
        ///   --fast        developer mode: minutes count as seconds, away threshold 10 s
        ///   --update-url URL   developer: read latest.json from URL instead of GitHub (still signature-checked)
        /// </summary>
        [STAThread]
        private static void Main(string[] args)
        {
            bool background = args.Any(a => string.Equals(a, "--background", StringComparison.OrdinalIgnoreCase));
            bool fast = args.Any(a => string.Equals(a, "--fast", StringComparison.OrdinalIgnoreCase));
            int feed = Array.FindIndex(args, a => string.Equals(a, "--update-url", StringComparison.OrdinalIgnoreCase));
            if (feed >= 0 && feed + 1 < args.Length) Platform.UpdateClient.FeedOverride = args[feed + 1];

            using (var mutex = new Mutex(true, MutexName, out bool firstInstance))
            {
                if (!firstInstance)
                {
                    // Already running. A background start (sign-in, or the relaunch after an update) has nothing
                    // to do; anything else is the user opening the app, so show its settings window.
                    if (background) return;
                    try
                    {
                        AllowSetForegroundWindow(-1);
                        using (var show = EventWaitHandle.OpenExisting(ShowEventName)) show.Set();
                    }
                    catch (Exception) { }
                    return;
                }

                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => LogError(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => LogError(e.ExceptionObject as Exception);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
                using (var app = new TrayApp(background, fast, show))
                {
                    Application.Run(app);
                }
                GC.KeepAlive(mutex);
            }
        }

        /// <summary>Errors go to a local file only (%APPDATA%\StandMate\error.log). Nothing is ever sent anywhere.</summary>
        public static void LogError(Exception ex)
        {
            if (ex == null) return;
            try
            {
                Directory.CreateDirectory(Settings.DataDir);
                string path = Path.Combine(Settings.DataDir, "error.log");
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
            }
            catch (Exception) { }
        }

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);
    }
}
