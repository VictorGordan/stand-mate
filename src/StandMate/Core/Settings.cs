using System;
using System.IO;
using System.Runtime.Serialization;

namespace StandMate.Core
{
    public static class ModeIds
    {
        public const string Fixed = "fixed";
        public const string Sequence = "sequence";
        public const string Clock = "clock";
        public const string Progressive = "progressive";
        public const string Random = "random";
    }

    public static class Anchors
    {
        public const string Custom = "Custom";
        public static readonly string[] Grid =
        {
            "TopLeft", "Top", "TopRight",
            "Left", "Center", "Right",
            "BottomLeft", "Bottom", "BottomRight",
        };
    }

    /// <summary>User settings, stored as %APPDATA%\StandMate\settings.json. All durations are minutes.</summary>
    [DataContract]
    public sealed class Settings
    {
        // Schedule
        [DataMember(Name = "mode")] public string Mode;
        [DataMember(Name = "fixedSit")] public int FixedSit;
        [DataMember(Name = "fixedStand")] public int FixedStand;
        [DataMember(Name = "sequence")] public string Sequence;
        [DataMember(Name = "clockStandAt")] public int ClockStandAt;
        [DataMember(Name = "clockStandFor")] public int ClockStandFor;
        [DataMember(Name = "progSit")] public int ProgSit;
        [DataMember(Name = "progStandStart")] public int ProgStandStart;
        [DataMember(Name = "progStandStep")] public int ProgStandStep;
        [DataMember(Name = "progStandMax")] public int ProgStandMax;
        [DataMember(Name = "randSitMin")] public int RandSitMin;
        [DataMember(Name = "randSitMax")] public int RandSitMax;
        [DataMember(Name = "randStandMin")] public int RandStandMin;
        [DataMember(Name = "randStandMax")] public int RandStandMax;
        [DataMember(Name = "startWith")] public string StartWith;      // "sit" | "stand": at startup and after sleep

        // Away (no mouse/keyboard input)
        [DataMember(Name = "pauseWhenAway")] public bool PauseWhenAway;
        [DataMember(Name = "awayMinutes")] public int AwayMinutes;
        [DataMember(Name = "awayRewind")] public bool AwayRewind;       // true: rewind to last input, then pause

        // Mate
        [DataMember(Name = "mate")] public string MateId;

        // Popup
        [DataMember(Name = "screen")] public string ScreenDevice;   // e.g. \\.\DISPLAY2; null/empty = primary
        [DataMember(Name = "anchor")] public string Anchor;
        [DataMember(Name = "customX")] public double CustomX;       // 0..1 across the free space of the work area
        [DataMember(Name = "customY")] public double CustomY;
        [DataMember(Name = "width")] public int PopupWidth;         // logical px (scaled by monitor DPI)
        [DataMember(Name = "volume")] public int Volume;            // 0..100
        [DataMember(Name = "repeatSound")] public bool RepeatSound;
        // Off by default: games are exactly when people forget to move. Stored under a new key (was
        // "waitFullscreen", default on) so the old saved default doesn't carry over; the old key is ignored.
        [DataMember(Name = "holdForFullscreen")] public bool WaitForFullscreen;

        [DataMember(Name = "matesSeeded")] public bool MatesSeeded;          // legacy (1.0–1.1); kept so old files still read
        [DataMember(Name = "seededMates")] public string[] SeededMates;      // bundled mate ids already copied once

        public Settings() => SetDefaults();

        // DataContract deserialization skips constructors; this gives missing fields sane defaults.
        [OnDeserializing]
        private void OnDeserializing(StreamingContext _) => SetDefaults();

        private void SetDefaults()
        {
            Mode = ModeIds.Fixed;
            FixedSit = 45; FixedStand = 15;
            Sequence = "sit 30, stand 10, sit 45, stand 15";
            ClockStandAt = 50; ClockStandFor = 10;
            ProgSit = 45; ProgStandStart = 5; ProgStandStep = 5; ProgStandMax = 20;
            RandSitMin = 30; RandSitMax = 60; RandStandMin = 10; RandStandMax = 20;
            StartWith = "sit";
            PauseWhenAway = true; AwayMinutes = 5; AwayRewind = true;
            MateId = "default";
            ScreenDevice = null;
            Anchor = "BottomRight";
            CustomX = 1; CustomY = 1;
            PopupWidth = 320;
            Volume = 70;
            RepeatSound = false;
            WaitForFullscreen = false;
            MatesSeeded = false;
            SeededMates = null;
        }

        /// <summary>Clamps everything into valid ranges (protects against hand-edited files).</summary>
        public void Normalize()
        {
            FixedSit = Clamp(FixedSit, 1, 600); FixedStand = Clamp(FixedStand, 1, 600);
            ClockStandAt = Clamp(ClockStandAt, 0, 59); ClockStandFor = Clamp(ClockStandFor, 1, 55);
            ProgSit = Clamp(ProgSit, 1, 600);
            ProgStandStart = Clamp(ProgStandStart, 1, 600);
            ProgStandStep = Clamp(ProgStandStep, 0, 120);
            ProgStandMax = Clamp(ProgStandMax, ProgStandStart, 600);
            RandSitMin = Clamp(RandSitMin, 1, 600); RandSitMax = Clamp(RandSitMax, RandSitMin, 600);
            RandStandMin = Clamp(RandStandMin, 1, 600); RandStandMax = Clamp(RandStandMax, RandStandMin, 600);
            if (Sequence == null) Sequence = "";
            if (StartWith != "stand") StartWith = "sit";
            AwayMinutes = Clamp(AwayMinutes, 1, 120);
            if (Array.IndexOf(Anchors.Grid, Anchor) < 0 && Anchor != Anchors.Custom) Anchor = "BottomRight";
            CustomX = Math.Max(0, Math.Min(1, CustomX)); CustomY = Math.Max(0, Math.Min(1, CustomY));
            PopupWidth = Clamp(PopupWidth, 120, 900);
            Volume = Clamp(Volume, 0, 100);
            if (Mode != ModeIds.Fixed && Mode != ModeIds.Sequence && Mode != ModeIds.Clock &&
                Mode != ModeIds.Progressive && Mode != ModeIds.Random) Mode = ModeIds.Fixed;
            if (Mode == ModeIds.Sequence && !SequenceParser.TryParse(Sequence, out _, out _)) Mode = ModeIds.Fixed;
        }

        public Phase StartPhase => StartWith == "stand" ? Phase.Stand : Phase.Sit;

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

        public Settings Clone() => (Settings)MemberwiseClone();

        // ---- storage ----

        public static string DataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StandMate");

        public static string FilePath => Path.Combine(DataDir, "settings.json");

        public static Settings Load()
        {
            var s = Json.TryRead<Settings>(FilePath);
            if (s == null)
            {
                // Unreadable (e.g. a broken hand edit): keep a copy instead of silently overwriting it.
                try { if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".unreadable", true); } catch (Exception) { }
                s = new Settings();
            }
            s.Normalize();
            return s;
        }

        public void Save()
        {
            try { Json.Write(FilePath, this); }
            catch (Exception) { /* settings are best-effort; never crash the tray app over a write */ }
        }
    }
}
