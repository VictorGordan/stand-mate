using System;
using System.Drawing;
using System.Windows.Forms;
using StandMate.Platform;

namespace StandMate.UI
{
    /// <summary>Shows a big screen number in the middle of each monitor for a moment.</summary>
    internal sealed class IdentifyOverlay : Form
    {
        private readonly string _text;
        private readonly Font _font;

        public static void ShowAll()
        {
            var screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++) new IdentifyOverlay(screens[i], (i + 1).ToString()).Show();
        }

        private IdentifyOverlay(Screen screen, string text)
        {
            _text = text;
            float scale = Screens.Scale(screen);
            int size = (int)Math.Round(160 * scale);
            _font = new Font("Segoe UI Semibold", 84 * scale, GraphicsUnit.Pixel);
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Theme.Surface;
            var b = screen.Bounds;
            Bounds = new Rectangle(b.Left + (b.Width - size) / 2, b.Top + (b.Height - size) / 2, size, size);
            var t = new Timer { Interval = 1600 };
            t.Tick += (s, e) => { t.Dispose(); Close(); };
            t.Start();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = Native.DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            int border = Theme.ColorRef(Theme.Accent);
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_DPICHANGED) { m.Result = IntPtr.Zero; return; }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            TextRenderer.DrawText(e.Graphics, _text, _font, ClientRectangle, Theme.Accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _font.Dispose();
            base.Dispose(disposing);
        }
    }
}
