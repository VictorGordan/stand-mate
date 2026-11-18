using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StandMate.Core
{
    /// <summary>
    /// Going back to an older version: an optional copy of settings.json and the mates, kept in
    /// %APPDATA%\StandMate\backups\&lt;version it was made by&gt;\. Older versions drop what they don't know when they
    /// save (e.g. the break settings, or a mate's break GIFs when it's edited there). Once a version at least as new as
    /// the backup runs, it puts those things back; everything the older version kept, including changes made there,
    /// stays as it is. Then the backup is deleted.
    /// </summary>
    public static class SettingsBackup
    {
        public static string Root(string dataDir) => Path.Combine(dataDir, "backups");

        /// <summary>Copies settings.json and the mates into backups\&lt;version&gt;\ (replacing an older backup of that version).</summary>
        public static void Create(string dataDir, Version version)
        {
            string dir = Path.Combine(Root(dataDir), version.ToString());
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            string settings = Path.Combine(dataDir, "settings.json");
            if (File.Exists(settings)) File.Copy(settings, Path.Combine(dir, "settings.json"));
            string mates = Path.Combine(dataDir, "mates");
            if (Directory.Exists(mates)) CopyTree(mates, Path.Combine(dir, "mates"));
        }

        /// <summary>
        /// Merges every backup made by <paramref name="current"/> or an older version back in, newest first, and
        /// deletes it. Backups of newer versions are kept for when that version comes back. Returns the merged versions.
        /// </summary>
        public static List<Version> RestoreDue(string dataDir, Version current)
        {
            var merged = new List<Version>();
            string root = Root(dataDir);
            if (current == null || !Directory.Exists(root)) return merged;

            var due = Directory.GetDirectories(root)
                .Select(d => (Dir: d, Version: Updates.ParseVersion(Path.GetFileName(d))))
                .Where(b => b.Version != null && !Updates.IsNewer(current, b.Version))
                .OrderByDescending(b => b.Version)
                .ToList();
            foreach (var (dir, version) in due)
            {
                MergeSettings(dataDir, dir);
                MergeMates(dataDir, dir);
                Directory.Delete(dir, true);
                merged.Add(version);
            }
            if (Directory.GetFileSystemEntries(root).Length == 0) Directory.Delete(root);
            return merged;
        }

        private static void MergeSettings(string dataDir, string backupDir)
        {
            string from = Path.Combine(backupDir, "settings.json");
            string target = Path.Combine(dataDir, "settings.json");
            if (!File.Exists(from)) return;
            if (!File.Exists(target)) File.Copy(from, target);
            else Json.FillMissingKeys(target, from);
        }

        /// <summary>Mates deleted in the meantime stay deleted; for the others, missing keys and their media come back.</summary>
        private static void MergeMates(string dataDir, string backupDir)
        {
            string backupMates = Path.Combine(backupDir, "mates");
            if (!Directory.Exists(backupMates)) return;
            foreach (var from in Directory.GetDirectories(backupMates))
            {
                string to = Path.Combine(dataDir, "mates", Path.GetFileName(from));
                if (!File.Exists(Path.Combine(to, "mate.json"))) continue;
                foreach (var value in Json.FillMissingKeys(Path.Combine(to, "mate.json"), Path.Combine(from, "mate.json")))
                {
                    // An older editor deletes files it doesn't know are in use (e.g. break-start.gif): copy them back.
                    foreach (var e in value.DescendantsAndSelf().Where(e => e.Name.LocalName == "image" || e.Name.LocalName == "sound"))
                    {
                        string name = Path.GetFileName(e.Value);
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        string src = Path.Combine(from, name), dest = Path.Combine(to, name);
                        if (File.Exists(src) && !File.Exists(dest)) File.Copy(src, dest);
                    }
                }
            }
        }

        private static void CopyTree(string src, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dest, Path.GetFileName(f)));
            foreach (var d in Directory.GetDirectories(src)) CopyTree(d, Path.Combine(dest, Path.GetFileName(d)));
        }
    }
}
