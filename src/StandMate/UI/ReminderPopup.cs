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
        /// <summary>The real reminder: click = confirmed, right-click = its menu (snooze/skip).</summary>
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
        private const string PlacementHint = "Esc to cancel";

        private readonly PopupKind _kind;
        private readonly string _kanji;
        private readonly string _caption;
        private readonly string _soundPath;
        private readonly int _volume;
        private readonly float _scale;
        private readonly Rectangle _targetBounds;
        private readonly Font _captionFont, _kanjiFont, _hintFont;

        private MemoryStream _imageStream;
        private Image _image;
        private bool _animating;
        private Rectangle _imageRect, _captionRect, _kanjiRect, _textRect;
        private Timer _repeatTimer;
        private DarkToolTip _tip;
        private bool _soundStarted;
        private bool _dragging;
        private Point _dragOffset;
        private ContextMenuStrip _menu;

        public event Action Confirmed;
        public event Action<Rectangle> PlacementSaved;

        public PopupKind Kind => _kind;

        /// <summary>Replay the sound every few seconds until clicked. Defaults to the setting, for reminders. Set before Show.</summary>
        public bool Repeat { get; set; }

        /// <summary>Reminders: the right-click menu. Each item closes the popup after running.</summary>
        public (string Text, Action Action)[] MenuItems { get; set; } = new (string, Action)[0];

        /// <summary>Reminders: the tooltip.</summary>
        public string Hint
        {
            set
            {
                if (_tip == null) _tip = new DarkToolTip(_hintFont, _scale) { InitialDelay = 900 };
                _tip.SetToolTip(this, value);
            }
        }

        public ReminderPopup(PopupKind kind, Cue cue, Settings s)
        {
            cue = cue ?? new Cue { Caption = Phase.Stand.Label(), Kanji = Phase.Stand.Kanji() };
            _kind = kind;
            _kanji = cue.Kanji ?? "";
            _caption = kind == PopupKind.Placement ? "Drag me, then double-click" : cue.Caption ?? "";
            _soundPath = kind == PopupKind.Placement ? null : cue.SoundPath;
            _volume = s.Volume;
            Repeat = s.RepeatSound && kind == PopupKind.Reminder;

            var screen = Screens.Find(s.ScreenDevice);
            _scale = Screens.Scale(screen);
            float textPx = s.PopupTextSize * _scale;
            _captionFont = CaptionFont(s.PopupFont, textPx);
            _kanjiFont = new Font(Theme.KanjiFontName, textPx * 17f / PopupText.DefaultSize, FontStyle.Bold, GraphicsUnit.Pixel);
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

            LoadImage(cue.ImagePath);
            var size = ComputeLayout(s.PopupWidth);
            var loc = Screens.Place(size, screen, s.Anchor, s.CustomX, s.CustomY, (int)Math.Round(20 * _scale));
            _targetBounds = new Rectangle(loc, size);
            Bounds = _targetBounds;
        }

        /// <summary>The caption font: the chosen family in a style it has, or the default when it isn't installed.</summary>
        internal static Font CaptionFont(string family, float px)
        {
            try
            {
                using (var f = new FontFamily(family))
                    foreach (var style in new[] { FontStyle.Regular, FontStyle.Bold, FontStyle.Italic, FontStyle.Bold | FontStyle.Italic })
                        if (f.IsStyleAvailable(style)) return new Font(f.Name, px, style, GraphicsUnit.Pixel);
            }
            catch (ArgumentException) { /* not installed */ }
            return new Font(PopupText.DefaultFont, px, GraphicsUnit.Pixel);
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
                    if ((int)m.WParam == Native.MCI_NOTIFY_SUCCESSFUL && Repeat && !IsDisposed)
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
                _soundStarted = Audio.Play(_soundPath, _volume, Repeat ? Handle : IntPtr.Zero);
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

        private const TextFormatFlags CaptionFlags =
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.Left;

        private Size ComputeLayout(int logicalWidth)
        {
            int w = (int)Math.Round(logicalWidth * _scale);
            int pad = (int)Math.Round(6 * _scale);
            bool hasImage = _image != null && _image.Width > 0 && _image.Height > 0;

            // Caption: kanji, then the text, wrapped onto more lines when it doesn't fit (big fonts, narrow popups).
            int gap = (int)Math.Round(8 * _scale);
            var kSize = TextRenderer.MeasureText(_kanji, _kanjiFont, Size.Empty, TextFormatFlags.NoPadding);
            int hintW = _kind == PopupKind.Placement ? TextRenderer.MeasureText(PlacementHint, _hintFont).Width + gap : 0;
            int textX = pad + gap / 2 + kSize.Width + gap;
            int textW = Math.Max(1, w - pad - textX - hintW);
            var tSize = TextRenderer.MeasureText(_caption, _captionFont, new Size(textW, int.MaxValue), CaptionFlags);
            int capH = Math.Max((int)Math.Round((hasImage ? 36 : 52) * _scale),
                                Math.Max(tSize.Height, kSize.Height) + 2 * pad);
            int capTop;
            Size size;

            if (hasImage)
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
                capTop = pad + ih;
                size = new Size(w, pad + ih + capH);
            }
            else
            {
                _imageRect = Rectangle.Empty;
                capTop = pad;
                size = new Size(w, 2 * pad + capH);
            }

            _captionRect = new Rectangle(pad, capTop, w - 2 * pad, capH);
            _kanjiRect = new Rectangle(pad + gap / 2, capTop, kSize.Width, capH);
            int textH = Math.Min(tSize.Height, capH);
            _textRect = new Rectangle(textX, capTop + (capH - textH) / 2, textW, textH);
            return size;
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

            // 立 / 座 / 休 in the accent colour, then the caption.
            var r = _captionRect;
            TextRenderer.DrawText(g, _kanji, _kanjiFont, _kanjiRect, Theme.Accent,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, _caption, _captionFont, _textRect, Theme.Text, CaptionFlags);

            if (_kind == PopupKind.Placement)
            {
                string hint = PlacementHint;
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
            if (MenuItems == null || MenuItems.Length == 0) return;
            var items = new (string, Action)[MenuItems.Length];
            for (int i = 0; i < items.Length; i++)
            {
                var (text, action) = MenuItems[i];
                items[i] = (text, () => { action?.Invoke(); if (!IsDisposed) Close(); });
            }
            ShowMenu(p, items);
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
