using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using StandMate.Core;
using Xunit;

namespace StandMate.Tests
{
    public class UpdateTests
    {
        private static readonly byte[] Manifest = Encoding.UTF8.GetBytes(
            "{ \"version\": \"1.3.0\", \"url\": \"https://github.com/o/r/releases/download/v1.3.0/StandMate-Setup.exe\", " +
            "\"sha256\": \"" + new string('a', 64) + "\", \"size\": 1234, \"notes\": \"New stuff\" }");

        private static (string publicXml, string signature) Sign(byte[] data)
        {
            using (var rsa = new RSACryptoServiceProvider(2048) { PersistKeyInCsp = false })
            {
                var sig = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                return (rsa.ToXmlString(false), Convert.ToBase64String(sig));
            }
        }

        [Theory]
        [InlineData("1.2.0", "1.2.1", true)]
        [InlineData("1.2.0", "1.10.0", true)]
        [InlineData("1.2.0", "v2.0", true)]
        [InlineData("1.2.0", "1.2.0", false)]
        [InlineData("1.2.0", "1.1.9", false)]
        [InlineData("1.2.0", "nonsense", false)]
        public void Newer_versions_are_detected(string current, string offered, bool expected)
        {
            Assert.Equal(expected, Updates.IsNewer(Updates.ParseVersion(current), Updates.ParseVersion(offered)));
        }

        [Fact]
        public void Only_a_different_major_counts_as_major()
        {
            Assert.False(Updates.IsMajorChange(new Version(1, 2, 0), new Version(1, 9, 3)));
            Assert.True(Updates.IsMajorChange(new Version(1, 9, 3), new Version(2, 0, 0)));
        }

        [Fact]
        public void A_correctly_signed_manifest_verifies_and_parses()
        {
            var (key, sig) = Sign(Manifest);
            Assert.True(Updates.VerifySignature(Manifest, sig, key));
            var m = Updates.ParseManifest(Manifest);
            Assert.Equal("1.3.0", m.Version);
            Assert.Equal(1234, m.Size);
            Assert.Null(Updates.Validate(m, allowHttp: false));
        }

        [Fact]
        public void A_tampered_manifest_is_rejected()
        {
            var (key, sig) = Sign(Manifest);
            var tampered = (byte[])Manifest.Clone();
            tampered[20] ^= 1;
            Assert.False(Updates.VerifySignature(tampered, sig, key));
        }

        [Fact]
        public void A_signature_from_another_key_is_rejected()
        {
            var (_, sig) = Sign(Manifest);
            var (otherKey, _) = Sign(Manifest);
            Assert.False(Updates.VerifySignature(Manifest, sig, otherKey));
            Assert.False(Updates.VerifySignature(Manifest, "not base64!", otherKey));
            Assert.False(Updates.VerifySignature(Manifest, "", otherKey));
        }

        [Fact]
        public void Insecure_or_incomplete_manifests_are_rejected()
        {
            var m = Updates.ParseManifest(Manifest);
            m.Url = "http://example.com/setup.exe";
            Assert.NotNull(Updates.Validate(m, allowHttp: false));
            Assert.NotNull(Updates.Validate(m, allowHttp: true));   // http only for localhost testing
            m.Url = "http://127.0.0.1:8000/setup.exe";
            Assert.NotNull(Updates.Validate(m, allowHttp: false));
            Assert.Null(Updates.Validate(m, allowHttp: true));

            m = Updates.ParseManifest(Manifest);
            m.Sha256 = "abc";
            Assert.NotNull(Updates.Validate(m, false));
            m = Updates.ParseManifest(Manifest);
            m.Size = Updates.MaxInstallerBytes + 1;
            Assert.NotNull(Updates.Validate(m, false));
            Assert.NotNull(Updates.Validate(null, false));
            Assert.Null(Updates.ParseManifest(Encoding.UTF8.GetBytes("<html>")));
        }

        [Fact]
        public void Downloaded_file_must_match_hash_and_size()
        {
            string path = Path.Combine(Path.GetTempPath(), "standmate_hash_test.bin");
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("hello"));
            try
            {
                const string helloSha = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";
                Assert.True(Updates.FileMatches(path, helloSha.ToUpperInvariant(), 5));
                Assert.False(Updates.FileMatches(path, helloSha, 6));
                Assert.False(Updates.FileMatches(path, new string('0', 64), 5));
                Assert.False(Updates.FileMatches(path + ".missing", helloSha, 5));
            }
            finally { File.Delete(path); }
        }
    }
}
