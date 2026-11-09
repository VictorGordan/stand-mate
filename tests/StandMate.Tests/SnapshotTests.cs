using System;
using System.IO;
using StandMate.Core;
using Xunit;

namespace StandMate.Tests
{
    /// <summary>The timer handed from one version to the next during an in-app update.</summary>
    public class SnapshotTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 9, 9, 0, 0, DateTimeKind.Utc);
        private static TimeSpan Min(double m) => TimeSpan.FromMinutes(m);
        private static readonly Version V120 = new Version(1, 2, 0);

        private static ScheduleEngine Fixed() =>
            new ScheduleEngine(new FixedMode(45, 15)) { AwayThreshold = Min(5), PauseWhenAway = true };

        /// <summary>Through JSON, like the real handover file.</summary>
        private static EngineSnapshot RoundTrip(EngineSnapshot s)
        {
            string path = Path.Combine(Path.GetTempPath(), "standmate_handover_test.json");
            try
            {
                Json.Write(path, s);
                return Json.Read<EngineSnapshot>(path);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Seven_minutes_of_standing_left_stays_seven_minutes()
        {
            var old = Fixed();
            old.Start(Phase.Stand, T0);                       // 15 min stand
            var saved = T0.AddMinutes(8);                     // 7 min left
            var snap = RoundTrip(old.Snapshot(saved));

            var fresh = Fixed();
            var restartedAt = saved.AddSeconds(40);           // setup took 40 s
            Assert.True(fresh.Restore(snap, restartedAt));
            Assert.Equal(Phase.Stand, fresh.Phase);
            Assert.Equal(RunState.Running, fresh.State);
            Assert.Equal(Min(7), fresh.Remaining(restartedAt));
        }

        [Fact]
        public void An_overdue_reminder_waiting_for_fullscreen_is_still_overdue()
        {
            var old = Fixed();
            old.Start(Phase.Sit, T0);
            var snap = RoundTrip(old.Snapshot(T0.AddMinutes(50)));  // 5 min past due, held back
            var fresh = Fixed();
            var now = T0.AddMinutes(51);
            Assert.True(fresh.Restore(snap, now));
            Assert.True(fresh.Update(now, TimeSpan.Zero, 1, busy: false));
            Assert.Equal(Phase.Stand, fresh.NextPhase);
        }

        [Fact]
        public void Paused_stays_paused_with_its_time_and_end()
        {
            var old = Fixed();
            old.Start(Phase.Sit, T0);
            old.Pause(T0.AddMinutes(20), Min(60));           // 25 min left, paused until T0+80
            var snap = RoundTrip(old.Snapshot(T0.AddMinutes(30)));

            var fresh = Fixed();
            Assert.True(fresh.Restore(snap, T0.AddMinutes(31)));
            Assert.Equal(RunState.Paused, fresh.State);
            Assert.Equal(Min(25), fresh.Remaining(T0.AddMinutes(31)));
            Assert.Equal(T0.AddMinutes(80), fresh.PauseUntilUtc);
        }

        [Fact]
        public void Away_resumes_with_the_frozen_time()
        {
            var old = Fixed();
            old.Start(Phase.Sit, T0);
            old.Update(T0.AddMinutes(16), Min(6), 7, false);  // last input at T0+10 -> 35 min left, frozen
            Assert.Equal(RunState.Away, old.State);
            var snap = RoundTrip(old.Snapshot(T0.AddMinutes(16)));

            var fresh = Fixed();
            var now = T0.AddMinutes(17);
            Assert.True(fresh.Restore(snap, now));
            Assert.Equal(RunState.Running, fresh.State);
            Assert.Equal(Min(35), fresh.Remaining(now));
        }

        [Fact]
        public void A_showing_reminder_shows_again()
        {
            var old = Fixed();
            old.Start(Phase.Sit, T0);
            old.Update(T0.AddMinutes(45), TimeSpan.Zero, 1, false);
            var snap = RoundTrip(old.Snapshot(T0.AddMinutes(46)));

            var fresh = Fixed();
            Assert.True(fresh.Restore(snap, T0.AddMinutes(47)));
            Assert.Equal(RunState.Due, fresh.State);
            Assert.Equal(Phase.Stand, fresh.NextPhase);
        }

        [Fact]
        public void Clock_mode_keeps_its_wall_clock_end()
        {
            var old = new ScheduleEngine(new ClockMode(50, 10));
            old.Start(Phase.Sit, T0);
            var end = old.EndUtc;
            var snap = RoundTrip(old.Snapshot(T0.AddMinutes(5)));
            var fresh = new ScheduleEngine(new ClockMode(50, 10));
            Assert.True(fresh.Restore(snap, T0.AddMinutes(6)));
            Assert.Equal(end, fresh.EndUtc);
        }

        [Fact]
        public void Sequence_position_carries_over()
        {
            SequenceParser.TryParse("sit 30, stand 10, sit 50, stand 15", out var steps, out _);
            var old = new ScheduleEngine(new SequenceMode(steps));
            old.Start(Phase.Sit, T0);                        // sit 30
            old.Start(Phase.Stand, T0.AddMinutes(30));       // stand 10
            old.Start(Phase.Sit, T0.AddMinutes(40));         // sit 50 (third step)
            var snap = RoundTrip(old.Snapshot(T0.AddMinutes(45)));

            var fresh = new ScheduleEngine(new SequenceMode(steps));
            Assert.True(fresh.Restore(snap, T0.AddMinutes(45)));
            fresh.Start(Phase.Stand, T0.AddMinutes(90));
            Assert.Equal(T0.AddMinutes(105), fresh.EndUtc); // stand 15, the step after sit 50
        }

        [Fact]
        public void Progressive_progress_carries_over()
        {
            var day = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Local);
            var a = new ProgressiveMode(45, 5, 5, 20);
            a.NextDuration(Phase.Stand, day.AddHours(9));
            a.NextDuration(Phase.Stand, day.AddHours(10));   // next would be 15
            var b = new ProgressiveMode(45, 5, 5, 20);
            b.ImportState(a.ExportState());
            Assert.Equal(Min(15), b.NextDuration(Phase.Stand, day.AddHours(11)));
            b.ImportState("garbage");                        // ignored
            Assert.Equal(Min(20), b.NextDuration(Phase.Stand, day.AddHours(12)));
        }

        [Fact]
        public void Garbage_snapshots_are_refused()
        {
            Assert.False(Fixed().Restore(null, T0));
            Assert.False(Fixed().Restore(new EngineSnapshot { Phase = "Sit", State = "Exploded", SavedUtc = T0 }, T0));
        }

        [Theory]
        [InlineData("1.2.0", "fixed", 3, true)]
        [InlineData("1.1.1", "fixed", 3, true)]      // minor/patch update: keep the timer
        [InlineData("0.9.0", "fixed", 3, false)]     // major update: fresh start
        [InlineData("1.2.0", "random", 3, false)]    // another mode
        [InlineData("1.2.0", "fixed", 11, false)]    // stale (update failed, PC was off...)
        [InlineData("junk", "fixed", 3, false)]
        public void Handover_rules(string savedVersion, string savedMode, int minutesAgo, bool usable)
        {
            var snap = new EngineSnapshot { AppVersion = savedVersion, ModeId = savedMode, SavedUtc = T0 };
            Assert.Equal(usable, Handover.IsUsable(snap, V120, "fixed", T0.AddMinutes(minutesAgo)));
            Assert.False(Handover.IsUsable(null, V120, "fixed", T0));
        }
    }
}
