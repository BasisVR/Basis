using Unity.Scripting.LifecycleManagement;
using System.Reflection;
using NUnit.Framework;

[NoAutoStaticsCleanup]
public class BasisContentRemovalRulesTests
{
    private static readonly PropertyInfo RemovalLocked = typeof(BasisNetworkModeration).GetProperty(nameof(BasisNetworkModeration.GlobalContentRemovalLocked));
    private const string SharerUuid = "did:key:sharer";
    private const string NewcomerUuid = "did:key:newcomer";

    private static void SetRemovalLock(bool locked) => RemovalLocked.SetValue(null, locked);

    [TearDown]
    public void TearDown()
    {
        SetRemovalLock(false);
    }

    [Test]
    public void AnyoneMayRemoveAnUnprotectedShare()
    {
        SetRemovalLock(false);
        Assert.IsTrue(BasisContentShareManager.CanLocalPlayerRemove(4242, false));
    }

    [Test]
    public void NobodyButTheSharerAndModeratorsMayRemoveAProtectedPlayersShare()
    {
        SetRemovalLock(false);
        Assert.IsFalse(BasisContentShareManager.CanLocalPlayerRemove(4242, true));
    }

    [Test]
    public void ANewcomerHandedTheSharersNetIdIsNotTheSharer()
    {
        Assert.IsTrue(BasisContentShareManager.IsLocalSharer(4242, SharerUuid, 4242, SharerUuid));
        Assert.IsFalse(BasisContentShareManager.IsLocalSharer(4242, NewcomerUuid, 4242, SharerUuid));
        Assert.IsFalse(BasisContentShareManager.IsLocalSharer(17, SharerUuid, 4242, SharerUuid));
    }

    [Test]
    public void TheRemovalLockLeavesOnlyTheSharerAndModerators()
    {
        SetRemovalLock(true);
        Assert.IsTrue(BasisNetworkModeration.GlobalContentRemovalLocked);
        Assert.IsFalse(BasisContentShareManager.CanLocalPlayerRemove(4242, false));
        BasisNetworkModeration.ResetGlobalLockState();
        Assert.IsFalse(BasisNetworkModeration.GlobalContentRemovalLocked, "leaving a server must drop its removal lock");
        Assert.IsTrue(BasisContentShareManager.CanLocalPlayerRemove(4242, false));
    }
}
