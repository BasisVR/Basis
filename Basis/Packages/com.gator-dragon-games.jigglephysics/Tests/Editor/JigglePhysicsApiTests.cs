using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace GatorDragonGames.JigglePhysics.Tests {

/// <summary>
/// Covers the JigglePhysics static facade: the per-frame entry points a host calls, the tree
/// regeneration budget that spreads a join storm over several frames, the culling and render hooks,
/// and the startup-setting latch. These are the functions an integrator touches, so most of them are
/// checked through the real frame loop rather than in isolation.
/// </summary>
[TestFixture]
internal unsafe class JigglePhysicsApiTests {
    private const float FixedDeltaTime = 0.02f;
    private const int DefaultRegenerationBudget = 4;

    private JiggleBoneScene scene;
    private readonly List<JiggleTreeSegment> segments = new List<JiggleTreeSegment>();
    private double time;

    private float savedCellSize;
    private int savedMaxColliderCellSpan;
    private bool savedCullingEnabled;

    [SetUp]
    public void SetUp() {
        savedCellSize = JiggleSettings.BroadPhaseCellSize;
        savedMaxColliderCellSpan = JiggleSettings.MaxColliderCellSpan;
        savedCullingEnabled = JiggleSettings.CullingEnabled;
        JiggleRuntimeStatics.Boot();
        scene = new JiggleBoneScene();
        time = 0.0;
    }

    [TearDown]
    public void TearDown() {
        JigglePhysics.SetMaxTreeRegenerationsPerFlush(DefaultRegenerationBudget);
        JigglePhysics.SetCollisionCulling(false, false, 20f);
        JigglePhysics.SetCullingCameras(null);
        for (int i = 0; i < segments.Count; i++) {
            JiggleSceneFactory.FreeStruct(segments[i].jiggleTree);
        }
        segments.Clear();
        // Shutdown resets the startup-setting latch, so the saved values below can be written back.
        JiggleRuntimeStatics.Shutdown();
        JiggleSettings.BroadPhaseCellSize = savedCellSize;
        JiggleSettings.MaxColliderCellSpan = savedMaxColliderCellSpan;
        JiggleSettings.CullingEnabled = savedCullingEnabled;
        scene?.Dispose();
        scene = null;
    }

    private JiggleTreeSegment Register(string prefix, int boneCount = 3) {
        var root = scene.Chain(boneCount, 0.25f, prefix);
        var segment = new JiggleTreeSegment(new JiggleTestRigProvider(JiggleSceneFactory.Rig(root)));
        segments.Add(segment);
        JigglePhysics.AddJiggleTreeSegment(segment);
        return segment;
    }

    private void RegisterMany(int count) {
        for (int i = 0; i < count; i++) {
            Register($"rig{i}_");
        }
    }

    private int BuiltCount() {
        var built = 0;
        for (int i = 0; i < segments.Count; i++) {
            if (segments[i].jiggleTree != null) {
                built++;
            }
        }
        return built;
    }

    /// <summary>
    /// One host frame. The time step is nudged past the fixed delta because ScheduleSimulate only
    /// advances on a strictly greater comparison, and SchedulePose is what clears the once-per-frame
    /// latch.
    /// </summary>
    private void Frame(int count = 1) {
        for (int i = 0; i < count; i++) {
            time += FixedDeltaTime + 0.001;
            JigglePhysics.ScheduleSimulate(time, FixedDeltaTime);
            JigglePhysics.SchedulePose(time);
            JigglePhysics.CompletePose();
        }
        JigglePhysics.CompleteSimulate();
    }

    // ------------------------------------------------------------ frame loop

    [Test]
    public void ScheduleSimulate_BuildsAndRunsRegisteredRigs() {
        var segment = Register("solo");

        Frame(4);

        Assert.IsNotNull(segment.jiggleTree);
        Assert.IsFalse(segment.jiggleTree.dirty);
    }

    [Test]
    public void ScheduleSimulate_BeforeAFixedStepHasElapsed_DoesNothing() {
        Register("solo");

        JigglePhysics.ScheduleSimulate(FixedDeltaTime * 0.5, FixedDeltaTime);

        Assert.AreEqual(0, BuiltCount());
    }

    /// <summary>
    /// Hosts call ScheduleSimulate from more than one place (update, late update, a camera hook), so
    /// the second call in a frame has to be inert rather than stepping the simulation twice.
    /// </summary>
    [Test]
    public void ScheduleSimulate_TwiceInOneFrame_OnlyFlushesOnce() {
        JigglePhysics.SetMaxTreeRegenerationsPerFlush(1);
        RegisterMany(3);

        time += FixedDeltaTime + 0.001;
        JigglePhysics.ScheduleSimulate(time, FixedDeltaTime);
        time += FixedDeltaTime + 0.001;
        JigglePhysics.ScheduleSimulate(time, FixedDeltaTime);

        Assert.AreEqual(1, BuiltCount());
    }

    [Test]
    public void SchedulePose_ClearsTheOncePerFrameLatch() {
        JigglePhysics.SetMaxTreeRegenerationsPerFlush(1);
        RegisterMany(3);

        time += FixedDeltaTime + 0.001;
        JigglePhysics.ScheduleSimulate(time, FixedDeltaTime);
        JigglePhysics.SchedulePose(time);
        time += FixedDeltaTime + 0.001;
        JigglePhysics.ScheduleSimulate(time, FixedDeltaTime);
        JigglePhysics.CompletePose();
        JigglePhysics.CompleteSimulate();

        Assert.AreEqual(2, BuiltCount());
    }

    [Test]
    public void CompleteCalls_WithoutAnythingScheduled_AreSafe() {
        Assert.DoesNotThrow(() => JigglePhysics.CompletePose());
        Assert.DoesNotThrow(() => JigglePhysics.CompleteSimulate());
    }

    [Test]
    public void FrameLoop_AfterDispose_IsSafe() {
        Register("solo");
        Frame(3);

        JigglePhysics.Dispose();

        Assert.DoesNotThrow(() => JigglePhysics.SchedulePose(time));
        Assert.DoesNotThrow(() => JigglePhysics.CompletePose());
        Assert.DoesNotThrow(() => JigglePhysics.CompleteSimulate());
    }

    // ---------------------------------------------------- regeneration budget

    /// <summary>
    /// Many avatars loading on one frame would otherwise rebuild every tree in a single flush and
    /// spike the main thread. The budget caps rebuilds per flush and leaves the rest dirty.
    /// </summary>
    [Test]
    public void RegenerationBudget_CapsHowManyRigsRebuildPerFlush() {
        JigglePhysics.SetMaxTreeRegenerationsPerFlush(2);
        RegisterMany(6);

        Frame(1);

        Assert.AreEqual(2, BuiltCount());
    }

    [Test]
    public void RegenerationBudget_BacklogDrainsOverSubsequentFlushes() {
        JigglePhysics.SetMaxTreeRegenerationsPerFlush(2);
        RegisterMany(6);

        Frame(6);

        Assert.AreEqual(6, BuiltCount());
    }

    [Test]
    public void RegenerationBudget_OfZeroMeansUnlimited() {
        JigglePhysics.SetMaxTreeRegenerationsPerFlush(0);
        RegisterMany(6);

        Frame(1);

        Assert.AreEqual(6, BuiltCount());
    }

    [Test]
    public void DirtyingASegment_RebuildsItOnTheNextFlush() {
        var segment = Register("solo");
        Frame(3);
        var built = segment.jiggleTree;
        segment.SetDirty();

        Frame(2);

        Assert.AreSame(built, segment.jiggleTree);
        Assert.IsFalse(segment.jiggleTree.dirty);
    }

    [Test]
    public void SetGlobalDirty_IsAcceptedAtAnyTime() {
        Register("solo");

        Assert.DoesNotThrow(() => JigglePhysics.SetGlobalDirty());
        Assert.DoesNotThrow(() => Frame(2));
    }

    // ---------------------------------------------------------------- culling

    [Test]
    public void SetCullingCameras_WithNull_ClearsTheCameras() {
        Assert.DoesNotThrow(() => JigglePhysics.SetCullingCameras(null));
    }

    [Test]
    public void SetCullingCameras_SkipsNullEntries() {
        var camera = scene.Spawn("camera").gameObject.AddComponent<Camera>();

        Assert.DoesNotThrow(() => JigglePhysics.SetCullingCameras(new List<Camera> { null, camera, null }));
    }

    /// <summary>
    /// The camera buffer is a fixed 16 slots, so a host handing over more must be truncated rather
    /// than writing past the end of it.
    /// </summary>
    [Test]
    public void SetCullingCameras_ClampsToTheSupportedCameraCount() {
        var cameras = new List<Camera>();
        for (int i = 0; i < 20; i++) {
            cameras.Add(scene.Spawn($"camera{i}").gameObject.AddComponent<Camera>());
        }

        Assert.DoesNotThrow(() => JigglePhysics.SetCullingCameras(cameras));
        Assert.DoesNotThrow(() => Frame(2));
    }

    [Test]
    public void SetCullingCameras_HonoursTheFrustumExpansionSetting() {
        var camera = scene.Spawn("camera").gameObject.AddComponent<Camera>();
        JiggleSettings.CullFrustumExpansion = 1.5f;

        Assert.DoesNotThrow(() => JigglePhysics.SetCullingCameras(new List<Camera> { camera }));

        JiggleSettings.CullFrustumExpansion = 1f;
    }

    [Test]
    public void SetCollisionCulling_IsAcceptedAndSurvivesAFrame() {
        Register("solo");
        JigglePhysics.AddJiggleCollider(JiggleSceneFactory.SphereCollider(scene.Spawn("world"), 0.5f));

        JigglePhysics.SetCollisionCulling(true, true, 8f);

        Assert.DoesNotThrow(() => Frame(4));
    }

    // -------------------------------------------------------------- colliders

    [Test]
    public void AddAndRemoveJiggleCollider_RoundTripThroughTheFrameLoop() {
        var collider = JiggleSceneFactory.SphereCollider(scene.Spawn("world"), 0.5f);
        Register("solo");

        JigglePhysics.AddJiggleCollider(collider);
        Frame(3);
        JigglePhysics.RemoveJiggleCollider(collider);

        Assert.DoesNotThrow(() => Frame(3));
    }

    [Test]
    public void AddAndRemoveJiggleColliders_BatchRoundTripThroughTheFrameLoop() {
        var batch = new List<JiggleColliderSerializable> {
            JiggleSceneFactory.SphereCollider(scene.Spawn("a"), 0.2f),
            JiggleSceneFactory.SphereCollider(scene.Spawn("b"), 0.3f),
        };
        Register("solo");

        JigglePhysics.AddJiggleColliders(batch);
        Frame(3);
        JigglePhysics.RemoveJiggleColliders(batch);

        Assert.DoesNotThrow(() => Frame(3));
    }

    /// <summary>
    /// Rigs get disabled during teardown, after the pipeline has already been torn down. The removal
    /// side of the API is null guarded for exactly that ordering.
    /// </summary>
    [Test]
    public void RemovalApis_AfterDispose_AreNullSafe() {
        var collider = JiggleSceneFactory.SphereCollider(scene.Spawn("world"));
        JigglePhysics.Dispose();

        Assert.DoesNotThrow(() => JigglePhysics.RemoveJiggleCollider(collider));
        Assert.DoesNotThrow(() => JigglePhysics.RemoveJiggleColliders(new List<JiggleColliderSerializable> { collider }));
        Assert.DoesNotThrow(() => JigglePhysics.ScheduleRemoveJiggleTree(null));
        Assert.DoesNotThrow(() => JigglePhysics.Teleport(null, default));
    }

    [Test]
    public void FreeOnCommitFlip_WithoutAPipeline_FreesImmediately() {
        JigglePhysics.Dispose();
        var scratch = (IntPtr)UnsafeUtility.Malloc(64, 16, Allocator.Persistent);

        Assert.DoesNotThrow(() => JigglePhysics.FreeOnCommitFlip(scratch));
    }

    /// <summary>
    /// Trees are disposed during teardown, sometimes after the pipeline has already gone. Both free
    /// paths have to cope with that rather than only the commit-flip one.
    /// </summary>
    [Test]
    public void FreeOnComplete_WithoutAPipeline_FreesImmediately() {
        JigglePhysics.Dispose();
        var scratch = (IntPtr)UnsafeUtility.Malloc(64, 16, Allocator.Persistent);

        Assert.DoesNotThrow(() => JigglePhysics.FreeOnComplete(scratch));
    }

    [Test]
    public void DisposingATreeAfterThePipelineIsGone_IsSafe() {
        var root = scene.Chain(3, 0.25f, "orphan");
        var tree = JigglePhysics.CreateJiggleTree(JiggleSceneFactory.Rig(root), null);
        tree.GetStruct();
        JigglePhysics.Dispose();

        Assert.DoesNotThrow(() => tree.Dispose());
    }

    // ----------------------------------------------------------------- render

    [Test]
    public void ScheduleRender_WithoutAPipeline_IsANoOp() {
        JigglePhysics.Dispose();

        Assert.DoesNotThrow(() => JigglePhysics.ScheduleRender());
        Assert.DoesNotThrow(() => JigglePhysics.CompleteRender(null, null));
    }

    /// <summary>
    /// The render prepass has to be primed before the first simulated step, because the chunk
    /// buffers are only allocated from the finish-simulate callback it subscribes to.
    /// </summary>
    [Test]
    public void ScheduleRender_ThenCompleteRender_RunsTheRenderPrepass() {
        Register("solo");
        JigglePhysics.ScheduleRender();
        Frame(5);

        JigglePhysics.ScheduleRender();

        Assert.DoesNotThrow(() => JigglePhysics.CompleteRender(null, null, null));
    }

    [Test]
    public void CompleteRender_WithoutMeshes_SkipsInstancing() {
        Register("solo");
        JigglePhysics.ScheduleRender();
        Frame(5);
        JigglePhysics.ScheduleRender();

        Assert.DoesNotThrow(() => JigglePhysics.CompleteRender(null, null));
    }

    [Test]
    public void OnDrawGizmos_OutsidePlayMode_IsANoOp() {
        Register("solo");
        Frame(3);

        Assert.DoesNotThrow(() => JigglePhysics.OnDrawGizmos());
    }

    // ------------------------------------------------------- startup settings

    /// <summary>
    /// Startup settings are baked into the job structs the first time the system simulates. Changing
    /// one afterwards silently would leave the setting and the running jobs disagreeing, so it is
    /// rejected out loud instead.
    /// </summary>
    [Test]
    public void StartupSettings_AreRejectedOnceTheSystemHasSimulated() {
        Register("solo");
        Frame(4);
        var before = JiggleSettings.BroadPhaseCellSize;
        LogAssert.Expect(LogType.Error,
            "JiggleSettings.BroadPhaseCellSize is a startup setting and was changed after the jiggle system began simulating. The change was ignored; set it before the first jiggle rig simulates.");

        JiggleSettings.BroadPhaseCellSize = before + 1f;

        Assert.AreEqual(before, JiggleSettings.BroadPhaseCellSize, 1e-6f);
    }

    [Test]
    public void LiveSettings_StillApplyAfterTheSystemHasSimulated() {
        Register("solo");
        Frame(4);

        JiggleSettings.CullingEnabled = false;

        Assert.IsFalse(JiggleSettings.CullingEnabled);
    }

    [Test]
    public void Dispose_ReopensTheStartupSettingLatch() {
        Register("solo");
        Frame(4);

        JigglePhysics.Dispose();
        JiggleSettings.MaxColliderCellSpan = 77;

        Assert.AreEqual(77, JiggleSettings.MaxColliderCellSpan);
    }

    // ----------------------------------------------------------- chain length

    [Test]
    public void VisitForLength_MeasuresTheWholeChain() {
        var root = scene.Chain(4, 0.5f);
        var rig = JiggleSceneFactory.Rig(root);

        JigglePhysics.VisitForLength(root, rig, root.position, 0f, out var totalLength);

        Assert.AreEqual(1.5f, totalLength, 1e-4f);
    }

    [Test]
    public void VisitForLength_ReportsTheLongestBranch() {
        var root = scene.Spawn("root");
        var longA = scene.Spawn("longA", root, new Vector3(0f, -0.5f, 0f));
        scene.Spawn("longB", longA, new Vector3(0f, -0.5f, 0f));
        scene.Spawn("short", root, new Vector3(0.25f, 0f, 0f));
        var rig = JiggleSceneFactory.Rig(root);

        JigglePhysics.VisitForLength(root, rig, root.position, 0f, out var totalLength);

        Assert.AreEqual(1f, totalLength, 1e-4f);
    }

    [Test]
    public void VisitForLength_StopsAtAnExcludedBone() {
        var root = scene.Chain(4, 0.5f);
        var bones = JiggleBoneScene.Descend(root, 4);
        var rig = JiggleSceneFactory.Rig(root, bones[2]);

        JigglePhysics.VisitForLength(root, rig, root.position, 0f, out var totalLength);

        Assert.AreEqual(0.5f, totalLength, 1e-4f);
    }

    [Test]
    public void VisitForLength_OfANullBone_ReturnsTheFloorValue() {
        var rig = JiggleSceneFactory.Rig(scene.Chain(2));

        JigglePhysics.VisitForLength(null, rig, Vector3.zero, 0f, out var totalLength);

        Assert.AreEqual(0.001f, totalLength, 1e-6f);
    }
}

/// <summary>
/// Covers the validation helpers on JiggleTreeCurvedFloat. They exist so the runtime path can skip
/// clamping entirely, which means a miss here shows up as out-of-range parameters in the jobs.
/// </summary>
[TestFixture]
internal class JiggleCurvedFloatTests {
    [Test]
    public void Ensure01_ClampsAboveOne() {
        var value = new JiggleTreeCurvedFloat(4f);

        value.Ensure01();

        Assert.AreEqual(1f, value.value, 1e-6f);
    }

    [Test]
    public void Ensure01_ClampsBelowZero() {
        var value = new JiggleTreeCurvedFloat(-4f);

        value.Ensure01();

        Assert.AreEqual(0f, value.value, 1e-6f);
    }

    [Test]
    public void Ensure01_LeavesAnInRangeValueAlone() {
        var value = new JiggleTreeCurvedFloat(0.5f);

        value.Ensure01();

        Assert.AreEqual(0.5f, value.value, 1e-6f);
    }

    [Test]
    public void EnsureNonNegative_ClampsANegativeValueToZero() {
        var value = new JiggleTreeCurvedFloat(-2f);

        value.EnsureNonNegative();

        Assert.AreEqual(0f, value.value, 1e-6f);
    }

    /// <summary>Collision radius is a distance, so it is allowed to exceed one.</summary>
    [Test]
    public void EnsureNonNegative_LeavesValuesAboveOneAlone() {
        var value = new JiggleTreeCurvedFloat(3f);

        value.EnsureNonNegative();

        Assert.AreEqual(3f, value.value, 1e-6f);
    }
}

}
