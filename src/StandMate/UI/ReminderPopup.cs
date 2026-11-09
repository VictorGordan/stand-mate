using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using StandMate.Core;
using StandMate.Platform;

namespace StandMate.UI
{
    internal enum PopupKind
    {
        /// <summary>The real reminder: click = switched, right-click = snooze/skip.</summary>
        Reminder,
        /// <summary>Settings preview: any click closes.</summary>
        Preview,
        /// <summary>Drag to position, double-click/Enter to save, Esc to cancel.</summary>
        Placement,
    }

    /// <summary>
    /// The GIF card. Borderless, always on top, never steals focus, never in the taskbar/Alt-Tab.
    /// Everything is sized in physical pixels for the target monitor's DPI.
    /// </summary>
    internal sealed class ReminderPopup : Form
    {
        private const int RepeatDelayMs = 8000;

        private readonly PopupKind _kind;
        private readonly Phase _phase;
        private readonly string _caption;
        private readonly string _soundPath;
        private readonly int _volume;
        private readonly bool _repeat;
        private readonly float _scale;
        private readonly Rectangle _targetBounds;
        private readonly Font _captionFont, _kanjiFont, _hintFont;

        private MemoryStream _imageStream;
        private Image _image;
        private bool _animating;
        private Rectangle _imageRect, _captionRect;
        private Timer _repeatTimer;
        private ToolTip _tip;
        private bool _soundStarted;
        private bool _dragging;
        private Point _dragOffset;
        private ContextMenuStrip _menu;

        public event Action Confirmed;
        public event Action<int> Snoozed;
        public event Action Skipped;
        public event Action<Rectangle> PlacementSaved;

        public PopupKind Kind => _kind;

        public ReminderPopup(PopupKind kind, Mate mate, Phase phase, Settings s)
        {
            _kind = kind;
            _phase = phase;
            _caption = kind == PopupKind.Placement ? "Drag me, then double-click" : (mate?.Caption(phase) ?? phase.Label());
            _soundPath = kind == PopupKind.Placement ? null : mate?.SoundPath(phase);
            _volume = s.Volume;
            _repeat = s.RepeatSound && kind == PopupKind.Reminder;

            var screen = Screens.Find(s.ScreenDevice);
            _scale = Screens.Scale(screen);
            _captionFont = new Font("Segoe UI Semibold", 13f * _scale, GraphicsUnit.Pixel);
            _kanjiFont = new Font(Theme.KanjiFontName, 17f * _scale, FontStyle.Bold, GraphicsUnit.Pixel);
            _hintFont = new Font("Segoe UI", 11f * _scale, GraphicsUnit.Pixel);

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
            BackColor = Theme.Surface;
            Text = "StandMate";
            KeyPreview = true;
            Cursor = kind == PopupKind.Placement ? Cursors.SizeAll : Cursors.Hand;

            LoadImage(mate?.ImagePath(phase));
            var size = ComputeLayout(s.PopupWidth);
            var loc = Screens.Place(size, screen, s.Anchor, s.CustomX, s.CustomY, (int)Math.Round(20 * _scale));
            _targetBounds = new Rectangle(loc, size);
            Bounds = _targetBounds;

            if (kind == PopupKind.Reminder)
            {
                _tip = new ToolTip { ShowAlways = true, InitialDelay = 900 };
                _tip.SetToolTip(this, "Click once you've switched  ·  right-click to snooze or skip");
            }
        }

        // ---------------- window plumbing ----------------

        protected override bool ShowWithoutActivation => _kind != PopupKind.Placement;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
                if (_kind != PopupKind.Placement) cp.ExStyle |= Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = Native.DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            int border = Theme.ColorRef(_kind == PopupKind.Placement ? Theme.Accent : Theme.Border);
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_MOUSEACTIVATE when _kind != PopupKind.Placement:
                    m.Result = (IntPtr)Native.MA_NOACTIVATE; // clicks work without stealing focus
                    return;
                case Native.WM_DPICHANGED:
                    m.Result = IntPtr.Zero; // we size ourselves in physical pixels
                    return;
                case Native.MM_MCINOTIFY:
                    if ((int)m.WParam == Native.MCI_NOTIFY_SUCCESSFUL && _repeat && !IsDisposed)
                    {
                        if (_repeatTimer == null)
                        {
                            _repeatTimer = new Timer { Interval = RepeatDelayMs };
                            _repeatTimer.Tick += (s, e) => { _repeatTimer.Stop(); Audio.Replay(Handle); };
                        }
                        _repeatTimer.Start();
                    }
                    return;
            }
            base.WndProc(ref m);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Bounds = _targetBounds; // in case WinForms rescaled anything during handle creation
            if (_image != null && ImageAnimator.CanAnimate(_image))
            {
                ImageAnimator.Animate(_image, OnFrameChanged);
                _animating = true;
            }
            if (_soundPath != null)
                _soundStarted = Audio.Play(_soundPath, _volume, _repeat ? Handle : IntPtr.Zero);
            if (_kind == PopupKind.Placement) Activate();
        }

        private void OnFrameChanged(object sender, EventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(() => Invalidate(_imageRect))); }
            catch (InvalidOperationException) { /* closing */ }
        }

        // ---------------- layout & paint ----------------

        private void LoadImage(string path)
        {
            if (path == null) return;
            try
            {
                // Load from memory so the file is never locked (the mate editor may replace it).
                _imageStream = new MemoryStream(File.ReadAllBytes(path));
                _image = Image.FromStream(_imageStream);
            }
            catch (Exception)
            {
                _image?.Dispose(); _image = null;
                _imageStream?.Dispose(); _imageStream = null;
            }
        }

        private Size ComputeLayout(int logicalWidth)
        {
            int w = (int)Math.Round(logicalWidth * _scale);
            int pad = (int)Math.Round(6 * _scale);
            int capH = (int)Math.Round(36 * _scale);

            if (_image != null && _image.Width > 0 && _image.Height > 0)
            {
                int iw = w - 2 * pad;
                int ih = (int)Math.Round(iw * (double)_image.Height / _image.Width);
                int maxH = (int)(iw * 1.3);
                if (ih > maxH) // very tall GIFs: limit height, keep aspect
                {
                    ih = maxH;
                    iw = (int)Math.Round(ih * (double)_image.Width / _image.Height);
                }
                _imageRect = new Rectangle((w - iw) / 2, pad, iw, ih);
                _captionRect = new Rectangle(pad, pad + ih, w - 2 * pad, capH);
                return new Size(w, pad + ih + capH);
            }

            _imageRect = Rectangle.Empty;
            _captionRect = new Rectangle(pad, pad, w - 2 * pad, (int)Math.Round(52 * _scale));
            return new Size(w, 2 * pad + _captionRect.Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;

            if (_image != null)
            {
                if (_animating) ImageAnimator.UpdateFrames(_image);
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(_image, _imageRect);
                g.PixelOffsetMode = PixelOffsetMode.Default;
            }

            if (!e.ClipRectangle.IntersectsWith(_captionRect) && _kind != PopupKind.Placement) return;

            // 立 / 座 in the accent colour, then the caption.
            var r = _captionRect;
            int gap = (int)Math.Round(8 * _scale);
            string kanji = _phase.Kanji();
            var kSize = TextRenderer.MeasureText(g, kanji, _kanjiFont, Size.Empty, TextFormatFlags.NoPadding);
            var kRect = new Rectangle(r.Left + gap / 2, r.Top, kSize.Width, r.Height);
            TextRenderer.DrawText(g, kanji, _kanjiFont, kRect, Theme.Accent,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);

            var tRect = new Rectangle(kRect.Right + gap, r.Top, r.Right - kRect.Right - gap, r.Height);
            TextRenderer.DrawText(g, _caption, _captionFont, tRect, Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            if (_kind == PopupKind.Placement)
            {
                string hint = "Esc to cancel";
                var hs = TextRenderer.MeasureText(g, hint, _hintFont);
                TextRenderer.DrawText(g, hint, _hintFont,
                    new Rectangle(r.Right - hs.Width, r.Top, hs.Width, r.Height), Theme.Muted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
                using (var pen = new Pen(Theme.Accent, Math.Max(1, _scale * 1.5f)) { DashStyle = DashStyle.Dash })
                    g.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
            }
        }

        // ---------------- input ----------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_kind == PopupKind.Placement && e.Button == MouseButtons.Left)
            {
                _dragging = true;
                _dragOffset = e.Location;
                Capture = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging)
            {
                var p = Cursor.Position;
                Location = new Point(p.X - _dragOffset.X, p.Y - _dragOffset.Y);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_kind == PopupKind.Placement)
            {
                _dragging = false;
                Capture = false;
                if (e.Button == MouseButtons.Right) ShowPlacementMenu(e.Location);
                return;
            }

            if (e.Button == MouseButtons.Left)
            {
                if (_kind == PopupKind.Reminder) Confirmed?.Invoke();
                Close();
            }
            else if (e.Button == MouseButtons.Right)
            {
                if (_kind == PopupKind.Reminder) ShowReminderMenu(e.Location);
                else Close();
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (_kind == PopupKind.Placement && e.Button == MouseButtons.Left) SavePlacement();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_kind != PopupKind.Placement) return;
            if (e.KeyCode == Keys.Escape) Close();
            else if (e.KeyCode == Keys.Enter) SavePlacement();
        }

        private void SavePlacement()
        {
            PlacementSaved?.Invoke(Bounds);
            Close();
        }

        private void ShowReminderMenu(Point p)
        {
            ShowMenu(p,
                ("Snooze 5 min", () => { Snoozed?.Invoke(5); Close(); }),
                ("Snooze 15 min", () => { Snoozed?.Invoke(15); Close(); }),
                ("Skip this switch", () => { Skipped?.Invoke(); Close(); }));
        }

        private void ShowPlacementMenu(Point p)
        {
            ShowMenu(p,
                ("Save position", SavePlacement),
                ("Cancel", Close));
        }

        private void ShowMenu(Point p, params (string text, Action action)[] items)
        {
            _menu?.Dispose();
            _menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, Font = Theme.MenuFont };
            foreach (var (text, action) in items)
            {
                var a = action;
                _menu.Items.Add(text, null, (s, e) => BeginInvoke(a));
            }
            // Let the menu take focus so it closes normally when clicking elsewhere.
            NativeForeground(Handle);
            _menu.Show(this, p);
        }

        private static void NativeForeground(IntPtr hwnd)
        {
            try { SetForegroundWindow(hwnd); } catch (Exception) { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        // ---------------- cleanup ----------------

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_soundStarted) Audio.Stop();
            _repeatTimer?.Dispose();
            if (_animating) ImageAnimator.StopAnimate(_image, OnFrameChanged);
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _menu?.Dispose();
                _tip?.Dispose();
                _image?.Dispose();
                _imageStream?.Dispose();
                _captionFont.Dispose();
                _kanjiFont.Dispose();
                _hintFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
