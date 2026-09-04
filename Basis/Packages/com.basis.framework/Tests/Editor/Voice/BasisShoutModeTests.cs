using Basis.BasisUI;
using Basis.Scripts.Networking;
using BasisPermissions;
using NUnit.Framework;

/// <summary>
/// Shout is the proximity loud mode: twice the microphone range, a broadband boost, and still on
/// the ordinary spatialized voice path. It is the counterpart to Announce, which leaves the world
/// entirely and is heard by everyone at the same level — so the two must not be confused for each
/// other anywhere in the mode machinery. Both are opted into from the Admin tab and both are
/// therefore gated on the same permission that tab is.
/// </summary>
public class BasisShoutModeTests
{
    private bool _previousShout;
    private bool _previousAnnounce;
    private bool _previousNoOne;
    private bool _hadPermission;

    [SetUp]
    public void SetUp()
    {
        _previousShout = BasisSettingsDefaults.ShoutMode.RawValue;
        _previousAnnounce = BasisSettingsDefaults.AnnounceShowOnMenuBar.RawValue;
        _previousNoOne = BasisSettingsDefaults.TalkToNoOne.RawValue;
        _hadPermission = BasisNetworkManagement.LocalPermissions.Contains(PermNodes.PermissionsView);
    }

    [TearDown]
    public void TearDown()
    {
        BasisTalkModeManager.OnAdminShoutChanged(false);
        BasisTalkModeManager.SetMode(BasisTalkMode.Normal);
        BasisSettingsDefaults.ShoutMode.SetValueWithoutNotify(_previousShout);
        BasisSettingsDefaults.AnnounceShowOnMenuBar.SetValueWithoutNotify(_previousAnnounce);
        BasisSettingsDefaults.TalkToNoOne.SetValueWithoutNotify(_previousNoOne);
        SetPermission(_hadPermission);
    }

    private static void SetPermission(bool granted)
    {
        if (granted) BasisNetworkManagement.LocalPermissions.Add(PermNodes.PermissionsView);
        else BasisNetworkManagement.LocalPermissions.Remove(PermNodes.PermissionsView);
    }

    /// <summary>Shout opted into, and the admin rights that opt-in is only reachable through.</summary>
    private static void EnableShout()
    {
        SetPermission(true);
        BasisSettingsDefaults.ShoutMode.SetValueWithoutNotify(true);
    }

    [Test]
    public void ShoutAndAnnounceAreDistinctWireValues()
    {
        Assert.AreNotEqual((byte)BasisTalkMode.Announce, (byte)BasisTalkMode.Shout);

        // Announce keeps the byte it shipped with; renaming it must not have renumbered the
        // enum, or every already-deployed client reads the wrong mode off the wire.
        Assert.AreEqual(4, (byte)BasisTalkMode.Announce);
        Assert.AreEqual(6, (byte)BasisTalkMode.Shout);
    }

    [Test]
    public void ShoutCarriesTwiceTheRangeAndMoreLevel()
    {
        Assert.AreEqual(2f, BasisShout.RangeMultiplier);
        Assert.AreEqual(BasisShout.RangeMultiplier * BasisShout.RangeMultiplier, BasisShout.RangeMultiplierSquared);
        Assert.Greater(BasisShout.Gain, 1f, "Shout is supposed to be louder, not just further.");
    }

    [Test]
    public void ModeIsOfferedOnlyWhenEnabled()
    {
        SetPermission(true);

        BasisSettingsDefaults.ShoutMode.SetValueWithoutNotify(false);
        Assert.IsFalse(BasisTalkModeManager.ModeAvailable(BasisTalkMode.Shout));

        BasisSettingsDefaults.ShoutMode.SetValueWithoutNotify(true);
        Assert.IsTrue(BasisTalkModeManager.ModeAvailable(BasisTalkMode.Shout));
    }

    /// <summary>
    /// The toggle is persisted per machine but the permission is not — losing admin has to take
    /// shout away with it, otherwise the stored pref keeps a demoted admin shouting forever.
    /// </summary>
    [Test]
    public void TheToggleAloneIsNotEnoughWithoutThePermission()
    {
        BasisSettingsDefaults.ShoutMode.SetValueWithoutNotify(true);

        SetPermission(false);
        Assert.IsFalse(BasisTalkModeManager.ShoutAvailable());
        Assert.IsFalse(BasisTalkModeManager.ModeAvailable(BasisTalkMode.Shout));

        SetPermission(true);
        Assert.IsTrue(BasisTalkModeManager.ShoutAvailable());
    }

    [Test]
    public void CycleReachesShoutWhenEnabled()
    {
        EnableShout();
        BasisTalkModeManager.SetMode(BasisTalkMode.Normal);

        for (int Step = 0; Step < 8 && BasisTalkModeManager.CurrentMode != BasisTalkMode.Shout; Step++)
        {
            BasisTalkModeManager.CycleMode();
        }

        Assert.AreEqual(BasisTalkMode.Shout, BasisTalkModeManager.CurrentMode);
    }

    [Test]
    public void CycleSkipsShoutWhenDisabled()
    {
        SetPermission(true);
        BasisSettingsDefaults.ShoutMode.SetValueWithoutNotify(false);
        BasisTalkModeManager.SetMode(BasisTalkMode.Normal);

        for (int Step = 0; Step < 8; Step++)
        {
            BasisTalkModeManager.CycleMode();
            Assert.AreNotEqual(BasisTalkMode.Shout, BasisTalkModeManager.CurrentMode);
        }
    }

    /// <summary>
    /// Announce needs a server round trip (an admin grants it), so SetMode only *requests* it and
    /// the mode does not change until the server says so. Shout is client-side and must apply
    /// immediately — a shout that waited on a permission reply would silently do nothing offline.
    /// </summary>
    [Test]
    public void ShoutAppliesLocallyWithoutAServerRoundTrip()
    {
        EnableShout();
        BasisTalkModeManager.SetMode(BasisTalkMode.Shout);

        Assert.AreEqual(BasisTalkMode.Shout, BasisTalkModeManager.CurrentMode);
        Assert.IsTrue(BasisTalkModeManager.LocalIsShouting);
    }

    [Test]
    public void ShoutStillTransmitsAndReachesEveryoneInRange()
    {
        EnableShout();
        BasisTalkModeManager.SetMode(BasisTalkMode.Shout);

        // Not a private mode: the recipient list comes from microphone range, not a member set,
        // so IsRecipient (which only answers for the allowlist modes) must stay false.
        Assert.IsFalse(BasisTalkModeManager.TransmitBlockedLocally);
        Assert.IsFalse(BasisTalkModeManager.IsRecipient(1));
    }

    /// <summary>
    /// Turning the setting off while shouting has to leave the mode; the pref handler does that
    /// live, but its subscription is installed from a RuntimeInitializeOnLoadMethod that EditMode
    /// tests cannot rely on having run. Drive the cycle instead — with the setting off, shout must
    /// not be somewhere the button can land or stay.
    /// </summary>
    [Test]
    public void CycleLeavesShoutOnceTheSettingIsOff()
    {
        EnableShout();
        BasisTalkModeManager.SetMode(BasisTalkMode.Shout);
        Assert.AreEqual(BasisTalkMode.Shout, BasisTalkModeManager.CurrentMode);

        BasisSettingsDefaults.ShoutMode.SetValueWithoutNotify(false);
        BasisTalkModeManager.CycleMode();

        Assert.AreNotEqual(BasisTalkMode.Shout, BasisTalkModeManager.CurrentMode);
        Assert.IsFalse(BasisTalkModeManager.LocalIsShouting);
    }

    /// <summary>
    /// An admin can put someone into shout from the player menu. The grant arrives as a server
    /// broadcast, so the target enters the mode without having opted into the menu-bar toggle and
    /// without holding the permission themselves — the whole point of it being a grant.
    /// </summary>
    [Test]
    public void AnAdminGrantPutsYouInShoutWithoutTheToggleOrThePermission()
    {
        SetPermission(false);
        BasisSettingsDefaults.ShoutMode.SetValueWithoutNotify(false);
        BasisTalkModeManager.SetMode(BasisTalkMode.Normal);

        BasisTalkModeManager.OnAdminShoutChanged(true);

        Assert.AreEqual(BasisTalkMode.Shout, BasisTalkModeManager.CurrentMode);
        Assert.IsTrue(BasisTalkModeManager.LocalIsShouting);
        Assert.IsTrue(BasisTalkModeManager.ShoutAvailable(), "the mode we are in must never read as unavailable.");
    }

    [Test]
    public void RevokingAnAdminGrantReturnsToNormal()
    {
        SetPermission(false);
        BasisSettingsDefaults.ShoutMode.SetValueWithoutNotify(false);
        BasisTalkModeManager.OnAdminShoutChanged(true);
        Assert.AreEqual(BasisTalkMode.Shout, BasisTalkModeManager.CurrentMode);

        BasisTalkModeManager.OnAdminShoutChanged(false);

        Assert.AreEqual(BasisTalkMode.Normal, BasisTalkModeManager.CurrentMode);
        Assert.IsFalse(BasisTalkModeManager.LocalIsShouting);
        Assert.IsFalse(BasisTalkModeManager.ShoutAvailable(), "with the grant gone and no toggle, shout is unavailable again.");
    }

    /// <summary>
    /// A held shout is the server's to release. Cycling must not walk out of it locally, or a
    /// moderator's grant lasts exactly as long as it takes the target to press the mode button.
    /// </summary>
    [Test]
    public void CycleCannotWalkOutOfAnAdminHeldShout()
    {
        SetPermission(false);
        BasisSettingsDefaults.ShoutMode.SetValueWithoutNotify(false);
        BasisSettingsDefaults.TalkToNoOne.SetValueWithoutNotify(true);
        BasisTalkModeManager.OnAdminShoutChanged(true);

        for (int Step = 0; Step < 8; Step++)
        {
            BasisTalkModeManager.CycleMode();
            Assert.AreEqual(BasisTalkMode.Shout, BasisTalkModeManager.CurrentMode,
                "a held shout only ends when the server says so.");
        }

        BasisTalkModeManager.OnAdminShoutChanged(false);
        Assert.AreEqual(BasisTalkMode.Normal, BasisTalkModeManager.CurrentMode);
    }

    [Test]
    public void ShoutAloneIsEnoughToShowTheModeButton()
    {
        SetPermission(true);
        BasisSettingsDefaults.TalkToNoOne.SetValueWithoutNotify(false);
        BasisSettingsDefaults.AnnounceShowOnMenuBar.SetValueWithoutNotify(false);
        BasisTalkModeManager.SetMode(BasisTalkMode.Normal);

        BasisSettingsDefaults.ShoutMode.SetValueWithoutNotify(false);
        bool withoutShout = BasisTalkModeManager.ShouldShowModeButton();

        BasisSettingsDefaults.ShoutMode.SetValueWithoutNotify(true);
        Assert.IsTrue(BasisTalkModeManager.ShouldShowModeButton());
        Assert.IsFalse(withoutShout, "Nothing else should have been offering the button in this state.");
    }
}
