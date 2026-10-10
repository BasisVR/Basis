using System.Reflection;
using NUnit.Framework;

public class BasisContentRemovalRulesTests
{
    private static readonly PropertyInfo RemovalLocked = typeof(BasisNetworkModeration).GetProperty(nameof(BasisNetworkModeration.GlobalContentRemovalLocked));

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
