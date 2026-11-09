using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;
using StandMate.Core;
using StandMate.Platform;

namespace StandMate.UI
{
    /// <summary>
    /// Tray icons drawn once at startup from a font glyph: 座 while sitting, 立 while standing,
    /// greyed out while paused/away. No image assets needed.
    /// </summary>
    internal sealed class TrayIcons : IDisposable
    {
        private readonly Icon _sit, _stand, _sitDim, _standDim;

        public TrayIcons()
        {
            int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
            _sit = Make(Phase.Sit.Kanji(), Theme.Accent, Theme.Bg, size);
            _stand = Make(Phase.Stand.Kanji(), Theme.Accent, Theme.Bg, size);
            _sitDim = Make(Phase.Sit.Kanji(), Theme.Dim, Theme.Bg, size);
            _standDim = Make(Phase.Stand.Kanji(), Theme.Dim, Theme.Bg, size);
        }

        public Icon For(Phase phase, bool dim) =>
            phase == Phase.Sit ? (dim ? _sitDim : _sit) : (dim ? _standDim : _stand);

        public static Icon Make(string glyph, Color bg, Color fg, int size)
        {
            using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);
                    using (var path = Theme.RoundedRect(new RectangleF(0, 0, size - 0.5f, size - 0.5f), size * 0.22f))
                    using (var b = new SolidBrush(bg))
                        g.FillPath(b, path);

                    using (var family = new FontFamily(Theme.KanjiFontName))
                    using (var glyphPath = new GraphicsPath())
                    {
                        glyphPath.AddString(glyph, family, (int)FontStyle.Bold, size * 0.8f, PointF.Empty, StringFormat.GenericTypographic);
                        var gb = glyphPath.GetBounds();
                        float target = size * 0.74f;
                        float k = target / Math.Max(gb.Width, gb.Height);
                        using (var m = new Matrix())
                        {
                            m.Translate(size / 2f, size / 2f);
                            m.Scale(k, k);
                            m.Translate(-(gb.X + gb.Width / 2f), -(gb.Y + gb.Height / 2f));
                            glyphPath.Transform(m);
                        }
                        using (var b = new SolidBrush(fg))
                            g.FillPath(b, glyphPath);
                    }
                }
                IntPtr h = bmp.GetHicon();
                try
                {
                    using (var tmp = Icon.FromHandle(h))
                        return (Icon)tmp.Clone(); // the clone owns its own handle
                }
                finally
                {
                    Native.DestroyIcon(h);
                }
            }
        }

        public void Dispose()
        {
            _sit.Dispose(); _stand.Dispose(); _sitDim.Dispose(); _standDim.Dispose();
        }
    }
}
