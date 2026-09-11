using System;
using Basis.Scripts.Avatar;
using Basis.Scripts.BasisSdk.Players;
using NUnit.Framework;

public class BasisRemoteDownloadLimitTests
{
    private const long Uncapped = 4L * 1024 * 1024 * 1024;
    private const long Cap = 64L * 1024 * 1024;
    private long savedCap;
    private bool savedBypassAll;

    [SetUp]
    public void SaveStatics()
    {
        savedCap = BasisAvatarFactory.MaxDownloadSizeInMBRemote;
        savedBypassAll = BasisAvatarPerformanceLimits.BypassAllLimits;
        BasisAvatarFactory.MaxDownloadSizeInMBRemote = Cap;
        BasisAvatarPerformanceLimits.BypassAllLimits = false;
    }

    [TearDown]
    public void RestoreStatics()
    {
        BasisAvatarFactory.MaxDownloadSizeInMBRemote = savedCap;
        BasisAvatarPerformanceLimits.BypassAllLimits = savedBypassAll;
    }

    private static string ThisPlatform()
    {
        foreach (string name in Enum.GetNames(typeof(BasisBundleConnector.BuildTarget)))
        {
            if (BasisBundleConnector.PlatformMatch(name))
            {
                return name;
            }
        }
        Assert.Ignore("No bee platform maps to this editor.");
        return null;
    }

    private static BasisBundleGenerated Section(string platform, long bytes)
    {
        return new BasisBundleGenerated { Platform = platform, EndByte = bytes };
    }

    private static BasisRemotePlayer RefusedPlayer(params BasisBundleGenerated[] sections)
    {
        return new BasisRemotePlayer
        {
            HasFailedAvatarLoadGlobally = true,
            AvatarLoadErrorMessage = "Loading avatar failed",
            AlwaysRequestedAvatar = new BasisLoadableBundle { BasisBundleConnector = new BasisBundleConnector { UniqueVersion = "v", BasisBundleGenerated = sections } },
        };
    }

    [Test]
    public void TheCapAppliesWhenNothingIsBypassed()
    {
        Assert.AreEqual(Cap, BasisAvatarFactory.RemoteDownloadLimit(new BasisRemotePlayer()));
    }

    [Test]
    public void PerPlayerBypassLiftsTheCap()
    {
        Assert.AreEqual(Uncapped, BasisAvatarFactory.RemoteDownloadLimit(new BasisRemotePlayer { BypassPerformanceLimits = true }));
    }

    [Test]
    public void AlwaysShowAvatarLiftsTheCap()
    {
        Assert.AreEqual(Uncapped, BasisAvatarFactory.RemoteDownloadLimit(new BasisRemotePlayer { AlwaysShowAvatar = true }));
    }

    [Test]
    public void SessionBypassLiftsTheCap()
    {
        BasisAvatarPerformanceLimits.BypassAllLimits = true;
        Assert.AreEqual(Uncapped, BasisAvatarFactory.RemoteDownloadLimit(new BasisRemotePlayer()));
    }

    [Test]
    public void BypassClearsAFailureTheCapCaused()
    {
        BasisRemotePlayer player = RefusedPlayer(Section(ThisPlatform(), Cap + 1));
        player.BypassPerformanceLimits = true;
        Assert.IsTrue(BasisAvatarFactory.ClearDownloadLimitFailure(player));
        Assert.IsFalse(player.HasFailedAvatarLoadGlobally);
        Assert.IsNull(player.AvatarLoadErrorMessage);
    }

    [Test]
    public void WithoutBypassTheFailureStands()
    {
        BasisRemotePlayer player = RefusedPlayer(Section(ThisPlatform(), Cap + 1));
        Assert.IsFalse(BasisAvatarFactory.ClearDownloadLimitFailure(player));
        Assert.IsTrue(player.HasFailedAvatarLoadGlobally);
    }

    [Test]
    public void AFailureWithinTheCapIsLeftAlone()
    {
        BasisAvatarPerformanceLimits.BypassAllLimits = true;
        BasisRemotePlayer player = RefusedPlayer(Section(ThisPlatform(), Cap));
        Assert.IsFalse(BasisAvatarFactory.ClearDownloadLimitFailure(player));
        Assert.IsTrue(player.HasFailedAvatarLoadGlobally);
    }

    [Test]
    public void AnyMatchingSectionOverTheCapCounts()
    {
        string platform = ThisPlatform();
        BasisRemotePlayer player = RefusedPlayer(Section(platform, Cap / 2), Section(platform, Cap * 2));
        player.AlwaysShowAvatar = true;
        Assert.IsTrue(BasisAvatarFactory.ClearDownloadLimitFailure(player));
    }

    [Test]
    public void TheGenericSectionCountsOnlyWhenThisPlatformHasNone()
    {
        BasisAvatarPerformanceLimits.BypassAllLimits = true;
        Assert.IsTrue(BasisAvatarFactory.ClearDownloadLimitFailure(RefusedPlayer(Section(BasisBundleConnector.GenericPlatform, Cap + 1))));
        Assert.IsFalse(BasisAvatarFactory.ClearDownloadLimitFailure(RefusedPlayer(Section(ThisPlatform(), Cap), Section(BasisBundleConnector.GenericPlatform, Cap + 1))));
    }

    [Test]
    public void HuskAndMalformedConnectorsAreSkippedWithoutThrowing()
    {
        BasisAvatarPerformanceLimits.BypassAllLimits = true;
        Assert.IsFalse(BasisAvatarFactory.ClearDownloadLimitFailure(new BasisRemotePlayer { HasFailedAvatarLoadGlobally = true }));
        Assert.IsFalse(BasisAvatarFactory.ClearDownloadLimitFailure(new BasisRemotePlayer { HasFailedAvatarLoadGlobally = true, AlwaysRequestedAvatar = new BasisLoadableBundle { BasisBundleConnector = new BasisBundleConnector() } }));
        Assert.IsFalse(BasisAvatarFactory.ClearDownloadLimitFailure(RefusedPlayer(null, Section(null, Cap + 1))));
    }
}
