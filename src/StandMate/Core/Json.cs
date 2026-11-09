using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

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
        public static void Write<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(fs, new UTF8Encoding(false), false, true, "  "))
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(writer, value);
                writer.Flush();
            }
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }
}
