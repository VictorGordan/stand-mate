using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using StandMate.Core;

namespace StandMate.Platform
{
    /// <summary>
    /// The only network code in StandMate. It runs solely when the user presses "Check for updates" / "Update now":
    /// it downloads latest.json + its signature from the project's GitHub releases and, if asked, the installer.
    /// Nothing about the user or this PC is sent: just a plain HTTPS GET with a generic User-Agent.
    /// </summary>
    internal static class UpdateClient
    {
        private const string Placeholder = "OWNER/stand-mate";
        private const int TimeoutMs = 15000;

        /// <summary>Set by the developer flag --update-url (testing against a local server).</summary>
        public static string FeedOverride { get; set; }

        public static Version CurrentVersion
        {
            get
            {
                var v = typeof(UpdateClient).Assembly.GetName().Version;
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        /// <summary>"owner/repo" baked in at build time (csproj UpdateRepo); null when this build has none.</summary>
        public static string Repo
        {
            get
            {
                var repo = typeof(UpdateClient).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(a => a.Key == "UpdateRepo")?.Value;
                return string.IsNullOrWhiteSpace(repo) || repo == Placeholder ? null : repo.Trim();
            }
        }

        public static bool IsConfigured => FeedOverride != null || Repo != null;

        private static string ManifestUrl => FeedOverride ??
            $"https://github.com/{Repo}/releases/latest/download/latest.json";

        /// <summary>Downloads and verifies latest.json. Throws <see cref="UpdateException"/> with a readable message.</summary>
        public static UpdateManifest FetchManifest(CancellationToken ct)
        {
            if (!IsConfigured) throw new UpdateException("Updates aren't set up in this build.");
            byte[] manifest = Get(ManifestUrl, Updates.MaxManifestBytes, null, ct);
            byte[] sig = Get(ManifestUrl + ".sig", 16 * 1024, null, ct);

            if (!Updates.VerifySignature(manifest, System.Text.Encoding.ASCII.GetString(sig), ReleaseKey.PublicKeyXml))
                throw new UpdateException("The update information isn't signed by StandMate's release key, so it was ignored.");

            var m = Updates.ParseManifest(manifest);
            string problem = Updates.Validate(m, allowHttp: FeedOverride != null);
            if (problem != null) throw new UpdateException(problem);
            return m;
        }

        /// <summary>Downloads the installer into %TEMP% and checks its size and SHA-256 against the signed manifest.</summary>
        public static string DownloadInstaller(UpdateManifest m, Action<int> progress, CancellationToken ct)
        {
            string dir = Path.Combine(Path.GetTempPath(), "StandMate-update");
            Directory.CreateDirectory(dir);
            foreach (var old in Directory.GetFiles(dir, "*.exe")) TryDelete(old);
            string path = Path.Combine(dir, "StandMate-Setup-" + Updates.ParseVersion(m.Version) + ".exe");

            using (var file = File.Create(path))
                Get(m.Url, m.Size, file, ct, progress);

            if (!Updates.FileMatches(path, m.Sha256, m.Size))
            {
                TryDelete(path);
                throw new UpdateException("The download didn't match the published checksum, so it wasn't installed.");
            }
            return path;
        }

        /// <summary>GET with a size cap. Returns the bytes, or streams them into <paramref name="sink"/>.</summary>
        private static byte[] Get(string url, long maxBytes, Stream sink, CancellationToken ct, Action<int> progress = null)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "StandMate/" + CurrentVersion;
                req.Timeout = TimeoutMs;
                req.ReadWriteTimeout = TimeoutMs;
                req.AllowAutoRedirect = true;      // GitHub release links redirect to its download host
                req.AutomaticDecompression = DecompressionMethods.None;
                using (ct.Register(() => req.Abort()))
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var body = resp.GetResponseStream())
                {
                    long expected = resp.ContentLength > 0 ? resp.ContentLength : maxBytes;
                    if (resp.ContentLength > maxBytes) throw new UpdateException("The download is larger than expected.");
                    var buffer = new MemoryStream();
                    var target = sink ?? buffer;
                    var chunk = new byte[81920];
                    long total = 0;
                    int lastPct = -1, n;
                    while ((n = body.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        total += n;
                        if (total > maxBytes) throw new UpdateException("The download is larger than expected.");
                        target.Write(chunk, 0, n);
                        int pct = (int)(total * 100 / Math.Max(1, expected));
                        if (progress != null && pct != lastPct) { lastPct = pct; progress(Math.Min(100, pct)); }
                    }
                    return sink == null ? buffer.ToArray() : null;
                }
            }
            catch (WebException ex) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException("Cancelled.", ex, ct);
            }
            catch (WebException ex)
            {
                if (ex.Response is HttpWebResponse r && r.StatusCode == HttpStatusCode.NotFound)
                    throw new UpdateException("No release has been published yet.");
                throw new UpdateException("Couldn't reach GitHub. Check your connection and try again.");
            }
            catch (IOException)
            {
                throw new UpdateException("The download was interrupted. Try again.");
            }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    internal sealed class UpdateException : Exception
    {
        public UpdateException(string message) : base(message) { }
    }
}
