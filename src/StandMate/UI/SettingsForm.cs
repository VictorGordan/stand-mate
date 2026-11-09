using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using StandMate.Core;
using StandMate.Platform;

namespace StandMate.UI
{
    /// <summary>
    /// The one settings window. Built entirely in code, scaled explicitly for its monitor's DPI
    /// (rebuilt when dragged to a monitor with a different DPI), and disposed when closed so the
    /// tray app stays light.
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        private const int FieldWidth = 92;
        private const int ContentWidth = 470;
        private static readonly string[] Pages = { "Schedule", "Mates", "Popup", "General" };

        private readonly IAppHost _host;
        private readonly Timer _statusTimer;
        private readonly List<Button> _nav = new List<Button>();
        private Ui _ui;
        private Panel _sidebar, _content;
        private Label _status;
        private Label _updateLabel;
        private string _page = Pages[0];

        public SettingsForm(IAppHost host, string page = null)
        {
            _host = host;
            if (page != null && Array.IndexOf(Pages, page) >= 0) _page = page;
            _host.Updater.Changed += OnUpdateChanged;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Text = "StandMate";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.Manual;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            // Open centred on the monitor under the mouse, sized for that monitor's DPI.
            var screen = Screen.FromPoint(Cursor.Position);
            _ui = Ui.ForScreen(screen);
            Font = _ui.Text;
            ClientSize = _ui.S(720, 580);
            var wa = screen.WorkingArea;
            Location = new Point(wa.Left + Math.Max(0, (wa.Width - Width) / 2), wa.Top + Math.Max(0, (wa.Height - Height) / 2));

            _statusTimer = new Timer { Interval = 1000 };
            _statusTimer.Tick += (s, e) => { if (_status != null) _status.Text = _host.StatusText(); };
            _statusTimer.Start();

            Build();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(Handle);
        }

        /// <summary>Moved to a monitor with another DPI: rebuild everything at the new scale.</summary>
        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            e.Cancel = true; // we scale ourselves
            var old = _ui;
            _ui = new Ui(e.DeviceDpiNew / 96f);
            Font = _ui.Text;
            var loc = e.SuggestedRectangle.Location;
            ClientSize = _ui.S(720, 580);
            Location = loc;
            Build();
            old.Dispose();
        }

        private void Build()
        {
            SuspendLayout();
            foreach (Control c in Controls.Cast<Control>().ToList()) c.Dispose();
            _nav.Clear();

            _content = new Panel { Dock = DockStyle.Fill, Padding = _ui.P(28, 20, 24, 16), BackColor = Theme.Bg, AutoScroll = true };
            _sidebar = new Panel { Dock = DockStyle.Left, Width = _ui.S(176), BackColor = Theme.Surface };
            Controls.Add(_content);
            Controls.Add(_sidebar);
            BuildSidebar();
            ShowPage(_page);
            ResumeLayout(true);
        }

        /// <summary>Rebuilds the current page (e.g. after the mode or mate was changed from the tray menu).</summary>
        public void RefreshPage() => ShowPage(_page);

        public void ShowPage(string page)
        {
            if (Array.IndexOf(Pages, page) < 0) page = Pages[0];
            _page = page;
            foreach (var b in _nav) StyleNav(b, (string)b.Tag == page);

            _content.SuspendLayout();
            foreach (Control c in _content.Controls.Cast<Control>().ToList()) c.Dispose();
            Control body;
            switch (page)
            {
                case "Mates": body = BuildMates(); break;
                case "Popup": body = BuildPopup(); break;
                case "General": body = BuildGeneral(); break;
                default: body = BuildSchedule(); break;
            }
            _content.Controls.Add(body);
            _content.ResumeLayout(true);
        }

        // ---------------- sidebar ----------------

        private void BuildSidebar()
        {
            _sidebar.Controls.Add(new Label
            {
                Text = "StandMate", Font = _ui.Brand, ForeColor = Theme.Text, AutoSize = true, Location = new Point(_ui.S(18), _ui.S(20)),
            });
            _sidebar.Controls.Add(new Label
            {
                Text = "立つ ・ 座る", Font = _ui.Text, ForeColor = Theme.Accent, AutoSize = true, Location = new Point(_ui.S(20), _ui.S(50)),
            });

            int y = _ui.S(92);
            foreach (var p in Pages)
            {
                var page = p;
                var b = new Button
                {
                    Text = page,
                    Tag = page,
                    FlatStyle = FlatStyle.Flat,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Padding = _ui.P(16, 0, 0, 0),
                    Location = new Point(0, y),
                    Size = _ui.S(176, 36),
                    Cursor = Cursors.Hand,
                    UseVisualStyleBackColor = false,
                    TabStop = false,
                };
                b.FlatAppearance.BorderSize = 0;
                b.FlatAppearance.MouseOverBackColor = Theme.ControlHover;
                b.FlatAppearance.MouseDownBackColor = Theme.Control;
                b.Click += (s, e) => ShowPage(page);
                b.Paint += (s, e) =>
                {
                    if ((string)b.Tag != _page) return;
                    using (var br = new SolidBrush(Theme.Accent))
                        e.Graphics.FillRectangle(br, 0, b.Height / 4, Math.Max(3, _ui.S(3)), b.Height / 2);
                };
                _nav.Add(b);
                _sidebar.Controls.Add(b);
                y += _ui.S(38);
            }

            _status = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Bottom,
                Height = _ui.S(64),
                Padding = _ui.P(20, 0, 12, 16),
                TextAlign = ContentAlignment.BottomLeft,
                ForeColor = Theme.Muted,
                Font = _ui.Small,
                Text = _host.StatusText(),
            };
            _sidebar.Controls.Add(_status);
        }

        private void StyleNav(Button b, bool selected)
        {
            b.BackColor = selected ? Theme.Control : Theme.Surface;
            b.ForeColor = selected ? Theme.Text : Theme.Muted;
            b.Font = selected ? _ui.Semibold : _ui.Text;
            b.Invalidate();
        }

        // ---------------- pages ----------------

        private FlowLayoutPanel Page(string title, string subtitle)
        {
            var col = _ui.Column();
            col.Controls.Add(new Label { Text = title, Font = _ui.Heading, ForeColor = Theme.Text, AutoSize = true, Margin = _ui.P(0, 0, 0, 2) });
            if (!string.IsNullOrEmpty(subtitle)) col.Controls.Add(_ui.Paragraph(subtitle, ContentWidth, 18));
            else col.Controls.Add(new Label { AutoSize = false, Height = _ui.S(12), Margin = new Padding(0) });
            return col;
        }

        private Label Field(string text) => _ui.Field(text, FieldWidth);

        private Control Indent(Control c)
        {
            c.Margin = new Padding(_ui.S(FieldWidth), 0, 0, _ui.S(6));
            return c;
        }

        private void ScheduleChanged() => _host.SettingsChanged(true);

        private Control BuildSchedule()
        {
            var s = _host.Settings;
            var col = Page("Schedule", "How StandMate cycles you between sitting and standing.");

            var combo = _ui.Combo(220);
            foreach (var m in ModeInfo.All) combo.Items.Add(m.Name);
            combo.SelectedIndex = Math.Max(0, Array.FindIndex(ModeInfo.All, m => m.Id == s.Mode));
            var desc = _ui.Label(ModeInfo.Get(s.Mode).Description, muted: true);
            var detail = _ui.Column();
            detail.Margin = _ui.P(0, 12, 0, 6);

            col.Controls.Add(_ui.Row(Field("Mode"), combo));
            col.Controls.Add(Indent(desc));
            col.Controls.Add(detail);
            BuildStartWith(col);
            BuildAway(col);
            col.Controls.Add(_ui.Paragraph("Changes apply right away and restart the current phase.", ContentWidth, 0, _ui.Small));

            BuildModeDetail(detail, s.Mode);
            combo.SelectedIndexChanged += (o, e) =>
            {
                s.Mode = ModeInfo.All[combo.SelectedIndex].Id;
                desc.Text = ModeInfo.All[combo.SelectedIndex].Description;
                ScheduleChanged();
                detail.SuspendLayout();
                foreach (Control c in detail.Controls.Cast<Control>().ToList()) c.Dispose();
                BuildModeDetail(detail, s.Mode);
                detail.ResumeLayout(true);
            };
            return col;
        }

        private void BuildModeDetail(FlowLayoutPanel p, string mode)
        {
            var s = _host.Settings;
            switch (mode)
            {
                case ModeIds.Sequence:
                {
                    var tb = _ui.TextBox(s.Sequence, 360);
                    var msg = _ui.Label("", muted: true);
                    void Validate(bool apply)
                    {
                        if (SequenceParser.TryParse(tb.Text, out var steps, out var err))
                        {
                            msg.ForeColor = Theme.Muted;
                            msg.Text = $"{steps.Count} steps · {steps.Sum(x => x.Minutes)} min per loop";
                            if (apply) { s.Sequence = tb.Text.Trim(); ScheduleChanged(); }
                        }
                        else
                        {
                            msg.ForeColor = Theme.Error;
                            msg.Text = err;
                        }
                    }
                    tb.TextChanged += (o, e) => Validate(true);
                    Validate(false);
                    p.Controls.Add(_ui.Row(Field("Steps"), tb));
                    p.Controls.Add(Indent(msg));
                    p.Controls.Add(Indent(_ui.Label("Example: sit 30, stand 10, sit 50, stand 10. It loops forever.", muted: true)));
                    break;
                }
                case ModeIds.Clock:
                {
                    var hint = _ui.Label("", muted: true);
                    void Hint() => hint.Text = $"Stand at :{s.ClockStandAt:00}, sit again at :{(s.ClockStandAt + s.ClockStandFor) % 60:00}, every hour.";
                    var at = _ui.Number(0, 59, s.ClockStandAt, 5, "", v => { s.ClockStandAt = v; Hint(); ScheduleChanged(); });
                    var dur = _ui.Number(1, 55, s.ClockStandFor, 5, " min", v => { s.ClockStandFor = v; Hint(); ScheduleChanged(); });
                    p.Controls.Add(_ui.Row(Field("Stand at"), at, _ui.Label("minutes past the hour", muted: true)));
                    p.Controls.Add(_ui.Row(Field("Stand for"), dur));
                    Hint();
                    p.Controls.Add(Indent(hint));
                    break;
                }
                case ModeIds.Progressive:
                {
                    NumberBox start = null, max = null;
                    var sit = _ui.Number(1, 600, s.ProgSit, 5, " min", v => { s.ProgSit = v; ScheduleChanged(); });
                    start = _ui.Number(1, 600, s.ProgStandStart, 1, " min", v =>
                    {
                        s.ProgStandStart = v;
                        if (s.ProgStandMax < v) { s.ProgStandMax = v; max.SetSilently(v); }
                        ScheduleChanged();
                    });
                    var step = _ui.Number(0, 120, s.ProgStandStep, 1, " min", v => { s.ProgStandStep = v; ScheduleChanged(); });
                    max = _ui.Number(1, 600, s.ProgStandMax, 5, " min", v =>
                    {
                        s.ProgStandMax = v;
                        if (s.ProgStandStart > v) { s.ProgStandStart = v; start.SetSilently(v); }
                        ScheduleChanged();
                    });
                    p.Controls.Add(_ui.Row(Field("Sit"), sit));
                    p.Controls.Add(_ui.Row(Field("Stand from"), start, _ui.Label("up to", muted: true), max));
                    p.Controls.Add(_ui.Row(Field("Grow by"), step, _ui.Label("each cycle", muted: true)));
                    p.Controls.Add(Indent(_ui.Label("Standing time starts over every day.", muted: true)));
                    break;
                }
                case ModeIds.Random:
                {
                    p.Controls.Add(RangeRow("Sit", s.RandSitMin, s.RandSitMax, (a, b) => { s.RandSitMin = a; s.RandSitMax = b; }));
                    p.Controls.Add(RangeRow("Stand", s.RandStandMin, s.RandStandMax, (a, b) => { s.RandStandMin = a; s.RandStandMax = b; }));
                    p.Controls.Add(Indent(_ui.Label("Each phase picks a random length in its range.", muted: true)));
                    break;
                }
                default:
                {
                    var sit = _ui.Number(1, 600, s.FixedSit, 5, " min", v => { s.FixedSit = v; ScheduleChanged(); });
                    var stand = _ui.Number(1, 600, s.FixedStand, 5, " min", v => { s.FixedStand = v; ScheduleChanged(); });
                    p.Controls.Add(_ui.Row(Field("Sit"), sit));
                    p.Controls.Add(_ui.Row(Field("Stand"), stand));
                    var presets = _ui.Row(Field("Presets"));
                    foreach (var (a, b) in new[] { (45, 15), (30, 30), (50, 10), (20, 10) })
                    {
                        presets.Controls.Add(_ui.Button($"{a} / {b}", (o, e) =>
                        {
                            s.FixedSit = a; s.FixedStand = b;
                            sit.SetSilently(a); stand.SetSilently(b);
                            ScheduleChanged();
                        }));
                    }
                    p.Controls.Add(presets);
                    break;
                }
            }
        }

        /// <summary>"Start with: [Sitting] [Standing]", used at startup and after sleep/hibernate.</summary>
        private void BuildStartWith(FlowLayoutPanel col)
        {
            var s = _host.Settings;
            Button sit = null, stand = null;
            void Sync()
            {
                foreach (var (b, on) in new[] { (sit, s.StartPhase == Phase.Sit), (stand, s.StartPhase == Phase.Stand) })
                {
                    b.BackColor = on ? Theme.Accent : Theme.Control;
                    b.ForeColor = on ? Theme.Bg : Theme.Text;
                    b.Font = on ? _ui.Semibold : _ui.Text;
                    b.FlatAppearance.BorderColor = on ? Theme.Accent : Theme.Border;
                    b.FlatAppearance.MouseOverBackColor = on ? Theme.AccentHover : Theme.ControlHover;
                }
            }
            void Pick(string value)
            {
                if (s.StartWith == value) return;
                s.StartWith = value;
                Sync();
                _host.SettingsChanged(false); // takes effect at the next startup / wake
            }
            sit = _ui.Button("座  Sitting", (o, e) => Pick("sit"));
            stand = _ui.Button("立  Standing", (o, e) => Pick("stand"));
            sit.Margin = _ui.P(0, 0, 0, 0);
            Sync();
            col.Controls.Add(_ui.Row(Field("Start with"), sit, stand));
            col.Controls.Add(Indent(_ui.Paragraph(
                "Each time Windows starts or wakes from sleep/hibernation, a fresh cycle begins with this. " +
                "On the clock follows the clock instead.", ContentWidth - FieldWidth, 10, _ui.Small)));
        }

        /// <summary>The "Away" section: pause on inactivity, after how long, and rewind vs. plain pause.</summary>
        private void BuildAway(FlowLayoutPanel col)
        {
            var s = _host.Settings;
            var minutes = _ui.Number(1, 120, s.AwayMinutes, 1, " min", v => { s.AwayMinutes = v; _host.SettingsChanged(false); });
            var how = _ui.Combo(330);
            how.Items.Add("Rewind to my last input, then pause");
            how.Items.Add("Just pause (the idle minutes count)");
            how.SelectedIndex = s.AwayRewind ? 0 : 1;
            how.SelectedIndexChanged += (o, e) => { s.AwayRewind = how.SelectedIndex == 0; _host.SettingsChanged(false); };

            var afterRow = _ui.Row(_ui.Label("after", muted: true), minutes, _ui.Label("with no mouse or keyboard input", muted: true));
            var howRow = _ui.Row(how);
            afterRow.Margin = new Padding(_ui.S(FieldWidth + 26), 0, 0, _ui.S(4));
            howRow.Margin = new Padding(_ui.S(FieldWidth + 26), 0, 0, _ui.S(8));
            afterRow.Visible = howRow.Visible = s.PauseWhenAway;

            var check = _ui.Check("Pause the countdown when I'm away", s.PauseWhenAway, v =>
            {
                s.PauseWhenAway = v;
                afterRow.Visible = howRow.Visible = v;
                _host.SettingsChanged(false);
            });
            col.Controls.Add(_ui.Row(Field("Away"), check));
            col.Controls.Add(afterRow);
            col.Controls.Add(howRow);
        }

        private Control RangeRow(string label, int min, int max, Action<int, int> set)
        {
            NumberBox lo = null, hi = null;
            lo = _ui.Number(1, 600, min, 5, " min", v =>
            {
                if (hi.Value < v) hi.SetSilently(v);
                set(v, hi.Value); ScheduleChanged();
            });
            hi = _ui.Number(1, 600, max, 5, " min", v =>
            {
                if (lo.Value > v) lo.SetSilently(v);
                set(lo.Value, v); ScheduleChanged();
            });
            return _ui.Row(Field(label), lo, _ui.Label("to", muted: true), hi);
        }

        private Control BuildMates()
        {
            var col = Page("Mates", "A mate is a GIF + sound for standing up, and another pair for sitting down.");

            var list = new ListBox
            {
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = _ui.S(32),
                Size = _ui.S(300, 222),
                BackColor = Theme.Surface,
                ForeColor = Theme.Text,
                Font = _ui.Text,
                BorderStyle = BorderStyle.None,
                IntegralHeight = false,
                Margin = _ui.P(0, 0, 14, 0),
            };
            list.DrawItem += (o, e) => DrawMateItem(list, e);

            void Fill(string selectId = null)
            {
                list.BeginUpdate();
                list.Items.Clear();
                foreach (var m in _host.Mates) list.Items.Add(m);
                string want = selectId ?? _host.ActiveMate?.Id;
                for (int i = 0; i < list.Items.Count; i++)
                    if (((Mate)list.Items[i]).Id == want) list.SelectedIndex = i;
                if (list.SelectedIndex < 0 && list.Items.Count > 0) list.SelectedIndex = 0;
                list.EndUpdate();
            }
            Mate Selected() => list.SelectedItem as Mate;
            void Use()
            {
                if (Selected() == null) return;
                _host.SetActiveMate(Selected().Id);
                list.Invalidate();
            }

            var buttons = _ui.Column();
            Button Wide(string text, EventHandler click, bool accent = false)
            {
                var b = _ui.Button(text, click, accent);
                b.AutoSize = false;
                b.Size = _ui.S(128, 30);
                b.Margin = _ui.P(0, 0, 0, 6);
                return b;
            }

            buttons.Controls.Add(Wide("Use this mate", (o, e) => Use(), accent: true));
            buttons.Controls.Add(Wide("New…", (o, e) =>
            {
                var m = MateStore.Create("New mate");
                if (EditMate(m, isNew: true)) { _host.ReloadMates(); Fill(m.Id); }
            }));
            buttons.Controls.Add(Wide("Edit…", (o, e) =>
            {
                if (Selected() == null) return;
                var m = MateStore.Load(Selected().Folder);
                if (m != null && EditMate(m, isNew: false)) { _host.ReloadMates(); Fill(m.Id); }
            }));
            buttons.Controls.Add(Wide("Duplicate", (o, e) =>
            {
                if (Selected() == null) return;
                try
                {
                    var copy = MateStore.Duplicate(Selected());
                    _host.ReloadMates();
                    Fill(copy?.Id);
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "StandMate"); }
            }));
            buttons.Controls.Add(Wide("Delete", (o, e) =>
            {
                var m = Selected();
                if (m == null) return;
                if (_host.Mates.Count <= 1)
                {
                    MessageBox.Show(this, "Keep at least one mate.", "StandMate", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (MessageBox.Show(this, $"Delete \"{m.Name}\" and its files?", "StandMate",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                try { MateStore.Delete(m); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "StandMate"); }
                _host.ReloadMates();
                Fill();
            }));
            // Picks up changes made by hand in the mates folder (new folders, edited mate.json, replaced files).
            buttons.Controls.Add(Wide("Refresh", (o, e) =>
            {
                string keep = Selected()?.Id;
                _host.ReloadMates();
                Fill(keep);
                list.Invalidate();
            }));
            list.DoubleClick += (o, e) => Use();

            col.Controls.Add(_ui.Row(list, buttons));
            col.Controls.Add(_ui.Row(
                Field("Preview"),
                _ui.Button("立  Stand", (o, e) => { if (Selected() != null) _host.Preview(Selected(), Phase.Stand); }),
                _ui.Button("座  Sit", (o, e) => { if (Selected() != null) _host.Preview(Selected(), Phase.Sit); }),
                _ui.Button("Open mates folder", (o, e) => OpenFolder(MateStore.Root))));
            col.Controls.Add(_ui.Paragraph(
                "Any GIF (or PNG/JPG) works, with a WAV or MP3 sound. Files are copied into the mate's folder, " +
                "so you can move or delete the originals.", ContentWidth, 0, _ui.Small));

            Fill();
            return col;
        }

        private void DrawMateItem(ListBox list, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var m = (Mate)list.Items[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            bool active = _host.ActiveMate?.Id == m.Id;
            var g = e.Graphics;
            using (var bg = new SolidBrush(selected ? Theme.Control : Theme.Surface))
                g.FillRectangle(bg, e.Bounds);

            int pad = _ui.S(12), dot = _ui.S(6);
            if (active)
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(Theme.Accent))
                    g.FillEllipse(b, e.Bounds.Left + pad, e.Bounds.Top + (e.Bounds.Height - dot) / 2, dot, dot);
            }
            var textRect = new Rectangle(e.Bounds.Left + pad * 2, e.Bounds.Top, e.Bounds.Width - pad * 3, e.Bounds.Height);
            TextRenderer.DrawText(g, m.Name, active ? _ui.Semibold : _ui.Text, textRect, Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (active)
                TextRenderer.DrawText(g, "in use", _ui.Small, textRect, Theme.Muted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        }

        private bool EditMate(Mate m, bool isNew)
        {
            using (var ed = new MateEditorForm(m, isNew, _host, _ui.Scale))
                return ed.ShowDialog(this) == DialogResult.OK;
        }

        private Control BuildPopup()
        {
            var s = _host.Settings;
            var col = Page("Popup", "Where the reminder appears, how big it is and how it sounds.");

            // Screen
            var screens = Screen.AllScreens;
            var screenCombo = _ui.Combo(250);
            for (int i = 0; i < screens.Length; i++) screenCombo.Items.Add(Screens.Describe(screens[i], i));
            bool syncing = false;
            void SyncScreen()
            {
                syncing = true;
                screenCombo.SelectedIndex = Math.Max(0, Screens.IndexOf(Screens.Find(s.ScreenDevice)));
                syncing = false;
            }
            SyncScreen();
            screenCombo.SelectedIndexChanged += (o, e) =>
            {
                if (syncing) return;
                s.ScreenDevice = screens[screenCombo.SelectedIndex].DeviceName;
                _host.SettingsChanged(false);
            };
            col.Controls.Add(_ui.Row(Field("Screen"), screenCombo, _ui.Button("Identify", (o, e) => IdentifyOverlay.ShowAll())));

            // Position: 3x3 grid + drag-to-place
            var grid = new TableLayoutPanel { ColumnCount = 3, RowCount = 3, AutoSize = true, Margin = _ui.P(0, 2, 16, 0), BackColor = Color.Transparent };
            var cells = new List<Button>();
            var posLabel = _ui.Label("", muted: true);
            posLabel.Margin = _ui.P(0, 2, 0, 0);
            void SyncPosition()
            {
                foreach (var c in cells)
                {
                    bool on = (string)c.Tag == s.Anchor;
                    c.BackColor = on ? Theme.Accent : Theme.Control;
                    c.FlatAppearance.BorderColor = on ? Theme.Accent : Theme.Border;
                }
                posLabel.Text = s.Anchor == Anchors.Custom ? "Custom (dragged)" : Pretty(s.Anchor);
            }
            foreach (var a in Anchors.Grid)
            {
                var anchor = a;
                var cell = new Button
                {
                    Tag = anchor,
                    FlatStyle = FlatStyle.Flat,
                    Size = _ui.S(30, 20),
                    Margin = _ui.P(2, 2, 2, 2),
                    Cursor = Cursors.Hand,
                    UseVisualStyleBackColor = false,
                    TabStop = false,
                };
                cell.FlatAppearance.MouseOverBackColor = Theme.AccentHover;
                cell.Click += (o, e) => { s.Anchor = anchor; SyncPosition(); _host.SettingsChanged(false); };
                cells.Add(cell);
                grid.Controls.Add(cell);
            }
            var place = _ui.Button("Place by dragging…", (o, e) => _host.StartPlacement(() =>
            {
                if (IsDisposed) return;
                SyncPosition();
                SyncScreen();
            }));
            place.Margin = _ui.P(0, 8, 0, 0);
            var posCol = _ui.Column();
            posCol.Controls.Add(posLabel);
            posCol.Controls.Add(place);
            SyncPosition();
            col.Controls.Add(_ui.Row(Field("Position"), grid, posCol));

            // Size & sound
            col.Controls.Add(_ui.Row(Field("Width"),
                _ui.Number(160, 800, s.PopupWidth, 20, " px", v => { s.PopupWidth = v; _host.SettingsChanged(false); })));
            col.Controls.Add(_ui.Row(Field("Volume"),
                _ui.Number(0, 100, s.Volume, 5, " %", v => { s.Volume = v; _host.SettingsChanged(false); }),
                _ui.Button("Test", (o, e) => _host.TestSound(_host.ActiveMate, Phase.Stand))));

            var checks = _ui.Column();
            checks.Controls.Add(_ui.Check("Repeat the sound every few seconds until I click", s.RepeatSound,
                v => { s.RepeatSound = v; _host.SettingsChanged(false); }));
            checks.Controls.Add(_ui.Check("Hold reminders until fullscreen apps (games, videos) close", s.WaitForFullscreen,
                v => { s.WaitForFullscreen = v; _host.SettingsChanged(false); }));
            col.Controls.Add(_ui.Row(Field("Behaviour"), checks));

            col.Controls.Add(_ui.Row(Field(""),
                _ui.Button("Preview popup", (o, e) => _host.Preview(_host.ActiveMate, Phase.Stand), accent: true)));
            return col;
        }

        private Control BuildGeneral()
        {
            var col = Page("General", null);
            col.Controls.Add(_ui.Check("Start StandMate when I sign in to Windows", Autostart.IsEnabled, Autostart.Set));
            col.Controls.Add(_ui.Paragraph(
                "Closing this window keeps StandMate running in the tray: click the ^ arrow next to the clock to find it. " +
                "Use Quit in its menu to exit completely.", ContentWidth, 14));
            col.Controls.Add(_ui.Row(_ui.Button("Open settings folder", (o, e) => OpenFolder(Settings.DataDir))));

            BuildUpdates(col);

            col.Controls.Add(new Label { Text = "About", Font = _ui.Semibold, ForeColor = Theme.Text, AutoSize = true, Margin = _ui.P(0, 14, 0, 6) });
            col.Controls.Add(_ui.Paragraph(
                "No telemetry, no accounts. StandMate only goes online when you press Check for updates or Update now, " +
                "and then only to download from its GitHub releases.", ContentWidth));
            col.Controls.Add(_ui.Paragraph(
                "Away detection only asks Windows when the last input happened. It never sees which keys you press " +
                "or where your mouse is, and nothing is recorded.", ContentWidth));
            col.Controls.Add(_ui.Paragraph(
                "Sounds: \"Interface Sounds\" by Kenney (kenney.nl), CC0.", ContentWidth));
            return col;
        }

        private void BuildUpdates(FlowLayoutPanel col)
        {
            var u = _host.Updater;
            col.Controls.Add(new Label { Text = "Updates", Font = _ui.Semibold, ForeColor = Theme.Text, AutoSize = true, Margin = _ui.P(0, 14, 0, 6) });

            Button action;
            if (u.Stage == UpdateStage.Available)
                action = _ui.Button($"Update to {u.OfferVersion}", (o, e) => u.Install(), accent: true);
            else
                action = _ui.Button("Check for updates", (o, e) => u.Check());
            action.Enabled = !u.Busy;

            _updateLabel = _ui.Label(u.Stage == UpdateStage.Idle ? $"Version {u.Current}" : u.Message, muted: true);
            _updateLabel.MaximumSize = new Size(_ui.S(ContentWidth - 170), 0);
            var row = _ui.Row(action, _updateLabel);
            if (u.Stage == UpdateStage.Downloading)
                row.Controls.Add(_ui.Button("Cancel", (o, e) => u.Cancel()));
            col.Controls.Add(row);

            if (u.Stage == UpdateStage.Available)
            {
                if (u.OfferIsMajor)
                    col.Controls.Add(_ui.Paragraph(
                        "This is a major update: your settings and mates are kept, but the timer starts fresh.", ContentWidth, 6));
                else
                    col.Controls.Add(_ui.Paragraph(
                        "Your settings, mates and the current timer are kept.", ContentWidth, 6));
                if (!string.IsNullOrWhiteSpace(u.Offer.Notes))
                    col.Controls.Add(_ui.Paragraph(u.Offer.Notes.Trim(), ContentWidth, 6));
            }
        }

        private void OnUpdateChanged()
        {
            if (IsDisposed || _page != "General") return;
            // Progress ticks only change the text; anything else rebuilds the page (buttons change).
            if (_host.Updater.Stage == UpdateStage.Downloading && _updateLabel != null && !_updateLabel.IsDisposed &&
                _updateLabel.Text.StartsWith("Downloading", StringComparison.Ordinal))
                _updateLabel.Text = _host.Updater.Message;
            else
                RefreshPage();
        }

        // ---------------- helpers ----------------

        private static string Pretty(string anchor)
        {
            switch (anchor)
            {
                case "TopLeft": return "Top left";
                case "TopRight": return "Top right";
                case "BottomLeft": return "Bottom left";
                case "BottomRight": return "Bottom right";
                default: return anchor;
            }
        }

        private static void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception) { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _host.Updater.Changed -= OnUpdateChanged;
            _statusTimer.Stop();
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _statusTimer.Dispose();
                _ui?.Dispose(); // fonts last, after the controls using them are gone
            }
        }
    }
}
