using System;
using System.Text;

namespace StandMate.Platform
{
    /// <summary>Detects games, fullscreen videos and presentations so reminders can wait for them.</summary>
    internal static class Fullscreen
    {
        private const int QUNS_BUSY = 2;                  // a fullscreen app is running
        private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
        private const int QUNS_PRESENTATION_MODE = 4;

        public static bool IsActive()
        {
            try
            {
                if (Native.SHQueryUserNotificationState(out int state) == 0 &&
                    (state == QUNS_BUSY || state == QUNS_RUNNING_D3D_FULL_SCREEN || state == QUNS_PRESENTATION_MODE))
                    return true;
                return ForegroundCoversMonitor();
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Catches borderless "fullscreen" windows that the shell doesn't report.</summary>
        private static bool ForegroundCoversMonitor()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == Native.GetShellWindow() || fg == Native.GetDesktopWindow()) return false;

            var cls = new StringBuilder(64);
            Native.GetClassName(fg, cls, cls.Capacity);
            string c = cls.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") return false;

            // A normal maximized window (e.g. with an auto-hiding taskbar) is not "fullscreen".
            if (Native.IsZoomed(fg)) return false;
            if ((Native.GetWindowLong(fg, Native.GWL_STYLE) & Native.WS_CAPTION) == Native.WS_CAPTION) return false;

            if (!Native.GetWindowRect(fg, out var r)) return false;
            var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MONITORINFO)) };
            if (!Native.GetMonitorInfo(Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST), ref mi)) return false;
            var m = mi.rcMonitor;
            return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
        }
    }
}
