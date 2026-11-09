using System;
using System.Linq;
using StandMate.Core;
using Xunit;

namespace StandMate.Tests
{
    public class ModeTests
    {
        private static readonly DateTime Day = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Local);

        [Fact]
        public void Fixed_returns_configured_lengths()
        {
            var m = new FixedMode(45, 15);
            Assert.Equal(TimeSpan.FromMinutes(45), m.NextDuration(Phase.Sit, Day));
            Assert.Equal(TimeSpan.FromMinutes(15), m.NextDuration(Phase.Stand, Day));
            Assert.False(m.IsClockBased);
        }

        [Fact]
        public void Sequence_parser_accepts_alternating_steps()
        {
            Assert.True(SequenceParser.TryParse("sit 30, Stand 10 ;sit 45min,\nstand 15 minutes", out var steps, out var err), err);
            Assert.Equal(new[] { 30, 10, 45, 15 }, steps.Select(s => s.Minutes));
            Assert.Equal(new[] { Phase.Sit, Phase.Stand, Phase.Sit, Phase.Stand }, steps.Select(s => s.Phase));
        }

        [Theory]
        [InlineData("")]
        [InlineData("sit 30")]
        [InlineData("sit 30, sit 10")]
        [InlineData("sit 30, stand 10, sit 20")]   // sit -> sit across the loop boundary
        [InlineData("sit 30, jump 10")]
        [InlineData("sit 0, stand 10")]
        [InlineData("sit 700, stand 10")]
        public void Sequence_parser_rejects_bad_input(string text)
        {
            Assert.False(SequenceParser.TryParse(text, out _, out var err));
            Assert.False(string.IsNullOrEmpty(err));
        }

        [Fact]
        public void Sequence_mode_walks_the_loop()
        {
            SequenceParser.TryParse("sit 30, stand 10, sit 50, stand 15", out var steps, out _);
            var m = new SequenceMode(steps);
            var got = new[]
            {
                m.NextDuration(Phase.Sit, Day), m.NextDuration(Phase.Stand, Day),
                m.NextDuration(Phase.Sit, Day), m.NextDuration(Phase.Stand, Day),
                m.NextDuration(Phase.Sit, Day), m.NextDuration(Phase.Stand, Day),
            }.Select(t => (int)t.TotalMinutes);
            Assert.Equal(new[] { 30, 10, 50, 15, 30, 10 }, got);
        }

        [Fact]
        public void Sequence_mode_handles_manual_phase_jumps()
        {
            SequenceParser.TryParse("sit 30, stand 10, sit 50, stand 15", out var steps, out _);
            var m = new SequenceMode(steps);
            Assert.Equal(10, (int)m.NextDuration(Phase.Stand, Day).TotalMinutes); // "Stand now" before any sit
            Assert.Equal(50, (int)m.NextDuration(Phase.Sit, Day).TotalMinutes);
            Assert.Equal(15, (int)m.NextDuration(Phase.Stand, Day).TotalMinutes);
        }

        [Fact]
        public void Clock_sit_runs_until_the_stand_minute()
        {
            var m = new ClockMode(50, 10);
            Assert.True(m.IsClockBased);
            var now = Day.AddHours(10).AddMinutes(5);
            Assert.Equal(TimeSpan.FromMinutes(45), m.NextDuration(Phase.Sit, now));
        }

        [Fact]
        public void Clock_sit_skips_a_slot_that_is_too_close()
        {
            var m = new ClockMode(50, 10);
            var now = Day.AddHours(10).AddMinutes(47); // only 3 min to :50
            Assert.Equal(TimeSpan.FromMinutes(63), m.NextDuration(Phase.Sit, now));
        }

        [Fact]
        public void Clock_stand_ends_when_the_window_closes()
        {
            var m = new ClockMode(50, 10);
            var now = Day.AddHours(10).AddMinutes(52);
            Assert.Equal(TimeSpan.FromMinutes(8), m.NextDuration(Phase.Stand, now));
        }

        [Fact]
        public void Clock_stand_never_exceeds_the_stand_length_when_late()
        {
            var m = new ClockMode(50, 10);
            var now = Day.AddHours(11).AddMinutes(3); // window already over
            Assert.Equal(TimeSpan.FromMinutes(10), m.NextDuration(Phase.Stand, now));
        }

        [Fact]
        public void Clock_next_at_minute_is_strictly_after_now()
        {
            var now = Day.AddHours(9).AddMinutes(50);
            Assert.Equal(Day.AddHours(10).AddMinutes(50), ClockMode.NextAtMinute(now, 50));
            Assert.Equal(Day.AddHours(10), ClockMode.NextAtMinute(now, 0));
        }

        [Theory]
        [InlineData(49, Phase.Sit)]
        [InlineData(50, Phase.Stand)]
        [InlineData(59, Phase.Stand)]
        [InlineData(0, Phase.Sit)]
        public void Clock_phase_follows_the_stand_window(int minute, Phase expected)
        {
            var m = new ClockMode(50, 10);
            Assert.Equal(expected, m.ClockPhase(Day.AddHours(10).AddMinutes(minute)));
        }

        [Fact]
        public void Clock_window_can_wrap_past_the_hour()
        {
            var m = new ClockMode(55, 10); // :55 to :05
            Assert.Equal(Phase.Stand, m.ClockPhase(Day.AddHours(10).AddMinutes(3)));
            Assert.Equal(Phase.Sit, m.ClockPhase(Day.AddHours(10).AddMinutes(5)));
        }

        [Fact]
        public void Non_clock_modes_have_no_clock_phase()
        {
            Assert.Null(new FixedMode(45, 15).ClockPhase(Day));
            Assert.Null(new ProgressiveMode(45, 5, 5, 20).ClockPhase(Day));
            Assert.Null(new RandomMode(30, 60, 10, 20).ClockPhase(Day));
        }

        [Fact]
        public void Settings_default_and_clamp_the_new_options()
        {
            var s = new Settings();
            Assert.Equal(Phase.Sit, s.StartPhase);
            Assert.Equal(5, s.AwayMinutes);
            Assert.True(s.AwayRewind);
            s.StartWith = "lying down"; s.AwayMinutes = 0;
            s.Normalize();
            Assert.Equal(Phase.Sit, s.StartPhase);
            Assert.Equal(1, s.AwayMinutes);
            s.StartWith = "stand";
            Assert.Equal(Phase.Stand, s.StartPhase);
        }

        [Fact]
        public void Progressive_grows_caps_and_resets_daily()
        {
            var m = new ProgressiveMode(45, 5, 5, 20);
            Assert.Equal(45, (int)m.NextDuration(Phase.Sit, Day).TotalMinutes);
            var stands = Enumerable.Range(0, 6).Select(_ => (int)m.NextDuration(Phase.Stand, Day.AddHours(9)).TotalMinutes);
            Assert.Equal(new[] { 5, 10, 15, 20, 20, 20 }, stands);
            Assert.Equal(5, (int)m.NextDuration(Phase.Stand, Day.AddDays(1).AddHours(9)).TotalMinutes);
        }

        [Fact]
        public void Random_stays_within_bounds()
        {
            var m = new RandomMode(30, 60, 10, 20, new Random(42));
            for (int i = 0; i < 500; i++)
            {
                var sit = m.NextDuration(Phase.Sit, Day).TotalMinutes;
                var stand = m.NextDuration(Phase.Stand, Day).TotalMinutes;
                Assert.InRange(sit, 30, 60);
                Assert.InRange(stand, 10, 20);
            }
        }

        [Fact]
        public void Json_reads_files_saved_with_a_utf8_bom()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "standmate_bom_test.json");
            var utf8Bom = new System.Text.UTF8Encoding(true);
            System.IO.File.WriteAllText(path, "{ \"name\": \"Café\", \"stand\": { \"image\": \"a.gif\" } }", utf8Bom);
            try
            {
                var m = Json.Read<Mate>(path);
                Assert.Equal("Café", m.Name);
                Assert.Equal("a.gif", m.Stand.Image);
            }
            finally { System.IO.File.Delete(path); }
        }

        [Fact]
        public void Settings_round_trip_through_json()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "standmate_settings_test.json");
            var s = new Settings { Mode = ModeIds.Clock, ClockStandAt = 25, Anchor = Anchors.Custom, CustomX = 0.25, ScreenDevice = @"\\.\DISPLAY3" };
            try
            {
                Json.Write(path, s);
                var back = Json.Read<Settings>(path);
                Assert.Equal(ModeIds.Clock, back.Mode);
                Assert.Equal(25, back.ClockStandAt);
                Assert.Equal(Anchors.Custom, back.Anchor);
                Assert.Equal(0.25, back.CustomX);
                Assert.Equal(@"\\.\DISPLAY3", back.ScreenDevice);
                Assert.Equal(45, back.FixedSit); // untouched default survives
            }
            finally { System.IO.File.Delete(path); }
        }

        [Fact]
        public void Missing_fields_get_defaults_when_reading_old_files()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "standmate_partial_test.json");
            System.IO.File.WriteAllText(path, "{ \"mode\": \"random\" }");
            try
            {
                var s = Json.Read<Settings>(path);
                Assert.Equal(ModeIds.Random, s.Mode);
                Assert.Equal(70, s.Volume);
                Assert.False(s.WaitForFullscreen);
                Assert.True(s.PauseWhenAway);
            }
            finally { System.IO.File.Delete(path); }
        }

        [Fact]
        public void Old_saved_fullscreen_wait_is_dropped_so_reminders_show_in_games()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "standmate_fullscreen_test.json");
            System.IO.File.WriteAllText(path, "{ \"waitFullscreen\": true, \"volume\": 40 }");
            try
            {
                var s = Json.Read<Settings>(path);
                Assert.False(s.WaitForFullscreen);
                Assert.Equal(40, s.Volume);

                s.WaitForFullscreen = true;          // a deliberate choice under the new key is kept
                Json.Write(path, s);
                Assert.True(Json.Read<Settings>(path).WaitForFullscreen);
            }
            finally { System.IO.File.Delete(path); }
        }

        [Fact]
        public void Settings_normalize_clamps_garbage()
        {
            var s = new Settings { FixedSit = -5, Volume = 900, Anchor = "Nowhere", Mode = "bogus", RandSitMin = 50, RandSitMax = 10 };
            s.Normalize();
            Assert.Equal(1, s.FixedSit);
            Assert.Equal(100, s.Volume);
            Assert.Equal("BottomRight", s.Anchor);
            Assert.Equal(ModeIds.Fixed, s.Mode);
            Assert.True(s.RandSitMax >= s.RandSitMin);
        }
    }
}
