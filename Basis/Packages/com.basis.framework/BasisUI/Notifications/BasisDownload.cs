using System;
using System.Threading;

namespace Basis.BasisUI
{
    public sealed class BasisDownload
    {
        public readonly Guid Id = Guid.NewGuid();
        public readonly DateTime StartedUtc = DateTime.UtcNow;

        public BasisDownloadKind Kind;
        public string Url;
        public string Owner;
        public BasisLoadableBundle Bundle;

        public volatile float Progress;
        public volatile string Stage;
        public volatile bool CancelRequested;

        internal CancellationTokenSource Cancellation;
        internal BasisProgressReport Report;
        internal bool Ended;

        public bool CanCancel => Cancellation != null && !Ended;

        public string ContentName
        {
            get
            {
                string name = Bundle?.BasisBundleConnector?.BasisBundleDescription?.AssetBundleName;
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
        }

        internal void OnProgress(string uniqueId, float progress, string stage)
        {
            Progress = progress;
            if (!string.IsNullOrEmpty(stage))
            {
                Stage = stage;
            }
        }
    }
}
