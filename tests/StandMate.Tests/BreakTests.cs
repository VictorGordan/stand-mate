using System;
using System.IO;
using StandMate.Core;
using Xunit;

namespace StandMate.Tests
{
    public class BreakTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 9, 9, 0, 0, DateTimeKind.Utc);
        private static TimeSpan Min(double m) => TimeSpan.FromMinutes(m);
        private static DateTime At(double minutes) => T0.AddMinutes(minutes);

        private static BreakRule EveryMinutes(int minutes, int length = 5) => new BreakRule(BreakTrigger.Minutes, minutes, 2, length);
        private static BreakRule EverySwitches(int n, int length = 5) => new BreakRule(BreakTrigger.Switches, 60, n, length);

        /// <summary>Fixed 45 sit / 15 stand, starting with sitting at T0.</summary>
        private static ScheduleEngine Engine(BreakRule rule)
        {
            var e = new ScheduleEngine(new FixedMode(45, 15)) { AwayThreshold = Min(5), PauseWhenAway = true };
            e.SetBreaks(rule, T0);
            e.StartFresh(Phase.Sit, T0);
            return e;
        }

        private static uint _tick = 1;
        private static bool Tick(ScheduleEngine e, double minutes, double idle = 0) => e.Update(At(minutes), Min(idle), _tick++, false);

        [Fact]
        public void Breaks_are_off_by_default()
        {
            var s = new Settings();
            Assert.False(s.Breaks);
            Assert.Equal(BreakTrigger.Off, BreakRule.From(s).Trigger);

            var e = new ScheduleEngine(new FixedMode(45, 15));
            e.SetBreaks(BreakRule.From(s), T0);
            e.StartFresh(Phase.Sit, T0);
            for (int i = 0; i < 10; i++)
            {
                var end = e.EndUtc;
                Assert.True(e.Update(end, TimeSpan.Zero, _tick++, false));
                Assert.Equal(Reminder.Switch, e.Reminder);
                e.Acknowledge(end);
            }
            Assert.Null(e.BreakIn(T0));
            Assert.Null(e.SwitchesBeforeBreak);
        }

        [Fact]
        public void Every_2nd_switch_becomes_a_break_and_you_switch_once_it_is_over()
        {
            var e = Engine(EverySwitches(2));
            Assert.Equal(1, e.SwitchesBeforeBreak);

            Assert.True(Tick(e, 45));                                 // 1st switch: a normal reminder
            Assert.Equal(Reminder.Switch, e.Reminder);
            e.Acknowledge(At(45));                                    // standing until 60
            Assert.Equal(0, e.SwitchesBeforeBreak);

            Assert.True(Tick(e, 60));                                 // 2nd switch: a break instead
            Assert.Equal(RunState.Break, e.State);
            Assert.Equal(Reminder.BreakStart, e.Reminder);
            Assert.Equal(Phase.Stand, e.Phase);
            Assert.Equal(Phase.Sit, e.PhaseAfterBreak);
            Assert.Equal(At(65), e.EndUtc);

            // No input during the break is expected: it neither pauses nor ends it early.
            Assert.False(Tick(e, 64, idle: 4));
            Assert.Equal(RunState.Break, e.State);
            Assert.True(Tick(e, 65, idle: 5));
            Assert.Equal(RunState.Due, e.State);
            Assert.Equal(Reminder.BreakEnd, e.Reminder);
            Assert.Null(e.NextCheck(At(65), Min(5), false));         // waits for the click

            e.Acknowledge(At(67));                                    // back 2 minutes later: the switch happens now
            Assert.Equal(RunState.Running, e.State);
            Assert.Equal(Phase.Sit, e.Phase);
            Assert.Equal(At(112), e.EndUtc);
            Assert.Equal(1, e.SwitchesBeforeBreak);

            Assert.True(Tick(e, 112));
            Assert.Equal(Reminder.Switch, e.Reminder);
            e.Acknowledge(At(112));
            Assert.True(Tick(e, 127));
            Assert.Equal(RunState.Break, e.State);
        }

        [Fact]
        public void Every_switch_can_be_a_break()
        {
            var e = Engine(EverySwitches(1));
            Assert.Equal(0, e.SwitchesBeforeBreak);
            Assert.True(Tick(e, 45));
            Assert.Equal(RunState.Break, e.State);
            Assert.True(Tick(e, 50));
            e.Acknowledge(At(50));
            Assert.Equal(Phase.Stand, e.Phase);
            Assert.True(Tick(e, 65));
            Assert.Equal(RunState.Break, e.State);
            Assert.Equal(Phase.Sit, e.PhaseAfterBreak);
        }

        [Fact]
        public void A_time_based_break_interrupts_the_phase_which_then_carries_on()
        {
            var e = Engine(EveryMinutes(30));
            Assert.Equal(Min(30), e.BreakIn(T0));
            Assert.False(Tick(e, 29));
            Assert.True(Tick(e, 30));
            Assert.Equal(RunState.Break, e.State);
            Assert.Equal(Phase.Sit, e.PhaseAfterBreak);               // 15 min of sitting are still owed
            Assert.Null(e.BreakIn(At(30)));

            Assert.True(Tick(e, 35));
            Assert.Equal(Reminder.BreakEnd, e.Reminder);
            e.Acknowledge(At(36));
            Assert.Equal(Phase.Sit, e.Phase);
            Assert.Equal(At(51), e.EndUtc);
            Assert.Equal(Min(30), e.BreakIn(At(36)));
        }

        [Fact]
        public void A_break_due_shortly_after_a_switch_takes_its_place()
        {
            var e = Engine(EveryMinutes(50));                         // due at 50, the sit ends at 45
            Assert.False(Tick(e, 44));
            Assert.True(Tick(e, 45));
            Assert.Equal(RunState.Break, e.State);
            Assert.Equal(Phase.Stand, e.PhaseAfterBreak);
            Assert.True(Tick(e, 50));
            e.Acknowledge(At(50));
            Assert.Equal(Phase.Stand, e.Phase);
            Assert.Equal(At(65), e.EndUtc);
            Assert.Equal(Min(50), e.BreakIn(At(50)));
        }

        [Fact]
        public void A_break_due_shortly_before_a_switch_waits_for_it()
        {
            var e = Engine(EveryMinutes(40));                         // due at 40, the sit ends at 45
            Assert.False(Tick(e, 40));
            Assert.Equal(RunState.Running, e.State);
            Assert.True(Tick(e, 45));
            Assert.Equal(RunState.Break, e.State);
            Assert.Equal(Phase.Stand, e.PhaseAfterBreak);
        }

        [Fact]
        public void Time_away_and_pauses_do_not_count_towards_a_time_based_break()
        {
            var e = Engine(EveryMinutes(30, length: 10));
            // Last input at 9:10, noticed at 9:16: away, and the break clock rewinds to 9:10 (20 min left).
            Assert.False(Tick(e, 16, idle: 6));
            Assert.Equal(RunState.Away, e.State);
            Assert.Equal(Min(20), e.BreakIn(At(16)));
            Assert.False(e.Update(At(18), Min(8), _tick - 1, false));   // still away, shorter than a break
            Assert.Equal(Min(20), e.BreakIn(At(18)));
            Assert.False(Tick(e, 19));                                 // back
            Assert.Equal(Min(20), e.BreakIn(At(19)));

            e.Pause(At(25), null);
            Assert.Equal(Min(14), e.BreakIn(At(90)));
            e.Resume(At(90));
            Assert.Equal(Min(14), e.BreakIn(At(90)));
            Assert.Equal(Min(4), e.BreakIn(At(100)));
        }

        [Fact]
        public void Being_away_for_a_breaks_length_counts_as_the_break()
        {
            var e = Engine(EveryMinutes(30, length: 5));
            Assert.False(Tick(e, 20, idle: 8));                        // away since 9:12: long enough
            Assert.Equal(RunState.Away, e.State);
            Assert.False(Tick(e, 40));                                  // back at 9:40
            Assert.Equal(Min(30), e.BreakIn(At(40)));

            var s = Engine(EverySwitches(2));
            Tick(s, 45); s.Acknowledge(At(45));
            Assert.Equal(0, s.SwitchesBeforeBreak);
            Assert.False(s.Update(At(50), Min(6), _tick++, false));    // away for 6 min
            Assert.Equal(1, s.SwitchesBeforeBreak);
        }

        [Fact]
        public void Idle_time_never_counts_as_a_break_when_away_detection_is_off()
        {
            var e = Engine(EveryMinutes(30));
            e.PauseWhenAway = false;
            Assert.False(Tick(e, 20, idle: 15));
            Assert.Equal(Min(10), e.BreakIn(At(20)));
            Assert.True(Tick(e, 30, idle: 25));
            Assert.Equal(RunState.Break, e.State);
        }

        [Fact]
        public void Snoozing_the_break_end_makes_the_break_longer()
        {
            var e = Engine(EverySwitches(1));
            Tick(e, 45);
            Assert.True(Tick(e, 50));
            e.Snooze(Min(5), At(51));
            Assert.Equal(RunState.Break, e.State);
            Assert.Equal(At(56), e.EndUtc);
            Assert.True(e.OnBreak);
            Assert.False(Tick(e, 55));
            Assert.True(Tick(e, 56));
            Assert.Equal(Reminder.BreakEnd, e.Reminder);
            e.Acknowledge(At(56));
            Assert.Equal(Phase.Stand, e.Phase);
        }

        [Fact]
        public void Snoozing_a_break_that_just_started_brings_it_back()
        {
            var e = Engine(EveryMinutes(30));
            Assert.True(Tick(e, 30));
            e.Snooze(Min(5), At(30));
            Assert.Equal(RunState.Running, e.State);
            Assert.Equal(Phase.Sit, e.Phase);
            Assert.Equal(At(45), e.EndUtc);                            // the sit carries on
            Assert.False(Tick(e, 34));
            Assert.True(Tick(e, 35));
            Assert.Equal(RunState.Break, e.State);

            // A break that replaced a switch: the switch waits for it.
            var s = Engine(EverySwitches(1));
            Assert.True(Tick(s, 45));
            s.Snooze(Min(15), At(45));
            Assert.Equal(At(60), s.EndUtc);
            Assert.True(Tick(s, 60));
            Assert.Equal(RunState.Break, s.State);
            Assert.Equal(Phase.Stand, s.PhaseAfterBreak);
        }

        [Fact]
        public void Skipping_a_break_that_replaced_a_switch_switches_now()
        {
            var e = Engine(EverySwitches(2));
            Tick(e, 45); e.Acknowledge(At(45));
            Assert.True(Tick(e, 60));
            e.Skip(At(60));
            Assert.Equal(RunState.Running, e.State);
            Assert.Equal(Phase.Sit, e.Phase);
            Assert.Equal(At(105), e.EndUtc);
            Assert.Equal(1, e.SwitchesBeforeBreak);                     // counted like a taken break
        }

        [Fact]
        public void Skipping_a_break_mid_phase_carries_on_with_the_phase()
        {
            var e = Engine(EveryMinutes(30));
            Assert.True(Tick(e, 30));
            e.Skip(At(30));
            Assert.Equal(RunState.Running, e.State);
            Assert.Equal(At(45), e.EndUtc);
            Assert.Equal(Min(30), e.BreakIn(At(30)));
        }

        [Fact]
        public void A_break_can_be_taken_and_ended_early_from_the_tray()
        {
            var e = Engine(EveryMinutes(60, length: 10));
            Assert.True(e.TakeBreak(At(20)));
            Assert.Equal(RunState.Break, e.State);
            Assert.Equal(At(30), e.EndUtc);
            Assert.False(e.TakeBreak(At(21)));                         // already on one
            e.EndBreak(At(24));
            Assert.Equal(RunState.Running, e.State);
            Assert.Equal(At(49), e.EndUtc);                            // 25 min of sitting were left
            Assert.Equal(Min(60), e.BreakIn(At(24)));

            e.Pause(At(30), null);
            Assert.False(e.TakeBreak(At(31)));

            // While a switch reminder is up, the break takes the switch's place.
            var d = Engine(EveryMinutes(60));
            Assert.True(Tick(d, 45));
            Assert.True(d.TakeBreak(At(46)));
            Assert.Equal(Phase.Stand, d.PhaseAfterBreak);
        }

        [Fact]
        public void Switching_from_the_tray_ends_a_break_and_counts_switches()
        {
            var e = Engine(EverySwitches(3));
            e.SwitchTo(Phase.Stand, At(10));
            Assert.Equal(1, e.SwitchesBeforeBreak);
            e.TakeBreak(At(12));
            e.SwitchTo(Phase.Sit, At(13));
            Assert.Equal(RunState.Running, e.State);
            Assert.Equal(Phase.Sit, e.Phase);
            Assert.Equal(2, e.SwitchesBeforeBreak);                     // the break counted; this switch didn't
        }

        [Fact]
        public void Pausing_during_a_break_ends_it()
        {
            var e = Engine(EverySwitches(1));
            Assert.True(Tick(e, 45));
            e.Pause(At(47), null);
            Assert.Equal(RunState.Paused, e.State);
            Assert.False(e.OnBreak);
            Assert.Equal(Phase.Stand, e.Phase);                         // the switch the break replaced happened
            e.Resume(At(100));
            Assert.Equal(At(115), e.EndUtc);
        }

        [Fact]
        public void Fullscreen_holds_a_break_and_its_end()
        {
            var e = Engine(EveryMinutes(30));
            Assert.True(e.ReminderDue(At(30)));
            Assert.False(e.Update(At(30), TimeSpan.Zero, _tick++, busy: true));
            Assert.Equal(RunState.Running, e.State);
            Assert.True(e.Update(At(31), TimeSpan.Zero, _tick++, busy: false));
            Assert.Equal(RunState.Break, e.State);
            Assert.False(e.ReminderDue(At(35)));
            Assert.True(e.ReminderDue(At(36)));
            Assert.False(e.Update(At(36), TimeSpan.Zero, _tick++, busy: true));
            Assert.Equal(RunState.Break, e.State);
            Assert.True(e.Update(At(37), TimeSpan.Zero, _tick++, busy: false));
            Assert.Equal(Reminder.BreakEnd, e.Reminder);
        }

        [Fact]
        public void Next_check_wakes_for_the_break_and_its_end()
        {
            var e = Engine(EveryMinutes(30));
            e.PauseWhenAway = false;
            Assert.Equal(TimeSpan.FromSeconds(60), e.NextCheck(At(29), TimeSpan.Zero, false));
            Assert.Equal(TimeSpan.FromSeconds(30), e.NextCheck(At(29.5), TimeSpan.Zero, false));
            Tick(e, 30);
            Assert.Equal(TimeSpan.FromSeconds(30), e.NextCheck(At(34.5), TimeSpan.Zero, false));
        }

        [Fact]
        public void Changing_the_mode_during_a_break_applies_after_it()
        {
            var e = Engine(EveryMinutes(30));
            Assert.True(Tick(e, 30));
            e.SetMode(new FixedMode(20, 10), At(31));
            Assert.Equal(RunState.Break, e.State);
            Assert.True(Tick(e, 35));
            e.Acknowledge(At(35));
            Assert.Equal(At(55), e.EndUtc);
        }

        [Fact]
        public void Changing_break_settings()
        {
            var e = Engine(EveryMinutes(60));
            Assert.Equal(Min(40), e.BreakIn(At(20)));
            e.SetBreaks(EveryMinutes(90), At(20));                     // a new interval starts over
            Assert.Equal(Min(90), e.BreakIn(At(20)));
            e.SetBreaks(EveryMinutes(90, length: 10), At(25));         // a new length doesn't
            Assert.Equal(Min(85), e.BreakIn(At(25)));
            e.SetBreaks(BreakRule.None, At(26));
            Assert.Null(e.BreakIn(At(26)));

            var s = Engine(EverySwitches(4));
            Tick(s, 45); s.Acknowledge(At(45));
            Tick(s, 60); s.Acknowledge(At(60));
            Assert.Equal(1, s.SwitchesBeforeBreak);
            s.SetBreaks(EverySwitches(3), At(61));                     // switches made so far still count
            Assert.Equal(0, s.SwitchesBeforeBreak);
        }

        [Fact]
        public void Fresh_start_restarts_the_break_countdown()
        {
            var e = Engine(EveryMinutes(30));
            Assert.True(Tick(e, 30));
            Assert.False(e.StartFresh(Phase.Sit, At(200)));
            Assert.Equal(RunState.Running, e.State);
            Assert.False(e.OnBreak);
            Assert.Equal(Min(30), e.BreakIn(At(200)));
        }

        [Fact]
        public void Clock_mode_resumes_at_the_wall_clock_and_switches_if_the_phase_ended_during_the_break()
        {
            var local = new DateTime(2026, 11, 9, 10, 5, 0, DateTimeKind.Local);
            DateTime L(double minutes) => local.AddMinutes(minutes).ToUniversalTime();
            var e = new ScheduleEngine(new ClockMode(50, 10));
            e.SetBreaks(EveryMinutes(30, length: 15), L(0));
            e.StartFresh(Phase.Sit, L(0));                            // sit until 10:50
            Assert.True(e.Update(L(30), TimeSpan.Zero, _tick++, false)); // 10:35: break until 10:50
            Assert.Equal(RunState.Break, e.State);
            Assert.Equal(Phase.Sit, e.PhaseAfterBreak);
            Assert.True(e.Update(L(45), TimeSpan.Zero, _tick++, false)); // 10:50: the stand window opened meanwhile
            Assert.Equal(Phase.Stand, e.PhaseAfterBreak);
            e.Acknowledge(L(46));
            Assert.Equal(Phase.Stand, e.Phase);
            Assert.Equal(L(55), e.EndUtc);                            // until 11:00

            // A short break mid-sit: the sit keeps its wall-clock end.
            var f = new ScheduleEngine(new ClockMode(50, 10));
            f.SetBreaks(EveryMinutes(30, length: 5), L(0));
            f.StartFresh(Phase.Sit, L(0));
            f.Update(L(30), TimeSpan.Zero, _tick++, false);
            f.Update(L(35), TimeSpan.Zero, _tick++, false);
            f.Acknowledge(L(37));
            Assert.Equal(Phase.Sit, f.Phase);
            Assert.Equal(L(45), f.EndUtc);                            // still 10:50
        }

        [Fact]
        public void Settings_clamp_break_values()
        {
            var s = new Settings { Breaks = true, BreakEvery = "sometimes", BreakMinutes = 3, BreakSwitches = 99, BreakLength = 0 };
            s.Normalize();
            Assert.Equal(BreakEveryIds.Minutes, s.BreakEvery);
            Assert.Equal(10, s.BreakMinutes);
            Assert.Equal(20, s.BreakSwitches);
            Assert.Equal(1, s.BreakLength);
            Assert.Equal(BreakTrigger.Minutes, BreakRule.From(s).Trigger);
            s.BreakEvery = BreakEveryIds.Switches;
            Assert.Equal(BreakTrigger.Switches, BreakRule.From(s).Trigger);
        }

        // ---------------- each mate's break GIFs and sounds ----------------

        /// <summary>A throwaway mate folder with the given mate.json and (empty) media files.</summary>
        private static string MateFolder(string json, params string[] files)
        {
            var dir = Path.Combine(Path.GetTempPath(), "standmate-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "mate.json"), json);
            foreach (var f in files) File.WriteAllBytes(Path.Combine(dir, f), new byte[0]);
            return dir;
        }

        [Fact]
        public void A_mate_without_break_media_plays_the_default_sounds()
        {
            var dir = MateFolder(@"{ ""name"": ""Old"", ""stand"": { ""sound"": ""stand.wav"" }, ""sit"": {} }", "stand.wav");
            try
            {
                var m = MateStore.Load(dir);
                Assert.Null(m.BreakStart);
                var start = m.BreakStartCue();
                var end = m.BreakEndCue(Phase.Sit);
                Assert.NotNull(start.SoundPath);                       // shipped in sounds\ next to the exe
                Assert.Equal(DefaultBreak.StartSound, start.SoundPath);
                Assert.Equal(DefaultBreak.EndSound, end.SoundPath);
                Assert.Null(start.ImagePath);                          // no default GIFs
                Assert.Null(end.ImagePath);
                Assert.Equal("休", start.Kanji);
                Assert.Equal("座", end.Kanji);
                Assert.Equal(DefaultBreak.StartCaption, start.Caption);
                Assert.Equal(DefaultBreak.EndCaption, end.Caption);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void A_mates_own_break_media_is_used_and_an_emptied_one_stays_silent()
        {
            var dir = MateFolder(
                @"{ ""name"": ""Chill"", ""stand"": {}, ""sit"": {},
                    ""breakStart"": { ""image"": ""break-start.gif"", ""sound"": ""break-start.mp3"", ""caption"": ""Tea time"" },
                    ""breakEnd"": {} }",
                "break-start.gif", "break-start.mp3");
            try
            {
                var m = MateStore.Load(dir);
                var start = m.BreakStartCue();
                Assert.Equal(Path.Combine(dir, "break-start.gif"), start.ImagePath);
                Assert.Equal(Path.Combine(dir, "break-start.mp3"), start.SoundPath);
                Assert.Equal("Tea time", start.Caption);

                var end = m.BreakEndCue(Phase.Stand);                  // cleared in the editor: no default sound
                Assert.Null(end.SoundPath);
                Assert.Null(end.ImagePath);
                Assert.Equal(DefaultBreak.EndCaption, end.Caption);
                Assert.Equal("立", end.Kanji);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Saving_a_mate_keeps_unset_break_media_unset_and_its_break_files()
        {
            var dir = MateFolder(@"{ ""name"": ""A"", ""stand"": {}, ""sit"": {} }", "break-end.wav", "stray.gif");
            try
            {
                var m = MateStore.Load(dir);
                MateStore.Save(m);
                Assert.DoesNotContain("break", File.ReadAllText(Path.Combine(dir, "mate.json")));
                Assert.Null(MateStore.Load(dir).BreakStart);

                m.BreakStart = new MateAction();
                m.BreakEnd = new MateAction { Sound = "break-end.wav" };
                MateStore.Save(m);
                MateStore.CleanUnused(dir, m.Stand, m.Sit, m.BreakStart, m.BreakEnd);
                var again = MateStore.Load(dir);
                Assert.NotNull(again.BreakStart);                       // emptied, not unset
                Assert.Null(again.BreakStartCue().SoundPath);
                Assert.True(File.Exists(Path.Combine(dir, "break-end.wav")));
                Assert.False(File.Exists(Path.Combine(dir, "stray.gif")));
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
