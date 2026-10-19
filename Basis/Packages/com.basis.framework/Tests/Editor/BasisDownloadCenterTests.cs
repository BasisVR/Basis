using System.Collections.Generic;
using System.Threading;
using Basis.BasisUI;
using NUnit.Framework;

namespace Basis.Tests.UI
{
    public class BasisDownloadCenterTests
    {
        private static bool IsListed(BasisDownload download)
        {
            List<BasisDownload> active = new List<BasisDownload>();
            BasisDownloadCenter.CopyActive(active);
            return active.Contains(download);
        }

        [Test]
        public void Begin_ListsTheDownloadAndEnd_RemovesIt()
        {
            int version = BasisDownloadCenter.Version;
            BasisDownload download = BasisDownloadCenter.Begin(BasisDownloadKind.World, "https://example.invalid/world.bee", null, null);
            try
            {
                Assert.That(IsListed(download), Is.True);
                Assert.That(BasisDownloadCenter.Version, Is.Not.EqualTo(version));
            }
            finally
            {
                version = BasisDownloadCenter.Version;
                BasisDownloadCenter.End(download);
            }
            Assert.That(IsListed(download), Is.False);
            Assert.That(BasisDownloadCenter.Version, Is.Not.EqualTo(version));
        }

        [Test]
        public void End_IsSafeToCallTwice()
        {
            BasisDownload download = BasisDownloadCenter.Begin(BasisDownloadKind.Prop, "https://example.invalid/prop.bee", null, null);
            BasisDownloadCenter.End(download);
            int version = BasisDownloadCenter.Version;
            BasisDownloadCenter.End(download);
            BasisDownloadCenter.End(null);
            Assert.That(BasisDownloadCenter.Version, Is.EqualTo(version));
        }

        [Test]
        public void Progress_FollowsTheReportUntilEnd()
        {
            BasisProgressReport report = new BasisProgressReport();
            BasisDownload download = BasisDownloadCenter.Begin(BasisDownloadKind.Avatar, "https://example.invalid/avatar.bee", null, null, report, "Someone");
            try
            {
                report.ReportProgress("a", 42f, "Downloading data...");
                Assert.That(download.Progress, Is.EqualTo(42f));
                Assert.That(download.Stage, Is.EqualTo("Downloading data..."));

                report.ReportProgress("a", 50f, string.Empty);
                Assert.That(download.Progress, Is.EqualTo(50f));
                Assert.That(download.Stage, Is.EqualTo("Downloading data..."), "an empty stage keeps the last one shown");
            }
            finally
            {
                BasisDownloadCenter.End(download);
            }

            report.ReportProgress("a", 90f, "Loading bundle");
            Assert.That(download.Progress, Is.EqualTo(50f), "an ended download no longer listens to its report");
        }

        [Test]
        public void Progress_ThroughAStageLandsOnTheRootValue()
        {
            BasisProgressReport report = new BasisProgressReport();
            BasisDownload download = BasisDownloadCenter.Begin(BasisDownloadKind.World, "https://example.invalid/world.bee", null, null, report);
            try
            {
                report.Stage("load", 0f, 50f).ReportProgress("inner", 50f, "Downloading data...");
                Assert.That(download.Progress, Is.EqualTo(25f));
            }
            finally
            {
                BasisDownloadCenter.End(download);
            }
        }

        [Test]
        public void Cancel_CancelsTheSourceAndFlagsTheDownload()
        {
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            BasisDownload download = BasisDownloadCenter.Begin(BasisDownloadKind.World, "https://example.invalid/world.bee", null, cancellation);
            try
            {
                Assert.That(download.CanCancel, Is.True);
                Assert.That(BasisDownloadCenter.Cancel(download), Is.True);
                Assert.That(cancellation.IsCancellationRequested, Is.True);
                Assert.That(download.CancelRequested, Is.True);
                Assert.That(IsListed(download), Is.True, "the load site ends the download once it has unwound");
            }
            finally
            {
                BasisDownloadCenter.End(download);
            }
        }

        [Test]
        public void Cancel_AfterEndDoesNothing()
        {
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            BasisDownload download = BasisDownloadCenter.Begin(BasisDownloadKind.Prop, "https://example.invalid/prop.bee", null, cancellation);
            BasisDownloadCenter.End(download);

            Assert.That(download.CanCancel, Is.False);
            Assert.That(BasisDownloadCenter.Cancel(download), Is.False);
            Assert.That(cancellation.IsCancellationRequested, Is.False);
            Assert.That(download.CancelRequested, Is.False);
        }

        [Test]
        public void Cancel_WithoutASourceReturnsFalse()
        {
            BasisDownload download = BasisDownloadCenter.Begin(BasisDownloadKind.Prop, "https://example.invalid/prop.bee", null, null);
            try
            {
                Assert.That(download.CanCancel, Is.False);
                Assert.That(BasisDownloadCenter.Cancel(download), Is.False);
                Assert.That(download.CancelRequested, Is.False);
            }
            finally
            {
                BasisDownloadCenter.End(download);
            }
        }

        [Test]
        public void Cancel_OnADisposedSourceDoesNotThrow()
        {
            CancellationTokenSource cancellation = new CancellationTokenSource();
            BasisDownload download = BasisDownloadCenter.Begin(BasisDownloadKind.Avatar, "https://example.invalid/avatar.bee", null, cancellation);
            try
            {
                cancellation.Dispose();
                Assert.DoesNotThrow(() => BasisDownloadCenter.Cancel(download));
            }
            finally
            {
                BasisDownloadCenter.End(download);
            }
        }

        [Test]
        public void ContentName_ComesFromTheConnectorOnceItArrives()
        {
            BasisLoadableBundle bundle = new BasisLoadableBundle();
            BasisDownload download = BasisDownloadCenter.Begin(BasisDownloadKind.World, "https://example.invalid/world.bee", bundle, null);
            try
            {
                Assert.That(download.ContentName, Is.Null);
                bundle.BasisBundleConnector = new BasisBundleConnector { BasisBundleDescription = new BasisBundleDescription { AssetBundleName = "  " } };
                Assert.That(download.ContentName, Is.Null);
                bundle.BasisBundleConnector.BasisBundleDescription.AssetBundleName = "Test World";
                Assert.That(download.ContentName, Is.EqualTo("Test World"));
            }
            finally
            {
                BasisDownloadCenter.End(download);
            }
        }
    }
}
