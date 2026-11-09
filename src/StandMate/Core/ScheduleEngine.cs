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

        private readonly double _timeScale;
        private TimeSpan _frozen;           // remaining time while Paused / Away
        private uint _awayLastInputTick;    // input tick at the moment we went Away
        private DateTime _runningSince;     // when the current countdown (re)started

        /// <param name="timeScale">Multiplier for phase lengths. 1 = real time; 1/60 = minutes become seconds (dev --fast).</param>
        public ScheduleEngine(IScheduleMode mode, double timeScale = 1.0)
        {
            Mode = mode;
            _timeScale = timeScale;
            State = RunState.Paused;
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
        public DateTime EndUtc { get; private set; }
        public DateTime? PauseUntilUtc { get; private set; }

        public TimeSpan Remaining(DateTime nowUtc)
        {
            switch (State)
            {
                case RunState.Running: return Max0(EndUtc - nowUtc);
                case RunState.Paused:
                case RunState.Away: return _frozen;
                default: return TimeSpan.Zero;
            }
        }

        // ---------------- commands ----------------

        /// <summary>Begin phase <paramref name="phase"/> now, with a fresh duration from the mode.</summary>
        public void Start(Phase phase, DateTime nowUtc)
        {
            Phase = phase;
            State = RunState.Running;
            PauseUntilUtc = null;
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
            var clock = Mode.ClockPhase(nowUtc.ToLocalTime());
            if (clock == Phase.Stand)
            {
                Phase = Phase.Sit;          // you're presumably sitting; the clock says stand
                State = RunState.Due;
                PauseUntilUtc = null;
                return true;
            }
            Start(clock ?? startPhase, nowUtc);
            return false;
        }

        /// <summary>The user clicked the reminder: switch to the next phase and start its timer.</summary>
        public void Acknowledge(DateTime nowUtc)
        {
            if (State == RunState.Due) Start(NextPhase, nowUtc);
        }

        /// <summary>Hide the reminder and bring it back after <paramref name="delay"/> (phase unchanged).</summary>
        public void Snooze(TimeSpan delay, DateTime nowUtc)
        {
            State = RunState.Running;
            PauseUntilUtc = null;
            _runningSince = nowUtc;
            EndUtc = nowUtc + Scaled(delay);
        }

        /// <summary>Stay in the current phase for another full interval.</summary>
        public void Skip(DateTime nowUtc) => Start(Phase, nowUtc);

        public void Pause(DateTime nowUtc, TimeSpan? duration)
        {
            if (State == RunState.Running) _frozen = Max0(EndUtc - nowUtc);
            else if (State == RunState.Due) _frozen = TimeSpan.Zero; // reminder comes back right after the pause
            State = RunState.Paused;
            PauseUntilUtc = duration.HasValue ? nowUtc + duration.Value : (DateTime?)null;
        }

        public void Resume(DateTime nowUtc)
        {
            if (State != RunState.Paused) return;
            State = RunState.Running;
            PauseUntilUtc = null;
            _runningSince = nowUtc;
            EndUtc = nowUtc + _frozen;
        }

        /// <summary>Schedule settings changed: restart the current phase with the new mode.</summary>
        public void SetMode(IScheduleMode mode, DateTime nowUtc)
        {
            Mode = mode;
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
        };

        /// <summary>
        /// Continues from a snapshot. A running countdown keeps the time it had left (the seconds spent updating
        /// don't count), except clock-based modes, which keep their wall-clock end. Away resumes as running with the
        /// time that was frozen; if you're still away it's noticed again. Returns false if the snapshot is unreadable.
        /// </summary>
        public bool Restore(EngineSnapshot s, DateTime nowUtc)
        {
            if (s == null ||
                !Enum.TryParse(s.Phase, out Phase phase) ||
                !Enum.TryParse(s.State, out RunState state))
                return false;

            Mode.ImportState(s.ModeState);
            Phase = phase;
            PauseUntilUtc = null;
            _runningSince = nowUtc;
            var frozen = Max0(TimeSpan.FromMilliseconds(s.FrozenMs));
            var gap = Max0(nowUtc - s.SavedUtc);

            switch (state)
            {
                case RunState.Running:
                    State = RunState.Running;
                    EndUtc = Mode.IsClockBased ? s.EndUtc : s.EndUtc + gap;
                    break;
                case RunState.Away:
                    State = RunState.Running;
                    EndUtc = nowUtc + frozen;
                    break;
                case RunState.Paused:
                    State = RunState.Paused;
                    _frozen = frozen;
                    PauseUntilUtc = s.PauseUntilUtc;
                    break;
                case RunState.Due:
                    State = RunState.Due;   // the caller shows the reminder again
                    break;
                default:
                    return false;
            }
            return true;
        }

        // ---------------- time ----------------

        /// <summary>
        /// Advances the state machine. Returns true when the reminder just became due
        /// (the caller shows the popup for <see cref="NextPhase"/>).
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
                    if (lastInputTick != _awayLastInputTick || !PauseWhenAway)
                    {
                        // Back at the keyboard: continue with exactly the time that was left.
                        State = RunState.Running;
                        _runningSince = nowUtc;
                        EndUtc = nowUtc + _frozen;
                        return Update(nowUtc, idle, lastInputTick, busy);
                    }
                    return false;

                case RunState.Running:
                    bool away = PauseWhenAway && idle >= AwayThreshold;
                    if (away && !Mode.IsClockBased)
                    {
                        // Rewind: freeze as of the last input, so the idle minutes don't count
                        // (but never earlier than when this countdown started). Otherwise freeze now.
                        var freezeAt = AwayRewind ? nowUtc - idle : nowUtc;
                        if (freezeAt < _runningSince) freezeAt = _runningSince;
                        _frozen = Max0(EndUtc - freezeAt);
                        _awayLastInputTick = lastInputTick;
                        State = RunState.Away;
                        return false;
                    }
                    if (nowUtc < EndUtc) return false;
                    if (busy) return false;   // wait until the fullscreen app is gone
                    if (away) return false;   // clock mode: hold the reminder until the user is back
                    State = RunState.Due;
                    return true;

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
                default:
                    if (nowUtc >= EndUtc) return busy ? BusyRetry : AwayPoll; // deferred reminder
                    var d = EndUtc - nowUtc;
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
