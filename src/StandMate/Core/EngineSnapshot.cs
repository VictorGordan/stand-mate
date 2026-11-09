using System;
using System.IO;
using System.Runtime.Serialization;

namespace StandMate.Core
{
    /// <summary>
    /// The running timer, saved right before an in-app update closes StandMate, so the new version carries on
    /// where the old one stopped (e.g. "7 min of standing left" is still 7 min). Only written by the update
    /// path: a normal boot, Quit or sleep never leaves one behind, so those still start fresh.
    /// </summary>
    [DataContract]
    public sealed class EngineSnapshot
    {
        [DataMember(Name = "appVersion")] public string AppVersion;
        [DataMember(Name = "savedUtc")] public DateTime SavedUtc;
        [DataMember(Name = "modeId")] public string ModeId;
        [DataMember(Name = "modeState")] public string ModeState;
        [DataMember(Name = "phase")] public string Phase;
        [DataMember(Name = "state")] public string State;
        [DataMember(Name = "endUtc")] public DateTime EndUtc;
        [DataMember(Name = "frozenMs")] public long FrozenMs;
        [DataMember(Name = "pauseUntilUtc")] public DateTime? PauseUntilUtc;
        [DataMember(Name = "settingsOpen")] public bool SettingsOpen;  // reopen Settings after the update
    }

    /// <summary>Reading/writing the snapshot file and deciding whether it may be used.</summary>
    public static class Handover
    {
        /// <summary>Older than this and it isn't a handover any more (e.g. the update failed and the PC was off).</summary>
        public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

        public static string FilePath => Path.Combine(Settings.DataDir, "handover.json");

        public static void Write(EngineSnapshot s) => Json.Write(FilePath, s);

        /// <summary>Reads and deletes the snapshot (it's single-use). Null when there is none.</summary>
        public static EngineSnapshot Take()
        {
            var s = Json.TryRead<EngineSnapshot>(FilePath);
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return s;
        }

        /// <summary>
        /// True when the snapshot can be restored: recent, from the same MAJOR version (a major update starts
        /// fresh) and for the same schedule mode.
        /// </summary>
        public static bool IsUsable(EngineSnapshot s, Version currentVersion, string currentModeId, DateTime nowUtc)
        {
            if (s == null) return false;
            var age = nowUtc - s.SavedUtc;
            if (age < TimeSpan.FromMinutes(-1) || age > MaxAge) return false;
            if (Updates.IsMajorChange(Updates.ParseVersion(s.AppVersion), currentVersion)) return false;
            return string.Equals(s.ModeId, currentModeId, StringComparison.Ordinal);
        }
    }
}
