using System;
using System.IO;

namespace StandMate.Core
{
    public enum BreakTrigger
    {
        Off,
        /// <summary>After this much sitting and standing time (pauses and time away don't count).</summary>
        Minutes,
        /// <summary>Every Nth switch between sitting and standing becomes a break.</summary>
        Switches,
    }

    /// <summary>When breaks come and how long they last. Immutable; built from <see cref="Settings"/>.</summary>
    public sealed class BreakRule
    {
        public static readonly BreakRule None = new BreakRule(BreakTrigger.Off, 60, 2, 5);

        public BreakRule(BreakTrigger trigger, int everyMinutes, int everySwitches, int lengthMinutes)
        {
            Trigger = trigger;
            EveryMinutes = Math.Max(1, everyMinutes);
            EverySwitches = Math.Max(1, everySwitches);
            LengthMinutes = Math.Max(1, lengthMinutes);
        }

        public BreakTrigger Trigger { get; }
        public int EveryMinutes { get; }
        public int EverySwitches { get; }
        public int LengthMinutes { get; }

        public static BreakRule From(Settings s) => !s.Breaks
            ? None
            : new BreakRule(s.BreakEvery == BreakEveryIds.Switches ? BreakTrigger.Switches : BreakTrigger.Minutes,
                s.BreakMinutes, s.BreakSwitches, s.BreakLength);
    }

    /// <summary>
    /// Break popups for mates that have no break GIF/sound set up yet (made before breaks existed, or by hand):
    /// two sounds shipped next to the exe in sounds\, no GIFs, default captions. Each mate's own break media
    /// lives in its mate.json ("breakStart", "breakEnd").
    /// </summary>
    public static class DefaultBreak
    {
        public const string StartCaption = "Time for a break";
        public const string EndCaption = "Break's over";

        public static string Folder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sounds");

        /// <summary>Absolute path of the default sound, or null if it's missing.</summary>
        public static string StartSound => MateStore.MediaPath(Folder, "break-start.wav");
        public static string EndSound => MateStore.MediaPath(Folder, "break-end.wav");

        /// <summary>The popup when a break starts: 休.</summary>
        public static Cue StartCue() => new Cue { SoundPath = StartSound, Caption = StartCaption, Kanji = PhaseExtensions.BreakKanji };

        /// <summary>The popup when a break is over, with the kanji of the phase you go back to.</summary>
        public static Cue EndCue(Phase back) => new Cue { SoundPath = EndSound, Caption = EndCaption, Kanji = back.Kanji() };
    }
}
