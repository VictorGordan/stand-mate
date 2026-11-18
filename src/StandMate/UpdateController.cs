using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StandMate.Core;
using StandMate.Platform;

namespace StandMate
{
    internal enum UpdateStage { Idle, Checking, UpToDate, Available, Downloading, Installing, Failed }

    /// <summary>
    /// "Check for updates" / "Update now", and going back to an older version from the version list. Network work
    /// runs on a background task; every state change is posted back to the UI thread and announced through
    /// <see cref="Changed"/>. Lives as long as the app, so reopening Settings shows where things are.
    /// </summary>
    internal sealed class UpdateController
    {
        private readonly Action<Action> _post;
        private readonly Action<string, bool> _install;
        private CancellationTokenSource _cts;

        /// <param name="post">Runs an action on the UI thread.</param>
        /// <param name="install">
        /// Hands the verified installer to the app, which saves its state and runs it. The flag asks it to back up the
        /// settings and mates first (going back to an older version).
        /// </param>
        public UpdateController(Action<Action> post, Action<string, bool> install)
        {
            _post = post;
            _install = install;
        }

        public event Action Changed;

        public UpdateStage Stage { get; private set; }
        public string Message { get; private set; }
        public UpdateManifest Offer { get; private set; }
        public int Progress { get; private set; }

        /// <summary>Published versions older than this one, newest first; null until the version list was opened.</summary>
        public List<Version> OlderVersions { get; private set; }

        /// <summary>The version list is being fetched.</summary>
        public bool Listing { get; private set; }

        public Version Current => UpdateClient.CurrentVersion;
        public Version OfferVersion => Updates.ParseVersion(Offer?.Version);
        public bool OfferIsMajor => Offer != null && Updates.IsMajorChange(Current, OfferVersion);
        public bool CanGoBack => UpdateClient.CanGoBack;
        public bool Busy => Listing || Stage == UpdateStage.Checking || Stage == UpdateStage.Downloading || Stage == UpdateStage.Installing;

        /// <summary>Set after an in-place update, e.g. "Updated to 1.3.0".</summary>
        public void ShowJustUpdated(string fromVersion)
        {
            Stage = UpdateStage.UpToDate;
            Message = $"Updated from {fromVersion} to {Current}.";
        }

        /// <summary>Set when a backup from before going back to an older version was merged in at startup.</summary>
        public void ShowBackupRestored()
        {
            const string note = "Settings the older version didn't keep were restored from your backup.";
            if (Stage == UpdateStage.Idle) Stage = UpdateStage.UpToDate;
            Message = string.IsNullOrEmpty(Message) ? note : Message + " " + note;
        }

        /// <summary>Fetches the published versions for the version list (only when it's opened).</summary>
        public void ListOlderVersions()
        {
            if (Busy) return;
            if (!UpdateClient.CanGoBack)
            {
                Set(UpdateStage.Failed, "Going back to older versions isn't available in this build.");
                return;
            }
            Listing = true;
            Changed?.Invoke();
            Task.Run(() =>
            {
                try
                {
                    var releases = UpdateClient.ListReleases(CancellationToken.None);
                    _post(() =>
                    {
                        Listing = false;
                        OlderVersions = Updates.OlderVersions(releases, Current);
                        Changed?.Invoke();
                    });
                }
                catch (Exception ex)
                {
                    _post(() => Listing = false);
                    Fail(ex);
                }
            });
        }

        /// <summary>Installs <paramref name="version"/>, verified like an update; <paramref name="backup"/> keeps a copy of the settings and mates first.</summary>
        public void Revert(Version version, bool backup)
        {
            if (Busy || version == null) return;
            Offer = null;
            Set(UpdateStage.Checking, $"Getting version {version}…");
            var ct = NewToken();
            Task.Run(() =>
            {
                try
                {
                    var m = UpdateClient.FetchManifest(version, ct);
                    _post(() => Download(m, backup));
                }
                catch (Exception ex) { Fail(ex); }
            });
        }

        public void Check()
        {
            if (Busy) return;
            if (!UpdateClient.IsConfigured)
            {
                Set(UpdateStage.Failed, "Updates aren't set up in this build.");
                return;
            }
            Offer = null;
            Set(UpdateStage.Checking, "Checking…");
            var ct = NewToken();
            Task.Run(() =>
            {
                try
                {
                    var m = UpdateClient.FetchManifest(ct);
                    _post(() =>
                    {
                        if (Updates.IsNewer(Current, Updates.ParseVersion(m.Version)))
                        {
                            Offer = m;
                            Set(UpdateStage.Available, $"Version {OfferVersion} is available.");
                        }
                        else Set(UpdateStage.UpToDate, $"You have the latest version ({Current}).");
                    });
                }
                catch (Exception ex) { Fail(ex); }
            });
        }

        public void Install()
        {
            if (Busy || Offer == null) return;
            Download(Offer, backup: false);
        }

        private void Download(UpdateManifest offer, bool backup)
        {
            Progress = 0;
            Set(UpdateStage.Downloading, "Downloading… 0 %");
            var ct = NewToken();
            Task.Run(() =>
            {
                try
                {
                    string path = UpdateClient.DownloadInstaller(offer, pct => _post(() =>
                    {
                        if (Stage != UpdateStage.Downloading || pct == Progress) return;
                        Progress = pct;
                        Set(UpdateStage.Downloading, $"Downloading… {pct} %");
                    }), ct);
                    _post(() =>
                    {
                        Set(UpdateStage.Installing, "Installing… StandMate will be back in a moment.");
                        _install(path, backup);
                    });
                }
                catch (Exception ex) { Fail(ex); }
            });
        }

        public void Cancel() => _cts?.Cancel();

        /// <summary>The app couldn't start the installer (or back up first): nothing was installed.</summary>
        public void InstallAborted(string message) => Set(UpdateStage.Failed, message);

        private CancellationToken NewToken()
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            return _cts.Token;
        }

        private void Fail(Exception ex)
        {
            string msg = ex is UpdateException ? ex.Message
                : ex is OperationCanceledException ? "Cancelled."
                : "Something went wrong while updating. Try again later.";
            if (!(ex is UpdateException) && !(ex is OperationCanceledException)) Program.LogError(ex);
            _post(() => Set(UpdateStage.Failed, msg));
        }

        private void Set(UpdateStage stage, string message)
        {
            Stage = stage;
            Message = message;
            Changed?.Invoke();
        }
    }
}
