using System;
using NUnit.Framework;
using UnityEngine;

namespace GatorDragonGames.JigglePhysics.Tests {

/// <summary>
/// Tree construction recurses once per bone, in Visit and again in VisitAndSetCacheData, with no
/// depth limit. The build tests elsewhere use three to six bone chains, so nothing covers what a
/// long hair or tail rig does to the stack. A stack overflow cannot be caught, so each depth has to
/// be its own test: if one dies it takes the run with it, and the rest never report.
/// </summary>
[TestFixture]
internal class JiggleTreeDepthTests {
    private GameObject[] bones;

    [TearDown]
    public void TearDown() {
        if (bones == null) {
            return;
        }
        for (int i = bones.Length - 1; i >= 0; i--) {
            if (bones[i] != null) {
                UnityEngine.Object.DestroyImmediate(bones[i]);
            }
        }
        bones = null;
    }

    private JiggleRigData BuildChainRig(int boneCount) {
        bones = new GameObject[boneCount];
        Transform previous = null;
        for (int i = 0; i < boneCount; i++) {
            bones[i] = new GameObject($"Bone{i}") { hideFlags = HideFlags.HideAndDontSave };
            var t = bones[i].transform;
            if (previous != null) {
                t.SetParent(previous, false);
            }
            t.localPosition = new Vector3(0f, -0.08f, 0f);
            previous = t;
        }

        var rig = new JiggleRigData {
            rootBone = bones[0].transform,
            excludeRoot = false,
            excludedTransforms = Array.Empty<Transform>(),
            jiggleColliders = Array.Empty<JiggleColliderSerializable>(),
            jiggleTreeInputParameters = new JiggleTreeInputParameters {
                stiffness = new JiggleTreeCurvedFloat(0.5f),
                angleLimit = new JiggleTreeCurvedFloat(0.5f),
                stretch = new JiggleTreeCurvedFloat(0f),
                drag = new JiggleTreeCurvedFloat(0.1f),
                airDrag = new JiggleTreeCurvedFloat(0.1f),
                gravity = new JiggleTreeCurvedFloat(1f),
                collisionRadius = new JiggleTreeCurvedFloat(0.03f),
            },
        };
        rig.BuildNormalizedDistanceFromRootList();
        return rig;
    }

    private void AssertChainBuilds(int boneCount) {
        var rig = BuildChainRig(boneCount);
        var tree = JigglePhysics.CreateJiggleTree(rig, null);
        Assert.IsNotNull(tree, $"{boneCount} bone chain produced no tree");
    }

    [Test]
    public void Chain32_Builds() => AssertChainBuilds(32);

    [Test]
    public void Chain64_Builds() => AssertChainBuilds(64);

    /// <summary>
    /// Currently overflows the stack and takes the whole test run down with it, so it is opt in
    /// rather than part of the suite. Explicit rather than Ignore because it is a real defect that
    /// should start passing, not a case to be written off: 32 and 64 bone chains build fine, 128
    /// dies, so the ceiling sits somewhere between. The exact number is not portable — it depends on
    /// the stack the calling thread happens to have and on frame sizes that differ between Mono and
    /// IL2CPP, so a player build may well fail lower than the editor does.
    /// </summary>
    [Test]
    [Explicit("Known failure: deep chains overflow the stack during tree construction.")]
    public void Chain128_Builds() => AssertChainBuilds(128);
}

}
