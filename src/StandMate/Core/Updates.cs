using System;
using System.IO;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace StandMate.Core
{
    /// <summary>
    /// latest.json, published with every GitHub release next to the installer and its signature
    /// (latest.json.sig). Written and signed by release.ps1.
    /// </summary>
    [DataContract]
    public sealed class UpdateManifest
    {
        [DataMember(Name = "version")] public string Version;
        [DataMember(Name = "url")] public string Url;          // the installer
        [DataMember(Name = "sha256")] public string Sha256;    // of the installer, hex
        [DataMember(Name = "size")] public long Size;           // of the installer, bytes
        [DataMember(Name = "notes")] public string Notes;      // shown in Settings › General
    }

    /// <summary>Pure update logic: versions, signature and hash checks. The network part is in Platform/UpdateClient.</summary>
    public static class Updates
    {
        public const long MaxManifestBytes = 64 * 1024;
        public const long MaxInstallerBytes = 50L * 1024 * 1024;

        /// <summary>Parses "1.2", "1.2.3" or "v1.2.3" into a three-part version; null when it isn't one.</summary>
        public static Version ParseVersion(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);
            if (!System.Version.TryParse(text, out var v)) return null;
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }

        public static bool IsNewer(Version current, Version offered) =>
            current != null && offered != null && Normalize(offered) > Normalize(current);

        /// <summary>A different MAJOR version: the update may change things enough that the timer starts fresh.</summary>
        public static bool IsMajorChange(Version from, Version to) =>
            from == null || to == null || from.Major != to.Major;

        private static Version Normalize(Version v) => new Version(v.Major, v.Minor, Math.Max(0, v.Build));

        /// <summary>RSA (PKCS#1 v1.5) over SHA-256. <paramref name="signatureBase64"/> is the content of latest.json.sig.</summary>
        public static bool VerifySignature(byte[] data, string signatureBase64, string publicKeyXml)
        {
            if (data == null || string.IsNullOrWhiteSpace(signatureBase64) || string.IsNullOrWhiteSpace(publicKeyXml)) return false;
            try
            {
                byte[] sig = Convert.FromBase64String(signatureBase64.Trim());
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.PersistKeyInCsp = false;
                    rsa.FromXmlString(publicKeyXml);
                    return rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }
            }
            catch (FormatException) { return false; }
            catch (CryptographicException) { return false; }
        }

        /// <summary>Checks a signed manifest's content. Returns null when it's usable, otherwise why not.</summary>
        public static string Validate(UpdateManifest m, bool allowHttp)
        {
            if (m == null) return "The update information couldn't be read.";
            if (ParseVersion(m.Version) == null) return "The update has no valid version number.";
            if (!Uri.TryCreate(m.Url, UriKind.Absolute, out var url)) return "The update has no download link.";
            if (url.Scheme != Uri.UriSchemeHttps && !(allowHttp && url.Scheme == Uri.UriSchemeHttp && url.IsLoopback))
                return "The update's download link isn't secure.";
            if (string.IsNullOrWhiteSpace(m.Sha256) || m.Sha256.Trim().Length != 64) return "The update has no checksum.";
            if (m.Size <= 0 || m.Size > MaxInstallerBytes) return "The update has an unexpected size.";
            return null;
        }

        public static UpdateManifest ParseManifest(byte[] data)
        {
            try { return data == null ? null : Json.Parse<UpdateManifest>(data); }
            catch (Exception) { return null; }
        }

        public static string Sha256Hex(Stream s)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(s);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static bool FileMatches(string path, string sha256Hex, long size)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length != size) return false;
                using (var f = File.OpenRead(path))
                    return string.Equals(Sha256Hex(f), sha256Hex?.Trim(), StringComparison.OrdinalIgnoreCase);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }
}
