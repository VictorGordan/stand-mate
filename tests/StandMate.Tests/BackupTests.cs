using System;
using System.IO;
using System.Linq;
using System.Text;
using StandMate.Core;
using Xunit;

namespace StandMate.Tests
{
    /// <summary>Going back to an older version and coming back: the version list and the settings backup.</summary>
    public class BackupTests : IDisposable
    {
        private static readonly Version V121 = new Version(1, 2, 1), V130 = new Version(1, 3, 0);
        private readonly string _data = Path.Combine(Path.GetTempPath(), "standmate-backup-test-" + Guid.NewGuid().ToString("N"));

        public BackupTests() => Directory.CreateDirectory(_data);

        public void Dispose()
        {
            try { Directory.Delete(_data, true); } catch (IOException) { }
        }

        private string P(params string[] parts) => Path.Combine(new[] { _data }.Concat(parts).ToArray());
        private void Write(string text, params string[] parts)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(P(parts)));
            File.WriteAllText(P(parts), text);
        }

        [Fact]
        public void The_version_list_holds_published_older_versions_newest_first()
        {
            // Trimmed from api.github.com/repos/…/releases: unknown fields (nested ones too) are ignored.
            var json = Encoding.UTF8.GetBytes(@"[
              { ""tag_name"": ""v1.4.0"", ""draft"": false, ""prerelease"": false, ""assets"": [ { ""name"": ""latest.json"", ""size"": 300 } ] },
              { ""tag_name"": ""v1.3.0"", ""draft"": false, ""prerelease"": false, ""author"": { ""id"": 123456789 } },
              { ""tag_name"": ""v1.2.1"", ""draft"": false, ""prerelease"": false, ""body"": ""First \/ release"" },
              { ""tag_name"": ""v1.2.9"", ""draft"": true, ""prerelease"": false },
              { ""tag_name"": ""v1.2.8"", ""draft"": false, ""prerelease"": true },
              { ""tag_name"": ""nightly"", ""draft"": false, ""prerelease"": false },
              { ""tag_name"": ""v1.2.2"", ""draft"": false, ""prerelease"": false, ""id"": 2600000000 }
            ]");
            var releases = Updates.ParseReleases(json);
            Assert.NotNull(releases);
            var older = Updates.OlderVersions(releases, V130);
            Assert.Equal(new[] { new Version(1, 2, 2), V121 }, older);
            Assert.Null(Updates.ParseReleases(Encoding.UTF8.GetBytes("{ not json")));
        }

        [Fact]
        public void Filling_missing_keys_keeps_existing_and_unknown_keys()
        {
            Write("﻿{ \"volume\": 40, \"futureThing\": [1, 2] }", "a.json");   // with a BOM, like Notepad saves
            Write("{ \"volume\": 70, \"breaks\": true, \"breakLength\": 9, \"nested\": { \"x\": \"y\" } }", "b.json");
            var added = Json.FillMissingKeys(P("a.json"), P("b.json"));
            Assert.Equal(3, added.Count);
            var text = File.ReadAllText(P("a.json"));
            Assert.Contains("\"volume\": 40", text);
            Assert.Contains("futureThing", text);
            Assert.Contains("\"breakLength\": 9", text);
            Assert.Contains("\"x\": \"y\"", text);
            Assert.Empty(Json.FillMissingKeys(P("a.json"), P("b.json")));          // nothing left to add
            Assert.Empty(Json.FillMissingKeys(P("a.json"), P("missing.json")));
        }

        [Fact]
        public void Coming_back_restores_what_the_older_version_dropped_and_keeps_its_changes()
        {
            // 1.3.0, with breaks set up and a mate with break media.
            Json.Write(P("settings.json"), new Settings { Volume = 70, Breaks = true, BreakLength = 9, MateId = "chill" });
            Write(@"{ ""name"": ""Chill"", ""stand"": { ""caption"": ""Up!"" }, ""sit"": {},
                      ""breakStart"": { ""image"": ""break-start.gif"", ""sound"": ""break-start.wav"" } }", "mates", "chill", "mate.json");
            Write("gif", "mates", "chill", "break-start.gif");
            Write("wav", "mates", "chill", "break-start.wav");
            Write(@"{ ""name"": ""Gone"", ""stand"": {}, ""sit"": {} }", "mates", "gone", "mate.json");
            SettingsBackup.Create(_data, V130);

            // In 1.2.1: settings saved without the keys it doesn't know (and the volume changed), Chill edited
            // (its break entries dropped, the break GIF deleted as unused), one mate deleted, one added.
            Write(@"{ ""volume"": 40, ""mate"": ""chill"", ""mode"": ""fixed"" }", "settings.json");
            Write(@"{ ""name"": ""Chill"", ""stand"": { ""caption"": ""Stand now"" }, ""sit"": {} }", "mates", "chill", "mate.json");
            File.Delete(P("mates", "chill", "break-start.gif"));
            Directory.Delete(P("mates", "gone"), true);
            Write(@"{ ""name"": ""New"", ""stand"": {}, ""sit"": {} }", "mates", "new", "mate.json");

            // Still on the older version: the backup waits.
            Assert.Empty(SettingsBackup.RestoreDue(_data, V121));
            Assert.True(Directory.Exists(P("backups", "1.3.0")));

            // Back on 1.3.0 (or newer).
            Assert.Equal(new[] { V130 }, SettingsBackup.RestoreDue(_data, new Version(1, 3, 1)));
            var s = Json.Read<Settings>(P("settings.json"));
            Assert.Equal(40, s.Volume);                       // changed in 1.2.1: carried over
            Assert.True(s.Breaks);                            // unknown to 1.2.1: restored
            Assert.Equal(9, s.BreakLength);

            var chill = MateStore.Load(P("mates", "chill"));
            Assert.Equal("Stand now", chill.Stand.Caption);   // edited in 1.2.1: kept
            Assert.NotNull(chill.BreakStart);                 // dropped by 1.2.1: restored, with its files
            Assert.Equal(P("mates", "chill", "break-start.gif"), chill.BreakStartCue().ImagePath);
            Assert.False(Directory.Exists(P("mates", "gone"))); // deleted in 1.2.1: stays deleted
            Assert.True(File.Exists(P("mates", "new", "mate.json")));
            Assert.False(Directory.Exists(P("backups")));     // used up
        }

        [Fact]
        public void Without_changes_in_between_coming_back_changes_nothing()
        {
            Json.Write(P("settings.json"), new Settings { Volume = 55, Breaks = true });
            var before = File.ReadAllText(P("settings.json"));
            SettingsBackup.Create(_data, V130);
            SettingsBackup.RestoreDue(_data, V130);
            Assert.Equal(before, File.ReadAllText(P("settings.json")));
        }

        [Fact]
        public void Several_backups_merge_newest_first()
        {
            Write(@"{ ""breakLength"": 5 }", "backups", "1.3.0", "settings.json");
            Write(@"{ ""breakLength"": 9, ""breaks"": true }", "backups", "1.4.0", "settings.json");
            Write(@"{ ""breakLength"": 2 }", "backups", "2.0.0", "settings.json");   // newer than us: kept
            Write(@"{ ""volume"": 40 }", "settings.json");
            Assert.Equal(new[] { new Version(1, 4, 0), V130 }, SettingsBackup.RestoreDue(_data, new Version(1, 4, 0)));
            var s = Json.Read<Settings>(P("settings.json"));
            Assert.Equal(9, s.BreakLength);
            Assert.True(s.Breaks);
            Assert.Equal(40, s.Volume);
            Assert.True(Directory.Exists(P("backups", "2.0.0")));
        }

        [Fact]
        public void A_new_backup_of_the_same_version_replaces_the_old_one()
        {
            Write(@"{ ""volume"": 10 }", "settings.json");
            SettingsBackup.Create(_data, V130);
            Write(@"{ ""volume"": 20 }", "settings.json");
            SettingsBackup.Create(_data, V130);
            Assert.Contains("20", File.ReadAllText(P("backups", "1.3.0", "settings.json")));
        }
    }
}
