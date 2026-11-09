using System;
using System.Drawing;
using System.Windows.Forms;
using StandMate.Platform;

namespace StandMate.UI
{
    internal static class Screens
    {
        public static Screen Find(string deviceName)
        {
            if (!string.IsNullOrEmpty(deviceName))
                foreach (var s in Screen.AllScreens)
                    if (string.Equals(s.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase)) return s;
            return Screen.PrimaryScreen;
        }

        public static int IndexOf(Screen screen) => Array.IndexOf(Screen.AllScreens, screen);

        public static string Describe(Screen s, int index) =>
            $"Screen {index + 1}  ·  {s.Bounds.Width}×{s.Bounds.Height}{(s.Primary ? "  ·  main" : "")}";

        /// <summary>DPI scale of a monitor (1.0 = 100%). The app is per-monitor DPI aware.</summary>
        public static float Scale(Screen s)
        {
            try
            {
                var c = new Native.POINT(s.Bounds.Left + s.Bounds.Width / 2, s.Bounds.Top + s.Bounds.Height / 2);
                IntPtr mon = Native.MonitorFromPoint(c, Native.MONITOR_DEFAULTTONEAREST);
                if (Native.GetDpiForMonitor(mon, 0, out uint dpi, out _) == 0 && dpi > 0) return dpi / 96f;
            }
            catch (Exception) { }
            return 1f;
        }

        /// <summary>Where a window of <paramref name="size"/> goes for an anchor (or custom fraction) on a screen.</summary>
        public static Point Place(Size size, Screen screen, string anchor, double customX, double customY, int margin)
        {
            var wa = screen.WorkingArea;
            int left = wa.Left + margin, right = wa.Right - size.Width - margin;
            int top = wa.Top + margin, bottom = wa.Bottom - size.Height - margin;
            int cx = wa.Left + (wa.Width - size.Width) / 2, cy = wa.Top + (wa.Height - size.Height) / 2;

            int x, y;
            switch (anchor)
            {
                case "TopLeft": x = left; y = top; break;
                case "Top": x = cx; y = top; break;
                case "TopRight": x = right; y = top; break;
                case "Left": x = left; y = cy; break;
                case "Center": x = cx; y = cy; break;
                case "Right": x = right; y = cy; break;
                case "BottomLeft": x = left; y = bottom; break;
                case "Bottom": x = cx; y = bottom; break;
                case "Custom":
                    x = wa.Left + (int)Math.Round(customX * Math.Max(0, wa.Width - size.Width));
                    y = wa.Top + (int)Math.Round(customY * Math.Max(0, wa.Height - size.Height));
                    break;
                default: x = right; y = bottom; break; // BottomRight
            }
            x = Math.Max(wa.Left, Math.Min(x, wa.Right - size.Width));
            y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - size.Height));
            return new Point(x, y);
        }

        /// <summary>Inverse of <see cref="Place"/> for "Custom": the fractional position of a window inside a work area.</summary>
        public static void ToFraction(Rectangle bounds, Screen screen, out double fx, out double fy)
        {
            var wa = screen.WorkingArea;
            int freeW = Math.Max(1, wa.Width - bounds.Width), freeH = Math.Max(1, wa.Height - bounds.Height);
            fx = Math.Max(0, Math.Min(1, (bounds.Left - wa.Left) / (double)freeW));
            fy = Math.Max(0, Math.Min(1, (bounds.Top - wa.Top) / (double)freeH));
        }
    }
}
