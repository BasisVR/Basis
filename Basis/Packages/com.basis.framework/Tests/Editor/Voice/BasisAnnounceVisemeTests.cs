using Basis.Scripts.Drivers;
using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Announce mode routes a player's voice onto the announce channel and plays it through a separate,
/// non-spatialised AudioSource that ignores distance culling. The viseme driver is shared with
/// the normal path, and both of the rules that normally retire it — the viseme distance cutoff
/// and "the tracked AudioSource went idle" — misread an announcer as somebody who has stopped
/// talking. These lock the exemptions that keep their mouth moving.
/// </summary>
public class BasisAnnounceVisemeTests
{
    private readonly List<BasisAudioAndVisemeDriver> _registered = new List<BasisAudioAndVisemeDriver>();
    private readonly List<GameObject> _spawned = new List<GameObject>();

    [TearDown]
    public void TearDown()
    {
        // The registry is static and shared with every other viseme suite, so anything this
        // test put in has to come back out regardless of how the test ended.
        for (int Index = 0; Index < _registered.Count; Index++)
        {
            BasisRemoteAudioDriver.UnregisterDriver(_registered[Index]);
        }
        _registered.Clear();

        for (int Index = 0; Index < _spawned.Count; Index++)
        {
            if (_spawned[Index] != null) Object.DestroyImmediate(_spawned[Index]);
        }
        _spawned.Clear();
    }

    private BasisAudioAndVisemeDriver RegisterInRange()
    {
        BasisAudioAndVisemeDriver driver = new BasisAudioAndVisemeDriver();
        driver.InVisemeRange = true;
        BasisRemoteAudioDriver.RegisterDriver(driver);
        _registered.Add(driver);
        return driver;
    }

    private static bool IsTicking(BasisAudioAndVisemeDriver driver)
    {
        // ActiveDrivers is what Simulate/Apply actually iterate; InVisemeRange alone is not
        // enough, which is exactly the trap the announce path fell into.
        int count = BasisRemoteAudioDriver.ActiveDriversCount;
        for (int Index = 0; Index < count; Index++)
        {
            if (ReferenceEquals(BasisRemoteAudioDriver.ActiveDrivers[Index], driver)) return true;
        }
        return false;
    }

    // ── distance cutoff ────────────────────────────────────────────

    [Test]
    public void DistanceCutoffRetiresANormalPlayer()
    {
        // Baseline: the optimisation this feature has to survive still does its job.
        BasisAudioAndVisemeDriver driver = RegisterInRange();
        Assert.IsTrue(IsTicking(driver));

        BasisRemoteAudioDriver.SetVisemeRange(driver, false);

        Assert.IsFalse(driver.InVisemeRange);
        Assert.IsFalse(IsTicking(driver), "A distant, non-announcing player should stop costing viseme work.");
    }

    [Test]
    public void DistanceCutoffDoesNotRetireAAnnouncingPlayer()
    {
        BasisAudioAndVisemeDriver driver = RegisterInRange();
        driver.AnnounceActive = true;

        BasisRemoteAudioDriver.SetVisemeRange(driver, false);

        Assert.IsTrue(driver.InVisemeRange, "Announce is audible at any distance; the mouth has to follow.");
        Assert.IsTrue(IsTicking(driver), "A announcing player was dropped from the ticked set.");
    }

    [Test]
    public void AnnounceRestoresADriverTheDistanceCutoffAlreadyRetired()
    {
        // The realistic order: they were already far away and retired, and only then started
        // announcing. EnableAnnounceMode has to be able to pull them back.
        BasisAudioAndVisemeDriver driver = RegisterInRange();
        BasisRemoteAudioDriver.SetVisemeRange(driver, false);
        Assert.IsFalse(IsTicking(driver));

        driver.AnnounceActive = true;
        BasisRemoteAudioDriver.SetVisemeRange(driver, true);

        Assert.IsTrue(IsTicking(driver), "Announce did not bring a retired driver back into the ticked set.");
    }

    [Test]
    public void AnnounceReRegistersADriverThePoolReturnDropped()
    {
        // Out of hearing range the player's spatial AudioSource is pooled, and ResetForPool
        // unregisters the shared viseme driver outright — not merely deactivates it. Forcing
        // InVisemeRange is not enough on its own; the driver has to be registered again, which
        // is why EnableAnnounceMode still calls Initialize afterwards.
        BasisAudioAndVisemeDriver driver = RegisterInRange();
        BasisRemoteAudioDriver.UnregisterDriver(driver);
        Assert.IsFalse(IsTicking(driver));

        driver.AnnounceActive = true;
        BasisRemoteAudioDriver.SetVisemeRange(driver, true);
        Assert.IsFalse(IsTicking(driver), "An unregistered driver cannot be ticked by a range flag alone.");

        BasisRemoteAudioDriver.RegisterDriver(driver);
        _registered.Add(driver);

        Assert.IsTrue(IsTicking(driver), "Re-registering a announcing driver should put it straight back into the ticked set.");
    }

    [Test]
    public void ClearingAnnounceHandsTheDriverBackToTheDistanceRule()
    {
        BasisAudioAndVisemeDriver driver = RegisterInRange();
        driver.AnnounceActive = true;
        BasisRemoteAudioDriver.SetVisemeRange(driver, false);
        Assert.IsTrue(IsTicking(driver));

        driver.AnnounceActive = false;
        BasisRemoteAudioDriver.SetVisemeRange(driver, false);

        Assert.IsFalse(IsTicking(driver), "Once the announce ends the distance cutoff has to apply again.");
    }

    // ── idle spatial source ────────────────────────────────────────

    private AudioSource MakeAudioSource()
    {
        GameObject go = new GameObject("AnnounceVisemeTestSource");
        _spawned.Add(go);
        return go.AddComponent<AudioSource>();
    }

    [Test]
    public void IdleSpatialSourceReleasesTheSlotForANormalPlayer()
    {
        // Baseline again: the slot-recycling behaviour still works for everyone else.
        BasisAudioAndVisemeDriver driver = new BasisAudioAndVisemeDriver();
        driver.TrackedAudioSource = MakeAudioSource();
        driver.AudioSourceInactive = true;

        Assert.IsTrue(driver.ShouldReleaseForIdleSource());
    }

    [Test]
    public void IdleSpatialSourceDoesNotReleaseTheSlotWhileAnnouncing()
    {
        // An announcer sends on AnnounceVoiceChannel only, so their spatial source is legitimately
        // idle the entire time they are talking. Releasing here churned a brand new context
        // every frame and threw the buffered audio away before inference ran.
        BasisAudioAndVisemeDriver driver = new BasisAudioAndVisemeDriver();
        driver.TrackedAudioSource = MakeAudioSource();
        driver.AudioSourceInactive = true;
        driver.AnnounceActive = true;

        Assert.IsFalse(driver.ShouldReleaseForIdleSource(),
            "The announcing player's OpenLipSync context was released while they were mid-sentence.");
    }

    [Test]
    public void NoTrackedSourceNeverReleases()
    {
        // The far announcer's source was pooled and TrackedAudioSource nulled; that path must not
        // decide anything on its own.
        BasisAudioAndVisemeDriver driver = new BasisAudioAndVisemeDriver();
        driver.TrackedAudioSource = null;
        driver.AudioSourceInactive = true;

        Assert.IsFalse(driver.ShouldReleaseForIdleSource());
    }

    [Test]
    public void ActiveSpatialSourceNeverReleases()
    {
        BasisAudioAndVisemeDriver driver = new BasisAudioAndVisemeDriver();
        driver.TrackedAudioSource = MakeAudioSource();
        driver.AudioSourceInactive = false;

        Assert.IsFalse(driver.ShouldReleaseForIdleSource());
    }

    // ── viseme tap ownership ───────────────────────────────────────

    private BasisRemoteAudioDriver MakeAudioDriver(bool isAnnounceSource)
    {
        GameObject go = new GameObject(isAnnounceSource ? "AnnounceSource" : "SpatialSource");
        _spawned.Add(go);
        BasisRemoteAudioDriver driver = go.AddComponent<BasisRemoteAudioDriver>();
        driver.IsAnnounceSource = isAnnounceSource;
        return driver;
    }

    [Test]
    public void SpatialSourceOwnsTheTapWhenNobodyIsAnnouncing()
    {
        BasisAudioAndVisemeDriver viseme = new BasisAudioAndVisemeDriver();
        Assert.IsTrue(MakeAudioDriver(false).OwnsVisemeTap(viseme));
    }

    [Test]
    public void AnnounceSourceTakesTheTapFromTheSilentSpatialSource()
    {
        // The player's spatial source keeps running while they announce, and its queue is empty,
        // so every callback it hands the analyser a buffer of pure silence. Interleaved with
        // real announce speech that is worse than useless: it splices gaps through the mel stream
        // and two producers race one single-producer ingest buffer.
        BasisAudioAndVisemeDriver viseme = new BasisAudioAndVisemeDriver();
        viseme.AnnounceActive = true;

        BasisRemoteAudioDriver spatial = MakeAudioDriver(false);
        BasisRemoteAudioDriver announce = MakeAudioDriver(true);

        Assert.IsFalse(spatial.OwnsVisemeTap(viseme), "The silent spatial source is still feeding lip-sync during an announce.");
        Assert.IsTrue(announce.OwnsVisemeTap(viseme), "The announce source must be the one feeding lip-sync.");
    }

    [Test]
    public void SpatialSourceGetsTheTapBackWhenTheAnnounceEnds()
    {
        BasisAudioAndVisemeDriver viseme = new BasisAudioAndVisemeDriver();
        BasisRemoteAudioDriver spatial = MakeAudioDriver(false);

        viseme.AnnounceActive = true;
        Assert.IsFalse(spatial.OwnsVisemeTap(viseme));

        viseme.AnnounceActive = false;
        Assert.IsTrue(spatial.OwnsVisemeTap(viseme), "Normal-mode lip-sync did not resume after the announce.");
    }

    [Test]
    public void ExactlyOneSourceOwnsTheTapInEitherMode()
    {
        // The property that matters: never two producers, and never zero.
        BasisAudioAndVisemeDriver viseme = new BasisAudioAndVisemeDriver();
        BasisRemoteAudioDriver spatial = MakeAudioDriver(false);
        BasisRemoteAudioDriver announce = MakeAudioDriver(true);

        foreach (bool announcing in new[] { false, true })
        {
            viseme.AnnounceActive = announcing;
            int owners = (spatial.OwnsVisemeTap(viseme) ? 1 : 0) + (announce.OwnsVisemeTap(viseme) ? 1 : 0);
            Assert.AreEqual(1, owners, $"{owners} sources fed the analyser with AnnounceActive={announcing}.");
        }
    }

    [Test]
    public void PooledDriverDoesNotInheritAnnounceOwnership()
    {
        // Spatial sources come from a pool; a stale IsAnnounceSource would let a recycled object
        // keep feeding lip-sync during somebody else's announce.
        BasisRemoteAudioDriver driver = MakeAudioDriver(true);
        driver.ResetForPool();

        Assert.IsFalse(driver.IsAnnounceSource);
    }

    [Test]
    public void RegistryIsBalancedAcrossAAnnounceCycle()
    {
        // Enable/disable must not leak a registration into the static list — the whole
        // ActiveDrivers design exists so per-frame cost tracks the in-range set.
        int before = BasisRemoteAudioDriver.DriversCount;
        int activeBefore = BasisRemoteAudioDriver.ActiveDriversCount;

        BasisAudioAndVisemeDriver driver = new BasisAudioAndVisemeDriver();
        driver.InVisemeRange = false;

        driver.AnnounceActive = true;
        BasisRemoteAudioDriver.SetVisemeRange(driver, true);
        BasisRemoteAudioDriver.RegisterDriver(driver);
        Assert.IsTrue(IsTicking(driver));

        // What DisableAnnounceMode does when the spatial path is not holding the driver.
        driver.AnnounceActive = false;
        BasisRemoteAudioDriver.SetVisemeRange(driver, false);
        BasisRemoteAudioDriver.UnregisterDriver(driver);

        Assert.AreEqual(before, BasisRemoteAudioDriver.DriversCount, "Driver registry leaked across an announce cycle.");
        Assert.AreEqual(activeBefore, BasisRemoteAudioDriver.ActiveDriversCount, "Active set leaked across an announce cycle.");
    }
}
