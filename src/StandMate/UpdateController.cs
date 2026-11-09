using System;
using System.Threading;
using System.Threading.Tasks;
using StandMate.Core;
using StandMate.Platform;

namespace StandMate
{
    internal enum UpdateStage { Idle, Checking, UpToDate, Available, Downloading, Installing, Failed }

    /// <summary>
    /// "Check for updates" / "Update now". Network work runs on a background task; every state change is
    /// posted back to the UI thread and announced through <see cref="Changed"/>. Lives as long as the app,
    /// so reopening Settings shows where things are.
    /// </summary>
    internal sealed class UpdateController
    {
        private readonly Action<Action> _post;
        private readonly Action<string> _install;
        private CancellationTokenSource _cts;

        /// <param name="post">Runs an action on the UI thread.</param>
        /// <param name="install">Hands the verified installer to the app, which saves its state and runs it.</param>
        public UpdateController(Action<Action> post, Action<string> install)
        {
            _post = post;
            _install = install;
        }

        public event Action Changed;

        public UpdateStage Stage { get; private set; }
        public string Message { get; private set; }
        public UpdateManifest Offer { get; private set; }
        public int Progress { get; private set; }

        public Version Current => UpdateClient.CurrentVersion;
        public Version OfferVersion => Updates.ParseVersion(Offer?.Version);
        public bool OfferIsMajor => Offer != null && Updates.IsMajorChange(Current, OfferVersion);
        public bool Busy => Stage == UpdateStage.Checking || Stage == UpdateStage.Downloading || Stage == UpdateStage.Installing;

        /// <summary>Set after an in-place update, e.g. "Updated to 1.3.0".</summary>
        public void ShowJustUpdated(string fromVersion)
        {
            Stage = UpdateStage.UpToDate;
            Message = $"Updated from {fromVersion} to {Current}.";
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
            var offer = Offer;
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
                        _install(path);
                    });
                }
                catch (Exception ex) { Fail(ex); }
            });
        }

        public void Cancel() => _cts?.Cancel();

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
