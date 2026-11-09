using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;

namespace StandMate.Core
{
    [DataContract]
    public sealed class MateAction
    {
        [DataMember(Name = "image", EmitDefaultValue = false)] public string Image;
        [DataMember(Name = "sound", EmitDefaultValue = false)] public string Sound;
        [DataMember(Name = "caption", EmitDefaultValue = false)] public string Caption;

        public MateAction Clone() => (MateAction)MemberwiseClone();
    }

    /// <summary>
    /// A "mate": a GIF + sound (+ caption) for standing up, and another set for sitting down.
    /// Lives in its own folder: %APPDATA%\StandMate\mates\&lt;id&gt;\mate.json (+ the media files).
    /// </summary>
    [DataContract]
    public sealed class Mate
    {
        [DataMember(Name = "name")] public string Name;
        [DataMember(Name = "stand")] public MateAction Stand;
        [DataMember(Name = "sit")] public MateAction Sit;

        [IgnoreDataMember] public string Id;
        [IgnoreDataMember] public string Folder;

        public MateAction For(Phase p) => (p == Phase.Stand ? Stand : Sit) ?? new MateAction();

        /// <summary>Absolute path of a media file, or null if not set / missing.</summary>
        public string PathOf(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) || Folder == null) return null;
            var p = Path.Combine(Folder, Path.GetFileName(fileName));
            return File.Exists(p) ? p : null;
        }

        public string ImagePath(Phase p) => PathOf(For(p).Image);
        public string SoundPath(Phase p) => PathOf(For(p).Sound);

        public string Caption(Phase p)
        {
            var c = For(p).Caption;
            return string.IsNullOrWhiteSpace(c) ? (p == Phase.Stand ? "Time to stand" : "Time to sit") : c.Trim();
        }
    }

    public static class MateStore
    {
        public static readonly string[] ImageExtensions = { ".gif", ".png", ".jpg", ".jpeg", ".bmp" };
        public static readonly string[] SoundExtensions = { ".wav", ".mp3", ".wma" };

        public static string Root => Path.Combine(Settings.DataDir, "mates");

        /// <summary>Default mates shipped next to the exe (copied into %APPDATA% on first run).</summary>
        public static string BundledRoot => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mates");

        public static List<Mate> LoadAll()
        {
            var list = new List<Mate>();
            if (!Directory.Exists(Root)) return list;
            foreach (var dir in Directory.GetDirectories(Root))
            {
                var m = Load(dir);
                if (m != null) list.Add(m);
            }
            return list.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static Mate Load(string folder)
        {
            var m = Json.TryRead<Mate>(Path.Combine(folder, "mate.json"));
            if (m == null) return null;
            m.Id = Path.GetFileName(folder);
            m.Folder = folder;
            if (string.IsNullOrWhiteSpace(m.Name)) m.Name = m.Id;
            if (m.Stand == null) m.Stand = new MateAction();
            if (m.Sit == null) m.Sit = new MateAction();
            return m;
        }

        public static void Save(Mate m)
        {
            Directory.CreateDirectory(m.Folder);
            Json.Write(Path.Combine(m.Folder, "mate.json"), m);
        }

        /// <summary>
        /// Copies bundled mates the user hasn't been given yet into their mates folder (never overwrites).
        /// Each one is offered only once, so a default mate the user deleted stays deleted after updates.
        /// Returns the updated list of seeded ids.
        /// </summary>
        public static string[] SeedDefaults(IEnumerable<string> alreadySeeded)
        {
            var seeded = new HashSet<string>(alreadySeeded ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(BundledRoot)) return seeded.ToArray();
            foreach (var src in Directory.GetDirectories(BundledRoot))
            {
                string id = Path.GetFileName(src);
                if (!seeded.Add(id)) continue;
                var dest = Path.Combine(Root, id);
                if (!Directory.Exists(dest)) CopyDirectory(src, dest);
            }
            return seeded.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        /// <summary>Re-copies files a seeded default mate is missing (e.g. GIFs fetched after first run).</summary>
        public static void RepairDefaults()
        {
            if (!Directory.Exists(BundledRoot)) return;
            foreach (var src in Directory.GetDirectories(BundledRoot))
            {
                var dest = Path.Combine(Root, Path.GetFileName(src));
                if (!Directory.Exists(dest)) continue;
                foreach (var f in Directory.GetFiles(src))
                {
                    var target = Path.Combine(dest, Path.GetFileName(f));
                    if (!File.Exists(target)) TryCopy(f, target);
                }
            }
        }

        public static Mate Create(string name)
        {
            var m = new Mate
            {
                Name = string.IsNullOrWhiteSpace(name) ? "New mate" : name.Trim(),
                Stand = new MateAction(),
                Sit = new MateAction(),
            };
            m.Id = UniqueId(m.Name);
            m.Folder = Path.Combine(Root, m.Id);
            return m;
        }

        public static Mate Duplicate(Mate src)
        {
            var copy = Create(src.Name + " copy");
            CopyDirectory(src.Folder, copy.Folder);
            var loaded = Load(copy.Folder);
            loaded.Name = copy.Name;
            Save(loaded);
            return loaded;
        }

        public static void Delete(Mate m)
        {
            if (m?.Folder != null && Directory.Exists(m.Folder)) Directory.Delete(m.Folder, true);
        }

        /// <summary>
        /// Copies a user-picked file into the mate's folder as e.g. "stand.gif" / "sit-sound.mp3"
        /// so the mate keeps working if the original moves. Returns the stored file name.
        /// </summary>
        public static string Import(Mate m, string sourcePath, string baseName)
        {
            Directory.CreateDirectory(m.Folder);
            string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            string fileName = baseName + ext;
            string dest = Path.Combine(m.Folder, fileName);
            if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                File.Copy(sourcePath, dest, true);
            return fileName;
        }

        /// <summary>Deletes media files in the mate folder that mate.json no longer references.</summary>
        public static void CleanUnused(Mate m)
        {
            if (m.Folder == null || !Directory.Exists(m.Folder)) return;
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mate.json" };
            foreach (var a in new[] { m.Stand, m.Sit })
            {
                if (a?.Image != null) used.Add(Path.GetFileName(a.Image));
                if (a?.Sound != null) used.Add(Path.GetFileName(a.Sound));
            }
            foreach (var f in Directory.GetFiles(m.Folder))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                bool media = ImageExtensions.Contains(ext) || SoundExtensions.Contains(ext);
                if (media && !used.Contains(Path.GetFileName(f))) TryDelete(f);
            }
        }

        private static string UniqueId(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name.ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
            }
            string slug = sb.ToString().Trim('-');
            if (slug.Length == 0) slug = "mate";
            if (slug.Length > 32) slug = slug.Substring(0, 32).Trim('-');
            string id = slug;
            for (int i = 2; Directory.Exists(Path.Combine(Root, id)); i++) id = slug + "-" + i;
            return id;
        }

        private static void CopyDirectory(string src, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var f in Directory.GetFiles(src)) TryCopy(f, Path.Combine(dest, Path.GetFileName(f)));
        }

        private static void TryCopy(string src, string dest)
        {
            try { File.Copy(src, dest, false); } catch (IOException) { }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
