using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace GatorDragonGames.JigglePhysics.Tests {

/// <summary>
/// Covers the JiggleRig component — the thing an author actually drops on an avatar. The OnEnable
/// and OnDisable wrappers are not exercised because Unity does not run them outside play mode; they
/// are one line each and forward to OnInitialize and OnRemove, which are covered here directly.
/// </summary>
[TestFixture]
internal class JiggleRigComponentTests {
    private const float FixedDeltaTime = 0.02f;

    private JiggleBoneScene scene;
    private double time;

    [SetUp]
    public void SetUp() {
        JiggleRuntimeStatics.Boot();
        scene = new JiggleBoneScene();
        time = 0.0;
    }

    [TearDown]
    public void TearDown() {
        JiggleRuntimeStatics.Shutdown();
        scene?.Dispose();
        scene = null;
    }

    /// <summary>
    /// The component keeps its rig data in a private serialised field with no setter, so a test has
    /// to write it the way the inspector would.
    /// </summary>
    private static void SetRigData(JiggleRig rig, JiggleRigData data) {
        const string fieldName = "jiggleRigData";
        var field = typeof(JiggleRig).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null) {
            throw new InvalidOperationException($"JiggleRig.{fieldName} was renamed; the test helper needs updating.");
        }
        field.SetValue(rig, data);
    }

    private sealed class RigFixture {
        public JiggleRig component;
        public Transform[] bones;
        public Transform Tip => bones[bones.Length - 1];
    }

    private RigFixture CreateRig(string prefix, int boneCount = 4, float stiffness = 0.8f, float gravity = 0f,
        float drag = 0.1f) {
        // Horizontal, so gravity has something to bend; a chain hanging along gravity never moves.
        var root = scene.Chain(boneCount, 0.25f, prefix, new Vector3(0.25f, 0f, 0f));
        var host = scene.Spawn($"{prefix}host");
        host.gameObject.SetActive(false);
        var component = host.gameObject.AddComponent<JiggleRig>();
        var data = JiggleSceneFactory.Rig(root);
        data.jiggleTreeInputParameters.stiffness.value = stiffness;
        data.jiggleTreeInputParameters.gravity.value = gravity;
        data.jiggleTreeInputParameters.drag.value = drag;
        SetRigData(component, data);
        host.gameObject.SetActive(true);
        return new RigFixture { component = component, bones = JiggleBoneScene.Descend(root, boneCount) };
    }

    private void Frame(int count = 1) {
        for (int i = 0; i < count; i++) {
            time += FixedDeltaTime + 0.001;
            JigglePhysics.ScheduleSimulate(time, FixedDeltaTime);
            JigglePhysics.SchedulePose(time);
            JigglePhysics.CompletePose();
        }
        JigglePhysics.CompleteSimulate();
    }

    // ------------------------------------------------------------ registration

    [Test]
    public void OnInitialize_WithoutARootBone_Throws() {
        var host = scene.Spawn("host");
        host.gameObject.SetActive(false);
        var component = host.gameObject.AddComponent<JiggleRig>();
        SetRigData(component, JiggleRigData.Default());
        host.gameObject.SetActive(true);

        Assert.Throws<UnityException>(() => component.OnInitialize());
    }

    /// <summary>
    /// The whole point of the component: enabling it should make the bones actually jiggle, all the
    /// way from registration through to the transform write back.
    /// </summary>
    [Test]
    public void OnInitialize_MakesTheBonesSimulate() {
        var rig = CreateRig("live", stiffness: 0.2f, gravity: 6f);
        rig.component.OnInitialize();
        Frame(4);
        var startY = rig.Tip.position.y;

        Frame(50);

        Assert.Less(rig.Tip.position.y, startY - 0.01f, "the rig never reached the simulation");
    }

    [Test]
    public void OnInitialize_Twice_DoesNotRegisterTwice() {
        var rig = CreateRig("double");

        rig.component.OnInitialize();

        Assert.DoesNotThrow(() => rig.component.OnInitialize());
        Assert.DoesNotThrow(() => Frame(4));
    }

    [Test]
    public void OnRemove_StopsTheRigFromBeingSimulated() {
        var rig = CreateRig("removed", stiffness: 0.2f, gravity: 6f);
        rig.component.OnInitialize();
        Frame(20);

        rig.component.OnRemove();
        Frame(6);
        var settled = rig.Tip.position;
        Frame(30);

        Assert.AreEqual(0f, Vector3.Distance(settled, rig.Tip.position), 1e-4f,
            "the rig is still being written to after removal");
    }

    [Test]
    public void OnRemove_BeforeInitialize_IsANoOp() {
        var rig = CreateRig("never");

        Assert.DoesNotThrow(() => rig.component.OnRemove());
    }

    [Test]
    public void OnRemove_ThenOnInitialize_ReRegistersTheRig() {
        var rig = CreateRig("recycled", stiffness: 0.2f, gravity: 6f);
        rig.component.OnInitialize();
        Frame(6);
        rig.component.OnRemove();
        Frame(4);

        rig.component.OnInitialize();
        Frame(4);
        var startY = rig.Tip.position.y;
        Frame(50);

        Assert.Less(rig.Tip.position.y, startY - 0.01f, "the rig did not come back after being re-enabled");
    }

    // ------------------------------------------------------------- parameters

    [Test]
    public void GetJiggleRigData_ReturnsTheConfiguredData() {
        var rig = CreateRig("data");

        var data = rig.component.GetJiggleRigData();

        Assert.AreSame(rig.bones[0], data.rootBone);
    }

    [Test]
    public void GetInputParameters_ReturnsTheAuthoredParameters() {
        var rig = CreateRig("params", stiffness: 0.35f);

        var parameters = rig.component.GetInputParameters();

        Assert.AreEqual(0.35f, parameters.stiffness.value, 1e-6f);
    }

    [Test]
    public void SetInputParameters_ReplacesThemLocally() {
        var rig = CreateRig("params");
        var replacement = JiggleTreeInputParameters.Default();
        replacement.stiffness.value = 0.15f;

        rig.component.SetInputParameters(replacement);

        Assert.AreEqual(0.15f, rig.component.GetInputParameters().stiffness.value, 1e-6f);
    }

    /// <summary>
    /// SetInputParameters only edits the local copy — the documented way to get it onto a running rig
    /// is an explicit UpdateParameters call, so a rig that was frozen should start moving after one.
    /// </summary>
    [Test]
    public void SetInputParameters_ThenUpdateParameters_ReachesTheRunningRig() {
        var rig = CreateRig("push", stiffness: 1f, gravity: 0f);
        rig.component.OnInitialize();
        Frame(20);
        var startY = rig.Tip.position.y;

        var loosened = rig.component.GetInputParameters();
        loosened.stiffness.value = 0f;
        loosened.gravity.value = 20f;
        rig.component.SetInputParameters(loosened);
        rig.component.UpdateParameters();
        Frame(50);

        Assert.Less(rig.Tip.position.y, startY - 0.01f, "the pushed parameters never took effect");
    }

    [Test]
    public void UpdateParameters_BeforeInitialize_IsANoOp() {
        var rig = CreateRig("early");

        Assert.DoesNotThrow(() => rig.component.UpdateParameters());
    }

    [Test]
    public void HasAnimatedParameters_RoundTrips() {
        var rig = CreateRig("animated");

        Assert.IsFalse(rig.component.HasAnimatedParameters);
        rig.component.HasAnimatedParameters = true;
        Assert.IsTrue(rig.component.HasAnimatedParameters);
    }

    // -------------------------------------------------------------- rest pose

    [Test]
    public void SnapToRestPose_RestoresTheBones() {
        var rig = CreateRig("snap");
        rig.bones[1].localPosition = new Vector3(9f, 9f, 9f);

        rig.component.SnapToRestPose();

        Assert.AreEqual(0f, Vector3.Distance(new Vector3(0.25f, 0f, 0f), rig.bones[1].localPosition), 1e-4f);
    }

    [Test]
    public void ResampleRestPose_AdoptsTheCurrentPoseAsTheNewRest() {
        var rig = CreateRig("resample");
        rig.bones[1].localPosition = new Vector3(0.75f, 0f, 0f);

        rig.component.ResampleRestPose();
        rig.bones[1].localPosition = Vector3.zero;
        rig.component.SnapToRestPose();

        Assert.AreEqual(0f, Vector3.Distance(new Vector3(0.75f, 0f, 0f), rig.bones[1].localPosition), 1e-4f);
    }

    /// <summary>
    /// Resampling dirties the tree so it rebuilds against the new rest pose. The rebuild has to be
    /// preceded by a scheduled removal, or the rebuilt tree is added a second time under the same
    /// rootID — which leaks its transform slice and makes the next removal target the wrong tree.
    /// </summary>
    [Test]
    public void ResampleRestPose_OnALiveRig_DoesNotDuplicateTheTree() {
        var rig = CreateRig("resampleLive");
        rig.component.OnInitialize();
        Frame(6);

        rig.component.ResampleRestPose();

        Assert.DoesNotThrow(() => Frame(6));
    }

    // --------------------------------------------------------------- teleport

    [Test]
    public void Teleport_BeforeInitialize_IsANoOp() {
        var rig = CreateRig("teleportEarly");

        Assert.DoesNotThrow(() => rig.component.Teleport(new Vector3(1f, 2f, 3f)));
    }

    /// <summary>
    /// Teleporting has to carry the simulation with the avatar. Without it the points stay behind and
    /// the rig snaps violently back over the next few frames.
    /// </summary>
    /// <summary>
    /// Teleporting has to carry the simulation with the avatar. Without it the points stay behind and
    /// the rig either snaps violently back or gets clamped by the runaway guard. Heavily damped and
    /// settled first, so any change in the chain's shape is down to the teleport rather than to the
    /// two frames of ordinary swing either side of it.
    /// </summary>
    [Test]
    public void Teleport_CarriesTheSimulationWithTheAvatar() {
        var rig = CreateRig("teleport", stiffness: 0.2f, gravity: 6f, drag: 0.9f);
        rig.component.OnInitialize();
        Frame(150);
        var offsetBefore = rig.Tip.position - rig.bones[0].position;

        var jump = new Vector3(0f, 0f, 50f);
        rig.bones[0].position += jump;
        rig.component.Teleport(jump);
        Frame(2);

        var offsetAfter = rig.Tip.position - rig.bones[0].position;
        Assert.AreEqual(0f, Vector3.Distance(offsetBefore, offsetAfter), 0.02f,
            "the rig did not travel with the avatar");
    }
}

}
