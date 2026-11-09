using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using StandMate.Platform;

namespace StandMate.UI
{
    /// <summary>Quiet dark palette with a single muted sakura accent.</summary>
    internal static class Theme
    {
        public static readonly Color Bg = Hex(0x1C1D22);
        public static readonly Color Surface = Hex(0x26272E);
        public static readonly Color Control = Hex(0x2F3038);
        public static readonly Color ControlHover = Hex(0x3A3B45);
        public static readonly Color Border = Hex(0x42434D);
        public static readonly Color Text = Hex(0xE7E7EA);
        public static readonly Color Muted = Hex(0x9A9BA3);
        public static readonly Color Accent = Hex(0xE39AAE);
        public static readonly Color AccentHover = Hex(0xEDB1C1);
        public static readonly Color Error = Hex(0xEE8A7A);
        public static readonly Color Dim = Hex(0x6E6F78);

        /// <summary>Point-sized font for menus (WinForms scales ToolStrip menus itself).</summary>
        public static readonly Font MenuFont = new Font("Segoe UI", 9F);

        public const string KanjiFontName = "Yu Gothic UI";

        public static Color Hex(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

        public static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

        /// <summary>Dark title bar + caption colour matching the window (Windows 10 2004+ / 11).</summary>
        public static void DarkTitleBar(IntPtr hwnd)
        {
            int on = 1;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
            int caption = ColorRef(Bg);
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        }

        public static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>Dark renderer for the tray menu and popup menus.</summary>
    internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new Colors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.Muted;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            float s = Math.Min(r.Width, r.Height) * 0.36f;
            using (var b = new SolidBrush(Theme.Accent))
                g.FillEllipse(b, r.X + (r.Width - s) / 2f, r.Y + (r.Height - s) / 2f, s, s);
        }

        private sealed class Colors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Theme.Surface;
            public override Color ImageMarginGradientBegin => Theme.Surface;
            public override Color ImageMarginGradientMiddle => Theme.Surface;
            public override Color ImageMarginGradientEnd => Theme.Surface;
            public override Color MenuBorder => Theme.Border;
            public override Color MenuItemBorder => Theme.ControlHover;
            public override Color MenuItemSelected => Theme.ControlHover;
            public override Color MenuItemSelectedGradientBegin => Theme.ControlHover;
            public override Color MenuItemSelectedGradientEnd => Theme.ControlHover;
            public override Color MenuItemPressedGradientBegin => Theme.Control;
            public override Color MenuItemPressedGradientEnd => Theme.Control;
            public override Color SeparatorDark => Theme.Border;
            public override Color SeparatorLight => Theme.Surface;
            public override Color CheckBackground => Theme.Surface;
            public override Color CheckSelectedBackground => Theme.ControlHover;
            public override Color CheckPressedBackground => Theme.ControlHover;
        }
    }
}
