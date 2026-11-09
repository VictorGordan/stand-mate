using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using StandMate.Core;
using StandMate.Platform;

namespace StandMate.UI
{
    /// <summary>Create/edit a mate: name, and GIF + sound + caption for standing up and for sitting down.</summary>
    internal sealed class MateEditorForm : Form
    {
        private readonly Mate _mate;
        private readonly IAppHost _host;
        private readonly Ui _ui;
        private readonly TextBox _name;
        private readonly Side _stand, _sit;

        /// <summary>Pending edits for one phase; files are only copied in on Save.</summary>
        private sealed class Side
        {
            public string ImageSource;   // absolute path to show / import
            public string SoundSource;
            public bool ImageChanged, SoundChanged;
            public PictureBox Thumb;
            public Label ImageLabel, SoundLabel;
            public TextBox Caption;
            public MemoryStream ThumbStream;
        }

        public MateEditorForm(Mate mate, bool isNew, IAppHost host, float scale)
        {
            _mate = mate;
            _host = host;
            _ui = new Ui(scale);

            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = _ui.Text;
            Text = isNew ? "New mate" : "Edit mate";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = _ui.P(22, 18, 22, 16);

            var col = _ui.Column();
            _name = _ui.TextBox(mate.Name, 260);
            col.Controls.Add(_ui.Row(FieldLabel("Name"), _name));

            _stand = new Side { ImageSource = mate.ImagePath(Phase.Stand), SoundSource = mate.SoundPath(Phase.Stand) };
            _sit = new Side { ImageSource = mate.ImagePath(Phase.Sit), SoundSource = mate.SoundPath(Phase.Sit) };
            col.Controls.Add(BuildSide(_stand, "立  Standing up", mate.For(Phase.Stand).Caption));
            col.Controls.Add(BuildSide(_sit, "座  Sitting down", mate.For(Phase.Sit).Caption));

            var save = _ui.Button("Save", (o, e) => Save(), accent: true);
            var cancel = _ui.Button("Cancel", (o, e) => { DialogResult = DialogResult.Cancel; Close(); });
            var buttons = _ui.Row(save, cancel);
            buttons.Margin = _ui.P(0, 12, 0, 0);
            col.Controls.Add(buttons);

            col.Location = new Point(Padding.Left, Padding.Top);
            Controls.Add(col);
            AcceptButton = save;
            CancelButton = cancel;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(Handle);
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e) => e.Cancel = true; // short-lived dialog: keep its size

        private Control BuildSide(Side side, string title, string caption)
        {
            var box = _ui.Column();
            box.Margin = _ui.P(0, 12, 0, 0);
            box.Controls.Add(new Label { Text = title, Font = _ui.Semibold, ForeColor = Theme.Accent, AutoSize = true, Margin = _ui.P(0, 0, 0, 6) });

            side.Thumb = new PictureBox
            {
                Size = _ui.S(160, 96),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Theme.Surface,
                Margin = _ui.P(0, 0, 14, 0),
            };
            side.ImageLabel = FileLabel();
            side.SoundLabel = FileLabel();
            side.Caption = _ui.TextBox(caption, 230);

            var fields = _ui.Column();
            fields.Controls.Add(_ui.Row(FieldLabel("GIF"), side.ImageLabel,
                _ui.Button("Choose…", (o, e) => PickImage(side))));
            fields.Controls.Add(_ui.Row(FieldLabel("Sound"), side.SoundLabel,
                _ui.Button("Choose…", (o, e) => PickSound(side)),
                _ui.Button("▶", (o, e) => Audio.Play(side.SoundSource, _host.Settings.Volume)),
                _ui.Button("✕", (o, e) => { Audio.Stop(); side.SoundSource = null; side.SoundChanged = true; ShowSide(side); })));
            fields.Controls.Add(_ui.Row(FieldLabel("Caption"), side.Caption));

            box.Controls.Add(_ui.Row(side.Thumb, fields));
            ShowSide(side);
            return box;
        }

        private void ShowSide(Side side)
        {
            side.ImageLabel.Text = side.ImageSource != null ? Path.GetFileName(side.ImageSource) : "none";
            side.SoundLabel.Text = side.SoundSource != null ? Path.GetFileName(side.SoundSource) : "none";

            var old = side.Thumb.Image;
            var oldStream = side.ThumbStream;
            side.Thumb.Image = null;
            old?.Dispose();
            oldStream?.Dispose();
            side.ThumbStream = null;
            if (side.ImageSource == null || !File.Exists(side.ImageSource)) return;
            try
            {
                side.ThumbStream = new MemoryStream(File.ReadAllBytes(side.ImageSource));
                side.Thumb.Image = Image.FromStream(side.ThumbStream); // PictureBox animates GIFs itself
            }
            catch (Exception)
            {
                side.ThumbStream?.Dispose();
                side.ThumbStream = null;
            }
        }

        private void PickImage(Side side)
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "Choose a GIF",
                Filter = "Images (*.gif;*.png;*.jpg;*.jpeg;*.bmp)|*.gif;*.png;*.jpg;*.jpeg;*.bmp",
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try { using (Image.FromFile(dlg.FileName)) { } }
                catch (Exception)
                {
                    MessageBox.Show(this, "That file doesn't look like an image Windows can read.", "StandMate");
                    return;
                }
                side.ImageSource = dlg.FileName;
                side.ImageChanged = true;
                ShowSide(side);
            }
        }

        private void PickSound(Side side)
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "Choose a sound",
                Filter = "Sounds (*.wav;*.mp3;*.wma)|*.wav;*.mp3;*.wma",
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Audio.Stop();
                side.SoundSource = dlg.FileName;
                side.SoundChanged = true;
                ShowSide(side);
            }
        }

        private void Save()
        {
            string name = _name.Text.Trim();
            if (name.Length == 0)
            {
                MessageBox.Show(this, "Give the mate a name.", "StandMate");
                return;
            }
            Audio.Stop();
            try
            {
                _mate.Name = name;
                Apply(_stand, _mate.Stand, "stand");
                Apply(_sit, _mate.Sit, "sit");
                MateStore.Save(_mate);
                MateStore.CleanUnused(_mate);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't save the mate: " + ex.Message, "StandMate");
            }
        }

        private void Apply(Side side, MateAction action, string baseName)
        {
            if (side.ImageChanged)
                action.Image = side.ImageSource != null ? MateStore.Import(_mate, side.ImageSource, baseName) : null;
            if (side.SoundChanged)
                action.Sound = side.SoundSource != null ? MateStore.Import(_mate, side.SoundSource, baseName) : null;
            string caption = side.Caption.Text.Trim();
            action.Caption = caption.Length > 0 ? caption : null;
        }

        private Label FieldLabel(string text) => _ui.Field(text, 64);

        private Label FileLabel() => new Label
        {
            AutoSize = false,
            Size = _ui.S(150, 28),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Text,
            Font = _ui.Text,
            AutoEllipsis = true,
            Margin = _ui.P(0, 0, 6, 0),
        };

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Audio.Stop();
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var side in new[] { _stand, _sit }.Where(x => x?.Thumb != null))
                {
                    var img = side.Thumb.Image;
                    side.Thumb.Image = null; // stops the PictureBox's GIF animation first
                    img?.Dispose();
                    side.ThumbStream?.Dispose();
                }
            }
            base.Dispose(disposing);
            if (disposing) _ui.Dispose();
        }
    }
}
