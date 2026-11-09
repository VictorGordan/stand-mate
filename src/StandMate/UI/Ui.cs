using System;
using System.Drawing;
using System.Windows.Forms;

namespace StandMate.UI
{
    /// <summary>
    /// Explicit DPI scaling for one window: every size goes through <see cref="S(int)"/> and fonts are
    /// pixel-sized for the window's monitor. This gives crisp, correctly sized UI on mixed-DPI
    /// multi-monitor setups (WinForms' own auto-scaling on .NET Framework is unreliable there).
    /// </summary>
    internal sealed class Ui : IDisposable
    {
        public readonly float Scale;
        public readonly Font Text, Semibold, Small, Heading, Brand;

        public Ui(float scale)
        {
            Scale = scale <= 0 ? 1f : scale;
            Text = Px("Segoe UI", 12f);           // ≈ 9 pt
            Semibold = Px("Segoe UI Semibold", 12f);
            Small = Px("Segoe UI", 11f);
            Heading = Px("Segoe UI Semibold", 17f);
            Brand = Px("Segoe UI Semibold", 18f);
        }

        public static Ui ForScreen(Screen s) => new Ui(Screens.Scale(s));

        private Font Px(string family, float px) => new Font(family, px * Scale, FontStyle.Regular, GraphicsUnit.Pixel);

        public int S(int v) => (int)Math.Round(v * Scale);
        public Size S(int w, int h) => new Size(S(w), S(h));
        public Padding P(int l, int t, int r, int b) => new Padding(S(l), S(t), S(r), S(b));

        public void Dispose()
        {
            Text.Dispose(); Semibold.Dispose(); Small.Dispose(); Heading.Dispose(); Brand.Dispose();
        }

        // ---------------- control factories ----------------

        public Label Label(string text, bool muted = false, Font font = null)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                ForeColor = muted ? Theme.Muted : Theme.Text,
                Font = font ?? Text,
                Margin = P(0, 6, 6, 0),
            };
        }

        /// <summary>A fixed-width label column so rows line up.</summary>
        public Label Field(string text, int width)
        {
            return new Label
            {
                Text = text,
                AutoSize = false,
                Size = S(width, 28),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Theme.Muted,
                Font = Text,
                Margin = new Padding(0),
            };
        }

        /// <summary>A multi-line muted paragraph.</summary>
        public Label Paragraph(string text, int width, int bottom = 6, Font font = null)
        {
            var l = Label(text, muted: true, font: font);
            l.MaximumSize = new Size(S(width), 0);
            l.Margin = P(0, 0, 0, bottom);
            return l;
        }

        public Button Button(string text, EventHandler onClick, bool accent = false)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlatStyle = FlatStyle.Flat,
                BackColor = accent ? Theme.Accent : Theme.Control,
                ForeColor = accent ? Theme.Bg : Theme.Text,
                Font = accent ? Semibold : Text,
                Padding = P(8, 2, 8, 2),
                Margin = P(0, 0, 6, 0),
                MinimumSize = new Size(0, S(28)),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false,
            };
            b.FlatAppearance.BorderColor = accent ? Theme.Accent : Theme.Border;
            b.FlatAppearance.MouseOverBackColor = accent ? Theme.AccentHover : Theme.ControlHover;
            b.FlatAppearance.MouseDownBackColor = accent ? Theme.AccentHover : Theme.Border;
            if (onClick != null) b.Click += onClick;
            return b;
        }

        public CheckBox Check(string text, bool value, Action<bool> onChange)
        {
            var c = new DarkCheckBox(this)
            {
                Text = text,
                Checked = value,
                AutoSize = true,
                ForeColor = Theme.Text,
                Font = Text,
                Margin = P(0, 3, 0, 3),
                Cursor = Cursors.Hand,
            };
            c.CheckedChanged += (s, e) => onChange?.Invoke(c.Checked);
            return c;
        }

        public TextBox TextBox(string text, int width)
        {
            return new TextBox
            {
                Text = text ?? "",
                Width = S(width),
                BackColor = Theme.Control,
                ForeColor = Theme.Text,
                Font = Text,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = P(0, 3, 6, 2),
            };
        }

        /// <summary>Owner-drawn dark drop-down (no bright Windows selection highlight).</summary>
        public ComboBox Combo(int width)
        {
            var c = new DarkComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                DrawMode = DrawMode.OwnerDrawFixed,
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Control,
                ForeColor = Theme.Text,
                Font = Text,
                Width = S(width),
                ItemHeight = S(20),
                Margin = P(0, 2, 6, 2),
            };
            c.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                bool editPart = (e.State & DrawItemState.ComboBoxEdit) != 0;
                bool hot = !editPart && (e.State & DrawItemState.Selected) != 0;
                using (var bg = new SolidBrush(hot ? Theme.ControlHover : Theme.Control))
                    e.Graphics.FillRectangle(bg, e.Bounds);
                var r = new Rectangle(e.Bounds.X + S(4), e.Bounds.Y, e.Bounds.Width - S(4), e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, c.Items[e.Index].ToString(), Text, r, Theme.Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            return c;
        }

        public NumberBox Number(int min, int max, int value, int step, string suffix, Action<int> onChange)
        {
            var n = new NumberBox(this, min, max, value, step, suffix);
            if (onChange != null) n.ValueChanged += onChange;
            return n;
        }

        public FlowLayoutPanel Row(params Control[] controls)
        {
            var row = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = P(0, 0, 0, 8),
                BackColor = Color.Transparent,
            };
            row.Controls.AddRange(controls);
            return row;
        }

        public FlowLayoutPanel Column()
        {
            return new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0),
                BackColor = Color.Transparent,
            };
        }
    }

    /// <summary>Check box drawn in the app's style: rounded box, accent fill + dark tick when checked.</summary>
    internal sealed class DarkCheckBox : CheckBox
    {
        private readonly Ui _ui;
        private bool _hover;

        public DarkCheckBox(Ui ui)
        {
            _ui = ui;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var t = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPrefix);
            int box = _ui.S(16);
            return new Size(box + _ui.S(10) + t.Width + _ui.S(2), Math.Max(box, t.Height) + _ui.S(6));
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);
            int box = _ui.S(16);
            var r = new RectangleF(0.5f, (Height - box) / 2f, box - 1, box - 1);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = Theme.RoundedRect(r, _ui.S(3)))
            {
                if (Checked)
                {
                    using (var b = new SolidBrush(_hover ? Theme.AccentHover : Theme.Accent)) g.FillPath(b, path);
                    using (var pen = new Pen(Theme.Bg, Math.Max(1.5f, _ui.Scale * 2f)))
                    {
                        pen.StartCap = pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                        g.DrawLines(pen, new[]
                        {
                            new PointF(r.X + r.Width * 0.24f, r.Y + r.Height * 0.52f),
                            new PointF(r.X + r.Width * 0.43f, r.Y + r.Height * 0.71f),
                            new PointF(r.X + r.Width * 0.77f, r.Y + r.Height * 0.31f),
                        });
                    }
                }
                else
                {
                    using (var b = new SolidBrush(Theme.Control)) g.FillPath(b, path);
                    using (var pen = new Pen(_hover ? Theme.Muted : Theme.Border)) g.DrawPath(pen, path);
                }
            }
            var textRect = new Rectangle(box + _ui.S(10), 0, Width - box - _ui.S(10), Height);
            TextRenderer.DrawText(g, Text, Font, textRect, Enabled ? Theme.Text : Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>ComboBox whose drop-down button and border are repainted dark (WinForms' flat style draws them light).</summary>
    internal sealed class DarkComboBox : ComboBox
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct COMBOBOXINFO
        {
            public int cbSize;
            public Platform.Native.RECT rcItem;
            public Platform.Native.RECT rcButton;
            public int stateButton;
            public IntPtr hwndCombo, hwndItem, hwndList;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetComboBoxInfo(IntPtr hwnd, ref COMBOBOXINFO info);

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x000F) PaintChrome(); // WM_PAINT
        }

        protected override void OnDropDownClosed(EventArgs e)
        {
            base.OnDropDownClosed(e);
            Invalidate();
        }

        private void PaintChrome()
        {
            if (!IsHandleCreated) return;
            var info = new COMBOBOXINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(COMBOBOXINFO)) };
            if (!GetComboBoxInfo(Handle, ref info)) return;
            int left = info.rcButton.Left;
            using (var g = Graphics.FromHwnd(Handle))
            {
                using (var bg = new SolidBrush(Theme.Control))
                    g.FillRectangle(bg, Rectangle.FromLTRB(left - 3, 0, Width, Height));

                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                float cx = left + (Width - left) / 2f - 1, cy = Height / 2f, s = Math.Max(3f, Height / 9f);
                using (var pen = new Pen(Theme.Muted, Math.Max(1.5f, Height / 22f)))
                    g.DrawLines(pen, new[] { new PointF(cx - s, cy - s / 2), new PointF(cx, cy + s / 2), new PointF(cx + s, cy - s / 2) });
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;

                using (var pen = new Pen(DroppedDown || Focused ? Theme.Muted : Theme.Border))
                    g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
        }
    }

    /// <summary>Compact dark number input: [−] value [+], mouse wheel, arrow keys and typing supported.</summary>
    internal sealed class NumberBox : UserControl
    {
        private readonly TextBox _text;
        private readonly Button _minus, _plus;
        private int _value;
        private bool _silent;

        public int Minimum { get; }
        public int Maximum { get; }
        public int Step { get; }
        public string Suffix { get; }
        public event Action<int> ValueChanged;

        public NumberBox(Ui ui, int min, int max, int value, int step = 1, string suffix = "")
        {
            Minimum = min; Maximum = max; Step = step; Suffix = suffix ?? "";
            BackColor = Theme.Control;
            Margin = ui.P(0, 2, 6, 2);
            Size = ui.S(Suffix.Length > 0 ? 112 : 88, 26);

            _minus = MakeButton(ui, "−", () => Value -= Step);
            _plus = MakeButton(ui, "+", () => Value += Step);
            _text = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Control,
                ForeColor = Theme.Text,
                TextAlign = HorizontalAlignment.Center,
                Font = ui.Text,
            };
            _text.Leave += (s, e) => Commit();
            _text.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { Commit(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Up) { Value += Step; e.Handled = true; }
                else if (e.KeyCode == Keys.Down) { Value -= Step; e.Handled = true; }
            };
            _text.MouseWheel += (s, e) => Value += e.Delta > 0 ? Step : -Step;
            Controls.Add(_text);
            Controls.Add(_minus);
            Controls.Add(_plus);

            _value = Clamp(value);
            ShowValue();
        }

        public int Value
        {
            get => _value;
            set
            {
                int v = Clamp(value);
                if (v == _value) { ShowValue(); return; }
                _value = v;
                ShowValue();
                if (!_silent) ValueChanged?.Invoke(_value);
            }
        }

        /// <summary>Sets the value without raising ValueChanged.</summary>
        public void SetSilently(int v)
        {
            _silent = true;
            try { Value = v; } finally { _silent = false; }
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_text == null) return;
            int h = ClientSize.Height, bw = h;
            _minus.SetBounds(0, 0, bw, h);
            _plus.SetBounds(ClientSize.Width - bw, 0, bw, h);
            int th = _text.PreferredHeight;
            _text.SetBounds(bw + 2, Math.Max(0, (h - th) / 2 + 1), Math.Max(1, ClientSize.Width - 2 * bw - 4), th);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Value += e.Delta > 0 ? Step : -Step;
        }

        private Button MakeButton(Ui ui, string text, Action onClick)
        {
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Control,
                ForeColor = Theme.Muted,
                TabStop = false,
                Cursor = Cursors.Hand,
                Font = ui.Text,
                Padding = new Padding(0),
                TextAlign = ContentAlignment.MiddleCenter,
                UseVisualStyleBackColor = false,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Theme.ControlHover;
            b.FlatAppearance.MouseDownBackColor = Theme.Border;
            b.Click += (s, e) => { Commit(); onClick(); };
            return b;
        }

        private void Commit()
        {
            string digits = new string(Array.FindAll(_text.Text.ToCharArray(), char.IsDigit));
            if (digits.Length > 0 && int.TryParse(digits, out int v)) Value = v;
            else ShowValue();
        }

        private void ShowValue() => _text.Text = _value + Suffix;

        private int Clamp(int v) => v < Minimum ? Minimum : v > Maximum ? Maximum : v;
    }
}
