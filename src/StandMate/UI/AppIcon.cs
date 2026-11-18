using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace StandMate.UI
{
    /// <summary>
    /// The window icon in the current theme's colours. The app's own icon (assets/icon/standmate.ico, drawn in Sakura)
    /// is recoloured pixel by pixel, so every theme keeps exactly the same shape and all its sizes stay crisp.
    /// The .exe's embedded icon (Explorer, Start menu) can't change; this only affects open windows.
    /// </summary>
    internal static class AppIcon
    {
        private const string Resource = "StandMate.icon.ico";
        private static readonly Color SourceAccent = Theme.Hex(0xE39AAE); // see tools/make_icon.py
        private static readonly Color SourceInk = Theme.Hex(0x1C1D22);
        private static readonly Dictionary<string, Icon> Cache = new Dictionary<string, Icon>();
        private static readonly Dictionary<string, byte[]> Bytes = new Dictionary<string, byte[]>();

        /// <summary>The icon for the current theme; cached for the app's lifetime (a few KB each).</summary>
        public static Icon Current
        {
            get
            {
                var p = Theme.Current ?? Palette.All[0];
                if (Cache.TryGetValue(p.Id, out var icon)) return icon;
                try { icon = new Icon(new MemoryStream(BytesFor(p))); }
                catch (Exception ex)
                {
                    Program.LogError(ex);
                    try { icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { icon = null; }
                }
                Cache[p.Id] = icon;
                return icon;
            }
        }

        /// <summary>
        /// The current theme's icon as an .ico file, for the taskbar (which only takes icons from files). Written to
        /// %LOCALAPPDATA%\StandMate\icons\ under a name that changes with its content, so Windows' icon cache can't
        /// show an outdated one. Null on failure.
        /// </summary>
        public static string CurrentFile()
        {
            try
            {
                var p = Theme.Current ?? Palette.All[0];
                byte[] bytes = BytesFor(p);
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StandMate", "icons");
                string hash;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    hash = BitConverter.ToString(sha.ComputeHash(bytes), 0, 4).Replace("-", "").ToLowerInvariant();
                string path = Path.Combine(dir, $"{p.Id}-{hash}.ico");
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(dir);
                    foreach (var old in Directory.GetFiles(dir, p.Id + "-*.ico"))
                        try { File.Delete(old); } catch (Exception) { }
                    File.WriteAllBytes(path, bytes);
                }
                return path;
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
                return null;
            }
        }

        private static byte[] BytesFor(Palette p)
        {
            if (!Bytes.TryGetValue(p.Id, out var b)) Bytes[p.Id] = b = Build(p);
            return b;
        }

        private static byte[] Build(Palette p)
        {
            byte[] src;
            using (var s = typeof(AppIcon).Assembly.GetManifestResourceStream(Resource))
            {
                if (s == null) throw new InvalidOperationException("Missing resource " + Resource);
                using (var ms = new MemoryStream()) { s.CopyTo(ms); src = ms.ToArray(); }
            }

            int count = BitConverter.ToUInt16(src, 4);
            var frames = new List<byte[]>();
            for (int i = 0; i < count; i++)
            {
                int e = 6 + 16 * i;
                int size = BitConverter.ToInt32(src, e + 8), offset = BitConverter.ToInt32(src, e + 12);
                int w = src[e] == 0 ? 256 : src[e];
                using (var frame = DecodeFrame(src, offset, size, w))
                {
                    Recolour(frame, p);
                    if (w >= 256)
                        using (var ms = new MemoryStream()) { frame.Save(ms, ImageFormat.Png); frames.Add(ms.ToArray()); }
                    else
                        frames.Add(ToDib(frame));
                }
            }

            // Write a new .ico: header, directory, then the frames. Classic 32-bit DIB frames, because .NET
            // Framework's Icon class misreads PNG frames; only the 256 px one (used by the shell, never by windows)
            // is PNG, which keeps the file small.
            var outIco = new MemoryStream();
            using (var w = new BinaryWriter(outIco, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)count);
                int offset = 6 + 16 * count;
                for (int i = 0; i < count; i++)
                {
                    int e = 6 + 16 * i;
                    w.Write(src[e]); w.Write(src[e + 1]);   // width, height (0 = 256)
                    w.Write((byte)0); w.Write((byte)0);     // palette, reserved
                    w.Write((ushort)1); w.Write((ushort)32);
                    w.Write(frames[i].Length); w.Write(offset);
                    offset += frames[i].Length;
                }
                foreach (var f in frames) w.Write(f);
            }
            return outIco.ToArray();
        }

        private static Bitmap DecodeFrame(byte[] ico, int offset, int size, int width)
        {
            bool png = size > 8 && ico[offset] == 0x89 && ico[offset + 1] == (byte)'P';
            if (png)
                using (var ms = new MemoryStream(ico, offset, size))
                using (var img = Image.FromStream(ms))
                    return new Bitmap(img); // 32bpp ARGB copy, independent of the stream
            using (var ms = new MemoryStream(ico))
            using (var icon = new Icon(ms, width, width))
                return icon.ToBitmap();
        }

        /// <summary>An .ico frame: BITMAPINFOHEADER (double height), bottom-up BGRA pixels, then an empty AND mask.</summary>
        private static byte[] ToDib(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            int maskStride = ((w + 31) / 32) * 4;
            var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(40); bw.Write(w); bw.Write(h * 2);
                bw.Write((ushort)1); bw.Write((ushort)32);
                bw.Write(0); bw.Write(w * h * 4 + maskStride * h);   // BI_RGB, image size
                bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);

                var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var row = new byte[w * 4];
                    for (int y = h - 1; y >= 0; y--)
                    {
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                        bw.Write(row);
                    }
                }
                finally { bmp.UnlockBits(data); }
                bw.Write(new byte[maskStride * h]); // alpha decides transparency
                bw.Flush();
                return ms.ToArray();
            }
        }

        /// <summary>Maps each pixel's position between ink and accent onto the theme's ink and accent; alpha is kept.</summary>
        private static void Recolour(Bitmap bmp, Palette p)
        {
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int bytes = Math.Abs(data.Stride) * bmp.Height;
                var px = new byte[bytes];
                Marshal.Copy(data.Scan0, px, 0, bytes);
                float dr = SourceAccent.R - SourceInk.R, dg = SourceAccent.G - SourceInk.G, db = SourceAccent.B - SourceInk.B;
                float len2 = dr * dr + dg * dg + db * db;
                for (int i = 0; i < bytes; i += 4)
                {
                    if (px[i + 3] == 0) continue;
                    float t = ((px[i + 2] - SourceInk.R) * dr + (px[i + 1] - SourceInk.G) * dg + (px[i] - SourceInk.B) * db) / len2;
                    t = t < 0 ? 0 : t > 1 ? 1 : t;
                    px[i + 2] = Lerp(p.Bg.R, p.Accent.R, t);
                    px[i + 1] = Lerp(p.Bg.G, p.Accent.G, t);
                    px[i] = Lerp(p.Bg.B, p.Accent.B, t);
                }
                Marshal.Copy(px, 0, data.Scan0, bytes);
            }
            finally { bmp.UnlockBits(data); }
        }

        private static byte Lerp(byte a, byte b, float t) => (byte)Math.Round(a + (b - a) * t);
    }
}
