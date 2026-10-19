using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Basis.BasisUI
{
    [AutoStaticsCleanup]
    public static partial class BasisDownloadCenter
    {
        private static readonly List<BasisDownload> _active = new();
        [NoAutoStaticsCleanup] private static readonly object _lock = new();
        private static int _version;

        public static int Version => Volatile.Read(ref _version);

        public static int Count
        {
            get
            {
                lock (_lock)
                {
                    return _active.Count;
                }
            }
        }

        public static BasisDownload Begin(BasisDownloadKind kind, string url, BasisLoadableBundle bundle, CancellationTokenSource cancellation, BasisProgressReport report = null, string owner = null)
        {
            BasisDownload download = new BasisDownload
            {
                Kind = kind,
                Url = url,
                Bundle = bundle,
                Owner = owner,
                Cancellation = cancellation,
                Report = report,
            };

            if (report != null)
            {
                report.OnProgressReport += download.OnProgress;
            }

            lock (_lock)
            {
                _active.Add(download);
            }
            Interlocked.Increment(ref _version);
            return download;
        }

        public static void End(BasisDownload download)
        {
            if (download == null) return;

            BasisProgressReport report;
            lock (_lock)
            {
                if (download.Ended) return;
                download.Ended = true;
                download.Cancellation = null;
                report = download.Report;
                download.Report = null;
                _active.Remove(download);
            }

            if (report != null)
            {
                report.OnProgressReport -= download.OnProgress;
            }
            Interlocked.Increment(ref _version);
        }

        public static bool Cancel(BasisDownload download)
        {
            if (download == null) return false;

            CancellationTokenSource cancellation;
            lock (_lock)
            {
                cancellation = download.Cancellation;
                if (download.Ended || cancellation == null) return false;
                download.CancelRequested = true;
            }

            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            return true;
        }

        public static void CopyActive(List<BasisDownload> into)
        {
            into.Clear();
            lock (_lock)
            {
                into.AddRange(_active);
            }
        }
    }
}
