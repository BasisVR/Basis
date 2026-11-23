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
        public volatile string DisplayName, Details;
        public Action CancelAction;
        public Func<bool> CancelAllowed;

        internal CancellationTokenSource Cancellation;
        internal BasisProgressReport Report;
        internal bool Ended;

        public bool CanCancel => !Ended && (Cancellation != null || (CancelAction != null && (CancelAllowed == null || CancelAllowed())));

        public string ContentName
        {
            get
            {
                string name = DisplayName ?? Bundle?.BasisBundleConnector?.BasisBundleDescription?.AssetBundleName;
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
