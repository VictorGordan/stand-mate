namespace StandMate.Core
{
    public enum Phase
    {
        Sit,
        Stand,
    }

    public static class PhaseExtensions
    {
        public static Phase Other(this Phase p) => p == Phase.Sit ? Phase.Stand : Phase.Sit;

        public static string Label(this Phase p) => p == Phase.Sit ? "Sit" : "Stand";

        /// <summary>Kanji used in the tray icon and popup: 座 (sit) / 立 (stand).</summary>
        public static string Kanji(this Phase p) => p == Phase.Sit ? "座" : "立";

        /// <summary>休 (rest): the tray icon and popup kanji while on a break.</summary>
        public const string BreakKanji = "休";
    }
}
