using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace StandMate.Core
{
    /// <summary>Tiny JSON helper on top of the built-in DataContractJsonSerializer (no extra DLLs).</summary>
    public static class Json
    {
        public static T Read<T>(string path) where T : class => Parse<T>(File.ReadAllBytes(path));

        public static T Parse<T>(byte[] bytes) where T : class
        {
            // Editors like Notepad may save a UTF-8 byte-order mark, which the JSON reader rejects.
            int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            using (var ms = new MemoryStream(bytes, skip, bytes.Length - skip))
            {
                var ser = new DataContractJsonSerializer(typeof(T));
                return ser.ReadObject(ms) as T;
            }
        }

        public static T TryRead<T>(string path) where T : class
        {
            try { return File.Exists(path) ? Read<T>(path) : null; }
            catch (Exception) { return null; }
        }

        /// <summary>Writes indented JSON atomically (temp file + replace) so a crash never leaves a half-written file.</summary>
        public static void Write<T>(string path, T value) =>
            WriteAtomically(path, writer => new DataContractJsonSerializer(typeof(T)).WriteObject(writer, value));

        /// <summary>
        /// Copies the top-level keys of the JSON object in <paramref name="fromPath"/> that the one in
        /// <paramref name="targetPath"/> doesn't have; keys it already has are left alone. Works on any JSON object,
        /// so it keeps keys this version doesn't know either. Returns the added values (empty when nothing changed).
        /// </summary>
        public static List<XElement> FillMissingKeys(string targetPath, string fromPath)
        {
            var added = new List<XElement>();
            var target = ReadTree(targetPath);
            var from = ReadTree(fromPath);
            if (!IsObject(target) || !IsObject(from)) return added;

            var have = new HashSet<string>(target.Root.Elements().Select(KeyOf), StringComparer.Ordinal);
            foreach (var e in from.Root.Elements())
            {
                if (have.Contains(KeyOf(e))) continue;
                var copy = new XElement(e);
                target.Root.Add(copy);
                added.Add(copy);
            }
            if (added.Count == 0) return added;
            // JSON nulls read back as elements holding "", which the JSON writer refuses: make them truly empty.
            foreach (var n in target.Root.Descendants().Where(e => (string)e.Attribute("type") == "null").ToList())
                n.ReplaceWith(new XElement(n.Name, n.Attributes()));
            WriteAtomically(targetPath, writer => target.Root.WriteTo(writer));
            return added;
        }

        /// <summary>JSON as the XML tree of <see cref="JsonReaderWriterFactory"/>; null when missing or unreadable.</summary>
        private static XDocument ReadTree(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var bytes = File.ReadAllBytes(path);
                int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                using (var reader = JsonReaderWriterFactory.CreateJsonReader(bytes, skip, bytes.Length - skip, XmlDictionaryReaderQuotas.Max))
                    return XDocument.Load(reader);
            }
            catch (Exception) { return null; }
        }

        private static bool IsObject(XDocument d) => (string)d?.Root?.Attribute("type") == "object";

        /// <summary>The JSON key of an element (keys that aren't valid XML names are stored as &lt;item item="key"&gt;).</summary>
        private static string KeyOf(XElement e) => (string)e.Attribute("item") ?? e.Name.LocalName;

        private static void WriteAtomically(string path, Action<XmlDictionaryWriter> write)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(fs, new UTF8Encoding(false), false, true, "  "))
            {
                write(writer);
                writer.Flush();
            }
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }
}
