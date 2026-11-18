using System;

namespace StandMate.Core
{
    public enum RunState
    {
        /// <summary>Counting down the current phase.</summary>
        Running,
        /// <summary>The reminder popup is up; waiting for a click.</summary>
        Due,
        /// <summary>Paused from the tray menu.</summary>
        Paused,
        /// <summary>Countdown frozen because there was no mouse/keyboard input for a while.</summary>
        Away,
        /// <summary>On a break: its own countdown runs (input or not) while the sit/stand countdown waits.</summary>
        Break,
    }

    /// <summary>What the reminder popup is about.</summary>
    public enum Reminder
    {
        /// <summary>Switch to <see cref="ScheduleEngine.NextPhase"/> (state <see cref="RunState.Due"/>).</summary>
        Switch,
        /// <summary>A break just started (state <see cref="RunState.Break"/>; its popup needs no click).</summary>
        BreakStart,
        /// <summary>The break is over: back to <see cref="ScheduleEngine.PhaseAfterBreak"/> (state <see cref="RunState.Due"/>).</summary>
        BreakEnd,
    }

    /// <summary>
    /// The sit/stand state machine. Pure logic: time and input activity are passed in,
    /// which keeps it deterministic and unit-testable. The UI layer drives it with a timer.
    /// </summary>
    public sealed class ScheduleEngine
    {
        private static readonly TimeSpan BusyRetry = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan AwayPoll = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan MaxSleep = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan MinSleep = TimeSpan.FromMilliseconds(250);

        /// <summary>A time-based break this close to a switch is taken at the switch (at most a quarter of the interval).</summary>
        private static readonly TimeSpan MergeWindow = TimeSpan.FromMinutes(10);

        /// <summary>"Not set" that still serializes to JSON in any time zone (a local MinValue doesn't east of UTC).</summary>
        private static readonly DateTime Never = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);

        private readonly double _timeScale;
        private TimeSpan _frozen;           // phase time left while Paused / Away / on a break
        private uint _awayLastInputTick;    // input tick at the moment we went Away
        private DateTime _runningSince;     // when the current countdown (re)started

        // Breaks
        private BreakRule _breaks = BreakRule.None;
        private TimeSpan _breakLeft;        // Minutes trigger: sit/stand time left until the next break, as of _breakFrom
        private DateTime? _breakFrom;       // set while that clock counts, which is only while Running
        private int _switches;              // Switches trigger: switches since the last break
        private bool _switchAfterBreak;     // the break took a switch's place: the next phase starts after it
        private DateTime _resumeEndUtc = Never; // the interrupted phase's end (clock-based modes keep it)

        /// <param name="timeScale">Multiplier for phase lengths. 1 = real time; 1/60 = minutes become seconds (dev --fast).</param>
        public ScheduleEngine(IScheduleMode mode, double timeScale = 1.0)
        {
            Mode = mode;
            _timeScale = timeScale;
            State = RunState.Paused;
            EndUtc = Never;
        }

        public IScheduleMode Mode { get; private set; }
        public bool PauseWhenAway { get; set; } = true;
        public TimeSpan AwayThreshold { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// true: when you're found to be away, rewind to your last input and pause from there (idle minutes don't count).
        /// false: pause from the moment it's noticed (the idle minutes count).
        /// </summary>
        public bool AwayRewind { get; set; } = true;

        public Phase Phase { get; private set; } = Phase.Sit;
        public Phase NextPhase => Phase.Other();
        public RunState State { get; private set; }
        public Reminder Reminder { get; private set; }

        /// <summary>End of the current phase; on a break, end of the break.</summary>
        public DateTime EndUtc { get; private set; }
        public DateTime? PauseUntilUtc { get; private set; }

        public BreakRule Breaks => _breaks;

        /// <summary>On a break, or its "break's over" reminder is up.</summary>
        public bool OnBreak => State == RunState.Break || (State == RunState.Due && Reminder == Reminder.BreakEnd);

        /// <summary>The phase you're in once the current break is over.</summary>
        public Phase PhaseAfterBreak => _switchAfterBreak ? Phase.Other() : Phase;

        public TimeSpan Remaining(DateTime nowUtc)
        {
            switch (State)
            {
                case RunState.Running:
                case RunState.Break: return Max0(EndUtc - nowUtc);
                case RunState.Paused:
                case RunState.Away: return _frozen;
                default: return TimeSpan.Zero;
            }
        }

        /// <summary>Time-based breaks: sit/stand time left until the next one. Null for other triggers or while on a break.</summary>
        public TimeSpan? BreakIn(DateTime nowUtc) =>
            _breaks.Trigger == BreakTrigger.Minutes && !OnBreak ? Max0(BreakLeft(nowUtc)) : (TimeSpan?)null;

        /// <summary>Switch-based breaks: switches left before the one that becomes a break (0 = the next one).</summary>
        public int? SwitchesBeforeBreak =>
            _breaks.Trigger == BreakTrigger.Switches && !OnBreak ? Math.Max(0, _breaks.EverySwitches - 1 - _switches) : (int?)null;

        // ---------------- commands ----------------

        /// <summary>Begin phase <paramref name="phase"/> now, with a fresh duration from the mode.</summary>
        public void Start(Phase phase, DateTime nowUtc)
        {
            if (OnBreak) RestartBreakClock(nowUtc); // a break ended early still counts as taken
            Phase = phase;
            Reminder = Reminder.Switch;
            PauseUntilUtc = null;
            Enter(RunState.Running, nowUtc);
            _runningSince = nowUtc;
            EndUtc = nowUtc + Scaled(Mode.NextDuration(phase, nowUtc.ToLocalTime()));
        }

        /// <summary>
        /// Start a brand-new cycle (app start, wake from sleep/hibernate). Uses <paramref name="startPhase"/>,
        /// except clock-based modes, which follow the clock. Returns true when a reminder is due right away
        /// (clock mode woken inside its stand window).
        /// </summary>
        public bool StartFresh(Phase startPhase, DateTime nowUtc)
        {
            Mode.Reset();
            RestartBreakClock(nowUtc);
            _switchAfterBreak = false;
            var clock = Mode.ClockPhase(nowUtc.ToLocalTime());
            if (clock == Phase.Stand)
            {
                Phase = Phase.Sit;          // you're presumably sitting; the clock says stand
                Reminder = Reminder.Switch;
                PauseUntilUtc = null;
                Enter(RunState.Due, nowUtc);
                return true;
            }
            Start(clock ?? startPhase, nowUtc);
            return false;
        }

        /// <summary>The reminder was clicked: switch to the next phase, or (after "break's over") get back to it.</summary>
        public void Acknowledge(DateTime nowUtc)
        {
            if (State != RunState.Due) return;
            if (Reminder == Reminder.BreakEnd)
            {
                FinishBreak(nowUtc);
                return;
            }
            _switches++;
            Start(NextPhase, nowUtc);
        }

        /// <summary>"Sit now" / "Stand now" from the tray. Counts as a switch; on a break it ends the break.</summary>
        public void SwitchTo(Phase phase, DateTime nowUtc)
        {
            if (!OnBreak && phase != Phase) _switches++;
            Start(phase, nowUtc);
        }

        /// <summary>
        /// Hide the reminder and bring it back after <paramref name="delay"/>. A switch: the current phase goes on.
        /// A break that just started: you carry on as before and the break comes back. "Break's over": the break
        /// gets longer.
        /// </summary>
        public void Snooze(TimeSpan delay, DateTime nowUtc)
        {
            var d = Scaled(delay);
            if (State == RunState.Break)
            {
                Continue(nowUtc);
                if (_switchAfterBreak && EndUtc < nowUtc + d) EndUtc = nowUtc + d; // the switch it replaced waits too
                _breakLeft = d; // time-based: due again then (switch-based: the count still says so at the switch)
                return;
            }
            if (State == RunState.Due && Reminder == Reminder.BreakEnd)
            {
                Reminder = Reminder.BreakStart;
                Enter(RunState.Break, nowUtc);
                EndUtc = nowUtc + d;
                return;
            }
            Reminder = Reminder.Switch;
            PauseUntilUtc = null;
            Enter(RunState.Running, nowUtc);
            _runningSince = nowUtc;
            EndUtc = nowUtc + d;
        }

        /// <summary>
        /// "Skip this switch": stay in the current phase for another full interval. On a break: no break this
        /// time, the same as <see cref="EndBreak"/>.
        /// </summary>
        public void Skip(DateTime nowUtc)
        {
            if (OnBreak) EndBreak(nowUtc);
            else Start(Phase, nowUtc);
        }

        /// <summary>"Take a break now" from the tray. The current phase waits and carries on afterwards. False when paused or already on a break.</summary>
        public bool TakeBreak(DateTime nowUtc)
        {
            switch (State)
            {
                case RunState.Away:
                    Enter(RunState.Running, nowUtc);
                    _runningSince = nowUtc;
                    EndUtc = nowUtc + _frozen;
                    goto case RunState.Running;
                case RunState.Running:
                    StartBreak(nowUtc, switchAfter: nowUtc >= EndUtc);
                    return true;
                case RunState.Due when Reminder == Reminder.Switch:
                    StartBreak(nowUtc, switchAfter: true);
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Ends the break now (the break counts as taken): the switch it replaced happens, or the interrupted phase carries on.</summary>
        public void EndBreak(DateTime nowUtc)
        {
            if (OnBreak) FinishBreak(nowUtc);
        }

        public void Pause(DateTime nowUtc, TimeSpan? duration)
        {
            // Pausing ends a break (it counts as taken), then pauses what comes after it.
            if (OnBreak) FinishBreak(nowUtc);
            if (State == RunState.Running) _frozen = Max0(EndUtc - nowUtc);
            else if (State == RunState.Due) _frozen = TimeSpan.Zero; // reminder comes back right after the pause
            Enter(RunState.Paused, nowUtc);
            PauseUntilUtc = duration.HasValue ? nowUtc + duration.Value : (DateTime?)null;
        }

        public void Resume(DateTime nowUtc)
        {
            if (State != RunState.Paused) return;
            Enter(RunState.Running, nowUtc);
            PauseUntilUtc = null;
            _runningSince = nowUtc;
            EndUtc = nowUtc + _frozen;
        }

        /// <summary>Schedule settings changed: restart the current phase with the new mode.</summary>
        public void SetMode(IScheduleMode mode, DateTime nowUtc)
        {
            Mode = mode;
            if (OnBreak)
            {
                // The break goes on; whatever follows it uses the new schedule.
                if (!_switchAfterBreak)
                {
                    _frozen = Scaled(mode.NextDuration(Phase, nowUtc.ToLocalTime()));
                    _resumeEndUtc = nowUtc + _frozen;
                }
                return;
            }
            switch (State)
            {
                case RunState.Running:
                    Start(Phase, nowUtc);
                    break;
                case RunState.Paused:
                case RunState.Away:
                    _frozen = Scaled(mode.NextDuration(Phase, nowUtc.ToLocalTime()));
                    break;
            }
        }

        /// <summary>
        /// Break settings changed. A new trigger or interval starts the break countdown over; a new switch count
        /// keeps the switches made so far; a new length applies to the next break.
        /// </summary>
        public void SetBreaks(BreakRule rule, DateTime nowUtc)
        {
            var old = _breaks;
            _breaks = rule ?? BreakRule.None;
            if (_breaks.Trigger != old.Trigger ||
                (_breaks.Trigger == BreakTrigger.Minutes && _breaks.EveryMinutes != old.EveryMinutes))
                RestartBreakClock(nowUtc);
        }

        // ---------------- breaks ----------------

        /// <summary>Sets the state. The time-based break clock only counts while Running (and stops at <paramref name="at"/>).</summary>
        private void Enter(RunState state, DateTime at)
        {
            if (state == RunState.Running)
            {
                if (!_breakFrom.HasValue) _breakFrom = at;
            }
            else if (_breakFrom.HasValue)
            {
                _breakLeft = BreakLeft(at);
                _breakFrom = null;
            }
            State = state;
        }

        private TimeSpan BreakLeft(DateTime nowUtc) =>
            _breakFrom.HasValue ? _breakLeft - Max0(nowUtc - _breakFrom.Value) : _breakLeft;

        /// <summary>A break was taken or skipped, or you were away long enough: the next one is a full interval away.</summary>
        private void RestartBreakClock(DateTime nowUtc)
        {
            _switches = 0;
            _breakLeft = Scaled(TimeSpan.FromMinutes(_breaks.EveryMinutes));
            if (_breakFrom.HasValue) _breakFrom = nowUtc;
        }

        /// <summary>Away (no input) for at least a break's length counts as the break. Only when away detection is on.</summary>
        private bool Rested(TimeSpan idle) =>
            _breaks.Trigger != BreakTrigger.Off && PauseWhenAway &&
            idle >= AwayThreshold && idle >= Scaled(TimeSpan.FromMinutes(_breaks.LengthMinutes));

        private TimeSpan Merge
        {
            get
            {
                var quarter = TimeSpan.FromTicks(TimeSpan.FromMinutes(_breaks.EveryMinutes).Ticks / 4);
                return Scaled(quarter < MergeWindow ? quarter : MergeWindow);
            }
        }

        /// <summary>
        /// Time-based breaks, while Running: when the next break interrupts the current phase. Null when it doesn't:
        /// one due within <see cref="Merge"/> of the phase's end waits for the switch and takes its place.
        /// </summary>
        private DateTime? MidPhaseBreak(DateTime nowUtc)
        {
            if (State != RunState.Running || _breaks.Trigger != BreakTrigger.Minutes || !_breakFrom.HasValue) return null;
            var due = _breakFrom.Value + _breakLeft;
            if (due < nowUtc) due = nowUtc;
            return EndUtc - due > Merge ? due : (DateTime?)null;
        }

        /// <summary>At the end of a phase: does a break take this switch's place?</summary>
        private bool BreakAtSwitch(DateTime nowUtc)
        {
            switch (_breaks.Trigger)
            {
                case BreakTrigger.Switches: return _switches + 1 >= _breaks.EverySwitches;
                case BreakTrigger.Minutes: return BreakLeft(nowUtc) <= Merge;
                default: return false;
            }
        }

        private void StartBreak(DateTime nowUtc, bool switchAfter)
        {
            _switchAfterBreak = switchAfter;
            _frozen = Max0(EndUtc - nowUtc);
            _resumeEndUtc = EndUtc;
            Reminder = Reminder.BreakStart;
            PauseUntilUtc = null;
            Enter(RunState.Break, nowUtc);
            EndUtc = nowUtc + Scaled(TimeSpan.FromMinutes(_breaks.LengthMinutes));
        }

        /// <summary>Back from a break: start the phase whose switch it replaced, or continue the interrupted one.</summary>
        private void FinishBreak(DateTime nowUtc)
        {
            RestartBreakClock(nowUtc);
            if (_switchAfterBreak) Start(Phase.Other(), nowUtc);
            else Continue(nowUtc);
        }

        /// <summary>Continue the phase a break interrupted with the time it had left (clock-based: until its wall-clock end).</summary>
        private void Continue(DateTime nowUtc)
        {
            Reminder = Reminder.Switch;
            PauseUntilUtc = null;
            Enter(RunState.Running, nowUtc);
            _runningSince = nowUtc;
            EndUtc = Mode.IsClockBased ? _resumeEndUtc : nowUtc + _frozen;
        }

        // ---------------- in-place updates ----------------

        /// <summary>Captures the timer so a newer version can continue it (see <see cref="Restore"/>).</summary>
        public EngineSnapshot Snapshot(DateTime nowUtc) => new EngineSnapshot
        {
            SavedUtc = nowUtc,
            ModeState = Mode.ExportState(),
            Phase = Phase.ToString(),
            State = State.ToString(),
            EndUtc = EndUtc,
            FrozenMs = (long)_frozen.TotalMilliseconds,
            PauseUntilUtc = PauseUntilUtc,
            Reminder = Reminder.ToString(),
            BreakLeftMs = (long)BreakLeft(nowUtc).TotalMilliseconds,
            Switches = _switches,
            SwitchAfterBreak = _switchAfterBreak,
            ResumeEndUtc = OnBreak ? _resumeEndUtc : (DateTime?)null,
        };

        /// <summary>
        /// Continues from a snapshot. A running countdown keeps the time it had left (the seconds spent updating
        /// don't count), except clock-based modes, which keep their wall-clock end. Away resumes as running with the
        /// time that was frozen; if you're still away it's noticed again. A break keeps its wall-clock end.
        /// Snapshots from versions without breaks keep a full break interval. Returns false if the snapshot is unreadable.
        /// </summary>
        public bool Restore(EngineSnapshot s, DateTime nowUtc)
        {
            if (s == null ||
                !Enum.TryParse(s.Phase, out Phase phase) ||
                !Enum.TryParse(s.State, out RunState state) ||
                !Enum.IsDefined(typeof(RunState), state))
                return false;
            Reminder reminder = Reminder.Switch;
            if (!string.IsNullOrEmpty(s.Reminder) && !Enum.TryParse(s.Reminder, out reminder)) return false;

            Mode.ImportState(s.ModeState);
            Phase = phase;
            Reminder = state == RunState.Break ? Reminder.BreakStart
                : state == RunState.Due && reminder == Reminder.BreakEnd ? Reminder.BreakEnd
                : Reminder.Switch;
            PauseUntilUtc = null;
            _runningSince = nowUtc;
            _frozen = Max0(TimeSpan.FromMilliseconds(s.FrozenMs));
            _switches = Math.Max(0, s.Switches);
            _switchAfterBreak = s.SwitchAfterBreak;
            _resumeEndUtc = s.ResumeEndUtc ?? Never;
            _breakFrom = null;
            if (s.BreakLeftMs.HasValue)
            {
                var left = Max0(TimeSpan.FromMilliseconds(s.BreakLeftMs.Value));
                var full = Scaled(TimeSpan.FromMinutes(_breaks.EveryMinutes));
                _breakLeft = left < full ? left : full;
            }
            var gap = Max0(nowUtc - s.SavedUtc);

            switch (state)
            {
                case RunState.Running:
                    Enter(RunState.Running, nowUtc);
                    EndUtc = Mode.IsClockBased ? s.EndUtc : s.EndUtc + gap;
                    break;
                case RunState.Away:
                    Enter(RunState.Running, nowUtc);
                    EndUtc = nowUtc + _frozen;
                    break;
                case RunState.Paused:
                    Enter(RunState.Paused, nowUtc);
                    PauseUntilUtc = s.PauseUntilUtc;
                    break;
                case RunState.Due:
                    Enter(RunState.Due, nowUtc);   // the caller shows the reminder again
                    break;
                case RunState.Break:
                    Enter(RunState.Break, nowUtc);
                    EndUtc = s.EndUtc;
                    break;
            }
            return true;
        }

        // ---------------- time ----------------

        /// <summary>
        /// Advances the state machine. Returns true when a reminder just became due: see <see cref="Reminder"/>
        /// (a switch to <see cref="NextPhase"/>, a break that just started, or the end of a break).
        /// </summary>
        /// <param name="idle">Time since the last mouse/keyboard input.</param>
        /// <param name="lastInputTick">Raw tick of the last input; only compared for equality to detect new input.</param>
        /// <param name="busy">A fullscreen app is running and the user wants reminders to wait.</param>
        public bool Update(DateTime nowUtc, TimeSpan idle, uint lastInputTick, bool busy)
        {
            switch (State)
            {
                case RunState.Paused:
                    if (PauseUntilUtc.HasValue && nowUtc >= PauseUntilUtc.Value)
                    {
                        Resume(nowUtc);
                        return Update(nowUtc, idle, lastInputTick, busy);
                    }
                    return false;

                case RunState.Away:
                    if (Rested(idle)) RestartBreakClock(nowUtc);
                    if (lastInputTick != _awayLastInputTick || !PauseWhenAway)
                    {
                        // Back at the keyboard: continue with exactly the time that was left.
                        Enter(RunState.Running, nowUtc);
                        _runningSince = nowUtc;
                        EndUtc = nowUtc + _frozen;
                        return Update(nowUtc, idle, lastInputTick, busy);
                    }
                    return false;

                case RunState.Break:
                    // The break runs on the wall clock whether or not there's input; then "break's over" waits for a click.
                    if (nowUtc < EndUtc || busy) return false;
                    if (!_switchAfterBreak && Mode.IsClockBased && nowUtc >= _resumeEndUtc)
                        _switchAfterBreak = true; // the clock moved on to the next phase during the break
                    Reminder = Reminder.BreakEnd;
                    Enter(RunState.Due, nowUtc);
                    return true;

                case RunState.Running:
                    if (Rested(idle)) RestartBreakClock(nowUtc);
                    bool away = PauseWhenAway && idle >= AwayThreshold;
                    if (away && !Mode.IsClockBased)
                    {
                        // Rewind: freeze as of the last input, so the idle minutes don't count
                        // (but never earlier than when this countdown started). Otherwise freeze now.
                        var freezeAt = AwayRewind ? nowUtc - idle : nowUtc;
                        if (freezeAt < _runningSince) freezeAt = _runningSince;
                        _frozen = Max0(EndUtc - freezeAt);
                        _awayLastInputTick = lastInputTick;
                        Enter(RunState.Away, freezeAt);
                        return false;
                    }
                    bool phaseOver = nowUtc >= EndUtc;
                    bool breakNow = !phaseOver && MidPhaseBreak(nowUtc) is DateTime due && nowUtc >= due;
                    if (!phaseOver && !breakNow) return false;
                    if (busy) return false;   // wait until the fullscreen app is gone
                    if (away) return false;   // clock mode: hold the reminder until the user is back
                    if (breakNow) StartBreak(nowUtc, switchAfter: false);
                    else if (BreakAtSwitch(nowUtc)) StartBreak(nowUtc, switchAfter: true);
                    else
                    {
                        Reminder = Reminder.Switch;
                        Enter(RunState.Due, nowUtc);
                    }
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>A reminder would come up now if nothing held it back (so the fullscreen check only runs when needed).</summary>
        public bool ReminderDue(DateTime nowUtc)
        {
            switch (State)
            {
                case RunState.Running:
                    return nowUtc >= EndUtc || (MidPhaseBreak(nowUtc) is DateTime due && nowUtc >= due);
                case RunState.Break:
                    return nowUtc >= EndUtc;
                default:
                    return false;
            }
        }

        /// <summary>How long until <see cref="Update"/> should run again; null = nothing to wait for.</summary>
        public TimeSpan? NextCheck(DateTime nowUtc, TimeSpan idle, bool busy)
        {
            switch (State)
            {
                case RunState.Due:
                    return null;
                case RunState.Paused:
                    return PauseUntilUtc.HasValue ? Clamp(PauseUntilUtc.Value - nowUtc) : (TimeSpan?)null;
                case RunState.Away:
                    return AwayPoll;
                case RunState.Break:
                    return nowUtc >= EndUtc ? BusyRetry : Clamp(EndUtc - nowUtc); // past the end = held for fullscreen
                default:
                    var next = MidPhaseBreak(nowUtc) ?? EndUtc;
                    if (nowUtc >= next) return busy ? BusyRetry : AwayPoll; // deferred reminder
                    var d = next - nowUtc;
                    if (PauseWhenAway && !Mode.IsClockBased)
                    {
                        var untilAway = AwayThreshold - idle;
                        if (untilAway < d) d = untilAway;
                    }
                    return Clamp(d);
            }
        }

        private TimeSpan Scaled(TimeSpan t) => TimeSpan.FromTicks((long)(t.Ticks * _timeScale));

        private static TimeSpan Max0(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;

        private static TimeSpan Clamp(TimeSpan t) => t < MinSleep ? MinSleep : t > MaxSleep ? MaxSleep : t;
    }
}
