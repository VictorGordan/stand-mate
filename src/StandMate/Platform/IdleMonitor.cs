using System;
using System.Runtime.InteropServices;

namespace StandMate.Platform
{
    /// <summary>
    /// Tells how long it has been since the last mouse/keyboard input.
    ///
    /// PRIVACY: this calls only the Win32 function GetLastInputInfo, which returns a single
    /// number: the system tick count of the most recent input event. It carries no key codes,
    /// no mouse buttons, no cursor coordinates and no window information. StandMate installs
    /// no keyboard/mouse hooks (SetWindowsHookEx), registers no raw input, and stores
    /// nothing about your input anywhere.
    /// </summary>
    internal static class IdleMonitor
    {
        public static void Read(out TimeSpan idle, out uint lastInputTick)
        {
            var info = new Native.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(Native.LASTINPUTINFO)) };
            if (!Native.GetLastInputInfo(ref info))
            {
                idle = TimeSpan.Zero;
                lastInputTick = 0;
                return;
            }
            lastInputTick = info.dwTime;
            // Unsigned subtraction survives the 49.7-day tick-count wraparound.
            uint ms = unchecked((uint)Environment.TickCount - info.dwTime);
            idle = TimeSpan.FromMilliseconds(ms);
        }
    }
}
