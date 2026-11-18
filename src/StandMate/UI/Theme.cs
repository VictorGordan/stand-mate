using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using StandMate.Core;
using StandMate.Platform;

namespace StandMate.UI
{
    /// <summary>One colour scheme: a quiet dark base and a single muted accent.</summary>
    internal sealed class Palette
    {
        public string Id, Name, Kanji, Description;
        public Color Bg, Surface, Control, ControlHover, Border, Accent, AccentHover;

        public static readonly Palette[] All =
        {
            // The original look; the neutral greys are shared by the other themes, only slightly tinted.
            new Palette
            {
                Id = ThemeIds.Sakura, Name = "Sakura", Kanji = "桜", Description = "Cherry blossom pink",
                Bg = Theme.Hex(0x1C1D22), Surface = Theme.Hex(0x26272E), Control = Theme.Hex(0x2F3038),
                ControlHover = Theme.Hex(0x3A3B45), Border = Theme.Hex(0x42434D),
                Accent = Theme.Hex(0xE39AAE), AccentHover = Theme.Hex(0xEDB1C1),
            },
            new Palette
            {
                Id = ThemeIds.Minty, Name = "Minty", Kanji = "薄荷", Description = "Fresh mint green",
                Bg = Theme.Hex(0x1A1E1E), Surface = Theme.Hex(0x232828), Control = Theme.Hex(0x2C3232),
                ControlHover = Theme.Hex(0x363E3D), Border = Theme.Hex(0x3F4847),
                Accent = Theme.Hex(0x8FD6B8), AccentHover = Theme.Hex(0xA9E2CA),
            },
            new Palette
            {
                Id = ThemeIds.Lavender, Name = "Lavender", Kanji = "藤", Description = "Soft wisteria purple",
                Bg = Theme.Hex(0x1D1C23), Surface = Theme.Hex(0x27262F), Control = Theme.Hex(0x302F3A),
                ControlHover = Theme.Hex(0x3B3A47), Border = Theme.Hex(0x44434F),
                Accent = Theme.Hex(0xB5A3E6), AccentHover = Theme.Hex(0xC7B9EE),
            },
            new Palette
            {
                Id = ThemeIds.Sky, Name = "Sky", Kanji = "空", Description = "Clear sky blue",
                Bg = Theme.Hex(0x1B1D22), Surface = Theme.Hex(0x25282E), Control = Theme.Hex(0x2E3138),
                ControlHover = Theme.Hex(0x393D45), Border = Theme.Hex(0x42464E),
                Accent = Theme.Hex(0x8EC1E8), AccentHover = Theme.Hex(0xA9D0EE),
            },
            new Palette
            {
                Id = ThemeIds.Amber, Name = "Amber", Kanji = "柚子", Description = "Warm yuzu amber",
                Bg = Theme.Hex(0x1F1D1B), Surface = Theme.Hex(0x292724), Control = Theme.Hex(0x33302C),
                ControlHover = Theme.Hex(0x3E3A35), Border = Theme.Hex(0x48443E),
                Accent = Theme.Hex(0xE8BE7E), AccentHover = Theme.Hex(0xEECD99),
            },
        };

        public static Palette Get(string id) => Array.Find(All, p => p.Id == id) ?? All[0];
    }

    /// <summary>The colours in use: <see cref="Palette.Get"/> of the chosen theme (Sakura by default).</summary>
    internal static class Theme
    {
        public static Palette Current { get; private set; }

        /// <summary>Switches the theme. Windows built afterwards use it; the caller refreshes open ones.</summary>
        public static void Apply(string id) => Current = Palette.Get(id);

        public static Color Bg => (Current ?? Palette.All[0]).Bg;
        public static Color Surface => (Current ?? Palette.All[0]).Surface;
        public static Color Control => (Current ?? Palette.All[0]).Control;
        public static Color ControlHover => (Current ?? Palette.All[0]).ControlHover;
        public static Color Border => (Current ?? Palette.All[0]).Border;
        public static Color Accent => (Current ?? Palette.All[0]).Accent;
        public static Color AccentHover => (Current ?? Palette.All[0]).AccentHover;
        public static readonly Color Text = Hex(0xE7E7EA);
        public static readonly Color Muted = Hex(0x9A9BA3);
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
