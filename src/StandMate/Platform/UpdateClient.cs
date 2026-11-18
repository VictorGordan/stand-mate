using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using StandMate.Core;

namespace StandMate.Platform
{
    /// <summary>
    /// The only network code in StandMate. It runs solely when the user presses "Check for updates" / "Update now" or
    /// opens the version list: it downloads latest.json + its signature from the project's GitHub releases (or the
    /// list of releases from GitHub's API) and, if asked, the installer.
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

        /// <summary>Going back needs the real GitHub releases (not a --update-url test feed).</summary>
        public static bool CanGoBack => Repo != null && FeedOverride == null;

        /// <summary>Downloads and verifies latest.json. Throws <see cref="UpdateException"/> with a readable message.</summary>
        public static UpdateManifest FetchManifest(CancellationToken ct)
        {
            if (!IsConfigured) throw new UpdateException("Updates aren't set up in this build.");
            return FetchManifest(ManifestUrl, FeedOverride != null, "No release has been published yet.", ct);
        }

        /// <summary>
        /// The signed latest.json published with release v<paramref name="version"/>, for going back to it. Its signature
        /// and its version must match, like for an update.
        /// </summary>
        public static UpdateManifest FetchManifest(Version version, CancellationToken ct)
        {
            if (!CanGoBack) throw new UpdateException("Going back to older versions isn't available in this build.");
            var m = FetchManifest($"https://github.com/{Repo}/releases/download/v{version}/latest.json", false,
                $"Version {version} can't be installed from here.", ct);
            if (Updates.ParseVersion(m.Version) != version)
                throw new UpdateException($"The files published for {version} are for another version, so they were ignored.");
            return m;
        }

        /// <summary>The published releases (GitHub's API; only tags and flags are read).</summary>
        public static List<ReleaseInfo> ListReleases(CancellationToken ct)
        {
            if (!CanGoBack) throw new UpdateException("Going back to older versions isn't available in this build.");
            byte[] data = Get($"https://api.github.com/repos/{Repo}/releases?per_page=100", Updates.MaxReleaseListBytes, null, ct,
                accept: "application/vnd.github+json", notFound: "No release has been published yet.");
            return Updates.ParseReleases(data) ?? throw new UpdateException("GitHub's list of versions couldn't be read.");
        }

        private static UpdateManifest FetchManifest(string url, bool allowHttp, string notFound, CancellationToken ct)
        {
            byte[] manifest = Get(url, Updates.MaxManifestBytes, null, ct, notFound: notFound);
            byte[] sig = Get(url + ".sig", 16 * 1024, null, ct, notFound: notFound);

            if (!Updates.VerifySignature(manifest, System.Text.Encoding.ASCII.GetString(sig), ReleaseKey.PublicKeyXml))
                throw new UpdateException("The update information isn't signed by StandMate's release key, so it was ignored.");

            var m = Updates.ParseManifest(manifest);
            string problem = Updates.Validate(m, allowHttp);
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
        private static byte[] Get(string url, long maxBytes, Stream sink, CancellationToken ct, Action<int> progress = null,
            string accept = null, string notFound = "No release has been published yet.")
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "StandMate/" + CurrentVersion;
                if (accept != null) req.Accept = accept;
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
                    throw new UpdateException(notFound);
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
