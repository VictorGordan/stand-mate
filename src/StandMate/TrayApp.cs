using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using StandMate.Core;
using StandMate.Platform;
using StandMate.UI;
using Timer = System.Windows.Forms.Timer;

namespace StandMate
{
    /// <summary>
    /// The whole app lives here: a tray icon, one timer that drives the <see cref="ScheduleEngine"/>,
    /// and windows that only exist while they are on screen.
    /// </summary>
    internal sealed class TrayApp : ApplicationContext, IAppHost
    {
        private readonly bool _fast;
        private readonly Settings _settings;
        private readonly ScheduleEngine _engine;
        private readonly Timer _timer = new Timer();
        private readonly Timer _saveTimer = new Timer { Interval = 600 };
        private readonly Timer _trimTimer = new Timer { Interval = 2000 };
        private readonly NotifyIcon _tray;
        private readonly TrayIcons _icons;
        private readonly ContextMenuStrip _menu;
        private readonly Control _invoker;
        private readonly RegisteredWaitHandle _showWait;
        private readonly UpdateController _updates;

        private List<Mate> _mates = new List<Mate>();
        private ReminderPopup _popup;
        private SettingsForm _settingsForm;
        private System.Drawing.Icon _shownIcon;
        private DateTime _lastTooltip;
        private DateTime? _timerDueUtc;                 // when the timer should next fire (sleep detection)
        private DateTime _lastFreshStart = DateTime.MinValue;

        public TrayApp(bool background, bool fast, EventWaitHandle showSignal)
        {
            _fast = fast;
            _settings = Settings.Load();

            // Each bundled mate is copied into the user's mates once (1.0–1.1 only had a yes/no flag, but the mates
            // they seeded aren't bundled any more, so starting from an empty list is safe).
            var after = MateStore.SeedDefaults(_settings.SeededMates ?? new string[0]);
            MateStore.RepairDefaults();   // e.g. art added to a default mate in a later version
            if (_settings.SeededMates == null || !after.SequenceEqual(_settings.SeededMates))
            {
                _settings.SeededMates = after;
                _settings.MatesSeeded = true;
                _settings.Save();
            }
            LoadMates();

            _engine = new ScheduleEngine(ModeFactory.Create(_settings), fast ? 1.0 / 60 : 1.0);
            ApplyAwaySettings();

            _invoker = new Control();
            _invoker.CreateControl();
            _ = _invoker.Handle;

            _icons = new TrayIcons();
            _menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), Font = Theme.MenuFont };
            _menu.Opening += (s, e) => PopulateMenu();
            _menu.Items.Add("StandMate"); // placeholder so the first Opening isn't cancelled

            _tray = new NotifyIcon { ContextMenuStrip = _menu, Text = "StandMate" };
            _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowSettings(); };
            _tray.MouseMove += (s, e) => RefreshTooltip(throttle: true);

            _timer.Tick += (s, e) => Evaluate();
            _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); _settings.Save(); };
            _trimTimer.Tick += (s, e) => { _trimTimer.Stop(); TrimMemory(); };

            _showWait = ThreadPool.RegisterWaitForSingleObject(showSignal,
                (state, timedOut) => Post(() => ShowSettings()), null, Timeout.Infinite, false);

            // Coming back from sleep/hibernate starts a fresh cycle.
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            _updates = new UpdateController(Post, InstallUpdate);

            _tray.Visible = true;
            bool reopenSettings = false;
            if (!ContinueAfterUpdate(ref reopenSettings))
                FreshStart(); // startup counts as a fresh start too

            if (!background || reopenSettings) ShowSettings(reopenSettings ? "General" : null);
            else TrimSoon();
        }

        /// <summary>
        /// Right after an in-app update: carry on with the timer the previous version left behind, unless it was a
        /// major update (or the snapshot is stale / for another mode), in which case it's a normal fresh start.
        /// </summary>
        private bool ContinueAfterUpdate(ref bool reopenSettings)
        {
            var snap = Handover.Take();
            if (snap == null) return false;
            reopenSettings = snap.SettingsOpen;
            if (Core.Updates.ParseVersion(snap.AppVersion) is Version from && from != UpdateClient.CurrentVersion)
                _updates.ShowJustUpdated(from.ToString());

            var now = DateTime.UtcNow;
            if (!Handover.IsUsable(snap, UpdateClient.CurrentVersion, _settings.Mode, now) || !_engine.Restore(snap, now))
                return false;

            _lastFreshStart = now; // don't treat the restart as a wake from sleep
            RefreshTray();
            if (_engine.State == RunState.Due) ShowReminder();
            Evaluate();
            return true;
        }

        /// <summary>The verified installer is ready: save everything, hand the timer over, run setup and exit.</summary>
        private void InstallUpdate(string installerPath)
        {
            try
            {
                var now = DateTime.UtcNow;
                var snap = _engine.Snapshot(now);
                snap.AppVersion = UpdateClient.CurrentVersion.ToString();
                snap.ModeId = _settings.Mode;
                snap.SettingsOpen = _settingsForm != null && !_settingsForm.IsDisposed;
                Handover.Write(snap);

                // Setup closes and relaunches StandMate itself; the trailing start brings it back even if setup
                // fails (a second --background launch just exits when setup already started it).
                string exe = Application.ExecutablePath;
                var psi = new ProcessStartInfo("cmd.exe",
                    $"/d /c \"\"{installerPath}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL & start \"\" \"{exe}\" --background\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                Process.Start(psi)?.Dispose();
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
                try { System.IO.File.Delete(Handover.FilePath); } catch (Exception) { }
                MessageBox.Show("The update couldn't be started. Try again, or install it manually from the download page.",
                    "StandMate", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Quit();
        }

        // ---------------- scheduling ----------------

        private void Evaluate()
        {
            _timer.Stop();
            var now = DateTime.UtcNow;

            // Backup sleep detection (e.g. modern standby, where the power event may not arrive):
            // the timer never waits more than a minute, so firing far too late means the PC was asleep.
            var due = _timerDueUtc;
            _timerDueUtc = null;
            if (due.HasValue && now - due.Value > TimeSpan.FromMinutes(2) && WakeFreshStart()) return;
            IdleMonitor.Read(out var idle, out var lastInputTick);
            bool busy = _engine.State == RunState.Running && now >= _engine.EndUtc &&
                        _settings.WaitForFullscreen && Fullscreen.IsActive();

            var before = _engine.State;
            if (_engine.Update(now, idle, lastInputTick, busy)) ShowReminder();
            if (_engine.State != before) RefreshTray();

            var next = _engine.NextCheck(now, idle, busy);
            if (next.HasValue)
            {
                _timer.Interval = Math.Max(1, (int)Math.Ceiling(next.Value.TotalMilliseconds));
                _timer.Start();
                _timerDueUtc = now.AddMilliseconds(_timer.Interval);
            }
        }

        /// <summary>A brand-new cycle with the chosen starting phase (the clock decides in "On the clock").</summary>
        private void FreshStart()
        {
            _lastFreshStart = DateTime.UtcNow;
            ClosePopup();
            bool dueNow = _engine.StartFresh(_settings.StartPhase, DateTime.UtcNow);
            RefreshTray();
            if (dueNow) ShowReminder();
            Evaluate();
        }

        /// <summary>After sleep/hibernate. Returns false when skipped (still paused by the user, or just done).</summary>
        private bool WakeFreshStart()
        {
            var now = DateTime.UtcNow;
            if (now - _lastFreshStart < TimeSpan.FromMinutes(1)) return false;
            if (_engine.State == RunState.Paused && (!_engine.PauseUntilUtc.HasValue || now < _engine.PauseUntilUtc.Value))
                return false; // respect a pause that's still in effect
            FreshStart();
            return true;
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume) Post(() => WakeFreshStart());
        }

        private void ApplyAwaySettings()
        {
            _engine.PauseWhenAway = _settings.PauseWhenAway;
            _engine.AwayRewind = _settings.AwayRewind;
            // --fast: minutes become seconds (x2 so the 5-minute default is a 10 s test window)
            _engine.AwayThreshold = _fast
                ? TimeSpan.FromSeconds(_settings.AwayMinutes * 2)
                : TimeSpan.FromMinutes(_settings.AwayMinutes);
        }

        /// <summary>Runs a command on the engine, then re-evaluates and refreshes the tray.</summary>
        private void Do(Action<ScheduleEngine, DateTime> command)
        {
            command(_engine, DateTime.UtcNow);
            Evaluate();
            RefreshTray();
        }

        private void ShowReminder()
        {
            ClosePopup();
            var popup = new ReminderPopup(PopupKind.Reminder, ActiveMate, _engine.NextPhase, _settings);
            popup.Confirmed += () => Do((e, now) => e.Acknowledge(now));
            popup.Snoozed += minutes => Do((e, now) => e.Snooze(TimeSpan.FromMinutes(minutes), now));
            popup.Skipped += () => Do((e, now) => e.Skip(now));
            ShowPopup(popup);
        }

        private void ShowPopup(ReminderPopup popup, Action onClosed = null)
        {
            _popup = popup;
            popup.FormClosed += (s, e) =>
            {
                if (_popup == popup) _popup = null;
                onClosed?.Invoke();
                TrimSoon();
            };
            popup.Show();
        }

        private void ClosePopup()
        {
            var p = _popup;
            _popup = null;
            if (p != null && !p.IsDisposed) p.Close();
        }

        private bool ReminderShowing => _popup != null && !_popup.IsDisposed && _popup.Kind == PopupKind.Reminder;

        private void SwitchNow(Phase phase)
        {
            ClosePopup();
            Do((e, now) => e.Start(phase, now));
        }

        private void Pause(int? minutes)
        {
            ClosePopup();
            Do((e, now) => e.Pause(now, minutes.HasValue ? TimeSpan.FromMinutes(minutes.Value) : (TimeSpan?)null));
        }

        // ---------------- tray ----------------

        private void RefreshTray()
        {
            var state = _engine.State;
            var phase = state == RunState.Due ? _engine.NextPhase : _engine.Phase;
            var icon = _icons.For(phase, dim: state == RunState.Paused || state == RunState.Away);
            if (!ReferenceEquals(icon, _shownIcon))
            {
                _tray.Icon = icon;
                _shownIcon = icon;
            }
            RefreshTooltip(throttle: false);
        }

        private void RefreshTooltip(bool throttle)
        {
            var now = DateTime.UtcNow;
            if (throttle && (now - _lastTooltip).TotalMilliseconds < 1000) return;
            _lastTooltip = now;
            string text = "StandMate · " + StatusText();
            _tray.Text = text.Length > 63 ? text.Substring(0, 63) : text; // NotifyIcon limit
        }

        public string StatusText()
        {
            var now = DateTime.UtcNow;
            switch (_engine.State)
            {
                case RunState.Due:
                    return _engine.NextPhase == Phase.Stand ? "Time to stand" : "Time to sit";
                case RunState.Paused:
                    return _engine.PauseUntilUtc.HasValue
                        ? $"Paused until {_engine.PauseUntilUtc.Value.ToLocalTime():t}"
                        : "Paused";
                case RunState.Away:
                    return $"Away · paused with {Format(_engine.Remaining(now))} left";
                default:
                    string doing = _engine.Phase == Phase.Sit ? "Sitting" : "Standing";
                    string next = _engine.Phase == Phase.Sit ? "stand" : "sit";
                    if (now >= _engine.EndUtc) return $"{doing} · {next} when you're back";
                    return $"{doing} · {next} in {Format(_engine.Remaining(now))}";
            }
        }

        private string Format(TimeSpan t)
        {
            if (_fast) return $"{Math.Ceiling(t.TotalSeconds)} s";
            int min = Math.Max(1, (int)Math.Ceiling(t.TotalMinutes));
            return min < 60 ? $"{min} min" : $"{min / 60} h {min % 60:00} min";
        }

        private void PopulateMenu()
        {
            foreach (var item in _menu.Items.Cast<ToolStripItem>().ToList()) item.Dispose();
            _menu.Items.Clear();

            _menu.Items.Add(new ToolStripMenuItem(StatusText()) { Enabled = false });
            _menu.Items.Add(new ToolStripSeparator());

            var target = _engine.State == RunState.Due ? _engine.NextPhase : _engine.Phase.Other();
            _menu.Items.Add($"{target.Label()} now", null, (s, e) => SwitchNow(target));

            if (_engine.State == RunState.Paused)
            {
                _menu.Items.Add("Resume", null, (s, e) => Do((en, now) => en.Resume(now)));
            }
            else
            {
                var pause = Sub("Pause");
                pause.DropDownItems.Add("30 minutes", null, (s, e) => Pause(30));
                pause.DropDownItems.Add("1 hour", null, (s, e) => Pause(60));
                pause.DropDownItems.Add("2 hours", null, (s, e) => Pause(120));
                pause.DropDownItems.Add("Until I resume", null, (s, e) => Pause(null));
                _menu.Items.Add(pause);
            }

            var mode = Sub("Mode");
            foreach (var m in ModeInfo.All)
            {
                var id = m.Id;
                mode.DropDownItems.Add(new ToolStripMenuItem(m.Name, null, (s, e) =>
                {
                    _settings.Mode = id;
                    SettingsChanged(true);
                    RefreshSettingsWindow();
                }) { Checked = _settings.Mode == id });
            }
            _menu.Items.Add(mode);

            var mate = Sub("Mate");
            var active = ActiveMate;
            foreach (var m in _mates)
            {
                var id = m.Id;
                mate.DropDownItems.Add(new ToolStripMenuItem(m.Name, null, (s, e) =>
                {
                    SetActiveMate(id);
                    RefreshSettingsWindow();
                }) { Checked = active?.Id == id });
            }
            _menu.Items.Add(mate);

            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("Settings…", null, (s, e) => ShowSettings());
            _menu.Items.Add("Quit", null, (s, e) => Quit());
        }

        private void RefreshSettingsWindow()
        {
            if (_settingsForm != null && !_settingsForm.IsDisposed) _settingsForm.RefreshPage();
        }

        private ToolStripMenuItem Sub(string text)
        {
            var item = new ToolStripMenuItem(text);
            item.DropDown.Renderer = _menu.Renderer;
            return item;
        }

        // ---------------- windows ----------------

        private void ShowSettings(string page = null)
        {
            if (_settingsForm != null && !_settingsForm.IsDisposed)
            {
                if (_settingsForm.WindowState == FormWindowState.Minimized) _settingsForm.WindowState = FormWindowState.Normal;
                if (page != null) _settingsForm.ShowPage(page);
                _settingsForm.Activate();
                return;
            }
            _settingsForm = new SettingsForm(this, page);
            _settingsForm.FormClosed += (s, e) => { _settingsForm = null; TrimSoon(); };
            _settingsForm.Show();
            _settingsForm.Activate();
        }

        private void Quit()
        {
            _saveTimer.Stop();
            _settings.Save();
            ClosePopup();
            _settingsForm?.Close();
            _tray.Visible = false;
            ExitThread();
        }

        private void Post(Action a)
        {
            try { if (_invoker.IsHandleCreated) _invoker.BeginInvoke(a); }
            catch (InvalidOperationException) { }
        }

        private void TrimSoon()
        {
            _trimTimer.Stop();
            _trimTimer.Start();
        }

        /// <summary>Hands memory from closed windows/GIFs back to Windows so the tray app stays small.</summary>
        private static void TrimMemory()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            try { Native.SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1)); }
            catch (Exception) { }
        }

        // ---------------- IAppHost ----------------

        public Settings Settings => _settings;
        public UpdateController Updater => _updates;
        public IReadOnlyList<Mate> Mates => _mates;
        public Mate ActiveMate => _mates.FirstOrDefault(m => m.Id == _settings.MateId) ?? _mates.FirstOrDefault();

        public void SettingsChanged(bool scheduleChanged)
        {
            _settings.Normalize();
            ApplyAwaySettings();
            if (scheduleChanged) Do((e, now) => e.SetMode(ModeFactory.Create(_settings), now));
            else Evaluate();
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        public void SetActiveMate(string id)
        {
            _settings.MateId = id;
            SettingsChanged(false);
        }

        public void ReloadMates() => LoadMates();

        private void LoadMates()
        {
            _mates = MateStore.LoadAll();
            if (_mates.Count > 0 && _mates.All(m => m.Id != _settings.MateId))
            {
                _settings.MateId = _mates[0].Id;
                _settings.Save();
            }
        }

        public void Preview(Mate mate, Phase phase)
        {
            if (ReminderShowing) return; // never hide a real reminder behind a preview
            ClosePopup();
            ShowPopup(new ReminderPopup(PopupKind.Preview, mate, phase, _settings));
        }

        public void StartPlacement(Action done)
        {
            if (ReminderShowing) return;
            ClosePopup();
            var popup = new ReminderPopup(PopupKind.Placement, ActiveMate, Phase.Stand, _settings);
            popup.PlacementSaved += bounds =>
            {
                var screen = Screen.FromRectangle(bounds);
                Screens.ToFraction(bounds, screen, out double fx, out double fy);
                _settings.ScreenDevice = screen.DeviceName;
                _settings.Anchor = Anchors.Custom;
                _settings.CustomX = fx;
                _settings.CustomY = fy;
                SettingsChanged(false);
            };
            ShowPopup(popup, done);
        }

        public void TestSound(Mate mate, Phase phase)
        {
            if (ReminderShowing) return;
            Audio.Play(mate?.SoundPath(phase), _settings.Volume);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                _showWait.Unregister(null);
                _timer.Dispose();
                _saveTimer.Dispose();
                _trimTimer.Dispose();
                Audio.Stop();
                _tray.Visible = false;
                _tray.Dispose();
                _menu.Dispose();
                _icons.Dispose();
                _invoker.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
