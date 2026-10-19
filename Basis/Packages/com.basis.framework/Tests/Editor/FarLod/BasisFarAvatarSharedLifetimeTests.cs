using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Reflection;
using Basis.Scripts.BasisSdk;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

/// <summary>
/// Lifetime of the per-version shared far avatar assets (mesh, material, texture, humanoid rig,
/// prototype) across wearers arriving and leaving. The install path proper needs a running player
/// stack, but everything below the factory — AcquireShared, BuildPrototype, BuildAvatar,
/// ReleaseShared — is reachable here, and it is where a mesh can be destroyed under a live wearer.
/// </summary>
[NoAutoStaticsCleanup]
public class BasisFarAvatarSharedLifetimeTests
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly Type BuilderType = typeof(BasisFarAvatarBuilder);
    private static readonly Type SharedType = typeof(BasisFarAvatarBuilder.SharedAssets);

    private static object AcquireShared(string version, BasisFarLodPayload payload)
    {
        return BuilderType.GetMethod("AcquireShared", Hidden).Invoke(null, new object[] { version, payload });
    }

    private static BasisAvatar BuildAvatar(object shared, string displayName)
    {
        return (BasisAvatar)BuilderType.GetMethod("BuildAvatar", Hidden).Invoke(null, new[] { shared, displayName });
    }

    private static bool IsSharedUsable(string version)
    {
        return (bool)BuilderType.GetMethod("IsSharedUsable", Hidden).Invoke(null, new object[] { version });
    }

    private static void DrainPendingTeardowns()
    {
        BasisFarAvatarBuilder.DrainPendingTeardowns();
    }

    private static T Field<T>(object shared, string name)
    {
        return (T)SharedType.GetField(name).GetValue(shared);
    }

    private static List<BasisFarAvatarInstance> Wearers(object shared)
    {
        return Field<List<BasisFarAvatarInstance>>(shared, "Wearers");
    }

    private static SkinnedMeshRenderer RendererOf(BasisAvatar avatar)
    {
        return avatar.GetComponentInChildren<SkinnedMeshRenderer>(true);
    }

    private static string NewVersion()
    {
        return $"lifetime-{Guid.NewGuid():N}";
    }

    /// <summary>
    /// A wearer that reaches the factory without the release-owner component (or without its
    /// version) can never give its shared assets back: the count never falls, and the mirror the
    /// tick reads stays null, so IsWearingResolvedVersion is false forever and the avatar is
    /// reinstalled every pass.
    /// </summary>
    private static void AssertReleaseOwner(BasisAvatar avatar, string version, string who)
    {
        Assert.IsTrue(avatar.TryGetComponent(out BasisFarAvatarInstance instance), $"{who} must carry BasisFarAvatarInstance");
        Assert.AreEqual(version, instance.SharedVersion, $"{who} must own the shared-asset release key");
        Assert.AreEqual(version, avatar.FarLodSharedVersion, $"{who} must mirror the version for the tick");
        Assert.IsTrue(avatar.IsFarLodAvatar, $"{who} must be flagged as a far LOD avatar");
    }

    /// <summary>
    /// Retires a wearer the way a real teardown does. Edit mode never delivers OnDestroy to a plain
    /// MonoBehaviour, so BasisFarAvatarInstance cannot hand its reference back on its own here; the
    /// release it performs at runtime is invoked directly, then the object is destroyed.
    /// </summary>
    private static void RetireWearer(BasisAvatar avatar)
    {
        BasisFarAvatarInstance instance = avatar.GetComponent<BasisFarAvatarInstance>();
        BasisFarAvatarBuilder.ReleaseSharedByWearer(instance);
        instance.SharedVersion = null;
        UnityEngine.Object.DestroyImmediate(avatar.gameObject);
    }

    /// <summary>
    /// Every wearer is a clone of the shared prototype, so a teardown that runs while any of them is
    /// still alive leaves that wearer rendering nothing. This is the invariant the null-mesh report
    /// came down to.
    /// </summary>
    [Test]
    public void SharedMesh_SurvivesWhileAnotherWearerIsAlive()
    {
        string version = NewVersion();
        object shared = AcquireShared(version, BasisFarLodTestPayloads.CreateInstallable());
        Assert.IsNotNull(shared, "installable payload must produce shared assets");

        BasisAvatar first = BuildAvatar(shared, "wearer-one");
        Assert.IsNotNull(first, "first wearer must build");
        object secondShared = AcquireShared(version, null);
        Assert.AreSame(shared, secondShared, "a second wearer reuses the cached per-version assets");
        BasisAvatar second = BuildAvatar(secondShared, "wearer-two");
        Assert.IsNotNull(second, "second wearer must build");

        AssertReleaseOwner(first, version, "first wearer");
        AssertReleaseOwner(second, version, "second wearer");
        Assert.IsNotNull(RendererOf(first).sharedMesh, "first wearer starts with a mesh");
        Assert.IsNotNull(RendererOf(second).sharedMesh, "second wearer starts with a mesh");

        RetireWearer(first);
        DrainPendingTeardowns();

        Assert.IsNotNull(Field<Mesh>(shared, "Mesh"), "one wearer leaving must not free the shared mesh");
        Assert.IsNotNull(RendererOf(second).sharedMesh, "the remaining wearer must still have its mesh");

        RetireWearer(second);
        DrainPendingTeardowns();
        Assert.IsFalse(IsSharedUsable(version), "the last wearer leaving retires the version");
    }

    /// <summary>
    /// Teardown must not run inside Unity's destruction pass. ReleaseShared is reached from
    /// BasisFarAvatarInstance.OnDestroy, and destroying GameObjects (the prototype and its holder)
    /// from there is the unsafe point this system defers everywhere else — a dropped holder destroy
    /// strands a DontDestroyOnLoad "Far Avatar Build" whose renderer shows no mesh.
    /// </summary>
    [Test]
    public void LastWearerLeaving_DefersTeardownOutOfTheDestructionPass()
    {
        string version = NewVersion();
        object shared = AcquireShared(version, BasisFarLodTestPayloads.CreateInstallable());
        BasisAvatar wearer = BuildAvatar(shared, "only-wearer");
        Assert.IsNotNull(wearer);
        AssertReleaseOwner(wearer, version, "the only wearer");
        GameObject holder = Field<GameObject>(shared, "PrototypeHolder");
        Assert.IsNotNull(holder, "a built version keeps its prototype under a holder");

        RetireWearer(wearer);

        Assert.IsNotNull(Field<Mesh>(shared, "Mesh"), "OnDestroy must only queue the teardown, never run it");
        Assert.IsTrue(IsSharedUsable(version), "the version stays serviceable until the tick drains it");

        DrainPendingTeardowns();

        Assert.IsNull(Field<Mesh>(shared, "Mesh"), "the drain frees the shared mesh");
        Assert.IsTrue(holder == null, "the drain destroys the prototype holder instead of stranding it");
        Assert.IsFalse(IsSharedUsable(version), "the retired version is gone from the cache");
    }

    /// <summary>
    /// A wearer that leaves and comes straight back inside one tick — a range-boundary flip, or a far
    /// avatar swapped out and rebuilt — must reuse the assets rather than pay a fresh parse, decode,
    /// texture upload and AvatarBuilder rig rebuild.
    /// </summary>
    [Test]
    public void ReacquireBeforeDrain_ResurrectsInsteadOfRebuilding()
    {
        string version = NewVersion();
        object shared = AcquireShared(version, BasisFarLodTestPayloads.CreateInstallable());
        BasisAvatar first = BuildAvatar(shared, "flip-out");
        Mesh original = Field<Mesh>(shared, "Mesh");
        GameObject prototype = Field<GameObject>(shared, "Prototype");

        RetireWearer(first);
        object again = AcquireShared(version, null);
        Assert.AreSame(shared, again, "a re-acquire before the drain reuses the queued version");

        DrainPendingTeardowns();

        Assert.AreSame(original, Field<Mesh>(shared, "Mesh"), "resurrection must keep the same mesh");
        Assert.AreSame(prototype, Field<GameObject>(shared, "Prototype"), "resurrection must keep the same prototype");
        BasisAvatar second = BuildAvatar(shared, "flip-in");
        Assert.IsNotNull(second);
        Assert.IsNotNull(RendererOf(second).sharedMesh, "the resurrected version still builds a mesh-bearing wearer");

        RetireWearer(second);
        DrainPendingTeardowns();
    }

    /// <summary>
    /// If the engine objects ever die under a live cache entry, serving that entry clones a mesh-less
    /// prototype into every wearer from then on, silently — one failure latching into "every far
    /// avatar is broken for the rest of the session".
    /// </summary>
    [Test]
    public void DeadCacheEntry_IsEvictedAndRebuiltRatherThanServed()
    {
        string version = NewVersion();
        object shared = AcquireShared(version, BasisFarLodTestPayloads.CreateInstallable());
        BasisAvatar wearer = BuildAvatar(shared, "victim");
        Assert.IsNotNull(wearer);

        UnityEngine.Object.DestroyImmediate(Field<Mesh>(shared, "Mesh"));
        LogAssert.Expect(LogType.Error, new Regex("were destroyed under .* wearer"));
        Assert.IsFalse(IsSharedUsable(version), "an entry whose mesh died must not be reported usable");

        object rebuilt = AcquireShared(version, BasisFarLodTestPayloads.CreateInstallable());
        Assert.IsNotNull(rebuilt, "the version rebuilds from the payload after eviction");
        Assert.AreNotSame(shared, rebuilt, "the dead entry is replaced, not reused");
        BasisAvatar replacement = BuildAvatar(rebuilt, "rebuilt");
        Assert.IsNotNull(replacement);
        Assert.IsNotNull(RendererOf(replacement).sharedMesh, "the rebuilt version produces a live mesh");

        RetireWearer(wearer);
        RetireWearer(replacement);
        DrainPendingTeardowns();
    }

    /// <summary>
    /// A wearer that has already handed its reference back still carries the version string, and
    /// releases are looked up by that string — so a second release from it used to spend somebody
    /// else's reference. Handing the same wearer back twice (an install that released a failed
    /// build and then destroyed it, an OnDestroy landing behind an explicit release) must not
    /// retire a version another wearer is still on: that frees the shared mesh under a live
    /// renderer, which is exactly the null far LOD mesh and is unrecoverable for it.
    /// </summary>
    [Test]
    public void ReleasingTheSameWearerTwice_CannotRetireALiveVersion()
    {
        string version = NewVersion();
        object shared = AcquireShared(version, BasisFarLodTestPayloads.CreateInstallable());
        BasisAvatar first = BuildAvatar(shared, "leaver");
        BasisAvatar second = BuildAvatar(shared, "stayer");
        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Mesh mesh = Field<Mesh>(shared, "Mesh");
        BasisFarAvatarInstance leaver = first.GetComponent<BasisFarAvatarInstance>();

        BasisFarAvatarBuilder.ReleaseSharedByWearer(leaver);
        BasisFarAvatarBuilder.ReleaseSharedByWearer(leaver);
        DrainPendingTeardowns();

        Assert.AreSame(mesh, Field<Mesh>(shared, "Mesh"), "a repeated release must never free a mesh in use");
        Assert.IsNotNull(RendererOf(second).sharedMesh, "the remaining wearer keeps its mesh");
        Assert.AreEqual(1, Wearers(shared).Count, "only the wearer that left gives a reference up");
        Assert.IsTrue(IsSharedUsable(version), "the version stays serviceable while anyone is on it");

        UnityEngine.Object.DestroyImmediate(first.gameObject);
        RetireWearer(second);
        DrainPendingTeardowns();
        Assert.IsFalse(IsSharedUsable(version), "with every wearer gone the version finally retires");
    }

    /// <summary>
    /// A wearer stranded by an eviction was built from the dead entry but still names the version,
    /// and the rebuilt entry answers to that same name. Its teardown must find nothing to hand
    /// back: releasing the rebuilt version from there retires assets its own live wearers render.
    /// </summary>
    [Test]
    public void StrandedWearerOfAnEvictedEntry_CannotRetireTheRebuiltVersion()
    {
        string version = NewVersion();
        object dead = AcquireShared(version, BasisFarLodTestPayloads.CreateInstallable());
        BasisAvatar stranded = BuildAvatar(dead, "stranded");
        Assert.IsNotNull(stranded);

        UnityEngine.Object.DestroyImmediate(Field<Mesh>(dead, "Mesh"));
        LogAssert.Expect(LogType.Error, new Regex("were destroyed under 1 wearer"));
        Assert.IsFalse(IsSharedUsable(version), "an entry whose mesh died must not be reported usable");
        Assert.IsNull(stranded.FarLodSharedVersion, "eviction cuts the strand loose so the tick reinstalls it");

        object rebuilt = AcquireShared(version, BasisFarLodTestPayloads.CreateInstallable());
        BasisAvatar live = BuildAvatar(rebuilt, "rebuilt-wearer");
        Assert.IsNotNull(live);
        Mesh mesh = Field<Mesh>(rebuilt, "Mesh");

        RetireWearer(stranded);
        DrainPendingTeardowns();

        Assert.AreSame(mesh, Field<Mesh>(rebuilt, "Mesh"), "a stranded wearer must not retire the rebuilt version");
        Assert.IsNotNull(RendererOf(live).sharedMesh, "the rebuilt version's wearer keeps its mesh");
        Assert.IsTrue(IsSharedUsable(version), "the rebuilt version stays serviceable");

        RetireWearer(live);
        DrainPendingTeardowns();
        Assert.IsFalse(IsSharedUsable(version), "its real last wearer retires it");
    }
}
