using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace StandMate.Core
{
    /// <summary>A strategy for how long each sit/stand phase lasts.</summary>
    public interface IScheduleMode
    {
        /// <summary>
        /// Called once each time a phase begins. Returns how long it lasts.
        /// Stateful modes (sequence, progressive) advance here.
        /// </summary>
        TimeSpan NextDuration(Phase phase, DateTime nowLocal);

        /// <summary>Clock-based modes follow wall-clock times, so their countdown can't be paused.</summary>
        bool IsClockBased { get; }

        /// <summary>Start over from the beginning (used at startup and after sleep/hibernate).</summary>
        void Reset();

        /// <summary>For clock-based modes: the phase the clock says you should be in now; otherwise null.</summary>
        Phase? ClockPhase(DateTime nowLocal);

        /// <summary>Where a stateful mode is in its cycle (kept across in-place updates); null when stateless.</summary>
        string ExportState();

        /// <summary>Restores <see cref="ExportState"/> output. Ignores anything it can't read.</summary>
        void ImportState(string state);
    }

    public sealed class ModeInfo
    {
        public string Id, Name, Description;

        public static readonly ModeInfo[] All =
        {
            new ModeInfo { Id = ModeIds.Fixed, Name = "Fixed", Description = "Same sit and stand lengths, on repeat." },
            new ModeInfo { Id = ModeIds.Sequence, Name = "Sequence", Description = "Your own loop of sit and stand blocks." },
            new ModeInfo { Id = ModeIds.Clock, Name = "On the clock", Description = "Stand at the same minute every hour." },
            new ModeInfo { Id = ModeIds.Progressive, Name = "Progressive", Description = "Standing time grows each cycle; resets every day." },
            new ModeInfo { Id = ModeIds.Random, Name = "Random", Description = "Unpredictable lengths within your ranges." },
        };

        public static ModeInfo Get(string id) => Array.Find(All, m => m.Id == id) ?? All[0];
    }

    public static class ModeFactory
    {
        public static IScheduleMode Create(Settings s, Random rng = null)
        {
            switch (s.Mode)
            {
                case ModeIds.Sequence:
                    return SequenceParser.TryParse(s.Sequence, out var steps, out _)
                        ? new SequenceMode(steps)
                        : (IScheduleMode)new FixedMode(s.FixedSit, s.FixedStand);
                case ModeIds.Clock: return new ClockMode(s.ClockStandAt, s.ClockStandFor);
                case ModeIds.Progressive: return new ProgressiveMode(s.ProgSit, s.ProgStandStart, s.ProgStandStep, s.ProgStandMax);
                case ModeIds.Random: return new RandomMode(s.RandSitMin, s.RandSitMax, s.RandStandMin, s.RandStandMax, rng);
                default: return new FixedMode(s.FixedSit, s.FixedStand);
            }
        }
    }

    public sealed class FixedMode : IScheduleMode
    {
        private readonly int _sit, _stand;
        public FixedMode(int sitMinutes, int standMinutes) { _sit = sitMinutes; _stand = standMinutes; }
        public bool IsClockBased => false;
        public void Reset() { }
        public Phase? ClockPhase(DateTime nowLocal) => null;
        public string ExportState() => null;
        public void ImportState(string state) { }
        public TimeSpan NextDuration(Phase phase, DateTime nowLocal) =>
            TimeSpan.FromMinutes(phase == Phase.Sit ? _sit : _stand);
    }

    public struct SequenceStep
    {
        public Phase Phase;
        public int Minutes;
        public SequenceStep(Phase phase, int minutes) { Phase = phase; Minutes = minutes; }
    }

    public static class SequenceParser
    {
        private static readonly Regex StepRx = new Regex(
            @"^\s*(sit|stand)\s*(\d{1,3})\s*(m|min|mins|minutes?)?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Parses e.g. "sit 30, stand 10, sit 45, stand 15". Phases must alternate (including the wrap-around).</summary>
        public static bool TryParse(string text, out List<SequenceStep> steps, out string error)
        {
            steps = new List<SequenceStep>();
            error = null;
            if (string.IsNullOrWhiteSpace(text)) { error = "Enter steps like: sit 30, stand 10"; return false; }

            foreach (var raw in text.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (raw.Trim().Length == 0) continue;
                var m = StepRx.Match(raw);
                if (!m.Success) { error = $"Can't read \"{raw.Trim()}\". Use sit N or stand N."; return false; }
                int minutes = int.Parse(m.Groups[2].Value);
                if (minutes < 1 || minutes > 600) { error = "Each step must be 1–600 minutes."; return false; }
                var phase = m.Groups[1].Value.ToLowerInvariant() == "sit" ? Phase.Sit : Phase.Stand;
                steps.Add(new SequenceStep(phase, minutes));
            }

            if (steps.Count < 2) { error = "Add at least one sit and one stand step."; return false; }
            for (int i = 0; i < steps.Count; i++)
            {
                if (steps[i].Phase == steps[(i + 1) % steps.Count].Phase)
                {
                    error = "Sit and stand must alternate (the loop wraps around too).";
                    return false;
                }
            }
            return true;
        }
    }

    public sealed class SequenceMode : IScheduleMode
    {
        private readonly List<SequenceStep> _steps;
        private int _index = -1;

        public SequenceMode(List<SequenceStep> steps) { _steps = steps; }
        public bool IsClockBased => false;
        public void Reset() => _index = -1;
        public Phase? ClockPhase(DateTime nowLocal) => null;
        public string ExportState() => _index.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public void ImportState(string state)
        {
            if (int.TryParse(state, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int i) &&
                i >= -1 && i < _steps.Count)
                _index = i;
        }

        public TimeSpan NextDuration(Phase phase, DateTime nowLocal)
        {
            for (int k = 1; k <= _steps.Count; k++)
            {
                int i = (_index + k) % _steps.Count;
                if (_steps[i].Phase == phase)
                {
                    _index = i;
                    return TimeSpan.FromMinutes(_steps[i].Minutes);
                }
            }
            return TimeSpan.FromMinutes(30); // unreachable for a validated sequence
        }
    }

    /// <summary>Stand at :StandAt every hour for StandFor minutes; sit the rest of the hour.</summary>
    public sealed class ClockMode : IScheduleMode
    {
        private static readonly TimeSpan MinSit = TimeSpan.FromMinutes(5);
        private readonly int _standAt, _standFor;

        public ClockMode(int standAtMinute, int standForMinutes) { _standAt = standAtMinute; _standFor = standForMinutes; }
        public bool IsClockBased => true;
        public void Reset() { }
        public string ExportState() => null;
        public void ImportState(string state) { }

        /// <summary>Stand inside the window [:StandAt, :StandAt + StandFor), sit otherwise.</summary>
        public Phase? ClockPhase(DateTime nowLocal) =>
            (nowLocal.Minute - _standAt + 60) % 60 < _standFor ? Phase.Stand : Phase.Sit;

        public TimeSpan NextDuration(Phase phase, DateTime nowLocal)
        {
            if (phase == Phase.Sit)
            {
                // Sit until the next :StandAt. Skip a slot that is too close (e.g. you sat down late).
                var end = NextAtMinute(nowLocal, _standAt);
                if (end - nowLocal < MinSit) end = end.AddHours(1);
                return end - nowLocal;
            }
            else
            {
                // Stand until the window closes, but never longer than StandFor (e.g. you stood up late).
                var end = NextAtMinute(nowLocal, (_standAt + _standFor) % 60);
                var d = end - nowLocal;
                var max = TimeSpan.FromMinutes(_standFor);
                return d > max ? max : d;
            }
        }

        /// <summary>The next moment strictly after <paramref name="now"/> whose minute is <paramref name="minute"/> (seconds = 0).</summary>
        public static DateTime NextAtMinute(DateTime now, int minute)
        {
            var hourStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Kind);
            var t = hourStart.AddMinutes(minute);
            return t > now ? t : t.AddHours(1);
        }
    }

    /// <summary>Sit is fixed; stand starts short and grows by Step each cycle up to Max. Resets each day.</summary>
    public sealed class ProgressiveMode : IScheduleMode
    {
        private readonly int _sit, _start, _step, _max;
        private DateTime _day = DateTime.MinValue;
        private int _cycle;

        public ProgressiveMode(int sit, int standStart, int standStep, int standMax)
        {
            _sit = sit; _start = standStart; _step = standStep; _max = Math.Max(standStart, standMax);
        }

        public bool IsClockBased => false;
        public void Reset() { } // keeps today's progress; it starts over each day anyway
        public Phase? ClockPhase(DateTime nowLocal) => null;

        public string ExportState() =>
            _day.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";" +
            _cycle.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public void ImportState(string state)
        {
            var parts = state?.Split(';');
            if (parts == null || parts.Length != 2) return;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (long.TryParse(parts[0], System.Globalization.NumberStyles.Integer, inv, out long ticks) &&
                int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, inv, out int cycle) &&
                ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks && cycle >= 0)
            {
                _day = new DateTime(ticks);
                _cycle = cycle;
            }
        }

        public TimeSpan NextDuration(Phase phase, DateTime nowLocal)
        {
            if (phase == Phase.Sit) return TimeSpan.FromMinutes(_sit);
            if (nowLocal.Date != _day) { _day = nowLocal.Date; _cycle = 0; }
            int minutes = Math.Min(_max, _start + _step * _cycle);
            _cycle++;
            return TimeSpan.FromMinutes(minutes);
        }
    }

    public sealed class RandomMode : IScheduleMode
    {
        private readonly int _sitMin, _sitMax, _standMin, _standMax;
        private readonly Random _rng;

        public RandomMode(int sitMin, int sitMax, int standMin, int standMax, Random rng = null)
        {
            _sitMin = sitMin; _sitMax = Math.Max(sitMin, sitMax);
            _standMin = standMin; _standMax = Math.Max(standMin, standMax);
            _rng = rng ?? new Random();
        }

        public bool IsClockBased => false;
        public void Reset() { }
        public Phase? ClockPhase(DateTime nowLocal) => null;
        public string ExportState() => null;
        public void ImportState(string state) { }

        public TimeSpan NextDuration(Phase phase, DateTime nowLocal) => phase == Phase.Sit
            ? TimeSpan.FromMinutes(_rng.Next(_sitMin, _sitMax + 1))
            : TimeSpan.FromMinutes(_rng.Next(_standMin, _standMax + 1));
    }
}
