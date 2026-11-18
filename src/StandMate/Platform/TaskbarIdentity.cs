using System;
using System.Runtime.InteropServices;

namespace StandMate.Platform
{
    /// <summary>
    /// Gives a window its own taskbar identity. Windows 11 (with buttons combined, the default) shows the icon of the
    /// *app* a window belongs to, which is StandMate.exe's icon, not the window's own icon. A window with its own
    /// AppUserModelID and relaunch icon counts as a separate app, so its button shows that icon (as Chrome does for
    /// profiles). See https://learn.microsoft.com/windows/win32/shell/appids.
    /// </summary>
    internal static class TaskbarIdentity
    {
        private static readonly Guid AppUserModelFmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        private const int PidRelaunchCommand = 2, PidRelaunchIcon = 3, PidRelaunchName = 4, PidId = 5;

        /// <summary>
        /// Sets (or with <paramref name="appId"/> null, clears) the window's identity. Set it before the window is
        /// shown; the icon must be a file path ("C:\...\x.ico,0"). Best effort: failures leave the default identity.
        /// </summary>
        public static void Set(IntPtr hwnd, string appId, string iconResource, string relaunchCommand, string displayName)
        {
            IPropertyStore store = null;
            try
            {
                var iid = typeof(IPropertyStore).GUID;
                if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) != 0 || store == null) return;
                // Relaunch properties first and the ID last: the shell reads them when the ID changes.
                SetString(store, PidRelaunchCommand, appId == null ? null : relaunchCommand);
                SetString(store, PidRelaunchIcon, appId == null ? null : iconResource);
                SetString(store, PidRelaunchName, appId == null ? null : displayName);
                SetString(store, PidId, appId);
                store.Commit();
            }
            catch (Exception) { /* cosmetic only */ }
            finally
            {
                if (store != null) Marshal.ReleaseComObject(store);
            }
        }

        private static void SetString(IPropertyStore store, int pid, string value)
        {
            var key = new PropertyKey { fmtid = AppUserModelFmtid, pid = pid };
            var pv = new PropVariant();
            try
            {
                if (value != null)
                {
                    pv.vt = 31; // VT_LPWSTR
                    pv.p = Marshal.StringToCoTaskMemUni(value);
                }
                store.SetValue(ref key, ref pv);
            }
            finally
            {
                if (pv.p != IntPtr.Zero) Marshal.FreeCoTaskMem(pv.p);
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PropertyKey
        {
            public Guid fmtid;
            public int pid;
        }

        /// <summary>PROPVARIANT holding at most a string pointer (VT_EMPTY or VT_LPWSTR).</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant
        {
            public ushort vt;
            public ushort r1, r2, r3;
            public IntPtr p;
            public IntPtr p2;
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            void GetCount(out uint count);
            void GetAt(uint index, out PropertyKey key);
            void GetValue(ref PropertyKey key, out PropVariant value);
            void SetValue(ref PropertyKey key, ref PropVariant value);
            void Commit();
        }

        [DllImport("shell32.dll")]
        private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    }
}
