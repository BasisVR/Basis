using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace GatorDragonGames.JigglePhysics.Tests {

/// <summary>
/// Covers JiggleRigData — the serialised half of a rig, and the part an author edits directly.
/// Its distance cache drives every per-bone parameter, so the tests here concentrate on what that
/// cache says about a hierarchy once bones are excluded, branched, stacked or destroyed.
/// </summary>
[TestFixture]
internal class JiggleRigDataTests {
    private const float Tolerance = 1e-4f;

    private JiggleBoneScene scene;

    [SetUp]
    public void SetUp() {
        scene = new JiggleBoneScene();
    }

    [TearDown]
    public void TearDown() {
        scene?.Dispose();
        scene = null;
    }

    private static float Distance(JiggleRigData rig, Transform bone) {
        return rig.GetCache(bone).normalizedDistanceFromRoot;
    }

    // ------------------------------------------------------- distance cache

    [Test]
    public void NormalisedDistance_RunsFromZeroAtTheRootToOneAtTheTip() {
        var root = scene.Chain(4);
        var bones = JiggleBoneScene.Descend(root, 4);

        var rig = JiggleSceneFactory.Rig(root);

        Assert.AreEqual(0f, Distance(rig, bones[0]), Tolerance);
        Assert.AreEqual(1f, Distance(rig, bones[3]), Tolerance);
    }

    [Test]
    public void NormalisedDistance_IncreasesMonotonicallyAlongTheChain() {
        var root = scene.Chain(5);
        var bones = JiggleBoneScene.Descend(root, 5);

        var rig = JiggleSceneFactory.Rig(root);

        for (int i = 1; i < bones.Length; i++) {
            Assert.Greater(Distance(rig, bones[i]), Distance(rig, bones[i - 1]));
        }
    }

    /// <summary>
    /// Branches share one denominator, the longest chain in the rig, so a short branch never reaches
    /// 1 and its bones stay as stiff as bones the same distance down the long branch.
    /// </summary>
    [Test]
    public void NormalisedDistance_NormalisesBranchesAgainstTheLongestChain() {
        var root = scene.Spawn("root");
        var longA = scene.Spawn("longA", root, new Vector3(0f, -0.25f, 0f));
        var longB = scene.Spawn("longB", longA, new Vector3(0f, -0.25f, 0f));
        scene.Spawn("longC", longB, new Vector3(0f, -0.25f, 0f));
        var shortA = scene.Spawn("shortA", root, new Vector3(0.25f, 0f, 0f));

        var rig = JiggleSceneFactory.Rig(root);

        Assert.AreEqual(1f / 3f, Distance(rig, longA), Tolerance);
        Assert.AreEqual(1f / 3f, Distance(rig, shortA), Tolerance);
    }

    /// <summary>
    /// A rig whose bones all sit on one another has zero length. The denominator is floored so the
    /// normalised distance stays finite instead of poisoning every parameter with a NaN.
    /// </summary>
    [Test]
    public void NormalisedDistance_CollapsedRig_StaysFinite() {
        var root = scene.Spawn("root");
        var stacked = scene.Spawn("stacked", root);
        scene.Spawn("alsoStacked", stacked);

        var rig = JiggleSceneFactory.Rig(root);

        Assert.IsFalse(float.IsNaN(Distance(rig, stacked)));
        Assert.AreEqual(0f, Distance(rig, stacked), Tolerance);
    }

    [Test]
    public void NormalisedDistance_SkipsExcludedSubtrees() {
        var root = scene.Chain(4);
        var bones = JiggleBoneScene.Descend(root, 4);

        var rig = JiggleSceneFactory.Rig(root, bones[2]);

        Assert.AreEqual(1f, Distance(rig, bones[1]), Tolerance, "the chain now ends at the last kept bone");
        Assert.Throws<KeyNotFoundException>(() => rig.GetCache(bones[2]));
        Assert.Throws<KeyNotFoundException>(() => rig.GetCache(bones[3]));
    }

    [Test]
    public void BuildNormalizedDistanceFromRootList_WithoutARoot_IsANoOp() {
        var rig = JiggleRigData.Default();

        Assert.DoesNotThrow(() => rig.BuildNormalizedDistanceFromRootList());

        Assert.AreEqual(0, rig.transformCachedData.Length);
    }

    // ----------------------------------------------------------- exclusions

    [Test]
    public void GetIsExcluded_UsesTheLookupOnceTheCacheIsBuilt() {
        var root = scene.Chain(3);
        var bones = JiggleBoneScene.Descend(root, 3);

        var rig = JiggleSceneFactory.Rig(root, bones[1]);

        Assert.IsTrue(rig.GetIsExcluded(bones[1]));
        Assert.IsFalse(rig.GetIsExcluded(bones[0]));
    }

    /// <summary>
    /// The lookup set only exists after RegenerateCacheLookup, and exclusion is consulted during the
    /// very build that creates it, so the array scan fallback has to answer identically.
    /// </summary>
    [Test]
    public void GetIsExcluded_FallsBackToTheArrayScanBeforeTheCacheExists() {
        var bone = scene.Spawn("bone");
        var rig = JiggleRigData.Default();
        rig.excludedTransforms = new[] { bone };

        Assert.IsTrue(rig.GetIsExcluded(bone));
        Assert.IsFalse(rig.GetIsExcluded(scene.Spawn("other")));
    }

    [Test]
    public void GetValidChildrenCount_SkipsExcludedChildren() {
        var root = scene.Spawn("root");
        var kept = scene.Spawn("kept", root, new Vector3(0f, -0.25f, 0f));
        var dropped = scene.Spawn("dropped", root, new Vector3(0.25f, 0f, 0f));

        var rig = JiggleSceneFactory.Rig(root, dropped);

        Assert.AreEqual(1, rig.GetValidChildrenCount(root));
        Assert.AreSame(kept, rig.GetValidChild(root, 0));
    }

    [Test]
    public void GetValidChild_IndexesOnlyTheValidChildren() {
        var root = scene.Spawn("root");
        scene.Spawn("dropped", root, new Vector3(0.25f, 0f, 0f));
        var second = scene.Spawn("second", root, new Vector3(0f, -0.25f, 0f));
        var third = scene.Spawn("third", root, new Vector3(0f, 0f, 0.25f));
        var rig = JiggleSceneFactory.Rig(root, root.GetChild(0));

        Assert.AreSame(second, rig.GetValidChild(root, 0));
        Assert.AreSame(third, rig.GetValidChild(root, 1));
        Assert.IsNull(rig.GetValidChild(root, 2));
    }

    [Test]
    public void GetValidChildrenInto_ReplacesTheTargetListContents() {
        var root = scene.Chain(2);
        var rig = JiggleSceneFactory.Rig(root);
        var into = new List<Transform> { scene.Spawn("stale") };

        rig.GetValidChildrenInto(root, into);

        Assert.AreEqual(1, into.Count);
        Assert.AreSame(root.GetChild(0), into[0]);
    }

    [Test]
    public void GetValidChildrenCount_OfANullBone_IsZero() {
        var rig = JiggleSceneFactory.Rig(scene.Chain(2));

        Assert.AreEqual(0, rig.GetValidChildrenCount(null));
        Assert.IsNull(rig.GetValidChild(null, 0));
    }

    // ---------------------------------------------------------- cache state

    [Test]
    public void GetCacheIsValid_IsFalseBeforeAnythingIsBuilt() {
        var rig = JiggleRigData.Default();

        Assert.IsFalse(rig.GetCacheIsValid());
    }

    /// <summary>
    /// Avatars get torn down bone-first often enough that a cache full of destroyed transforms is
    /// routine. It has to report itself invalid so the tree builder rebuilds instead of dereferencing.
    /// </summary>
    [Test]
    public void GetCacheIsValid_IsFalseOnceABoneIsDestroyed() {
        var root = scene.Chain(3);
        var bones = JiggleBoneScene.Descend(root, 3);
        var rig = JiggleSceneFactory.Rig(root);
        Assert.IsTrue(rig.GetCacheIsValid());

        Object.DestroyImmediate(bones[2].gameObject);

        Assert.IsFalse(rig.GetCacheIsValid());
    }

    [Test]
    public void GetCache_ThrowsForATransformOutsideTheRig() {
        var rig = JiggleSceneFactory.Rig(scene.Chain(3));
        var stranger = scene.Spawn("stranger");

        Assert.Throws<KeyNotFoundException>(() => rig.GetCache(stranger));
    }

    // ------------------------------------------------------------ rest pose

    [Test]
    public void SnapToRestPose_RestoresTheChildLocalPoses() {
        var root = scene.Chain(3);
        var bones = JiggleBoneScene.Descend(root, 3);
        var rig = JiggleSceneFactory.Rig(root);
        bones[1].localPosition = new Vector3(9f, 9f, 9f);
        bones[1].localRotation = Quaternion.Euler(0f, 90f, 0f);

        rig.SnapToRestPose();

        Assert.AreEqual(0f, Vector3.Distance(new Vector3(0f, -0.25f, 0f), bones[1].localPosition), Tolerance);
        Assert.AreEqual(0f, Quaternion.Angle(Quaternion.identity, bones[1].localRotation), 1e-2f);
    }

    /// <summary>
    /// The root bone is animation driven, not jiggle driven, so snapping the rig back to rest must
    /// leave it wherever the animator put it.
    /// </summary>
    [Test]
    public void SnapToRestPose_LeavesTheRootBoneAlone() {
        var root = scene.Chain(3);
        var rig = JiggleSceneFactory.Rig(root);
        root.localPosition = new Vector3(4f, 0f, 0f);

        rig.SnapToRestPose();

        Assert.AreEqual(0f, Vector3.Distance(new Vector3(4f, 0f, 0f), root.localPosition), Tolerance);
    }

    [Test]
    public void ResampleRestPose_CapturesTheCurrentLocalPose() {
        var root = scene.Chain(3);
        var bones = JiggleBoneScene.Descend(root, 3);
        var rig = JiggleSceneFactory.Rig(root);
        bones[1].localPosition = new Vector3(0f, -0.75f, 0f);

        rig.ResampleRestPose();
        bones[1].localPosition = Vector3.zero;
        rig.SnapToRestPose();

        Assert.AreEqual(0f, Vector3.Distance(new Vector3(0f, -0.75f, 0f), bones[1].localPosition), Tolerance);
    }

    // ----------------------------------------------------------- validation

    [Test]
    public void OnValidate_ClampsCollidersToTheSupportedMaximum() {
        var root = scene.Chain(2);
        var rig = JiggleSceneFactory.Rig(root);
        var colliders = new JiggleColliderSerializable[33];
        for (int i = 0; i < colliders.Length; i++) {
            colliders[i] = JiggleSceneFactory.SphereCollider(scene.Spawn($"collider{i}"));
        }
        rig.jiggleColliders = colliders;
        LogAssert.Expect(LogType.Warning,
            "JigglePhysics: Maximum of 32 personal Jiggle Colliders are supported per tree. Extra colliders will be dropped.");

        rig.OnValidate();

        Assert.AreEqual(32, rig.jiggleColliders.Length);
    }

    [Test]
    public void OnValidate_ReplacesAMissingCurve() {
        var root = scene.Chain(2);
        var rig = JiggleSceneFactory.Rig(root);
        rig.jiggleTreeInputParameters.stiffness.curve = null;

        rig.OnValidate();

        Assert.IsNotNull(rig.jiggleTreeInputParameters.stiffness.curve);
        Assert.Greater(rig.jiggleTreeInputParameters.stiffness.curve.length, 0);
    }

    /// <summary>
    /// Presets authored before collision radius moved to world space carry version v0.0.0. Opening
    /// one has to walk it forward through every migration in one pass, rescaling the radius by the
    /// root's scale on the way.
    /// </summary>
    [Test]
    public void OnValidate_MigratesLegacySerialisedDataInOnePass() {
        var root = scene.Chain(3);
        root.localScale = Vector3.one * 2f;
        var rig = JiggleSceneFactory.Rig(root);
        rig.serializedVersion = "v0.0.0";
        rig.jiggleTreeInputParameters.collisionRadius.value = 0.4f;

        rig.OnValidate();

        Assert.AreEqual("v0.0.2", rig.serializedVersion);
        Assert.AreEqual(0.2f, rig.jiggleTreeInputParameters.collisionRadius.value, Tolerance);
    }

    [Test]
    public void OnValidate_LeavesCurrentDataAlone() {
        var root = scene.Chain(3);
        var rig = JiggleSceneFactory.Rig(root);
        rig.jiggleTreeInputParameters.collisionRadius.value = 0.4f;

        rig.OnValidate();

        Assert.AreEqual("v0.0.2", rig.serializedVersion);
        Assert.AreEqual(0.4f, rig.jiggleTreeInputParameters.collisionRadius.value, Tolerance);
    }

    // ------------------------------------------------------ parameter pushes

    /// <summary>
    /// Animated parameters are pushed straight onto a live tree, bypassing the tree builder. That
    /// push has to reproduce the builder's pinned root override, or turning on animated parameters
    /// silently unpins an excluded root and the whole chain starts swaying from the shoulder.
    /// </summary>
    [Test]
    public void UpdateParameters_KeepsAnExcludedRootPinned() {
        var root = scene.Chain(3);
        var rig = JiggleSceneFactory.Rig(root);
        rig.excludeRoot = true;
        var tree = JigglePhysics.CreateJiggleTree(rig, null);

        rig.UpdateParameters(tree, new List<JigglePointParameters>());

        Assert.AreEqual(1f, tree.parameters[1].angleElasticity, Tolerance);
        Assert.AreEqual(1f, tree.parameters[1].lengthElasticity, Tolerance);
        Assert.AreEqual(1f, tree.parameters[1].rootElasticity, Tolerance);
        JiggleSceneFactory.FreeStruct(tree);
    }

    [Test]
    public void UpdateParameters_LeavesTheRestOfTheChainOnTheAuthoredValues() {
        var root = scene.Chain(3);
        var rig = JiggleSceneFactory.Rig(root);
        rig.excludeRoot = true;
        var tree = JigglePhysics.CreateJiggleTree(rig, null);
        var stiffness = rig.jiggleTreeInputParameters.stiffness.value;

        rig.UpdateParameters(tree, new List<JigglePointParameters>());

        Assert.AreEqual(stiffness * stiffness, tree.parameters[2].angleElasticity, Tolerance);
        JiggleSceneFactory.FreeStruct(tree);
    }

    /// <summary>
    /// The animated-parameter push has to reproduce exactly what the tree builder assigned, or
    /// simply switching animated parameters on changes how a rig moves. The back projected root and
    /// the projected tips are the trap: they share a real bone, so a naive bone-keyed pin catches
    /// them too and zeroes the gravity and drag the first real bone integrates against.
    /// </summary>
    [Test]
    public void UpdateParameters_OnAnUnchangedRigWithAnExcludedRoot_ChangesNothing() {
        var root = scene.Chain(3);
        var rig = JiggleSceneFactory.Rig(root);
        rig.excludeRoot = true;
        var tree = JigglePhysics.CreateJiggleTree(rig, null);
        var before = (JigglePointParameters[])tree.parameters.Clone();

        rig.UpdateParameters(tree, new List<JigglePointParameters>());

        for (int i = 0; i < before.Length; i++) {
            Assert.AreEqual(before[i].angleElasticity, tree.parameters[i].angleElasticity, Tolerance, $"point {i} angleElasticity");
            Assert.AreEqual(before[i].rootElasticity, tree.parameters[i].rootElasticity, Tolerance, $"point {i} rootElasticity");
            Assert.AreEqual(before[i].lengthElasticity, tree.parameters[i].lengthElasticity, Tolerance, $"point {i} lengthElasticity");
            Assert.AreEqual(before[i].gravityMultiplier, tree.parameters[i].gravityMultiplier, Tolerance, $"point {i} gravityMultiplier");
            Assert.AreEqual(before[i].drag, tree.parameters[i].drag, Tolerance, $"point {i} drag");
        }
        JiggleSceneFactory.FreeStruct(tree);
    }

    [Test]
    public void UpdateParameters_OnAnUnchangedRig_ChangesNothing() {
        var root = scene.Chain(4);
        var rig = JiggleSceneFactory.Rig(root);
        var tree = JigglePhysics.CreateJiggleTree(rig, null);
        var before = (JigglePointParameters[])tree.parameters.Clone();

        rig.UpdateParameters(tree, new List<JigglePointParameters>());

        for (int i = 0; i < before.Length; i++) {
            Assert.AreEqual(before[i].angleElasticity, tree.parameters[i].angleElasticity, Tolerance, $"point {i} angleElasticity");
            Assert.AreEqual(before[i].gravityMultiplier, tree.parameters[i].gravityMultiplier, Tolerance, $"point {i} gravityMultiplier");
        }
        JiggleSceneFactory.FreeStruct(tree);
    }

    /// <summary>
    /// The back projected root carries the parameters the first real bone integrates against, so it
    /// must stay on the authored gravity even when the root bone itself is pinned.
    /// </summary>
    [Test]
    public void UpdateParameters_LeavesTheBackProjectedRootOnTheAuthoredGravity() {
        var root = scene.Chain(3);
        var rig = JiggleSceneFactory.Rig(root);
        rig.excludeRoot = true;
        rig.jiggleTreeInputParameters.gravity.value = 3f;
        var tree = JigglePhysics.CreateJiggleTree(rig, null);

        rig.UpdateParameters(tree, new List<JigglePointParameters>());

        Assert.AreEqual(3f, tree.parameters[0].gravityMultiplier, Tolerance);
        Assert.AreEqual(1f, tree.parameters[1].rootElasticity, Tolerance, "the root bone itself is still pinned");
        JiggleSceneFactory.FreeStruct(tree);
    }

    [Test]
    public void UpdateParameters_FollowsTheAuthoredCurveAlongTheChain() {
        var root = scene.Chain(3);
        var rig = JiggleSceneFactory.Rig(root);
        rig.jiggleTreeInputParameters.stiffness.value = 1f;
        rig.jiggleTreeInputParameters.stiffness.curveEnabled = true;
        rig.jiggleTreeInputParameters.stiffness.curve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
        var tree = JigglePhysics.CreateJiggleTree(rig, null);

        rig.UpdateParameters(tree, new List<JigglePointParameters>());

        Assert.AreEqual(0f, tree.parameters[1].angleElasticity, 1e-3f, "root end of the chain");
        Assert.AreEqual(0.25f, tree.parameters[2].angleElasticity, 1e-3f, "half way down");
        Assert.AreEqual(1f, tree.parameters[3].angleElasticity, 1e-3f, "tip of the chain");
        JiggleSceneFactory.FreeStruct(tree);
    }

    // -------------------------------------------------------------- surface

    [Test]
    public void GetJiggleCollidersAndTransforms_StayIndexAligned() {
        var root = scene.Chain(2);
        var first = scene.Spawn("first");
        var second = scene.Spawn("second");
        var rig = JiggleSceneFactory.Rig(root);
        rig.jiggleColliders = new[] {
            JiggleSceneFactory.SphereCollider(first, 0.1f),
            JiggleSceneFactory.SphereCollider(second, 0.2f),
        };
        var colliders = new List<JiggleCollider>();
        var transforms = new List<Transform>();

        rig.GetJiggleColliders(colliders);
        rig.GetJiggleColliderTransforms(transforms);

        Assert.AreEqual(2, colliders.Count);
        Assert.AreEqual(2, transforms.Count);
        Assert.AreEqual(0.1f, colliders[0].radius, Tolerance);
        Assert.AreSame(first, transforms[0]);
        Assert.AreEqual(0.2f, colliders[1].radius, Tolerance);
        Assert.AreSame(second, transforms[1]);
    }

    [Test]
    public void IsValid_RequiresTheRootToSitUnderTheGivenTransform() {
        var avatar = scene.Spawn("avatar");
        var root = scene.Chain(2);
        root.SetParent(avatar, true);
        var rig = JiggleSceneFactory.Rig(root);

        Assert.IsTrue(rig.IsValid(avatar));
        Assert.IsFalse(rig.IsValid(scene.Spawn("someoneElse")));
    }

    [Test]
    public void Default_HasNoRootAndTheCurrentSerialisedVersion() {
        var rig = JiggleRigData.Default();

        Assert.IsNull(rig.rootBone);
        Assert.IsTrue(rig.hasSerializedData);
        Assert.AreEqual("v0.0.2", rig.serializedVersion);
        Assert.AreEqual(0, rig.excludedTransforms.Length);
        Assert.AreEqual(0, rig.jiggleColliders.Length);
        Assert.IsTrue(rig.GetHasRootTransformError());
    }
}

}
