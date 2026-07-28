using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine.Jobs;
using Debug = UnityEngine.Debug;
using Random = Unity.Mathematics.Random;

namespace GatorDragonGames.JigglePhysics.Tests {

/// <summary>
/// Timing harness for the baked broad phase and scheduling constants, so their defaults can be set
/// from data instead of taste.
///
/// Cell size is the interesting one because it pulls in two directions at once. The simulate job
/// walks every cell in a tree's extent for every point in that tree, so shrinking cells multiplies
/// the hash lookups per point while growing them multiplies the colliders tested per lookup. Only
/// measuring both stages together shows where the curve actually bottoms out.
///
/// Cell size is passed to the jobs explicitly rather than through JiggleSettings, both to keep the
/// sweep independent of the startup latch and so a failed run cannot leave a global mutated.
///
/// Editor timings on whatever machine ran them. Relative, not absolute.
/// </summary>
[TestFixture]
[Category("Performance")]
[Explicit("Timing benchmark. Run it deliberately, it is too slow for the normal suite.")]
internal unsafe class JiggleSettingsPerformanceSimulation {
    private const int WarmupIterations = 5;
    private const int MeasuredIterations = 25;

    private bool previousSynchronousCompilation;

    /// <summary>
    /// Burst compiles asynchronously in the editor, so a job runs the slow managed path until its
    /// compiled version is ready. For a job as large as simulate that window is long enough to span
    /// several rows of a sweep, which reads as a 10x cliff partway down the table and looks exactly
    /// like a real effect of whatever is being swept. Forcing synchronous compilation makes the
    /// warmup actually warm.
    /// </summary>
    [OneTimeSetUp]
    public void EnableSynchronousBurst() {
        previousSynchronousCompilation = BurstCompiler.Options.EnableBurstCompileSynchronously;
        BurstCompiler.Options.EnableBurstCompileSynchronously = true;
    }

    [OneTimeTearDown]
    public void RestoreBurstCompilation() {
        BurstCompiler.Options.EnableBurstCompileSynchronously = previousSynchronousCompilation;
    }

    private const int CollidersPerAvatar = 38;
    private const int PointsPerTree = 8;
    private const float TreeSpacing = 0.08f;
    private const float CollisionRadius = 0.03f;

    /// <summary>
    /// A crowd of avatar shaped colliders with one jiggle chain hanging off each avatar, so the
    /// trees actually sit inside the collider field they are querying.
    /// </summary>
    private sealed class Crowd : IDisposable {
        public NativeArray<JiggleCollider> colliders;
        public NativeArray<JiggleColliderBroadPhaseEntry> entries;
        public NativeArray<JiggleCullingCamera> cameras;
        public NativeArray<JiggleTreeJobData> trees;
        public NativeArray<JiggleTransform> inputPoses;
        public NativeArray<PoseData> outputPoses;
        public NativeArray<JiggleCollider> personalColliders;
        public NativeHashMap<int2, JiggleGridCell> broadPhaseMap;
        public NativeReference<JiggleGridCell> globalCell;

        public int colliderCount;
        public int treeCount;

        private readonly List<IntPtr> allocations = new List<IntPtr>();
        private JiggleSimulatedPoint[][] treeSnapshots;
        private float3[] colliderOrigins;

        /// <summary>
        /// The simulate job integrates and depenetrates in place, so every run leaves the trees in a
        /// different state than it found them. Without restoring between timed runs the later rows of
        /// a sweep measure drifted trees rather than the setting being swept, which is exactly how the
        /// first version of this file produced an 11x discrepancy between two measurements of the
        /// same configuration.
        /// </summary>
        public void ResetTrees() {
            for (int t = 0; t < treeCount; t++) {
                var tree = trees[t];
                var snapshot = treeSnapshots[t];
                fixed (JiggleSimulatedPoint* source = snapshot) {
                    UnsafeUtility.MemCpy(tree.points, source, sizeof(JiggleSimulatedPoint) * tree.pointCount);
                }
            }
        }

        /// <summary>
        /// treeSpread tilts each chain away from straight down, which is the whole point of the
        /// extent sweep: the simulate job walks every cell in a tree's xz extent for every point, so
        /// a chain that hangs vertically touches one cell whatever the cell size, while one that
        /// swings out touches (extent / cellSize)^2 of them.
        /// </summary>
        public static Crowd Build(int avatarCount, float worldRadius = 30f, uint seed = 0x5F3759DF,
            float treeSpread = 0f, float spacing = TreeSpacing) {
            var colliderCount = avatarCount * CollidersPerAvatar;
            var pointsPerTree = PointsPerTree + 2;
            var crowd = new Crowd {
                colliderCount = colliderCount,
                treeCount = avatarCount,
                colliders = new NativeArray<JiggleCollider>(colliderCount, Allocator.Persistent),
                entries = new NativeArray<JiggleColliderBroadPhaseEntry>(colliderCount, Allocator.Persistent),
                cameras = new NativeArray<JiggleCullingCamera>(1, Allocator.Persistent),
                trees = new NativeArray<JiggleTreeJobData>(avatarCount, Allocator.Persistent),
                inputPoses = new NativeArray<JiggleTransform>(avatarCount * pointsPerTree, Allocator.Persistent),
                outputPoses = new NativeArray<PoseData>(avatarCount * pointsPerTree, Allocator.Persistent),
                personalColliders = new NativeArray<JiggleCollider>(1, Allocator.Persistent),
                broadPhaseMap = new NativeHashMap<int2, JiggleGridCell>(4096, Allocator.Persistent),
                globalCell = new NativeReference<JiggleGridCell>(
                    new JiggleGridCell(JiggleJobBroadPhase.MAX_COLLIDERS), Allocator.Persistent),
            };

            crowd.treeSnapshots = new JiggleSimulatedPoint[avatarCount][];
            crowd.colliderOrigins = new float3[colliderCount];

            var random = new Random(seed);
            var colliderIndex = 0;
            var parameters = JiggleTestFactory.Params(collisionRadius: CollisionRadius);

            for (int a = 0; a < avatarCount; a++) {
                var origin = new float3(
                    random.NextFloat(-worldRadius, worldRadius), 0f, random.NextFloat(-worldRadius, worldRadius));

                for (int f = 0; f < 30 && colliderIndex < colliderCount; f++) {
                    var jitter = random.NextFloat3(new float3(-0.35f, -0.2f, -0.35f), new float3(0.35f, 0.2f, 0.35f));
                    crowd.colliders[colliderIndex++] =
                        JiggleTestFactory.Sphere(origin + new float3(0f, 1.1f, 0f) + jitter, 0.012f);
                }
                for (int c = 0; c < 6 && colliderIndex < colliderCount; c++) {
                    var jitter = random.NextFloat3(new float3(-0.25f, 0f, -0.25f), new float3(0.25f, 1.4f, 0.25f));
                    crowd.colliders[colliderIndex++] =
                        JiggleTestFactory.Capsule(origin + jitter, 0.06f, 0.32f, (JiggleCollider.CapsuleAxis)(c % 3));
                }
                for (int t = 0; t < 2 && colliderIndex < colliderCount; t++) {
                    var jitter = random.NextFloat3(new float3(-0.2f, 0f, -0.2f), new float3(0.2f, 0.1f, 0.2f));
                    crowd.colliders[colliderIndex++] = JiggleTestFactory.Sphere(origin + jitter, 0.09f);
                }

                var treeStart = origin + new float3(0f, 1.5f, 0f);
                var direction = math.normalize(new float3(treeSpread, -1f, 0f));
                var tree = JiggleTestTree.Chain(PointsPerTree, treeStart, direction, spacing, parameters);
                var offset = a * pointsPerTree;
                var jobData = new JiggleTreeJobData(a, offset, 0, 0, tree.points, tree.parameters);
                crowd.allocations.Add((IntPtr)jobData.points);
                crowd.allocations.Add((IntPtr)jobData.parameters);
                crowd.trees[a] = jobData;
                crowd.treeSnapshots[a] = tree.points;

                for (int i = 0; i < pointsPerTree; i++) {
                    crowd.inputPoses[offset + i] = tree.inputPoses[i];
                    crowd.outputPoses[offset + i] = new PoseData {
                        pose = new JiggleTransform {
                            isVirtual = !tree.points[i].hasTransform,
                            position = tree.points[i].pose,
                            rotation = quaternion.identity,
                            scale = new float3(1f),
                        },
                    };
                }
            }

            for (int i = 0; i < colliderCount; i++) {
                crowd.colliderOrigins[i] = crowd.colliders[i].localToWorldMatrix.c3.xyz;
            }

            return crowd;
        }

        public JiggleJobColliderCull CullJob(float inverseCellSize, int maxColliderCellSpan) {
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

        public JiggleJobBroadPhaseClear ClearJob() {
            return new JiggleJobBroadPhaseClear {
                broadPhaseMap = broadPhaseMap,
                globalCell = globalCell,
                maxStalenessFrames = JiggleSettings.CellStalenessFrames,
            };
        }

        public JiggleJobBroadPhase BroadPhaseJob() {
            return new JiggleJobBroadPhase {
                broadPhaseMap = broadPhaseMap,
                globalCell = globalCell,
                broadPhaseEntries = entries,
                jiggleColliderCount = colliderCount,
            };
        }

        public JiggleJobSimulate SimulateJob(float inverseCellSize, int maxTreeCellSpan) {
            var job = new JiggleJobSimulate();
            job.SetFixedDeltaTime(0.02f);
            job.inverseCellSize = inverseCellSize;
            job.maxTreeCellSpan = maxTreeCellSpan;
            job.substeps = 1;
            job.gravity = float3.zero;
            job.timeStamp = 0.0;
            job.sceneColliderCount = colliderCount;
            job.inputPoses = inputPoses;
            job.outputPoses = outputPoses;
            job.jiggleTrees = trees;
            job.personalColliders = personalColliders;
            job.sceneColliders = colliders;
            job.broadPhaseMap = broadPhaseMap;
            job.globalCell = globalCell;
            return job;
        }

        /// <summary>
        /// Frees every grid cell and empties the map. Cell keys are a function of cell size, so a
        /// sweep has to start from an empty grid or stale keys from the previous size linger and
        /// corrupt the occupancy numbers.
        /// </summary>
        public void ResetGrid() {
            var keys = broadPhaseMap.GetKeyArray(Allocator.Temp);
            for (int i = 0; i < keys.Length; i++) {
                broadPhaseMap[keys[i]].Dispose();
            }
            keys.Dispose();
            broadPhaseMap.Clear();

            var cell = globalCell.Value;
            cell.count = 0;
            globalCell.Value = cell;
        }

        /// <summary>
        /// Sweeps every collider around a circle so cells are continually vacated and re-entered,
        /// which is the only condition under which cell staleness does anything.
        /// </summary>
        public void OrbitColliders(float phase) {
            for (int i = 0; i < colliderCount; i++) {
                var collider = colliders[i];
                var basePosition = colliderOrigins[i];
                var angle = phase + i * 0.017f;
                var offset = new float3(math.cos(angle), 0f, math.sin(angle)) * 0.6f;
                collider.Read(float4x4.Translate(basePosition + offset));
                colliders[i] = collider;
            }
        }

        public void PopulateGrid(float inverseCellSize, int maxColliderCellSpan) {
            ResetGrid();
            CullJob(inverseCellSize, maxColliderCellSpan).Run(colliderCount);
            BroadPhaseJob().Run();
        }

        public GridStats MeasureGrid() {
            var stats = new GridStats { cellCount = broadPhaseMap.Count };
            var values = broadPhaseMap.GetValueArray(Allocator.Temp);
            long total = 0;
            for (int i = 0; i < values.Length; i++) {
                var count = values[i].count;
                total += count;
                stats.maxOccupancy = math.max(stats.maxOccupancy, count);
                if (count >= JiggleJobBroadPhase.MAX_COLLIDERS - 1) {
                    stats.saturatedCells++;
                }
            }
            values.Dispose();
            stats.totalInsertions = total;
            stats.meanOccupancy = stats.cellCount > 0 ? total / (double)stats.cellCount : 0.0;
            stats.globalCount = globalCell.Value.count;
            return stats;
        }

        public void Dispose() {
            ResetGrid();
            if (broadPhaseMap.IsCreated) broadPhaseMap.Dispose();
            if (globalCell.IsCreated) {
                globalCell.Value.Dispose();
                globalCell.Dispose();
            }
            if (colliders.IsCreated) colliders.Dispose();
            if (entries.IsCreated) entries.Dispose();
            if (cameras.IsCreated) cameras.Dispose();
            if (trees.IsCreated) trees.Dispose();
            if (inputPoses.IsCreated) inputPoses.Dispose();
            if (outputPoses.IsCreated) outputPoses.Dispose();
            if (personalColliders.IsCreated) personalColliders.Dispose();
            foreach (var pointer in allocations) {
                UnsafeUtility.Free((void*)pointer, Allocator.Persistent);
            }
            allocations.Clear();
        }
    }

    private struct GridStats {
        public int cellCount;
        public int maxOccupancy;
        public int saturatedCells;
        public long totalInsertions;
        public double meanOccupancy;
        public int globalCount;
    }

    /// <summary>
    /// setup runs before every timed iteration and is excluded from the timing, so a measurement
    /// whose subject mutates state can start each sample from the same place.
    /// </summary>
    private static double MedianMilliseconds(Action action, Action setup = null) {
        for (int i = 0; i < WarmupIterations; i++) {
            setup?.Invoke();
            action();
        }
        var samples = new double[MeasuredIterations];
        var stopwatch = new Stopwatch();
        for (int i = 0; i < MeasuredIterations; i++) {
            setup?.Invoke();
            stopwatch.Restart();
            action();
            stopwatch.Stop();
            samples[i] = stopwatch.Elapsed.TotalMilliseconds;
        }
        Array.Sort(samples);
        return samples[MeasuredIterations / 2];
    }

    private static void Report(StringBuilder report) {
        Debug.Log(report.ToString());
        TestContext.Out.WriteLine(report.ToString());
    }

    private static readonly float[] CellSizes = { 0.125f, 0.25f, 0.5f, 1f, 2f, 4f };

    /// <summary>
    /// The headline sweep. Insert cost and simulate cost move in opposite directions as cell size
    /// changes, so only the total column decides the default.
    /// </summary>
    [Test]
    public void BroadPhaseCellSize_Sweep() {
        const int avatars = 60;
        var report = new StringBuilder();
        report.AppendLine("=== broad phase cell size sweep ===");
        report.AppendLine($"{avatars} avatars, {avatars * CollidersPerAvatar} colliders, {avatars} trees x {PointsPerTree} points, workers: {JobsUtility.JobWorkerCount}");
        report.AppendLine("cell m | insert ms | simulate ms | total ms |  cells | mean occ | max occ | inserts");
        report.AppendLine("-------+-----------+-------------+----------+--------+----------+---------+--------");

        foreach (var cellSize in CellSizes) {
            using var crowd = Crowd.Build(avatars);
            var inverse = 1f / cellSize;
            crowd.PopulateGrid(inverse, JiggleSettings.MaxColliderCellSpan);
            var stats = crowd.MeasureGrid();

            var clear = crowd.ClearJob();
            var cull = crowd.CullJob(inverse, JiggleSettings.MaxColliderCellSpan);
            var broadPhase = crowd.BroadPhaseJob();
            var insert = MedianMilliseconds(() => {
                clear.Run();
                cull.Run(crowd.colliderCount);
                broadPhase.Run();
            });

            var simulate = crowd.SimulateJob(inverse, JiggleSettings.MaxTreeCellSpan);
            var simulateMs = MedianMilliseconds(() => simulate.Run(crowd.treeCount), crowd.ResetTrees);

            report.AppendLine(
                $"{cellSize,6:F3} | {insert,9:F4} | {simulateMs,11:F4} | {insert + simulateMs,8:F4} | {stats.cellCount,6} | {stats.meanOccupancy,8:F2} | {stats.maxOccupancy,7} | {stats.totalInsertions,7}");
        }
        Report(report);
    }

    /// <summary>
    /// The deciding experiment for the cell size default. The cost model says the optimum scales
    /// with tree extent, because the per point cell walk is (extent / cellSize)^2 while the colliders
    /// tested per cell is density * cellSize^2. A chain that hangs straight down has almost no xz
    /// extent and so wants the smallest cell it can get; one that swings out wants a bigger cell.
    /// A single baked default has to serve both, so this prints where the optimum sits for each.
    /// </summary>
    [Test]
    public void CellSize_ByTreeExtent() {
        const int avatars = 60;
        var report = new StringBuilder();
        report.AppendLine("=== cell size optimum vs tree extent ===");
        report.AppendLine($"{avatars} avatars, {avatars * CollidersPerAvatar} colliders, chain length {(PointsPerTree - 1) * TreeSpacing:F2} m");
        report.AppendLine("spread | spacing | xz extent | best cell | total at best |  at 0.125 |   at 0.25 |    at 0.5 |      at 1 |      at 2 |      at 4");
        report.AppendLine("-------+---------+-----------+-----------+---------------+-----------+-----------+-----------+-----------+-----------+----------");

        // Spacing widens the chain past what spread alone can reach: 8 points at 0.08m span barely
        // half a metre however far they tilt, which is the compact regime that flatters small cells.
        // Long hair, tails and skirts live in the 1-3m rows and are the case that could want bigger.
        var cases = new[] {
            new float2(0f, 0.08f), new float2(1f, 0.08f), new float2(4f, 0.08f),
            new float2(1f, 0.25f), new float2(4f, 0.25f),
            new float2(1f, 0.50f), new float2(4f, 0.50f),
        };

        foreach (var testCase in cases) {
            var spread = testCase.x;
            var spacing = testCase.y;
            var direction = math.normalize(new float3(spread, -1f, 0f));
            var extent = (PointsPerTree - 1) * spacing * math.length(direction.xz);
            var totals = new double[CellSizes.Length];

            for (int i = 0; i < CellSizes.Length; i++) {
                var cellSize = CellSizes[i];
                using var crowd = Crowd.Build(avatars, treeSpread: spread, spacing: spacing);
                var inverse = 1f / cellSize;
                crowd.PopulateGrid(inverse, JiggleSettings.MaxColliderCellSpan);

                var clear = crowd.ClearJob();
                var cull = crowd.CullJob(inverse, JiggleSettings.MaxColliderCellSpan);
                var broadPhase = crowd.BroadPhaseJob();
                var insert = MedianMilliseconds(() => {
                    clear.Run();
                    cull.Run(crowd.colliderCount);
                    broadPhase.Run();
                });
                var simulate = crowd.SimulateJob(inverse, JiggleSettings.MaxTreeCellSpan);
                var simulateMs = MedianMilliseconds(() => simulate.Run(crowd.treeCount), crowd.ResetTrees);
                totals[i] = insert + simulateMs;
            }

            var bestIndex = 0;
            for (int i = 1; i < totals.Length; i++) {
                if (totals[i] < totals[bestIndex]) bestIndex = i;
            }

            report.AppendLine(
                $"{spread,6:F1} | {spacing,7:F2} | {extent,9:F2} | {CellSizes[bestIndex],9:F3} | {totals[bestIndex],13:F4} | {totals[0],9:F4} | {totals[1],9:F4} | {totals[2],9:F4} | {totals[3],9:F4} | {totals[4],9:F4} | {totals[5],9:F4}");
        }
        Report(report);
    }

    /// <summary>
    /// The bulk transform read is scheduled at a fixed batch of 128 like the interpolation jobs, but
    /// it is a transform job rather than a plain one: each item touches a Transform through the
    /// engine instead of a flat array, so its per item cost is higher and the batch that suits it
    /// need not match. Transform jobs have no Run(), so the single batch column stands in for serial.
    /// Every object is a root, which is the parallel friendly case; a deep hierarchy would restrict
    /// how freely the pool can split the work.
    /// </summary>
    [Test]
    public void TransformReadBatchSize_Sweep() {
        var report = new StringBuilder();
        report.AppendLine("=== bulk transform read: inner loop batch size (currently baked at 128) ===");
        report.AppendLine($"workers: {JobsUtility.JobWorkerCount}, flat hierarchy");
        report.AppendLine("transforms | 1 batch |   b32 |   b64 |  b128 |  b256 |  b512 | b1024 | b4096 | best");
        report.AppendLine("-----------+---------+-------+-------+-------+-------+-------+-------+-------+-----");

        foreach (var count in new[] { 512, 4096, 16384 }) {
            var objects = new UnityEngine.GameObject[count];
            var transforms = new UnityEngine.Transform[count];
            for (int i = 0; i < count; i++) {
                objects[i] = new UnityEngine.GameObject("JigglePerfTransform") {
                    hideFlags = UnityEngine.HideFlags.HideAndDontSave,
                };
                transforms[i] = objects[i].transform;
                transforms[i].position = new UnityEngine.Vector3(i * 0.01f, 1f, 0f);
            }

            var accessArray = new TransformAccessArray(transforms);
            var poses = new NativeArray<JiggleTransform>(count, Allocator.Persistent);
            var job = new JiggleJobBulkTransformRead { simulateInputPoses = poses };

            var single = MedianMilliseconds(() => job.ScheduleReadOnly(accessArray, count).Complete());
            var times = new double[InterpolationBatches.Length];
            var bestIndex = 0;
            for (int i = 0; i < InterpolationBatches.Length; i++) {
                var batch = InterpolationBatches[i];
                times[i] = MedianMilliseconds(() => job.ScheduleReadOnly(accessArray, batch).Complete());
                if (times[i] < times[bestIndex]) bestIndex = i;
            }

            report.AppendLine(
                $"{count,10} | {single,7:F4} | {times[0],5:F3} | {times[1],5:F3} | {times[2],5:F3} | {times[3],5:F3} | {times[4],5:F3} | {times[5],5:F3} | {times[6],5:F3} | {InterpolationBatches[bestIndex],4}");

            accessArray.Dispose();
            poses.Dispose();
            for (int i = 0; i < count; i++) {
                UnityEngine.Object.DestroyImmediate(objects[i]);
            }
        }
        Report(report);
    }

    /// <summary>
    /// transformAccessBatchSize caps how many TransformAccessArray registrations happen per commit,
    /// so a structural change costs ceil(count / batch) frames and each of those frames carries a
    /// spike proportional to the batch. Median throughput says nothing useful here: the setting
    /// exists to trade convergence latency against spike height, so this reports the worst single
    /// call in a rebuild cycle alongside how many calls the cycle took.
    ///
    /// A cycle is measured after a Flip, which is the real steady state case: the stale buffer is
    /// emptied in the first call and the add pass runs under the budget from there.
    /// </summary>
    [Test]
    public void TransformAccessBatchSize_ChurnSpike() {
        var report = new StringBuilder();
        report.AppendLine("=== transform access rebuild: batch size vs spike height (currently baked at 512) ===");
        report.AppendLine("worst call is the per frame main thread spike, calls is how many frames the change takes to apply");
        report.AppendLine("transforms | batch | calls | worst call ms | total ms | ms per 1k transforms");
        report.AppendLine("-----------+-------+-------+---------------+----------+---------------------");

        foreach (var count in new[] { 1024, 8192 }) {
            var objects = new UnityEngine.GameObject[count];
            var list = new List<UnityEngine.Transform>(count);
            for (int i = 0; i < count; i++) {
                objects[i] = new UnityEngine.GameObject("JiggleChurnTransform") {
                    hideFlags = UnityEngine.HideFlags.HideAndDontSave,
                };
                list.Add(objects[i].transform);
            }

            foreach (var batch in new[] { 64, 128, 256, 512, 1024, 2048, 8192 }) {
                var worstSamples = new double[9];
                var totalSamples = new double[9];
                var callSamples = new int[9];

                for (int repeat = 0; repeat < worstSamples.Length; repeat++) {
                    var doubleBuffer = new JiggleDoubleBufferTransformAccessArray(128);
                    var index = 0;
                    var finished = false;
                    while (!finished) {
                        doubleBuffer.GenerateNewAccessArrays(ref index, out finished, list, batch);
                    }

                    doubleBuffer.Flip();
                    index = 0;
                    finished = false;
                    var worst = 0.0;
                    var total = 0.0;
                    var calls = 0;
                    var stopwatch = new Stopwatch();
                    while (!finished) {
                        stopwatch.Restart();
                        doubleBuffer.GenerateNewAccessArrays(ref index, out finished, list, batch);
                        stopwatch.Stop();
                        var elapsed = stopwatch.Elapsed.TotalMilliseconds;
                        worst = math.max(worst, elapsed);
                        total += elapsed;
                        calls++;
                    }

                    worstSamples[repeat] = worst;
                    totalSamples[repeat] = total;
                    callSamples[repeat] = calls;
                    doubleBuffer.Dispose();
                }

                Array.Sort(worstSamples);
                Array.Sort(totalSamples);
                var medianWorst = worstSamples[worstSamples.Length / 2];
                var medianTotal = totalSamples[totalSamples.Length / 2];
                report.AppendLine(
                    $"{count,10} | {batch,5} | {callSamples[0],5} | {medianWorst,13:F4} | {medianTotal,8:F4} | {medianTotal * 1000.0 / count,19:F4}");
            }

            for (int i = 0; i < count; i++) {
                UnityEngine.Object.DestroyImmediate(objects[i]);
            }
        }
        Report(report);
    }

    /// <summary>
    /// CellStalenessFrames decides how long an empty grid cell survives before its collider buffer is
    /// freed, so it only means anything when colliders are moving between cells. A static crowd never
    /// ages a cell out, which is why the earlier sweeps could not see this at all. Colliders orbit
    /// here so cells are continually abandoned and reclaimed.
    /// </summary>
    [Test]
    public void CellStalenessFrames_ChurnCost() {
        const int avatars = 60;
        var report = new StringBuilder();
        report.AppendLine("=== cell staleness under moving colliders (currently baked at 3) ===");
        report.AppendLine($"{avatars} avatars, {avatars * CollidersPerAvatar} colliders orbiting, cell size {JiggleSettings.BroadPhaseCellSize:F3}");
        report.AppendLine("staleness | frame ms | live cells | peak cells");
        report.AppendLine("----------+----------+------------+-----------");

        foreach (var staleness in new[] { 1, 2, 3, 8, 32 }) {
            using var crowd = Crowd.Build(avatars);
            var inverse = JiggleSettings.InverseBroadPhaseCellSize;
            var clear = new JiggleJobBroadPhaseClear {
                broadPhaseMap = crowd.broadPhaseMap,
                globalCell = crowd.globalCell,
                maxStalenessFrames = staleness,
            };
            var cull = crowd.CullJob(inverse, JiggleSettings.MaxColliderCellSpan);
            var broadPhase = crowd.BroadPhaseJob();

            var frame = 0;
            var peakCells = 0;
            var frameMs = MedianMilliseconds(
                () => {
                    clear.Run();
                    cull.Run(crowd.colliderCount);
                    broadPhase.Run();
                    peakCells = math.max(peakCells, crowd.broadPhaseMap.Count);
                },
                () => crowd.OrbitColliders(frame++ * 0.05f));

            report.AppendLine(
                $"{staleness,9} | {frameMs,8:F4} | {crowd.broadPhaseMap.Count,10} | {peakCells,10}");
        }
        Report(report);
    }

    private static readonly int[] TransformCounts = { 512, 4096, 32768 };
    private static readonly int[] InterpolationBatches = { 32, 64, 128, 256, 512, 1024, 4096 };

    /// <summary>
    /// Both interpolation jobs are scheduled at a fixed inner loop batch of 128. They do very little
    /// per item — a lerp and a few adds — so per batch scheduling overhead is a large share of the
    /// total and the batch wants to be as large as load balancing allows. This is the opposite regime
    /// from the simulate job, where per item cost is high and variable, so the same constant being
    /// right for both would be a coincidence.
    /// </summary>
    [Test]
    public void InterpolationBatchSize_Sweep() {
        var report = new StringBuilder();
        report.AppendLine("=== interpolation jobs: inner loop batch size (currently baked at 128) ===");
        report.AppendLine($"workers: {JobsUtility.JobWorkerCount}");
        report.AppendLine("job    | transforms | serial ms |   b32 |   b64 |  b128 |  b256 |  b512 | b1024 | b4096 | best");
        report.AppendLine("-------+------------+-----------+-------+-------+-------+-------+-------+-------+-------+-----");

        foreach (var count in TransformCounts) {
            var previousPoses = new NativeArray<PoseData>(count, Allocator.Persistent);
            var currentPoses = new NativeArray<PoseData>(count, Allocator.Persistent);
            var roots = new NativeArray<float3>(count, Allocator.Persistent);
            var previousInputs = new NativeArray<JiggleTransform>(count, Allocator.Persistent);
            var currentInputs = new NativeArray<JiggleTransform>(count, Allocator.Persistent);
            var output = new NativeArray<JiggleTransform>(count, Allocator.Persistent);

            for (int i = 0; i < count; i++) {
                var pose = JiggleTestFactory.Pose(new float3(i * 0.01f, 1f, 0f));
                previousPoses[i] = new PoseData { pose = pose, rootSnapStrength = 1f };
                currentPoses[i] = new PoseData { pose = pose, rootSnapStrength = 1f };
                previousInputs[i] = pose;
                currentInputs[i] = pose;
            }

            var interpolation = new JiggleJobInterpolation {
                previousPoses = previousPoses, currentPoses = currentPoses,
                outputInterpolatedPoses = output, realRootPositions = roots,
                timeStamp = 1.0, previousTimeStamp = 0.98, currentTime = 1.0,
            };
            interpolation.SetFixedDeltaTime(0.02f);

            var inputInterpolation = new JiggleJobInputInterpolation {
                previousInputs = previousInputs, currentInputs = currentInputs,
                outputInterpolatedPoses = output,
                timeStamp = 1.0, previousTimeStamp = 0.98, currentTime = 1.0,
            };

            AppendBatchRow(report, "interp", count, () => interpolation.Run(count),
                batch => interpolation.ScheduleParallel(count, batch, default).Complete());
            AppendBatchRow(report, "input ", count, () => inputInterpolation.Run(count),
                batch => inputInterpolation.ScheduleParallel(count, batch, default).Complete());

            previousPoses.Dispose();
            currentPoses.Dispose();
            roots.Dispose();
            previousInputs.Dispose();
            currentInputs.Dispose();
            output.Dispose();
        }
        Report(report);
    }

    private static void AppendBatchRow(StringBuilder report, string label, int count,
        Action serialAction, Action<int> parallelAction) {
        var serial = MedianMilliseconds(serialAction);
        var times = new double[InterpolationBatches.Length];
        var bestIndex = 0;
        for (int i = 0; i < InterpolationBatches.Length; i++) {
            var batch = InterpolationBatches[i];
            times[i] = MedianMilliseconds(() => parallelAction(batch));
            if (times[i] < times[bestIndex]) bestIndex = i;
        }
        report.AppendLine(
            $"{label} | {count,10} | {serial,9:F4} | {times[0],5:F3} | {times[1],5:F3} | {times[2],5:F3} | {times[3],5:F3} | {times[4],5:F3} | {times[5],5:F3} | {times[6],5:F3} | {InterpolationBatches[bestIndex],4}");
    }

    /// <summary>
    /// MAX_COLLIDERS is a hard cap: once a cell holds that many, further colliders are silently
    /// dropped and stop colliding. Reports how close realistic crowds get, and at which cell sizes
    /// the cap actually bites.
    /// </summary>
    [Test]
    public void CellOccupancy_SaturationHeadroom() {
        var report = new StringBuilder();
        report.AppendLine($"=== cell occupancy vs MAX_COLLIDERS ({JiggleJobBroadPhase.MAX_COLLIDERS}) ===");
        report.AppendLine("dropped colliders never collide, so saturated cells are a correctness cliff not a slowdown");
        report.AppendLine("avatars | cell m |  cells | mean occ | max occ | saturated | global");
        report.AppendLine("--------+--------+--------+----------+---------+-----------+-------");

        foreach (var avatars in new[] { 20, 60, 150 }) {
            using var crowd = Crowd.Build(avatars);
            foreach (var cellSize in new[] { 0.25f, 0.5f, 1f, 2f }) {
                crowd.PopulateGrid(1f / cellSize, JiggleSettings.MaxColliderCellSpan);
                var stats = crowd.MeasureGrid();
                report.AppendLine(
                    $"{avatars,7} | {cellSize,6:F2} | {stats.cellCount,6} | {stats.meanOccupancy,8:F2} | {stats.maxOccupancy,7} | {stats.saturatedCells,9} | {stats.globalCount,6}");
            }
        }
        Report(report);
    }

    /// <summary>
    /// The simulate job is scheduled with an inner loop batch of 1. Trees vary a lot in cost, so a
    /// batch of 1 gives the best load balancing at the highest scheduling overhead. This is where
    /// that trade lands.
    /// </summary>
    [Test]
    public void SimulateBatchSize_Sweep() {
        var report = new StringBuilder();
        report.AppendLine("=== simulate job inner loop batch size ===");
        report.AppendLine($"workers: {JobsUtility.JobWorkerCount}, {PointsPerTree} points per tree");
        report.AppendLine("trees | serial ms | batch 1 | batch 2 | batch 4 | batch 8 | batch 16 | batch 32 | best");
        report.AppendLine("------+-----------+---------+---------+---------+---------+----------+----------+-----");

        foreach (var avatars in new[] { 8, 32, 128, 512 }) {
            using var crowd = Crowd.Build(avatars);
            var inverse = JiggleSettings.InverseBroadPhaseCellSize;
            crowd.PopulateGrid(inverse, JiggleSettings.MaxColliderCellSpan);
            var simulate = crowd.SimulateJob(inverse, JiggleSettings.MaxTreeCellSpan);

            var serial = MedianMilliseconds(() => simulate.Run(crowd.treeCount), crowd.ResetTrees);
            var batches = new[] { 1, 2, 4, 8, 16, 32 };
            var times = new double[batches.Length];
            for (int i = 0; i < batches.Length; i++) {
                var batch = batches[i];
                times[i] = MedianMilliseconds(
                    () => simulate.ScheduleParallel(crowd.treeCount, batch, default).Complete(), crowd.ResetTrees);
            }

            var bestIndex = 0;
            for (int i = 1; i < times.Length; i++) {
                if (times[i] < times[bestIndex]) bestIndex = i;
            }

            report.AppendLine(
                $"{avatars,5} | {serial,9:F4} | {times[0],7:F4} | {times[1],7:F4} | {times[2],7:F4} | {times[3],7:F4} | {times[4],8:F4} | {times[5],8:F4} | {batches[bestIndex],4}");
        }
        Report(report);
    }

    /// <summary>
    /// MaxColliderCellSpan decides when a collider stops being inserted per cell and goes in the
    /// global cell instead. Global colliders are tested against every point of every tree, so
    /// pushing too many there is expensive in the simulate stage even though it makes insert cheaper.
    /// </summary>
    [Test]
    public void MaxColliderCellSpan_Sweep() {
        const int avatars = 60;
        var report = new StringBuilder();
        report.AppendLine($"=== max collider cell span sweep (cell size {JiggleSettings.BroadPhaseCellSize:F3}) ===");
        report.AppendLine($"{avatars} avatars, {avatars * CollidersPerAvatar} colliders");
        report.AppendLine(" span | insert ms | simulate ms | total ms | global | cells");
        report.AppendLine("------+-----------+-------------+----------+--------+------");

        // Span is denominated in cells, so its meaning moves with cell size. Sweeping at the
        // configured size keeps this measuring the shipped configuration rather than a stale one.
        var inverse = JiggleSettings.InverseBroadPhaseCellSize;
        foreach (var span in new[] { 1, 4, 16, 100, 1024 }) {
            using var crowd = Crowd.Build(avatars);
            crowd.PopulateGrid(inverse, span);
            var stats = crowd.MeasureGrid();

            var clear = crowd.ClearJob();
            var cull = crowd.CullJob(inverse, span);
            var broadPhase = crowd.BroadPhaseJob();
            var insert = MedianMilliseconds(() => {
                clear.Run();
                cull.Run(crowd.colliderCount);
                broadPhase.Run();
            });

            var simulate = crowd.SimulateJob(inverse, JiggleSettings.MaxTreeCellSpan);
            var simulateMs = MedianMilliseconds(() => simulate.Run(crowd.treeCount), crowd.ResetTrees);

            report.AppendLine(
                $"{span,5} | {insert,9:F4} | {simulateMs,11:F4} | {insert + simulateMs,8:F4} | {stats.globalCount,6} | {stats.cellCount,5}");
        }
        Report(report);
    }
}

}
