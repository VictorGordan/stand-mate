using System;
using StandMate.Core;
using Xunit;

namespace StandMate.Tests
{
    public class EngineTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 9, 9, 0, 0, DateTimeKind.Utc);
        private static TimeSpan Min(double m) => TimeSpan.FromMinutes(m);

        private static ScheduleEngine NewEngine(out DateTime now)
        {
            now = T0;
            var e = new ScheduleEngine(new FixedMode(45, 15)) { AwayThreshold = Min(5), PauseWhenAway = true };
            e.Start(Phase.Sit, now);
            return e;
        }

        [Fact]
        public void Becomes_due_when_the_phase_ends_and_waits_for_the_click()
        {
            var e = NewEngine(out var now);
            Assert.False(e.Update(now.AddMinutes(44), Min(0), 1, busy: false));
            Assert.True(e.Update(now.AddMinutes(45), Min(0), 2, busy: false));
            Assert.Equal(RunState.Due, e.State);
            Assert.Equal(Phase.Stand, e.NextPhase);
            Assert.Null(e.NextCheck(now.AddMinutes(45), Min(0), false)); // no timer while waiting for the click

            // The next phase only starts on the click, even if that's 7 minutes later.
            var click = now.AddMinutes(52);
            e.Acknowledge(click);
            Assert.Equal(Phase.Stand, e.Phase);
            Assert.Equal(RunState.Running, e.State);
            Assert.Equal(click.AddMinutes(15), e.EndUtc);
        }

        [Fact]
        public void Snooze_keeps_the_phase_and_comes_back()
        {
            var e = NewEngine(out var now);
            e.Update(now.AddMinutes(45), Min(0), 1, false);
            e.Snooze(Min(5), now.AddMinutes(45));
            Assert.Equal(Phase.Sit, e.Phase);
            Assert.False(e.Update(now.AddMinutes(49), Min(0), 2, false));
            Assert.True(e.Update(now.AddMinutes(50), Min(0), 3, false));
        }

        [Fact]
        public void Skip_restarts_the_current_phase()
        {
            var e = NewEngine(out var now);
            e.Update(now.AddMinutes(45), Min(0), 1, false);
            e.Skip(now.AddMinutes(45));
            Assert.Equal(Phase.Sit, e.Phase);
            Assert.Equal(now.AddMinutes(90), e.EndUtc);
        }

        [Fact]
        public void Fullscreen_defers_the_reminder()
        {
            var e = NewEngine(out var now);
            Assert.False(e.Update(now.AddMinutes(46), Min(0), 1, busy: true));
            Assert.Equal(RunState.Running, e.State);
            Assert.True(e.Update(now.AddMinutes(47), Min(0), 1, busy: false));
        }

        [Fact]
        public void Idle_pauses_from_the_last_input_and_resumes_with_the_same_time_left()
        {
            var e = NewEngine(out var now);
            // Last input at 9:10 (35 min left). At 9:16 we've been idle 6 min -> Away, frozen at 35 min.
            uint lastTick = 1000;
            Assert.False(e.Update(now.AddMinutes(16), Min(6), lastTick, false));
            Assert.Equal(RunState.Away, e.State);
            Assert.Equal(Min(35), e.Remaining(now.AddMinutes(16)));

            // Still away an hour later (same input tick): nothing fires, time stays frozen.
            Assert.False(e.Update(now.AddMinutes(70), Min(60), lastTick, false));
            Assert.Equal(RunState.Away, e.State);
            Assert.Equal(Min(35), e.Remaining(now.AddMinutes(70)));

            // New input -> continue with exactly 35 min left.
            var back = now.AddMinutes(75);
            Assert.False(e.Update(back, TimeSpan.Zero, lastTick + 5, false));
            Assert.Equal(RunState.Running, e.State);
            Assert.Equal(back.AddMinutes(35), e.EndUtc);
        }

        [Fact]
        public void Idle_before_the_phase_started_is_not_added_to_the_remaining_time()
        {
            var e = NewEngine(out var now);
            // Phase started at 9:00, but the last input was at 8:58 (e.g. started from a timer).
            // At 9:04 idle = 6 min -> away; only the 45 min phase may remain, not 47.
            Assert.False(e.Update(now.AddMinutes(4), Min(6), 1, false));
            Assert.Equal(RunState.Away, e.State);
            Assert.Equal(Min(45), e.Remaining(now.AddMinutes(4)));
        }

        [Fact]
        public void Repeated_away_cycles_do_not_inflate_the_remaining_time()
        {
            var e = NewEngine(out var now);
            Assert.False(e.Update(now.AddMinutes(16), Min(6), 1, false));        // away, 35 left (input at 9:10)
            Assert.False(e.Update(now.AddMinutes(30), TimeSpan.FromSeconds(2), 2, false)); // input at 9:29:58 -> resumed at 9:30
            Assert.Equal(Min(35), e.Remaining(now.AddMinutes(30)));
            // Away again without new input: freeze from the resume moment, not the older input.
            Assert.False(e.Update(now.AddMinutes(36), Min(6).Add(TimeSpan.FromSeconds(2)), 2, false));
            Assert.Equal(RunState.Away, e.State);
            Assert.Equal(Min(35), e.Remaining(now.AddMinutes(36)));
        }

        [Fact]
        public void Plain_pause_freezes_when_noticed_so_idle_minutes_count()
        {
            var e = NewEngine(out var now);
            e.AwayRewind = false;
            // Last input 9:10; noticed at 9:15 (5 min idle) -> 30 min left, not 35.
            Assert.False(e.Update(now.AddMinutes(15), Min(5), 1, false));
            Assert.Equal(RunState.Away, e.State);
            Assert.Equal(Min(30), e.Remaining(now.AddMinutes(15)));
        }

        [Fact]
        public void Away_threshold_is_configurable()
        {
            var e = NewEngine(out var now);
            e.AwayThreshold = Min(15);
            Assert.False(e.Update(now.AddMinutes(20), Min(10), 1, false));
            Assert.Equal(RunState.Running, e.State);
            Assert.False(e.Update(now.AddMinutes(25), Min(15), 1, false));
            Assert.Equal(RunState.Away, e.State);
        }

        [Theory]
        [InlineData(Phase.Sit, 45)]
        [InlineData(Phase.Stand, 15)]
        public void Fresh_start_begins_with_the_chosen_phase(Phase start, int minutes)
        {
            var e = NewEngine(out var now);
            e.Update(now.AddMinutes(45), Min(0), 1, false); // a reminder was up when the PC went to sleep
            var wake = now.AddHours(3);
            Assert.False(e.StartFresh(start, wake));
            Assert.Equal(RunState.Running, e.State);
            Assert.Equal(start, e.Phase);
            Assert.Equal(wake.AddMinutes(minutes), e.EndUtc);
        }

        [Fact]
        public void Fresh_start_counts_from_the_wake_even_if_the_user_is_not_back_yet()
        {
            var e = NewEngine(out var now);
            var wake = now.AddHours(3);
            e.StartFresh(Phase.Sit, wake);
            // Woke by itself; no input for 3 h. Freezes with the whole fresh phase left.
            Assert.False(e.Update(wake.AddSeconds(2), Min(180), 9, false));
            Assert.Equal(RunState.Away, e.State);
            Assert.Equal(Min(45), e.Remaining(wake.AddSeconds(2)));
        }

        [Fact]
        public void Fresh_start_restarts_a_sequence_from_the_top()
        {
            SequenceParser.TryParse("sit 30, stand 10, sit 50, stand 15", out var steps, out _);
            var e = new ScheduleEngine(new SequenceMode(steps));
            e.StartFresh(Phase.Sit, T0);                      // sit 30
            e.Update(T0.AddMinutes(30), Min(0), 1, false); e.Acknowledge(T0.AddMinutes(30)); // stand 10
            e.Update(T0.AddMinutes(40), Min(0), 2, false); e.Acknowledge(T0.AddMinutes(40)); // sit 50
            Assert.Equal(T0.AddMinutes(90), e.EndUtc);
            e.StartFresh(Phase.Sit, T0.AddHours(5));
            Assert.Equal(T0.AddHours(5).AddMinutes(30), e.EndUtc);   // back to the first step
        }

        [Fact]
        public void Fresh_start_in_clock_mode_follows_the_clock()
        {
            var local = new DateTime(2026, 11, 9, 10, 52, 0, DateTimeKind.Local);
            var e = new ScheduleEngine(new ClockMode(50, 10));
            // 10:52 is inside the :50-:00 stand window -> stand reminder right away, whatever the start phase.
            Assert.True(e.StartFresh(Phase.Sit, local.ToUniversalTime()));
            Assert.Equal(RunState.Due, e.State);
            Assert.Equal(Phase.Stand, e.NextPhase);
            // 11:05 is outside -> sit until 11:50, even when "start with standing" is chosen.
            var later = local.AddMinutes(13);
            Assert.False(e.StartFresh(Phase.Stand, later.ToUniversalTime()));
            Assert.Equal(Phase.Sit, e.Phase);
            Assert.Equal(later.AddMinutes(45).ToUniversalTime(), e.EndUtc);
        }

        [Fact]
        public void Idle_pause_can_be_turned_off()
        {
            var e = NewEngine(out var now);
            e.PauseWhenAway = false;
            Assert.False(e.Update(now.AddMinutes(30), Min(20), 1, false));
            Assert.Equal(RunState.Running, e.State);
            Assert.True(e.Update(now.AddMinutes(45), Min(35), 1, false));
        }

        [Fact]
        public void Sleeping_through_the_end_of_a_phase_keeps_the_time_that_was_left()
        {
            var e = NewEngine(out var now);
            // Last input at 9:40 (5 min left), PC slept, woke at 11:00: idle = 80 min.
            Assert.False(e.Update(now.AddMinutes(120), Min(80), 7, false));
            Assert.Equal(RunState.Away, e.State);
            Assert.Equal(Min(5), e.Remaining(now.AddMinutes(120)));
        }

        [Fact]
        public void Clock_mode_holds_the_reminder_until_the_user_is_back()
        {
            var e = new ScheduleEngine(new FixedModeClockStub()) { AwayThreshold = Min(5) };
            var now = T0;
            e.Start(Phase.Sit, now);
            Assert.False(e.Update(now.AddMinutes(30), Min(10), 1, false));   // idle but clock-based: no freeze
            Assert.Equal(RunState.Running, e.State);
            Assert.False(e.Update(now.AddMinutes(61), Min(41), 1, false));   // due, but user away
            Assert.True(e.Update(now.AddMinutes(62), Min(0), 2, false));     // back -> show
        }

        [Fact]
        public void Pause_and_resume_keep_the_remaining_time()
        {
            var e = NewEngine(out var now);
            e.Pause(now.AddMinutes(10), null);
            Assert.Equal(RunState.Paused, e.State);
            Assert.Null(e.NextCheck(now.AddMinutes(10), Min(0), false));
            e.Resume(now.AddMinutes(100));
            Assert.Equal(now.AddMinutes(135), e.EndUtc);
        }

        [Fact]
        public void Timed_pause_resumes_by_itself()
        {
            var e = NewEngine(out var now);
            e.Pause(now.AddMinutes(10), Min(30));
            Assert.False(e.Update(now.AddMinutes(39), Min(0), 1, false));
            Assert.Equal(RunState.Paused, e.State);
            Assert.False(e.Update(now.AddMinutes(40), Min(0), 2, false));
            Assert.Equal(RunState.Running, e.State);
            Assert.Equal(now.AddMinutes(75), e.EndUtc);
        }

        [Fact]
        public void Changing_the_schedule_restarts_the_current_phase()
        {
            var e = NewEngine(out var now);
            e.SetMode(new FixedMode(30, 10), now.AddMinutes(20));
            Assert.Equal(Phase.Sit, e.Phase);
            Assert.Equal(now.AddMinutes(50), e.EndUtc);
        }

        [Fact]
        public void Next_check_wakes_rarely_while_active()
        {
            var e = NewEngine(out var now);
            // 45 min to go, user active (idle 0): wake again when the away threshold could be reached (5 min), capped at 60 s.
            Assert.Equal(TimeSpan.FromSeconds(60), e.NextCheck(now, Min(0), false));
            // Away polling is light (every 2 s).
            e.Update(now.AddMinutes(6), Min(6), 1, false);
            Assert.Equal(TimeSpan.FromSeconds(2), e.NextCheck(now.AddMinutes(6), Min(6), false));
        }

        [Fact]
        public void Fast_mode_scales_minutes_to_seconds()
        {
            var e = new ScheduleEngine(new FixedMode(45, 15), 1.0 / 60);
            e.Start(Phase.Sit, T0);
            Assert.Equal(T0.AddSeconds(45), e.EndUtc);
        }

        /// <summary>A clock-based mode with a fixed 60-minute sit, for engine tests independent of time zones.</summary>
        private sealed class FixedModeClockStub : IScheduleMode
        {
            public bool IsClockBased => true;
            public void Reset() { }
            public Phase? ClockPhase(DateTime nowLocal) => Phase.Sit;
            public string ExportState() => null;
            public void ImportState(string state) { }
            public TimeSpan NextDuration(Phase phase, DateTime nowLocal) => TimeSpan.FromMinutes(60);
        }
    }
}
