using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace GatorDragonGames.JigglePhysics.Tests {

/// <summary>
/// Covers JigglePhysics.CreateJiggleTree — the step that turns a bone hierarchy plus its authored
/// rig data into the point graph the jobs simulate. Every rig in the wild goes through here, so the
/// shapes exercised are the ones authors actually build: a chain, a branch, exclusions, a pinned
/// root and bones stacked on top of each other.
/// </summary>
[TestFixture]
internal unsafe class JiggleTreeBuildTests {
    private const float Tolerance = 1e-4f;

    private JiggleBoneScene scene;
    private readonly List<JiggleTree> trees = new List<JiggleTree>();

    [SetUp]
    public void SetUp() {
        scene = new JiggleBoneScene();
    }

    [TearDown]
    public void TearDown() {
        for (int i = 0; i < trees.Count; i++) {
            JiggleSceneFactory.FreeStruct(trees[i]);
        }
        trees.Clear();
        scene?.Dispose();
        scene = null;
    }

    private JiggleTree Build(JiggleRigData rig, JiggleTree existing = null) {
        var tree = JigglePhysics.CreateJiggleTree(rig, existing);
        if (!trees.Contains(tree)) {
            trees.Add(tree);
        }
        return tree;
    }

    private JiggleTree BuildChain(int boneCount, float spacing = 0.25f) {
        var root = scene.Chain(boneCount, spacing);
        return Build(JiggleSceneFactory.Rig(root));
    }

    [Test]
    public void Chain_ProducesAVirtualRootTheRealBonesAndAVirtualTip() {
        var tree = BuildChain(3);

        Assert.AreEqual(5, tree.points.Length);
        Assert.IsFalse(tree.points[0].hasTransform);
        Assert.IsTrue(tree.points[1].hasTransform);
        Assert.IsTrue(tree.points[2].hasTransform);
        Assert.IsTrue(tree.points[3].hasTransform);
        Assert.IsFalse(tree.points[4].hasTransform);
    }

    /// <summary>
    /// The memory bus refuses any tree whose bone array and point array disagree, regenerating it
    /// instead — so a builder that ever breaks this pairing wedges the rig into a rebuild loop.
    /// </summary>
    [Test]
    public void Chain_BoneArrayIsTheSameLengthAsThePointArray() {
        var tree = BuildChain(4);

        Assert.AreEqual(tree.points.Length, tree.bones.Length);
        Assert.AreEqual(tree.points.Length, tree.parameters.Length);
        Assert.AreEqual(tree.points.Length, tree.restPositions.Length);
        Assert.AreEqual(tree.points.Length, tree.restRotations.Length);
    }

    [Test]
    public void VirtualRoot_IsBackProjectedThroughTheFirstChild() {
        var tree = BuildChain(3);

        Assert.AreEqual(-1, tree.points[0].parentIndex);
        JiggleAssert.AreEqual(new float3(0f, 0.25f, 0f), tree.points[0].position, Tolerance);
    }

    /// <summary>
    /// A root with nothing under it has no child to mirror, so it falls back to its own up axis.
    /// The root is rotated here to prove the fallback is in the bone's space, not world space.
    /// </summary>
    [Test]
    public void VirtualRoot_ChildlessRoot_ProjectsAlongItsOwnUpAxis() {
        var root = scene.Spawn("lonely");
        root.rotation = Quaternion.Euler(0f, 0f, 90f);

        var tree = Build(JiggleSceneFactory.Rig(root));

        JiggleAssert.AreEqual(new float3(-0.25f, 0f, 0f), tree.points[0].position, 1e-3f);
    }

    [Test]
    public void ChildlessRoot_StillProducesARealPointAndAVirtualTip() {
        var root = scene.Spawn("lonely");

        var tree = Build(JiggleSceneFactory.Rig(root));

        Assert.AreEqual(3, tree.points.Length);
        Assert.IsTrue(tree.points[1].hasTransform);
        Assert.IsFalse(tree.points[2].hasTransform);
    }

    /// <summary>
    /// The simulate job walks points in array order and reads its parent's already-solved state, so
    /// a parent appearing after its child would silently simulate against last frame's data.
    /// </summary>
    [Test]
    public void Chain_PointsAreOrderedParentBeforeChild() {
        var tree = BuildChain(6);

        for (int i = 1; i < tree.points.Length; i++) {
            Assert.Less(tree.points[i].parentIndex, i, $"point {i} is solved before its parent");
        }
    }

    [Test]
    public void Chain_EveryChildIndexPointsAtARealSlot() {
        var tree = BuildChain(5);

        for (int i = 0; i < tree.points.Length; i++) {
            var point = tree.points[i];
            for (int c = 0; c < point.childrenCount; c++) {
                Assert.GreaterOrEqual(point.childrenIndices[c], 0);
                Assert.Less(point.childrenIndices[c], tree.points.Length);
            }
        }
    }

    [Test]
    public void Chain_PassesTheJobSideValidityCheck() {
        var tree = BuildChain(5);

        var valid = tree.GetStruct().GetIsValid(out var failReason);

        Assert.IsTrue(valid, failReason);
    }

    [Test]
    public void Chain_DistanceFromRootAccumulatesAlongTheChain() {
        var tree = BuildChain(4, 0.5f);

        Assert.AreEqual(0f, tree.points[1].distanceFromRoot, Tolerance);
        Assert.AreEqual(0.5f, tree.points[2].distanceFromRoot, Tolerance);
        Assert.AreEqual(1.0f, tree.points[3].distanceFromRoot, Tolerance);
        Assert.AreEqual(1.5f, tree.points[4].distanceFromRoot, Tolerance);
    }

    [Test]
    public void Chain_RealPointsSitOnTheirBones() {
        var root = scene.Chain(3);
        var bones = JiggleBoneScene.Descend(root, 3);

        var tree = Build(JiggleSceneFactory.Rig(root));

        JiggleAssert.AreEqual(bones[0].position, tree.points[1].position, Tolerance);
        JiggleAssert.AreEqual(bones[1].position, tree.points[2].position, Tolerance);
        JiggleAssert.AreEqual(bones[2].position, tree.points[3].position, Tolerance);
    }

    [Test]
    public void Chain_VirtualTipIsReflectedPastTheLastBone() {
        var tree = BuildChain(3);

        JiggleAssert.AreEqual(new float3(0f, -0.75f, 0f), tree.points[4].position, Tolerance);
    }

    /// <summary>
    /// The root bone is recorded twice — once for the virtual back projection and once for its own
    /// point — and every virtual tip repeats its parent's bone. The memory bus relies on that
    /// padding to keep the bone array and the point array index aligned.
    /// </summary>
    [Test]
    public void Chain_VirtualSlotsRepeatTheirNeighboursBone() {
        var root = scene.Chain(3);
        var bones = JiggleBoneScene.Descend(root, 3);

        var tree = Build(JiggleSceneFactory.Rig(root));

        Assert.AreSame(bones[0], tree.bones[0]);
        Assert.AreSame(bones[0], tree.bones[1]);
        Assert.AreSame(bones[2], tree.bones[3]);
        Assert.AreSame(bones[2], tree.bones[4]);
    }

    [Test]
    public void Branch_RootRecordsEveryBranchAsAChild() {
        var root = scene.Chain(1);
        scene.Spawn("left", root, new Vector3(-0.25f, -0.25f, 0f));
        scene.Spawn("right", root, new Vector3(0.25f, -0.25f, 0f));

        var tree = Build(JiggleSceneFactory.Rig(root));

        Assert.AreEqual(2, tree.points[1].childrenCount);
    }

    [Test]
    public void Branch_EachLeafGetsItsOwnVirtualTip() {
        var root = scene.Chain(1);
        scene.Spawn("left", root, new Vector3(-0.25f, -0.25f, 0f));
        scene.Spawn("right", root, new Vector3(0.25f, -0.25f, 0f));

        var tree = Build(JiggleSceneFactory.Rig(root));

        var virtualCount = 0;
        for (int i = 0; i < tree.points.Length; i++) {
            if (!tree.points[i].hasTransform) {
                virtualCount++;
            }
        }

        Assert.AreEqual(6, tree.points.Length);
        Assert.AreEqual(3, virtualCount, "one back projected root plus one tip per leaf");
    }

    [Test]
    public void ExcludedLeaf_IsDroppedFromTheTree() {
        var root = scene.Chain(3);
        var bones = JiggleBoneScene.Descend(root, 3);

        var tree = Build(JiggleSceneFactory.Rig(root, bones[2]));

        Assert.AreEqual(4, tree.points.Length);
        CollectionAssert.DoesNotContain(tree.bones, bones[2]);
    }

    /// <summary>
    /// Exclusion prunes the subtree, not just the bone: authors exclude a shoulder expecting the
    /// whole arm to stop jiggling, not for the hand to reattach itself further up the chain.
    /// </summary>
    [Test]
    public void ExcludedMidChainBone_TruncatesTheChainAboveIt() {
        var root = scene.Chain(4);
        var bones = JiggleBoneScene.Descend(root, 4);

        var tree = Build(JiggleSceneFactory.Rig(root, bones[1]));

        Assert.AreEqual(3, tree.points.Length);
        CollectionAssert.DoesNotContain(tree.bones, bones[1]);
        CollectionAssert.DoesNotContain(tree.bones, bones[2]);
        CollectionAssert.DoesNotContain(tree.bones, bones[3]);
    }

    [Test]
    public void ExcludeRoot_PinsTheRootBonesParameters() {
        var root = scene.Chain(3);
        var rig = JiggleSceneFactory.Rig(root);
        rig.excludeRoot = true;

        var tree = Build(rig);

        Assert.AreEqual(1f, tree.parameters[1].angleElasticity, Tolerance);
        Assert.AreEqual(1f, tree.parameters[1].lengthElasticity, Tolerance);
        Assert.AreEqual(1f, tree.parameters[1].rootElasticity, Tolerance);
        Assert.AreEqual(0f, tree.parameters[1].elasticitySoften, Tolerance);
    }

    [Test]
    public void ExcludeRoot_LeavesTheRestOfTheChainSimulated() {
        var root = scene.Chain(3);
        var rig = JiggleSceneFactory.Rig(root);
        rig.excludeRoot = true;

        var tree = Build(rig);

        var stiffness = rig.jiggleTreeInputParameters.stiffness.value;
        Assert.AreEqual(stiffness * stiffness, tree.parameters[2].angleElasticity, Tolerance);
    }

    /// <summary>
    /// Exporters routinely emit a helper bone sitting exactly on its parent. Simulating it would
    /// mean a zero length segment, so the builder folds it away and reparents its children.
    /// </summary>
    [Test]
    public void ZeroLengthBone_IsMergedIntoItsParent() {
        var root = scene.Chain(2);
        var mid = root.GetChild(0);
        var duplicate = scene.Spawn("duplicate", mid);
        var tip = scene.Spawn("tip", duplicate, new Vector3(0f, -0.25f, 0f));

        var tree = Build(JiggleSceneFactory.Rig(root));

        CollectionAssert.DoesNotContain(tree.bones, duplicate);
        CollectionAssert.Contains(tree.bones, tip);
        Assert.AreEqual(5, tree.points.Length);
    }

    /// <summary>
    /// The virtual root is back projected as root + (root - firstChild), which collapses onto the
    /// root itself when that first child is a helper bone sitting on it. The root bone then measures
    /// zero length against its own virtual parent and gets folded away, taking the only simulated
    /// bone in the rig with it.
    /// </summary>
    [Test]
    public void ZeroLengthBoneOnTheRoot_DoesNotFoldTheRootAway() {
        var root = scene.Chain(1);
        scene.Spawn("duplicate", root);

        var tree = Build(JiggleSceneFactory.Rig(root));

        CollectionAssert.Contains(tree.bones, root);
        Assert.IsTrue(tree.points[1].hasTransform, "the root bone must still be simulated");
        Assert.AreEqual(3, tree.points.Length);
    }

    [Test]
    public void ZeroLengthBoneOnTheRoot_StillReparentsWhatHangsBelowIt() {
        var root = scene.Chain(1);
        var duplicate = scene.Spawn("duplicate", root);
        var tip = scene.Spawn("tip", duplicate, new Vector3(0f, -0.25f, 0f));

        var tree = Build(JiggleSceneFactory.Rig(root));

        CollectionAssert.Contains(tree.bones, root);
        CollectionAssert.DoesNotContain(tree.bones, duplicate);
        CollectionAssert.Contains(tree.bones, tip);
        Assert.AreEqual(4, tree.points.Length);
    }

    /// <summary>
    /// A zero length bone with nothing under it cannot be folded away — there is no child to
    /// reparent — so it becomes the virtual tip instead, projected along its parent's own direction.
    /// </summary>
    [Test]
    public void ZeroLengthLeaf_BecomesTheVirtualTip() {
        var root = scene.Chain(2);
        var mid = root.GetChild(0);
        var duplicate = scene.Spawn("duplicate", mid);

        var tree = Build(JiggleSceneFactory.Rig(root));

        Assert.AreEqual(4, tree.points.Length);
        Assert.IsFalse(tree.points[3].hasTransform);
        Assert.AreSame(duplicate, tree.bones[3]);
        JiggleAssert.AreEqual(new float3(0f, -0.5f, 0f), tree.points[3].position, Tolerance);
    }

    /// <summary>
    /// A merged leaf registers itself against its parent as it is created, so it must report back
    /// "nothing to add" — otherwise the caller adds the same index again and the tip is counted
    /// twice in every per-child average the simulation takes.
    /// </summary>
    [Test]
    public void ZeroLengthLeaf_IsRegisteredOnItsParentExactlyOnce() {
        var root = scene.Chain(2);
        var mid = root.GetChild(0);
        scene.Spawn("duplicate", mid);

        var tree = Build(JiggleSceneFactory.Rig(root));

        Assert.AreEqual(1, tree.points[2].childrenCount, "the merged leaf was registered twice");
        Assert.AreEqual(3, tree.points[2].childrenIndices[0]);
    }

    [Test]
    public void EveryPoint_ListsEachChildOnlyOnce() {
        var root = scene.Chain(2);
        var mid = root.GetChild(0);
        scene.Spawn("duplicate", mid);
        scene.Spawn("realChild", mid, new Vector3(0f, -0.25f, 0f));

        var tree = Build(JiggleSceneFactory.Rig(root));

        for (int i = 0; i < tree.points.Length; i++) {
            var point = tree.points[i];
            var seen = new List<int>();
            for (int c = 0; c < point.childrenCount; c++) {
                CollectionAssert.DoesNotContain(seen, point.childrenIndices[c], $"point {i} repeats a child");
                seen.Add(point.childrenIndices[c]);
            }
        }
    }

    [Test]
    public void Regenerate_ReusesTheTreeInstance() {
        var root = scene.Chain(3);
        var rig = JiggleSceneFactory.Rig(root);
        var first = Build(rig);

        var second = Build(rig, first);

        Assert.AreSame(first, second);
    }

    [Test]
    public void Regenerate_ClearsTheDirtyFlag() {
        var root = scene.Chain(3);
        var rig = JiggleSceneFactory.Rig(root);
        var tree = Build(rig);
        tree.SetDirty();

        Build(rig, tree);

        Assert.IsFalse(tree.dirty);
    }

    /// <summary>
    /// Excluding bones at runtime shrinks the tree in place. The job side buffers are sized from the
    /// point count, so the resize path has to repoint them rather than reuse the old allocation.
    /// </summary>
    [Test]
    public void Regenerate_AfterExcludingABone_ShrinksThePointArrays() {
        var root = scene.Chain(4);
        var bones = JiggleBoneScene.Descend(root, 4);
        var tree = Build(JiggleSceneFactory.Rig(root));
        var before = tree.GetStruct().pointCount;

        Build(JiggleSceneFactory.Rig(root, bones[2]), tree);

        Assert.AreEqual(6u, before);
        Assert.AreEqual(4, tree.points.Length);
        Assert.AreEqual(4u, tree.GetStruct().pointCount);
        Assert.AreEqual(4, tree.bones.Length);
    }

    [Test]
    public void Regenerate_WithTheSamePointCount_KeepsTheJobBuffers() {
        var root = scene.Chain(3);
        var rig = JiggleSceneFactory.Rig(root);
        var tree = Build(rig);
        var before = tree.GetStruct().points;

        Build(rig, tree);

        Assert.IsTrue(before == tree.GetStruct().points);
    }

    [Test]
    public void RootID_IsStableAcrossRegeneration() {
        var root = scene.Chain(3);
        var rig = JiggleSceneFactory.Rig(root);
        var tree = Build(rig);
        var before = tree.rootID;

        Build(rig, tree);

        Assert.AreEqual(before, tree.rootID);
    }

    [Test]
    public void RootID_DiffersBetweenRigsOnDifferentRoots() {
        var first = Build(JiggleSceneFactory.Rig(scene.Chain(2, 0.25f, "a")));
        var second = Build(JiggleSceneFactory.Rig(scene.Chain(2, 0.25f, "b")));

        Assert.AreNotEqual(first.rootID, second.rootID);
    }

    [Test]
    public void SetDirty_RaisesTheDirtiedEventOnce() {
        var tree = BuildChain(2);
        var raised = 0;
        tree.dirtied += _ => raised++;

        tree.SetDirty();
        tree.SetDirty();

        Assert.AreEqual(1, raised);
        Assert.IsTrue(tree.dirty);
    }

    [Test]
    public void Colliders_AreCopiedFromTheRigData() {
        var root = scene.Chain(2);
        var first = scene.Spawn("colliderA");
        var second = scene.Spawn("colliderB");
        var rig = JiggleSceneFactory.Rig(root);
        rig.jiggleColliders = new[] {
            JiggleSceneFactory.SphereCollider(first, 0.2f),
            JiggleSceneFactory.SphereCollider(second, 0.3f),
        };

        var tree = Build(rig);

        Assert.AreEqual(2, tree.personalColliders.Length);
        Assert.AreEqual(0.2f, tree.personalColliders[0].radius, Tolerance);
        Assert.AreEqual(0.3f, tree.personalColliders[1].radius, Tolerance);
        Assert.AreSame(first, tree.personalColliderTransforms[0]);
        Assert.AreSame(second, tree.personalColliderTransforms[1]);
    }

    [Test]
    public void LongChain_BuildsAndStaysValid() {
        var tree = BuildChain(64, 0.05f);

        var valid = tree.GetStruct().GetIsValid(out var failReason);

        Assert.AreEqual(66, tree.points.Length);
        Assert.IsTrue(valid, failReason);
    }

    /// <summary>
    /// A point stores its children in a fixed 32 slot array. Hair cards fanned off a single scalp
    /// bone hit that ceiling, and the overflow has to be dropped loudly rather than corrupting the
    /// point next door in memory.
    /// </summary>
    [Test]
    public void WideBone_BeyondTheChildLimit_WarnsAndKeepsTheFirstThirtyTwo() {
        var root = scene.Chain(1);
        for (int i = 0; i < JiggleSimulatedPoint.MAX_CHILDREN + 1; i++) {
            scene.Spawn($"strand{i}", root, new Vector3(i * 0.01f + 0.05f, -0.25f, 0f));
        }
        LogAssert.Expect(LogType.Warning,
            $"JigglePhysics: Bone exceeded maximum of {JiggleSimulatedPoint.MAX_CHILDREN} children, extra children will be ignored.");

        var tree = Build(JiggleSceneFactory.Rig(root));

        Assert.AreEqual(JiggleSimulatedPoint.MAX_CHILDREN, tree.points[1].childrenCount);
    }

    [Test]
    public void ResetTransformsToRest_RestoresTheAuthoredLocalPose() {
        var root = scene.Chain(3);
        var bones = JiggleBoneScene.Descend(root, 3);
        var tree = Build(JiggleSceneFactory.Rig(root));
        bones[1].localPosition = new Vector3(5f, 5f, 5f);

        tree.ResetTransformsToRest();

        JiggleAssert.AreEqual(new float3(0f, -0.25f, 0f), (float3)(Vector3)bones[1].localPosition, Tolerance);
    }

    [Test]
    public void ResampleRestPose_AdoptsTheCurrentLocalPose() {
        var root = scene.Chain(3);
        var bones = JiggleBoneScene.Descend(root, 3);
        var tree = Build(JiggleSceneFactory.Rig(root));
        bones[1].localPosition = new Vector3(0f, -0.75f, 0f);

        tree.ResampleRestPose();
        bones[1].localPosition = Vector3.zero;
        tree.ResetTransformsToRest();

        JiggleAssert.AreEqual(new float3(0f, -0.75f, 0f), (float3)(Vector3)bones[1].localPosition, Tolerance);
    }

    [Test]
    public void Translate_ShiftsEveryPositionChannel() {
        var tree = BuildChain(3);
        var before = tree.points[2];
        var delta = new float3(1f, 2f, 3f);

        tree.Translate(delta);

        var after = tree.points[2];
        JiggleAssert.AreEqual(before.position + delta, after.position, Tolerance);
        JiggleAssert.AreEqual(before.lastPosition + delta, after.lastPosition, Tolerance);
        JiggleAssert.AreEqual(before.workingPosition + delta, after.workingPosition, Tolerance);
        JiggleAssert.AreEqual(before.pose + delta, after.pose, Tolerance);
        JiggleAssert.AreEqual(before.parentPose + delta, after.parentPose, Tolerance);
    }

    [Test]
    public void Translate_AlsoMovesTheAlreadyPublishedJobBuffer() {
        var tree = BuildChain(3);
        var data = tree.GetStruct();
        var before = data.points[2].position;

        tree.Translate(new float3(0f, 10f, 0f));

        JiggleAssert.AreEqual(before + new float3(0f, 10f, 0f), tree.GetStruct().points[2].position, Tolerance);
    }

    [Test]
    public void SetParameters_WithAMismatchedCount_IsRejected() {
        var tree = BuildChain(3);
        var before = tree.parameters[1].angleElasticity;
        var shortList = new List<JigglePointParameters> { JiggleTestFactory.Params(angleElasticity: 0.5f) };
        LogAssert.Expect(LogType.Error,
            $"JiggleTree.SetParameters: points count {tree.points.Length} does not match parameters count 1");

        tree.SetParameters(shortList);

        Assert.AreEqual(before, tree.parameters[1].angleElasticity, Tolerance);
    }

    [Test]
    public void SetParameters_PushesThroughToTheJobBuffer() {
        var tree = BuildChain(3);
        var data = tree.GetStruct();
        var updated = new List<JigglePointParameters>();
        for (int i = 0; i < tree.points.Length; i++) {
            updated.Add(JiggleTestFactory.Params(angleElasticity: 0.25f));
        }

        tree.SetParameters(updated);

        Assert.AreEqual(0.25f, data.parameters[1].angleElasticity, Tolerance);
    }

    [Test]
    public void GetStruct_ReturnsTheSameBuffersOnEveryCall() {
        var tree = BuildChain(3);

        var first = tree.GetStruct();
        var second = tree.GetStruct();

        Assert.IsTrue(first.points == second.points);
        Assert.IsTrue(first.parameters == second.parameters);
    }

    [Test]
    public void GetStruct_StartsUnassignedToASlice() {
        var tree = BuildChain(3);

        var data = tree.GetStruct();

        Assert.AreEqual(tree.rootID, data.rootID);
        Assert.AreEqual((uint)tree.points.Length, data.pointCount);
        Assert.AreEqual(0u, data.colliderCount);
    }

    [Test]
    public void SetTransformIndexOffset_RejectsANegativeSlice() {
        var tree = BuildChain(2);

        Assert.Throws<UnityException>(() => tree.SetTransformIndexOffset(-1));
    }
}

}
