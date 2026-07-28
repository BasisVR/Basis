using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace GatorDragonGames.JigglePhysics.Tests {

/// <summary>
/// Covers the segment registry — how JigglePhysics decides which rig owns which bones when rigs are
/// nested. Authors nest constantly (a tail rig inside a body rig, ears inside a head), and the rules
/// only show up when a rig is registered out of hierarchy order or removed from the middle.
/// </summary>
[TestFixture]
internal class JiggleTreeSegmentTests {
    private const float Tolerance = 1e-4f;

    private JiggleBoneScene scene;
    private readonly List<JiggleTreeSegment> segments = new List<JiggleTreeSegment>();

    private sealed class TestProvider : IJiggleParameterProvider {
        public JiggleRigData data;

        public TestProvider(JiggleRigData rigData) {
            data = rigData;
        }

        public JiggleRigData GetJiggleRigData() => data;

        public bool HasAnimatedParameters { get; set; }
    }

    [SetUp]
    public void SetUp() {
        JiggleRuntimeStatics.Boot();
        scene = new JiggleBoneScene();
    }

    [TearDown]
    public void TearDown() {
        for (int i = 0; i < segments.Count; i++) {
            JiggleSceneFactory.FreeStruct(segments[i].jiggleTree);
        }
        segments.Clear();
        JiggleRuntimeStatics.Shutdown();
        scene?.Dispose();
        scene = null;
    }

    private JiggleTreeSegment Register(Transform root, TestProvider provider = null) {
        provider ??= new TestProvider(JiggleSceneFactory.Rig(root));
        var segment = new JiggleTreeSegment(provider);
        segments.Add(segment);
        JigglePhysics.AddJiggleTreeSegment(segment);
        return segment;
    }

    /// <summary>avatar / body / spine / tail / tailTip, so nested rigs have somewhere to nest.</summary>
    private Transform[] Avatar() {
        var avatar = scene.Spawn("avatar");
        var body = scene.Spawn("body", avatar, new Vector3(0f, 1f, 0f));
        var spine = scene.Spawn("spine", body, new Vector3(0f, 0.25f, 0f));
        var tail = scene.Spawn("tail", spine, new Vector3(0f, 0.25f, 0f));
        var tailTip = scene.Spawn("tailTip", tail, new Vector3(0f, 0.25f, 0f));
        return new[] { avatar, body, spine, tail, tailTip };
    }

    [Test]
    public void Segment_WithNoRigAboveIt_HasNoParent() {
        var bones = Avatar();

        var segment = Register(bones[1]);

        Assert.IsNull(segment.parent);
        Assert.AreSame(bones[1], segment.transform);
    }

    [Test]
    public void Segment_RegisteredUnderAnExistingRig_IsParentedToIt() {
        var bones = Avatar();
        var body = Register(bones[1]);

        var tail = Register(bones[3]);

        Assert.AreSame(body, tail.parent);
        CollectionAssert.Contains(body.GetChildren(), tail);
    }

    /// <summary>
    /// Component enable order is not hierarchy order, so the outer rig regularly registers after the
    /// inner one. Registering it has to reach down and adopt the rigs already sitting underneath it.
    /// </summary>
    [Test]
    public void Segment_RegisteredAboveExistingRigs_AdoptsThemAsChildren() {
        var bones = Avatar();
        var tail = Register(bones[3]);
        Assert.IsNull(tail.parent);

        var body = Register(bones[1]);

        Assert.AreSame(body, tail.parent);
        CollectionAssert.Contains(body.GetChildren(), tail);
    }

    [Test]
    public void Segment_RegisteredTwiceOnTheSameRoot_IsRejectedWithAWarning() {
        var bones = Avatar();
        Register(bones[1]);
        LogAssert.Expect(LogType.Warning,
            "Multiple Jiggle trees detected targeting the same root transform, Jiggle Physics doesn't support this.");

        var duplicate = Register(bones[1]);

        Assert.IsNull(duplicate.parent, "the duplicate was never registered, so it never got a parent");
    }

    [Test]
    public void RemovingAMiddleSegment_ReParentsItsChildrenToItsParent() {
        var bones = Avatar();
        var body = Register(bones[1]);
        var spine = Register(bones[2]);
        var tail = Register(bones[3]);
        Assert.AreSame(spine, tail.parent);

        JigglePhysics.RemoveJiggleTreeSegment(spine);

        Assert.AreSame(body, tail.parent);
        CollectionAssert.Contains(body.GetChildren(), tail);
    }

    [Test]
    public void RemovingTheOutermostSegment_PromotesItsChildrenToRoots() {
        var bones = Avatar();
        var body = Register(bones[1]);
        var tail = Register(bones[3]);

        JigglePhysics.RemoveJiggleTreeSegment(body);

        Assert.IsNull(tail.parent);
    }

    [Test]
    public void RemovingASegment_DetachesItFromItsParent() {
        var bones = Avatar();
        var body = Register(bones[1]);
        var tail = Register(bones[3]);

        JigglePhysics.RemoveJiggleTreeSegment(tail);

        Assert.IsNull(tail.parent);
        CollectionAssert.DoesNotContain(body.GetChildren(), tail);
    }

    /// <summary>
    /// An inner rig going dirty invalidates the outer one too: the outer tree owns the bones between
    /// them, so its point graph has to be rebuilt against the inner rig's new parameters.
    /// </summary>
    [Test]
    public void SetDirty_PropagatesUpToTheParentSegment() {
        var bones = Avatar();
        var body = Register(bones[1]);
        var tail = Register(bones[3]);
        body.RegenerateJiggleTreeIfNeeded();
        tail.RegenerateJiggleTreeIfNeeded();

        tail.SetDirty();

        Assert.IsTrue(tail.jiggleTree.dirty);
        Assert.IsTrue(body.jiggleTree.dirty);
    }

    [Test]
    public void RegenerateJiggleTreeIfNeeded_BuildsATreeOnFirstUse() {
        var bones = Avatar();
        var segment = Register(bones[1]);
        Assert.IsNull(segment.jiggleTree);

        segment.RegenerateJiggleTreeIfNeeded();

        Assert.IsNotNull(segment.jiggleTree);
        Assert.IsFalse(segment.jiggleTree.dirty);
    }

    [Test]
    public void RegenerateJiggleTreeIfNeeded_LeavesACleanTreeAlone() {
        var bones = Avatar();
        var segment = Register(bones[1]);
        segment.RegenerateJiggleTreeIfNeeded();
        var built = segment.jiggleTree;

        segment.RegenerateJiggleTreeIfNeeded();

        Assert.AreSame(built, segment.jiggleTree);
    }

    [Test]
    public void RegenerateJiggleTreeIfNeeded_RebuildsIntoTheSameInstanceWhenDirty() {
        var bones = Avatar();
        var segment = Register(bones[1]);
        segment.RegenerateJiggleTreeIfNeeded();
        var built = segment.jiggleTree;
        segment.SetDirty();

        segment.RegenerateJiggleTreeIfNeeded();

        Assert.AreSame(built, segment.jiggleTree, "rebuilding should recycle the tree, not orphan it");
        Assert.IsFalse(segment.jiggleTree.dirty);
    }

    [Test]
    public void GetHasAnimatedParameters_IsFalseUntilATreeExists() {
        var bones = Avatar();
        var provider = new TestProvider(JiggleSceneFactory.Rig(bones[1])) { HasAnimatedParameters = true };
        var segment = Register(bones[1], provider);

        Assert.IsFalse(segment.GetHasAnimatedParameters(), "no tree yet, so there is nothing to push to");
        segment.RegenerateJiggleTreeIfNeeded();
        Assert.IsTrue(segment.GetHasAnimatedParameters());
    }

    [Test]
    public void GetHasAnimatedParameters_IsFalseForAStaticRig() {
        var bones = Avatar();
        var segment = Register(bones[1]);
        segment.RegenerateJiggleTreeIfNeeded();

        Assert.IsFalse(segment.GetHasAnimatedParameters());
    }

    [Test]
    public void Teleport_BeforeATreeExists_IsANoOp() {
        var bones = Avatar();
        var segment = Register(bones[1]);

        Assert.DoesNotThrow(() => segment.Teleport(new float3(1f, 2f, 3f)));
    }

    /// <summary>
    /// Teleporting a rig the simulation has not committed yet folds the delta straight into the
    /// tree, so a rig that spawns and teleports on the same frame does not snap back.
    /// </summary>
    [Test]
    public void Teleport_ShiftsAnUncommittedTree() {
        var bones = Avatar();
        var segment = Register(bones[1]);
        segment.RegenerateJiggleTreeIfNeeded();
        var before = segment.jiggleTree.points[1].position;

        segment.Teleport(new float3(0f, 0f, 4f));

        JiggleAssert.AreEqual(before + new float3(0f, 0f, 4f), segment.jiggleTree.points[1].position, Tolerance);
    }

    [Test]
    public void UpdateParameters_PushesTheCurrentRigDataOntoTheTree() {
        var bones = Avatar();
        var rig = JiggleSceneFactory.Rig(bones[1]);
        rig.jiggleTreeInputParameters.stiffness.value = 0.5f;
        var segment = Register(bones[1], new TestProvider(rig));
        segment.RegenerateJiggleTreeIfNeeded();

        segment.UpdateParameters();

        Assert.AreEqual(0.25f, segment.jiggleTree.parameters[1].angleElasticity, Tolerance);
    }

    /// <summary>
    /// Animated parameters are re-pushed every frame, so the flag is what keeps a static rig off the
    /// per-frame path entirely. Editing the rig data behind the segment's back is the only way to
    /// tell a skipped push apart from one that happened to write the same values.
    /// </summary>
    [Test]
    public void UpdateParametersIfNeeded_IsSkippedForAStaticRig() {
        var bones = Avatar();
        var rig = JiggleSceneFactory.Rig(bones[1]);
        rig.jiggleTreeInputParameters.stiffness.value = 0.5f;
        var provider = new TestProvider(rig);
        var segment = Register(bones[1], provider);
        segment.RegenerateJiggleTreeIfNeeded();
        provider.data.jiggleTreeInputParameters.stiffness.value = 1f;

        segment.UpdateParametersIfNeeded();

        Assert.AreEqual(0.25f, segment.jiggleTree.parameters[1].angleElasticity, Tolerance);
    }

    [Test]
    public void UpdateParametersIfNeeded_PushesForAnAnimatedRig() {
        var bones = Avatar();
        var rig = JiggleSceneFactory.Rig(bones[1]);
        rig.jiggleTreeInputParameters.stiffness.value = 0.5f;
        var provider = new TestProvider(rig) { HasAnimatedParameters = true };
        var segment = Register(bones[1], provider);
        segment.RegenerateJiggleTreeIfNeeded();
        provider.data.jiggleTreeInputParameters.stiffness.value = 1f;

        segment.UpdateParametersIfNeeded();

        Assert.AreEqual(1f, segment.jiggleTree.parameters[1].angleElasticity, Tolerance);
    }
}

}
