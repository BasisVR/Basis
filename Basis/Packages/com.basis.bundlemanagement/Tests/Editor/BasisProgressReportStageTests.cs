using System.Collections.Generic;
using NUnit.Framework;

public class BasisProgressReportStageTests
{
    private static List<(string Key, float Progress, string Info)> Capture(BasisProgressReport report)
    {
        List<(string Key, float Progress, string Info)> seen = new List<(string Key, float Progress, string Info)>();
        report.OnProgressReport += (key, progress, info) => seen.Add((key, progress, info));
        return seen;
    }

    [Test]
    public void StageReportsUnderTheParentKeyAcrossItsRange()
    {
        BasisProgressReport root = new BasisProgressReport();
        List<(string Key, float Progress, string Info)> seen = Capture(root);
        BasisProgressReport stage = root.Stage("world", 20, 60);

        stage.ReportProgress("inner", 0, "start");
        stage.ReportProgress("inner", 50, "half");
        stage.ReportProgress("inner", 100, "done");

        Assert.AreEqual(3, seen.Count);
        Assert.AreEqual("world", seen[0].Key);
        Assert.AreEqual("world", seen[2].Key);
        Assert.AreEqual(20f, seen[0].Progress);
        Assert.AreEqual(40f, seen[1].Progress);
        Assert.AreEqual(60f, seen[2].Progress);
        Assert.AreEqual("done", seen[2].Info);
    }

    [Test]
    public void ConsecutiveStagesPileIntoOneRisingValue()
    {
        BasisProgressReport root = new BasisProgressReport();
        List<(string Key, float Progress, string Info)> seen = Capture(root);
        BasisProgressReport download = root.Stage("world", 0, 50);
        BasisProgressReport build = root.Stage("world", 50, 100);

        download.ReportProgress("section", 50, "Downloading data...");
        download.ReportProgress("section", 100, "Downloading Complete");
        build.ReportProgress("bundle", 0, "Loading bundle");
        build.ReportProgress("bundle", 50, "Loading bundle");

        Assert.AreEqual(25f, seen[0].Progress);
        Assert.AreEqual(50f, seen[1].Progress);
        Assert.AreEqual(50f, seen[2].Progress);
        Assert.AreEqual(75f, seen[3].Progress);
    }

    [Test]
    public void StageCompletionNeverReachesMaxValue()
    {
        BasisProgressReport root = new BasisProgressReport();
        List<(string Key, float Progress, string Info)> seen = Capture(root);

        root.Stage("world", 0, 75).ReportProgress("inner", 100, "stage done");
        root.Stage("world", 75, 100).ReportProgress("inner", 100, "last stage done");

        Assert.AreEqual(75f, seen[0].Progress);
        Assert.AreEqual(BasisProgressReport.StageCeiling, seen[1].Progress);
        Assert.Less(seen[1].Progress, BasisProgressReport.MaxValue);
    }

    [Test]
    public void OnlyTheOwnerReachesMaxValue()
    {
        BasisProgressReport root = new BasisProgressReport();
        List<(string Key, float Progress, string Info)> seen = Capture(root);

        root.Stage("world", 0, 100).ReportProgress("inner", 100, "last step");
        root.ReportProgress("world", 100, "done");

        Assert.Less(seen[0].Progress, BasisProgressReport.MaxValue);
        Assert.AreEqual(BasisProgressReport.MaxValue, seen[1].Progress);
        Assert.AreEqual("world", seen[1].Key);
    }

    [Test]
    public void NestedStagesCompose()
    {
        BasisProgressReport root = new BasisProgressReport();
        List<(string Key, float Progress, string Info)> seen = Capture(root);
        BasisProgressReport inner = root.Stage("world", 0, 80).Stage("ignored", 50, 100);

        inner.ReportProgress("leaf", 0, "a");
        inner.ReportProgress("leaf", 100, "b");

        Assert.AreEqual("world", seen[0].Key);
        Assert.AreEqual(40f, seen[0].Progress, 0.0001f);
        Assert.AreEqual(80f, seen[1].Progress, 0.0001f);
    }

    [Test]
    public void StageClampsProgressIntoItsRange()
    {
        BasisProgressReport root = new BasisProgressReport();
        List<(string Key, float Progress, string Info)> seen = Capture(root);
        BasisProgressReport stage = root.Stage("world", 10, 20);

        stage.ReportProgress("inner", -5, "under");
        stage.ReportProgress("inner", 250, "over");

        Assert.AreEqual(10f, seen[0].Progress);
        Assert.AreEqual(20f, seen[1].Progress);
    }

    [Test]
    public void RootReportsPassThroughUnchanged()
    {
        BasisProgressReport root = new BasisProgressReport();
        List<(string Key, float Progress, string Info)> seen = Capture(root);

        root.ReportProgress("own", 33, "raw");

        Assert.AreEqual("own", seen[0].Key);
        Assert.AreEqual(33f, seen[0].Progress);
        Assert.AreEqual("raw", seen[0].Info);
    }
}
