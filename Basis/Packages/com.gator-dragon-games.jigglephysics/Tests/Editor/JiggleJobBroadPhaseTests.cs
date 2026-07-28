using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace GatorDragonGames.JigglePhysics.Tests {

/// <summary>
/// Culling and AABB-to-cell classification, which the parallel cull job precomputes into entries
/// for the serial broad phase job to consume.
/// </summary>
[TestFixture]
internal class JiggleJobColliderCullTests {
    private const float InverseCellSize = 2f;

    private NativeArray<JiggleCollider> colliders;
    private NativeArray<JiggleColliderBroadPhaseEntry> entries;
    private NativeArray<JiggleCullingCamera> cameras;

    [SetUp]
    public void SetUp() {
        colliders = new NativeArray<JiggleCollider>(8, Allocator.Persistent);
        entries = new NativeArray<JiggleColliderBroadPhaseEntry>(8, Allocator.Persistent);
        cameras = new NativeArray<JiggleCullingCamera>(4, Allocator.Persistent);
    }

    [TearDown]
    public void TearDown() {
        if (colliders.IsCreated) colliders.Dispose();
        if (entries.IsCreated) entries.Dispose();
        if (cameras.IsCreated) cameras.Dispose();
    }

    private JiggleJobColliderCull BuildJob(int maxColliderCellSpan = 100, float inverseCellSize = InverseCellSize) {
        return new JiggleJobColliderCull {
            jiggleColliders = colliders,
            broadPhaseEntries = entries,
            cullingCameras = cameras,
            cullingCameraCount = 0,
            frustumCull = 0,
            distanceCull = 0,
            maxCollisionDistance = 0f,
            nearKeepRadius = 0f,
            frustumMargin = 0f,
            inverseCellSize = inverseCellSize,
            maxColliderCellSpan = maxColliderCellSpan,
        };
    }

    /// <summary>A camera at the origin looking down +Z with a wide axis-aligned box frustum.</summary>
    private static JiggleCullingCamera ForwardCamera() {
        return new JiggleCullingCamera {
            position = float3.zero,
            plane0 = new float4(1f, 0f, 0f, 50f),
            plane1 = new float4(-1f, 0f, 0f, 50f),
            plane2 = new float4(0f, 1f, 0f, 50f),
            plane3 = new float4(0f, -1f, 0f, 50f),
            plane4 = new float4(0f, 0f, 1f, 0f),
            plane5 = new float4(0f, 0f, -1f, 100f),
        };
    }

    private const int MinBatch = 64;

    private static int Batch(int colliderCount, int workerCount) {
        return JiggleJobColliderCull.GetBatchSize(colliderCount, workerCount, MinBatch);
    }

    [Test]
    public void BatchSize_CollapsesToASingleBatchBelowOneBatchPerWorker() {
        Assert.AreEqual(128, Batch(128, 31));
        Assert.AreEqual(512, Batch(512, 31));
        Assert.AreEqual(1983, Batch(1983, 31), "31 workers x 64 is the spread threshold");
    }

    [Test]
    public void BatchSize_SpreadsOnceThereIsAFullBatchPerWorker() {
        Assert.AreEqual(64, Batch(1984, 31));
    }

    /// <summary>
    /// The measured optimum on a 31 worker machine was 64 colliders per batch at 8192; the derived
    /// size should land on that without anyone tuning a constant.
    /// </summary>
    [Test]
    public void BatchSize_ReproducesTheMeasuredOptimum() {
        Assert.AreEqual(64, Batch(8192, 31));
    }

    /// <summary>
    /// Batch size stays put as the scene grows; it is the batch count that scales. Sizing batches as
    /// a share of the work measured 25% slower at 32768 colliders.
    /// </summary>
    [Test]
    public void BatchSize_StaysConstantAsWorkGrows() {
        Assert.AreEqual(64, Batch(8192, 31));
        Assert.AreEqual(64, Batch(32768, 31));
        Assert.AreEqual(64, Batch(1_000_000, 31));
    }

    /// <summary>
    /// The same build ships to a headset with a handful of workers, where both the spread threshold
    /// and the batch size have to come out far smaller than they do on a desktop.
    /// </summary>
    [Test]
    public void BatchSize_AdaptsToASmallWorkerPool() {
        Assert.AreEqual(128, Batch(128, 4), "below 4 x 64 it should stay a single batch");
        Assert.AreEqual(64, Batch(1024, 4));
        Assert.AreEqual(64, Batch(8192, 4));
    }

    /// <summary>Batch count grows with the workload, which is what gives the pool stealing headroom.</summary>
    [Test]
    public void BatchCount_GrowsWithTheWorkload() {
        Assert.AreEqual(128, 8192 / Batch(8192, 31));
        Assert.AreEqual(512, 32768 / Batch(32768, 31));
    }

    [Test]
    public void BatchSize_NeverDropsBelowTheMinimum() {
        Assert.GreaterOrEqual(Batch(4096, 31), MinBatch);
        Assert.GreaterOrEqual(Batch(1_000_000, 2), MinBatch);
    }

    [Test]
    public void BatchSize_HandlesDegenerateInputs() {
        Assert.GreaterOrEqual(JiggleJobColliderCull.GetBatchSize(0, 31, 64), 1);
        Assert.GreaterOrEqual(JiggleJobColliderCull.GetBatchSize(8192, 0, 64), 1);
        Assert.GreaterOrEqual(JiggleJobColliderCull.GetBatchSize(8192, 31, 0), 1);
    }

    [Test]
    public void BatchSize_NeverExceedsTheWorkAvailable() {
        foreach (var count in new[] { 1, 7, 63, 64, 255, 1984, 8192, 32768 }) {
            foreach (var workers in new[] { 1, 2, 4, 8, 31 }) {
                var batch = Batch(count, workers);
                Assert.That(batch, Is.InRange(1, count), $"count {count}, workers {workers}");
            }
        }
    }

    /// <summary>
    /// A single batch must classify every collider exactly as the spread schedule does; the batch
    /// count is a scheduling hint, never a behavioural switch.
    /// </summary>
    [Test]
    public void SingleBatch_ClassifiesIdenticallyToSpreadBatches() {
        var count = 6;
        colliders[0] = JiggleTestFactory.Sphere(new float3(1f, 0f, 1f), 0.1f);
        colliders[1] = JiggleTestFactory.Plane(float3.zero, new float3(0f, 1f, 0f));
        colliders[2] = JiggleTestFactory.Sphere(float3.zero, 50f);
        colliders[3] = JiggleTestFactory.Capsule(float3.zero, 0.1f, 4f, JiggleCollider.CapsuleAxis.X);
        colliders[4] = JiggleTestFactory.Sphere(float3.zero, 0.2f, enabled: false);
        colliders[5] = JiggleTestFactory.Sphere(new float3(-3f, 0f, 2f), 0.3f);

        var job = BuildJob();
        for (int i = 0; i < count; i++) {
            job.Execute(i);
        }
        var spread = new JiggleColliderBroadPhaseEntry[count];
        for (int i = 0; i < count; i++) {
            spread[i] = entries[i];
        }

        job.Schedule(count, JiggleJobColliderCull.GetBatchSize(count, 31, 64)).Complete();

        for (int i = 0; i < count; i++) {
            Assert.AreEqual(spread[i].state, entries[i].state, $"collider {i} state");
            Assert.AreEqual(spread[i].minCell, entries[i].minCell, $"collider {i} minCell");
            Assert.AreEqual(spread[i].maxCell, entries[i].maxCell, $"collider {i} maxCell");
        }
    }

    [Test]
    public void DisabledCollider_IsMarkedSkip() {
        colliders[0] = JiggleTestFactory.Sphere(float3.zero, 0.25f, enabled: false);

        BuildJob().Execute(0);

        Assert.AreEqual(JiggleColliderBroadPhaseEntry.StateSkip, entries[0].state);
    }

    [Test]
    public void Sphere_IsMarkedForTheGrid() {
        colliders[0] = JiggleTestFactory.Sphere(new float3(1f, 0f, 1f), 0.1f);

        BuildJob().Execute(0);

        Assert.AreEqual(JiggleColliderBroadPhaseEntry.StateGrid, entries[0].state);
        Assert.AreEqual(new int2(2, 2), entries[0].minCell);
        Assert.AreEqual(new int2(2, 2), entries[0].maxCell);
    }

    [Test]
    public void Sphere_CellRangeCoversItsRadius() {
        colliders[0] = JiggleTestFactory.Sphere(float3.zero, 1.2f);

        BuildJob().Execute(0);

        Assert.AreEqual(new int2(-2, -2), entries[0].minCell);
        Assert.AreEqual(new int2(2, 2), entries[0].maxCell);
    }

    [Test]
    public void Sphere_CellRangeScalesWithTheCellSize() {
        colliders[0] = JiggleTestFactory.Sphere(float3.zero, 1.2f);

        BuildJob(inverseCellSize: 1f).Execute(0);

        Assert.AreEqual(new int2(-1, -1), entries[0].minCell);
        Assert.AreEqual(new int2(1, 1), entries[0].maxCell);
    }

    [Test]
    public void Plane_IsAlwaysMarkedGlobal() {
        colliders[0] = JiggleTestFactory.Plane(float3.zero, new float3(0f, 1f, 0f));

        BuildJob().Execute(0);

        Assert.AreEqual(JiggleColliderBroadPhaseEntry.StateGlobal, entries[0].state);
    }

    [Test]
    public void OversizedCollider_IsMarkedGlobal() {
        colliders[0] = JiggleTestFactory.Sphere(float3.zero, 50f);

        BuildJob().Execute(0);

        Assert.AreEqual(JiggleColliderBroadPhaseEntry.StateGlobal, entries[0].state);
    }

    [Test]
    public void ColliderCellSpanLimit_DecidesGridVersusGlobal() {
        colliders[0] = JiggleTestFactory.Sphere(float3.zero, 1.2f);

        BuildJob(maxColliderCellSpan: 25).Execute(0);
        Assert.AreEqual(JiggleColliderBroadPhaseEntry.StateGrid, entries[0].state);

        BuildJob(maxColliderCellSpan: 24).Execute(0);
        Assert.AreEqual(JiggleColliderBroadPhaseEntry.StateGlobal, entries[0].state);
    }

    /// <summary>
    /// Absurd radii push the cell keys past what an int can hold, and the resulting span can come
    /// out negative. However the arithmetic lands, the entry must never ask the broad phase to walk
    /// an unbounded range of cells.
    /// </summary>
    [Test]
    public void ColliderSpansThatOverflowIntegerKeys_NeverAskForAGiantGridWalk() {
        colliders[0] = JiggleTestFactory.Sphere(new float3(1e9f, 0f, 0f), 2e9f);

        BuildJob(maxColliderCellSpan: 100).Execute(0);

        Assert.LessOrEqual(WalkedCells(entries[0]), 100L);
    }

    [Test]
    public void SymmetricOverflow_AlsoStaysBounded() {
        colliders[0] = JiggleTestFactory.Sphere(float3.zero, 3e9f);

        BuildJob(maxColliderCellSpan: 100).Execute(0);

        Assert.LessOrEqual(WalkedCells(entries[0]), 100L);
    }

    private static long WalkedCells(JiggleColliderBroadPhaseEntry entry) {
        if (entry.state != JiggleColliderBroadPhaseEntry.StateGrid) {
            return 0L;
        }
        return ((long)entry.maxCell.x - entry.minCell.x + 1) * ((long)entry.maxCell.y - entry.minCell.y + 1);
    }

    [Test]
    public void CapsuleAlongX_SpansCellsOnX() {
        colliders[0] = JiggleTestFactory.Capsule(float3.zero, 0.1f, 4f, JiggleCollider.CapsuleAxis.X);

        BuildJob().Execute(0);

        Assert.AreEqual(new int2(-4, 0), entries[0].minCell);
        Assert.AreEqual(new int2(4, 0), entries[0].maxCell);
    }

    [Test]
    public void CapsuleAlongZ_SpansCellsOnZ() {
        colliders[0] = JiggleTestFactory.Capsule(float3.zero, 0.1f, 4f, JiggleCollider.CapsuleAxis.Z);

        BuildJob().Execute(0);

        Assert.AreEqual(new int2(0, -4), entries[0].minCell);
        Assert.AreEqual(new int2(0, 4), entries[0].maxCell);
    }

    [Test]
    public void CapsuleAlongY_StaysInASingleColumn() {
        colliders[0] = JiggleTestFactory.Capsule(float3.zero, 0.1f, 8f, JiggleCollider.CapsuleAxis.Y);

        BuildJob().Execute(0);

        Assert.AreEqual(new int2(0, 0), entries[0].minCell);
        Assert.AreEqual(new int2(0, 0), entries[0].maxCell);
    }

    [Test]
    public void Culling_IsSkippedWithoutCameras() {
        colliders[0] = JiggleTestFactory.Sphere(new float3(1000f, 0f, 0f), 0.25f);
        var job = BuildJob();
        job.cullingCameraCount = 0;
        job.distanceCull = 1;
        job.maxCollisionDistance = 1f;

        job.Execute(0);

        Assert.AreEqual(JiggleColliderBroadPhaseEntry.StateGrid, entries[0].state);
    }

    [Test]
    public void Culling_IsSkippedWhenNeitherModeIsEnabled() {
        colliders[0] = JiggleTestFactory.Sphere(new float3(1000f, 0f, 0f), 0.25f);
        cameras[0] = ForwardCamera();
        var job = BuildJob();
        job.cullingCameraCount = 1;

        job.Execute(0);

        Assert.AreEqual(JiggleColliderBroadPhaseEntry.StateGrid, entries[0].state);
    }

    [Test]
    public void DistanceCulling_DropsCollidersBeyondTheRange() {
        colliders[0] = JiggleTestFactory.Sphere(new float3(100f, 0f, 0f), 0.25f);
        cameras[0] = new JiggleCullingCamera { position = float3.zero };
        var job = BuildJob();
        job.cullingCameraCount = 1;
        job.distanceCull = 1;
        job.maxCollisionDistance = 10f;
        job.nearKeepRadius = 2.5f;

        job.Execute(0);

        Assert.AreEqual(JiggleColliderBroadPhaseEntry.StateSkip, entries[0].state);
    }

    [Test]
    public void DistanceCulling_KeepsCollidersWithinTheRange() {
        colliders[0] = JiggleTestFactory.Sphere(new float3(5f, 0f, 0f), 0.25f);
        cameras[0] = new JiggleCullingCamera { position = float3.zero };
        var job = BuildJob();
        job.cullingCameraCount = 1;
        job.distanceCull = 1;
        job.maxCollisionDistance = 10f;
        job.nearKeepRadius = 2.5f;

        job.Execute(0);

        Assert.AreEqual(JiggleColliderBroadPhaseEntry.StateGrid, entries[0].state);
    }

    [Test]
    public void NearKeepRadius_OverridesCullingForCloseColliders() {
        colliders[0] = JiggleTestFactory.Sphere(new float3(1f, 0f, 0f), 0.25f);
        cameras[0] = new JiggleCullingCamera { position = float3.zero };
        var job = BuildJob();
        job.cullingCameraCount = 1;
        job.distanceCull = 1;
        job.frustumCull = 1;
        job.maxCollisionDistance = 0.1f;
        job.nearKeepRadius = 2.5f;

        job.Execute(0);

        Assert.AreEqual(JiggleColliderBroadPhaseEntry.StateGrid, entries[0].state);
    }

    [Test]
    public void Planes_AreNeverCulled() {
        colliders[0] = JiggleTestFactory.Plane(new float3(9999f, 0f, 0f), new float3(0f, 1f, 0f));
        cameras[0] = new JiggleCullingCamera { position = float3.zero };
        var job = BuildJob();
        job.cullingCameraCount = 1;
        job.distanceCull = 1;
        job.frustumCull = 1;
        job.maxCollisionDistance = 1f;
        job.nearKeepRadius = 1f;

        job.Execute(0);

        Assert.AreEqual(JiggleColliderBroadPhaseEntry.StateGlobal, entries[0].state);
    }

    [Test]
    public void FrustumCulling_KeepsCollidersInsideTheFrustum() {
        colliders[0] = JiggleTestFactory.Sphere(new float3(0f, 0f, 20f), 0.25f);
        cameras[0] = ForwardCamera();
        var job = BuildJob();
        job.cullingCameraCount = 1;
        job.frustumCull = 1;

        job.Execute(0);

        Assert.AreEqual(JiggleColliderBroadPhaseEntry.StateGrid, entries[0].state);
    }

    [Test]
    public void FrustumCulling_DropsCollidersBehindTheCamera() {
        colliders[0] = JiggleTestFactory.Sphere(new float3(0f, 0f, -20f), 0.25f);
        cameras[0] = ForwardCamera();
        var job = BuildJob();
        job.cullingCameraCount = 1;
        job.frustumCull = 1;

        job.Execute(0);

        Assert.AreEqual(JiggleColliderBroadPhaseEntry.StateSkip, entries[0].state);
    }

    [Test]
    public void FrustumMargin_WidensTheKeptRegion() {
        colliders[0] = JiggleTestFactory.Sphere(new float3(0f, 0f, -1f), 0.25f);
        cameras[0] = ForwardCamera();
        var job = BuildJob();
        job.cullingCameraCount = 1;
        job.frustumCull = 1;
        job.frustumMargin = 5f;

        job.Execute(0);

        Assert.AreEqual(JiggleColliderBroadPhaseEntry.StateGrid, entries[0].state);
    }

    [Test]
    public void AnyVisibleCamera_KeepsTheCollider() {
        colliders[0] = JiggleTestFactory.Sphere(new float3(0f, 0f, -20f), 0.25f);
        cameras[0] = ForwardCamera();
        cameras[1] = new JiggleCullingCamera {
            position = new float3(0f, 0f, -40f),
            plane0 = new float4(1f, 0f, 0f, 50f),
            plane1 = new float4(-1f, 0f, 0f, 50f),
            plane2 = new float4(0f, 1f, 0f, 50f),
            plane3 = new float4(0f, -1f, 0f, 50f),
            plane4 = new float4(0f, 0f, 1f, 40f),
            plane5 = new float4(0f, 0f, -1f, 100f),
        };
        var job = BuildJob();
        job.cullingCameraCount = 2;
        job.frustumCull = 1;

        job.Execute(0);

        Assert.AreEqual(JiggleColliderBroadPhaseEntry.StateGrid, entries[0].state);
    }
}

[TestFixture]
internal unsafe class JiggleJobBroadPhaseTests {
    private NativeHashMap<int2, JiggleGridCell> map;
    private NativeReference<JiggleGridCell> globalCell;
    private NativeArray<JiggleColliderBroadPhaseEntry> entries;

    [SetUp]
    public void SetUp() {
        map = new NativeHashMap<int2, JiggleGridCell>(64, Allocator.Persistent);
        globalCell = new NativeReference<JiggleGridCell>(new JiggleGridCell(JiggleJobBroadPhase.MAX_COLLIDERS), Allocator.Persistent);
        entries = new NativeArray<JiggleColliderBroadPhaseEntry>(256, Allocator.Persistent);
    }

    [TearDown]
    public void TearDown() {
        if (map.IsCreated) {
            var keys = map.GetKeyArray(Allocator.Temp);
            for (int i = 0; i < keys.Length; i++) {
                map[keys[i]].Dispose();
            }
            keys.Dispose();
            map.Dispose();
        }
        if (globalCell.IsCreated) {
            globalCell.Value.Dispose();
            globalCell.Dispose();
        }
        if (entries.IsCreated) entries.Dispose();
    }

    private JiggleJobBroadPhase BuildJob(int colliderCount) {
        return new JiggleJobBroadPhase {
            broadPhaseMap = map,
            globalCell = globalCell,
            broadPhaseEntries = entries,
            jiggleColliderCount = colliderCount,
        };
    }

    private static JiggleColliderBroadPhaseEntry Grid(int2 min, int2 max) {
        return new JiggleColliderBroadPhaseEntry {
            minCell = min, maxCell = max, state = JiggleColliderBroadPhaseEntry.StateGrid,
        };
    }

    private List<int> CellContents(int2 key) {
        var result = new List<int>();
        if (!map.TryGetValue(key, out var cell)) {
            return result;
        }
        for (int i = 0; i < cell.count; i++) {
            result.Add(cell.colliderIndices[i]);
        }
        return result;
    }

    private List<int> GlobalContents() {
        var result = new List<int>();
        var cell = globalCell.Value;
        for (int i = 0; i < cell.count; i++) {
            result.Add(cell.colliderIndices[i]);
        }
        return result;
    }

    [Test]
    public void SkipEntries_AreNotInserted() {
        entries[0] = default;

        BuildJob(1).Execute();

        Assert.AreEqual(0, map.Count);
        CollectionAssert.IsEmpty(GlobalContents());
    }

    [Test]
    public void GlobalEntries_LandInTheGlobalCell() {
        entries[0] = new JiggleColliderBroadPhaseEntry { state = JiggleColliderBroadPhaseEntry.StateGlobal };

        BuildJob(1).Execute();

        CollectionAssert.Contains(GlobalContents(), 0);
        Assert.AreEqual(0, map.Count);
    }

    [Test]
    public void GridEntries_PopulateASingleCell() {
        entries[0] = Grid(new int2(2, 3), new int2(2, 3));

        BuildJob(1).Execute();

        CollectionAssert.Contains(CellContents(new int2(2, 3)), 0);
    }

    [Test]
    public void GridEntries_PopulateEveryCellInTheirRange() {
        entries[0] = Grid(new int2(-1, -1), new int2(1, 1));

        BuildJob(1).Execute();

        Assert.AreEqual(9, map.Count);
        CollectionAssert.Contains(CellContents(new int2(-1, -1)), 0);
        CollectionAssert.Contains(CellContents(new int2(0, 0)), 0);
        CollectionAssert.Contains(CellContents(new int2(1, 1)), 0);
    }

    [Test]
    public void EntriesBeyondTheColliderCount_AreIgnored() {
        entries[0] = Grid(new int2(0, 0), new int2(0, 0));
        entries[1] = Grid(new int2(5, 5), new int2(5, 5));

        BuildJob(1).Execute();

        CollectionAssert.IsEmpty(CellContents(new int2(5, 5)));
    }

    [Test]
    public void MultipleColliders_ShareACell() {
        entries[0] = Grid(new int2(1, 1), new int2(1, 1));
        entries[1] = Grid(new int2(1, 1), new int2(1, 1));

        BuildJob(2).Execute();

        var contents = CellContents(new int2(1, 1));
        CollectionAssert.Contains(contents, 0);
        CollectionAssert.Contains(contents, 1);
    }

    [Test]
    public void Insertion_ResetsCellStaleness() {
        var stale = new JiggleGridCell(JiggleJobBroadPhase.MAX_COLLIDERS) { staleness = 3 };
        map.Add(new int2(0, 0), stale);
        entries[0] = Grid(new int2(0, 0), new int2(0, 0));

        BuildJob(1).Execute();

        Assert.AreEqual(0, map[new int2(0, 0)].staleness);
    }

    [Test]
    public void CellOccupancy_SaturatesAtTheColliderLimit() {
        var count = JiggleJobBroadPhase.MAX_COLLIDERS + 20;
        for (int i = 0; i < count; i++) {
            entries[i] = Grid(new int2(0, 0), new int2(0, 0));
        }

        Assert.DoesNotThrow(() => BuildJob(count).Execute());
        Assert.LessOrEqual(map[new int2(0, 0)].count, JiggleJobBroadPhase.MAX_COLLIDERS);
    }

    [Test]
    public void GlobalOccupancy_SaturatesAtTheColliderLimit() {
        var count = JiggleJobBroadPhase.MAX_COLLIDERS + 20;
        for (int i = 0; i < count; i++) {
            entries[i] = new JiggleColliderBroadPhaseEntry { state = JiggleColliderBroadPhaseEntry.StateGlobal };
        }

        Assert.DoesNotThrow(() => BuildJob(count).Execute());
        Assert.LessOrEqual(globalCell.Value.count, JiggleJobBroadPhase.MAX_COLLIDERS);
    }

    [Test]
    public void CullThenBroadPhase_PlacesASphereInTheCellsCoveringIt() {
        var colliders = new NativeArray<JiggleCollider>(1, Allocator.Persistent);
        var cameras = new NativeArray<JiggleCullingCamera>(1, Allocator.Persistent);
        colliders[0] = JiggleTestFactory.Sphere(new float3(1f, 0f, 1f), 0.1f);

        new JiggleJobColliderCull {
            jiggleColliders = colliders, broadPhaseEntries = entries, cullingCameras = cameras,
            inverseCellSize = 2f, maxColliderCellSpan = 100,
        }.Execute(0);
        BuildJob(1).Execute();

        CollectionAssert.Contains(CellContents(new int2(2, 2)), 0);

        colliders.Dispose();
        cameras.Dispose();
    }
}

[TestFixture]
internal unsafe class JiggleJobBroadPhaseClearTests {
    private NativeHashMap<int2, JiggleGridCell> map;
    private NativeReference<JiggleGridCell> globalCell;

    [SetUp]
    public void SetUp() {
        map = new NativeHashMap<int2, JiggleGridCell>(64, Allocator.Persistent);
        globalCell = new NativeReference<JiggleGridCell>(new JiggleGridCell(JiggleJobBroadPhase.MAX_COLLIDERS), Allocator.Persistent);
    }

    [TearDown]
    public void TearDown() {
        if (map.IsCreated) {
            var keys = map.GetKeyArray(Allocator.Temp);
            for (int i = 0; i < keys.Length; i++) {
                map[keys[i]].Dispose();
            }
            keys.Dispose();
            map.Dispose();
        }
        if (globalCell.IsCreated) {
            globalCell.Value.Dispose();
            globalCell.Dispose();
        }
    }

    private JiggleJobBroadPhaseClear BuildJob(int maxStalenessFrames = 3) {
        return new JiggleJobBroadPhaseClear {
            broadPhaseMap = map, globalCell = globalCell, maxStalenessFrames = maxStalenessFrames,
        };
    }

    private void Seed(int2 key, int count, int staleness) {
        map.Add(key, new JiggleGridCell(JiggleJobBroadPhase.MAX_COLLIDERS) { count = count, staleness = staleness });
    }

    [Test]
    public void Clear_ResetsCellCounts() {
        Seed(new int2(1, 1), 5, 0);

        BuildJob().Execute();

        Assert.AreEqual(0, map[new int2(1, 1)].count);
    }

    [Test]
    public void Clear_IncrementsStaleness() {
        Seed(new int2(1, 1), 5, 0);

        BuildJob().Execute();

        Assert.AreEqual(1, map[new int2(1, 1)].staleness);
    }

    [Test]
    public void Clear_EvictsCellsPastTheStalenessLimit() {
        Seed(new int2(2, 2), 3, 3);

        BuildJob(maxStalenessFrames: 3).Execute();

        Assert.IsFalse(map.ContainsKey(new int2(2, 2)));
    }

    [Test]
    public void Clear_KeepsCellsWithinTheStalenessLimit() {
        Seed(new int2(3, 3), 3, 2);

        BuildJob(maxStalenessFrames: 3).Execute();

        Assert.IsTrue(map.ContainsKey(new int2(3, 3)));
    }

    [Test]
    public void StalenessLimit_IsConfigurable() {
        Seed(new int2(4, 4), 1, 0);

        BuildJob(maxStalenessFrames: 0).Execute();

        Assert.IsFalse(map.ContainsKey(new int2(4, 4)));
    }

    [Test]
    public void Clear_EvictsAfterTheConfiguredNumberOfIdleFrames() {
        Seed(new int2(5, 5), 1, 0);
        var job = BuildJob(maxStalenessFrames: 3);

        for (int i = 0; i < 4; i++) {
            job.Execute();
        }

        Assert.IsFalse(map.ContainsKey(new int2(5, 5)));
    }

    [Test]
    public void Clear_ResetsTheGlobalCellCount() {
        var cell = globalCell.Value;
        cell.count = 7;
        globalCell.Value = cell;

        BuildJob().Execute();

        Assert.AreEqual(0, globalCell.Value.count);
    }

    [Test]
    public void Clear_OnAnEmptyMapDoesNothing() {
        Assert.DoesNotThrow(() => BuildJob().Execute());
        Assert.AreEqual(0, map.Count);
    }
}

[TestFixture]
internal class JiggleSettingsTests {
    private float cellSize;
    private int colliderSpan;
    private int treeSpan;
    private int staleness;
    private int minBatch;
    private float nearKeep;
    private float frustumMargin;
    private float frustumExpansion;
    private bool cullingEnabled;

    [SetUp]
    public void SetUp() {
        cellSize = JiggleSettings.BroadPhaseCellSize;
        colliderSpan = JiggleSettings.MaxColliderCellSpan;
        treeSpan = JiggleSettings.MaxTreeCellSpan;
        staleness = JiggleSettings.CellStalenessFrames;
        minBatch = JiggleSettings.ColliderCullMinBatch;
        nearKeep = JiggleSettings.CullNearKeepRadius;
        frustumMargin = JiggleSettings.CullFrustumMargin;
        frustumExpansion = JiggleSettings.CullFrustumExpansion;
        cullingEnabled = JiggleSettings.CullingEnabled;
    }

    [TearDown]
    public void TearDown() {
        JiggleSettings.BroadPhaseCellSize = cellSize;
        JiggleSettings.MaxColliderCellSpan = colliderSpan;
        JiggleSettings.MaxTreeCellSpan = treeSpan;
        JiggleSettings.CellStalenessFrames = staleness;
        JiggleSettings.ColliderCullMinBatch = minBatch;
        JiggleSettings.CullNearKeepRadius = nearKeep;
        JiggleSettings.CullFrustumMargin = frustumMargin;
        JiggleSettings.CullFrustumExpansion = frustumExpansion;
        JiggleSettings.CullingEnabled = cullingEnabled;
    }

    [Test]
    public void BroadPhaseCellSize_UpdatesItsReciprocal() {
        JiggleSettings.BroadPhaseCellSize = 0.25f;

        Assert.AreEqual(0.25f, JiggleSettings.BroadPhaseCellSize, 1e-6f);
        Assert.AreEqual(4f, JiggleSettings.InverseBroadPhaseCellSize, 1e-6f);
    }

    [Test]
    public void BroadPhaseCellSize_IsClampedAwayFromZero() {
        JiggleSettings.BroadPhaseCellSize = 0f;

        Assert.AreEqual(0.01f, JiggleSettings.BroadPhaseCellSize, 1e-6f);
        Assert.IsTrue(math.isfinite(JiggleSettings.InverseBroadPhaseCellSize));
    }

    [Test]
    public void BroadPhaseCellSize_RejectsNegativeValues() {
        JiggleSettings.BroadPhaseCellSize = -5f;

        Assert.AreEqual(0.01f, JiggleSettings.BroadPhaseCellSize, 1e-6f);
    }

    [Test]
    public void MaxColliderCellSpan_IsAtLeastOne() {
        JiggleSettings.MaxColliderCellSpan = -10;

        Assert.AreEqual(1, JiggleSettings.MaxColliderCellSpan);
    }

    [Test]
    public void MaxTreeCellSpan_IsAtLeastOne() {
        JiggleSettings.MaxTreeCellSpan = 0;

        Assert.AreEqual(1, JiggleSettings.MaxTreeCellSpan);
    }

    [Test]
    public void CellStalenessFrames_IsAtLeastOne() {
        JiggleSettings.CellStalenessFrames = -4;

        Assert.AreEqual(1, JiggleSettings.CellStalenessFrames);
    }

    [Test]
    public void ColliderCullMinBatch_IsAtLeastOne() {
        JiggleSettings.ColliderCullMinBatch = 0;

        Assert.AreEqual(1, JiggleSettings.ColliderCullMinBatch);
    }

    [Test]
    public void ColliderCullMinBatch_RoundTrips() {
        JiggleSettings.ColliderCullMinBatch = 128;

        Assert.AreEqual(128, JiggleSettings.ColliderCullMinBatch);
    }

    [Test]
    public void CullNearKeepRadius_IsNonNegative() {
        JiggleSettings.CullNearKeepRadius = -3f;

        Assert.AreEqual(0f, JiggleSettings.CullNearKeepRadius, 1e-6f);
    }

    [Test]
    public void CullFrustumMargin_IsNonNegative() {
        JiggleSettings.CullFrustumMargin = -1f;

        Assert.AreEqual(0f, JiggleSettings.CullFrustumMargin, 1e-6f);
    }

    [Test]
    public void CullFrustumExpansion_NeverShrinksTheFrustum() {
        JiggleSettings.CullFrustumExpansion = 0.5f;

        Assert.AreEqual(1f, JiggleSettings.CullFrustumExpansion, 1e-6f);
    }

    [Test]
    public void CullingEnabled_RoundTrips() {
        JiggleSettings.CullingEnabled = false;
        Assert.IsFalse(JiggleSettings.CullingEnabled);

        JiggleSettings.CullingEnabled = true;
        Assert.IsTrue(JiggleSettings.CullingEnabled);
    }
}

}
