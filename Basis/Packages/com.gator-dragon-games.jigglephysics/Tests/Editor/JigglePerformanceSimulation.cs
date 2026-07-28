using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using Debug = UnityEngine.Debug;
using Random = Unity.Mathematics.Random;

namespace GatorDragonGames.JigglePhysics.Tests {

/// <summary>
/// Timing harness for the collider culling and broad phase stage.
///
/// Serial numbers come from Run(), not from a hand written loop: Run() executes the same Burst
/// compiled job on the calling thread, so the only difference against Schedule() is how many
/// workers touch it. A managed loop calling Execute(i) would have measured Burst versus no Burst
/// at the same time and made the parallel win look far bigger than it is.
///
/// Editor timings, so treat them as relative. A player build compiles Burst with different
/// settings and has a busier job queue.
/// </summary>
[TestFixture]
[Category("Performance")]
[Explicit("Timing benchmark. Run it deliberately, it is too slow for the normal suite.")]
internal unsafe class JigglePerformanceSimulation {
    private const int WarmupIterations = 10;
    private const int MeasuredIterations = 51;

    private bool previousSynchronousCompilation;

    /// <summary>
    /// Burst compiles asynchronously in the editor, so a job runs the slow managed path until its
    /// compiled version is ready. That window can span several rows of a sweep and reads as a step
    /// change partway down the table, indistinguishable from a real effect of whatever is being
    /// swept. Forcing synchronous compilation makes the warmup actually warm.
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

    /// <summary>
    /// Roughly one avatar's worth of colliders, matching the shape of a humanoid rig: a lot of
    /// small finger spheres, a handful of limb capsules, two feet.
    /// </summary>
    private const int FingerColliders = 30;
    private const int LimbColliders = 6;
    private const int FootColliders = 2;
    private const int CollidersPerAvatar = FingerColliders + LimbColliders + FootColliders;

    private sealed class Scenario : IDisposable {
        public NativeArray<JiggleCollider> colliders;
        public NativeArray<JiggleColliderBroadPhaseEntry> entries;
        public NativeArray<JiggleCullingCamera> cameras;
        public NativeHashMap<int2, JiggleGridCell> broadPhaseMap;
        public NativeReference<JiggleGridCell> globalCell;
        public int colliderCount;
        public int cameraCount;

        public static Scenario Build(int colliderCount, int cameraCount, float worldRadius = 40f, uint seed = 0x9E3779B9) {
            var scenario = new Scenario {
                colliderCount = colliderCount,
                cameraCount = cameraCount,
                colliders = new NativeArray<JiggleCollider>(colliderCount, Allocator.Persistent),
                entries = new NativeArray<JiggleColliderBroadPhaseEntry>(colliderCount, Allocator.Persistent),
                cameras = new NativeArray<JiggleCullingCamera>(math.max(cameraCount, 1), Allocator.Persistent),
                broadPhaseMap = new NativeHashMap<int2, JiggleGridCell>(1024, Allocator.Persistent),
                globalCell = new NativeReference<JiggleGridCell>(
                    new JiggleGridCell(JiggleJobBroadPhase.MAX_COLLIDERS), Allocator.Persistent),
            };
            scenario.FillColliders(worldRadius, seed);
            scenario.FillCameras(worldRadius);
            return scenario;
        }

        private void FillColliders(float worldRadius, uint seed) {
            var random = new Random(seed);
            var index = 0;
            while (index < colliderCount) {
                var origin = new float3(
                    random.NextFloat(-worldRadius, worldRadius), 0f, random.NextFloat(-worldRadius, worldRadius));

                for (int i = 0; i < FingerColliders && index < colliderCount; i++) {
                    var jitter = random.NextFloat3(new float3(-0.35f, -0.2f, -0.35f), new float3(0.35f, 0.2f, 0.35f));
                    colliders[index++] = JiggleTestFactory.Sphere(origin + new float3(0f, 1.1f, 0f) + jitter, 0.012f);
                }
                for (int i = 0; i < LimbColliders && index < colliderCount; i++) {
                    var jitter = random.NextFloat3(new float3(-0.25f, 0f, -0.25f), new float3(0.25f, 1.4f, 0.25f));
                    var axis = (JiggleCollider.CapsuleAxis)(i % 3);
                    colliders[index++] = JiggleTestFactory.Capsule(origin + jitter, 0.06f, 0.32f, axis);
                }
                for (int i = 0; i < FootColliders && index < colliderCount; i++) {
                    var jitter = random.NextFloat3(new float3(-0.2f, 0f, -0.2f), new float3(0.2f, 0.1f, 0.2f));
                    colliders[index++] = JiggleTestFactory.Sphere(origin + jitter, 0.09f);
                }
            }
        }

        /// <summary>
        /// Cameras sit at the origin looking down +Z with a box frustum, so a predictable slice of
        /// the crowd survives the frustum test instead of everything or nothing.
        /// </summary>
        private void FillCameras(float worldRadius) {
            for (int i = 0; i < cameras.Length; i++) {
                var angle = (math.PI * 2f * i) / math.max(cameras.Length, 1);
                var forward = new float3(math.sin(angle), 0f, math.cos(angle));
                var right = new float3(forward.z, 0f, -forward.x);
                cameras[i] = new JiggleCullingCamera {
                    position = float3.zero,
                    plane0 = new float4(right, worldRadius * 0.5f),
                    plane1 = new float4(-right, worldRadius * 0.5f),
                    plane2 = new float4(0f, 1f, 0f, worldRadius),
                    plane3 = new float4(0f, -1f, 0f, worldRadius),
                    plane4 = new float4(forward, 0f),
                    plane5 = new float4(-forward, worldRadius * 2f),
                };
            }
        }

        public JiggleJobColliderCull CullJob(bool frustumCull = true, bool distanceCull = true) {
            return new JiggleJobColliderCull {
                jiggleColliders = colliders,
                broadPhaseEntries = entries,
                cullingCameras = cameras,
                cullingCameraCount = cameraCount,
                frustumCull = (byte)(frustumCull ? 1 : 0),
                distanceCull = (byte)(distanceCull ? 1 : 0),
                maxCollisionDistance = 20f,
                nearKeepRadius = 2.5f,
                frustumMargin = 0.5f,
                inverseCellSize = JiggleSettings.InverseBroadPhaseCellSize,
                maxColliderCellSpan = JiggleSettings.MaxColliderCellSpan,
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

        public JiggleJobBroadPhaseClear ClearJob() {
            return new JiggleJobBroadPhaseClear {
                broadPhaseMap = broadPhaseMap,
                globalCell = globalCell,
                maxStalenessFrames = JiggleSettings.CellStalenessFrames,
            };
        }

        public int SurvivingColliderCount() {
            var kept = 0;
            for (int i = 0; i < colliderCount; i++) {
                if (entries[i].state != JiggleColliderBroadPhaseEntry.StateSkip) {
                    kept++;
                }
            }
            return kept;
        }

        public void Dispose() {
            if (broadPhaseMap.IsCreated) {
                var keys = broadPhaseMap.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < keys.Length; i++) {
                    broadPhaseMap[keys[i]].Dispose();
                }
                keys.Dispose();
                broadPhaseMap.Dispose();
            }
            if (globalCell.IsCreated) {
                globalCell.Value.Dispose();
                globalCell.Dispose();
            }
            if (colliders.IsCreated) colliders.Dispose();
            if (entries.IsCreated) entries.Dispose();
            if (cameras.IsCreated) cameras.Dispose();
        }
    }

    private static double MedianMilliseconds(Action action) {
        for (int i = 0; i < WarmupIterations; i++) {
            action();
        }
        var samples = new double[MeasuredIterations];
        var stopwatch = new Stopwatch();
        for (int i = 0; i < MeasuredIterations; i++) {
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

    private static readonly int[] ColliderCounts = { 128, 512, 2048, 8192, 32768 };

    /// <summary>
    /// The batch size production would derive for this collider count on this machine, so the
    /// tables track what JiggleJobs actually schedules rather than a fixed constant.
    /// </summary>
    private static int ProductionBatch(int colliderCount) {
        return JiggleJobColliderCull.GetBatchSize(colliderCount, JobsUtility.JobWorkerCount,
            JiggleSettings.ColliderCullMinBatch);
    }

    [Test]
    public void ColliderCull_SerialVersusParallel() {
        var report = new StringBuilder();
        report.AppendLine("=== collider cull: same Burst job, one thread (Run) vs workers (Schedule) ===");
        report.AppendLine($"job worker threads: {JobsUtility.JobWorkerCount}, batch: derived per count, cameras: 2");
        report.AppendLine("colliders |  serial ms | parallel ms | speedup |   kept");
        report.AppendLine("----------+------------+-------------+---------+-------");

        foreach (var count in ColliderCounts) {
            using var scenario = Scenario.Build(count, cameraCount: 2);
            var job = scenario.CullJob();
            var batch = ProductionBatch(count);

            var serial = MedianMilliseconds(() => job.Run(count));
            var kept = scenario.SurvivingColliderCount();
            var parallel = MedianMilliseconds(() => job.Schedule(count, batch).Complete());

            report.AppendLine(
                $"{count,9} | {serial,10:F4} | {parallel,11:F4} | {serial / parallel,6:F2}x | {kept,6}");
        }
        Report(report);
    }

    /// <summary>
    /// The end to end number that actually matters. The broad phase insertion stayed serial, so it
    /// caps how much the parallel cull can win: this prints that ceiling rather than just the
    /// cull stage in isolation.
    /// </summary>
    [Test]
    public void CollisionPipeline_FrameCost() {
        var report = new StringBuilder();
        report.AppendLine("=== collision prep per frame: clear + cull + broad phase insert ===");
        report.AppendLine($"job worker threads: {JobsUtility.JobWorkerCount}, cameras: 2");
        report.AppendLine("colliders | cull serial | cull parallel | broadphase | frame before | frame after | speedup");
        report.AppendLine("----------+-------------+---------------+------------+--------------+-------------+--------");

        foreach (var count in ColliderCounts) {
            using var scenario = Scenario.Build(count, cameraCount: 2);
            var cull = scenario.CullJob();
            var broadPhase = scenario.BroadPhaseJob();
            var clear = scenario.ClearJob();
            var batch = ProductionBatch(count);

            var cullSerial = MedianMilliseconds(() => cull.Run(count));
            var cullParallel = MedianMilliseconds(() => cull.Schedule(count, batch).Complete());
            var insert = MedianMilliseconds(() => {
                clear.Run();
                broadPhase.Run();
            });

            var before = cullSerial + insert;
            var after = cullParallel + insert;
            report.AppendLine(
                $"{count,9} | {cullSerial,11:F4} | {cullParallel,13:F4} | {insert,10:F4} | {before,12:F4} | {after,11:F4} | {before / after,5:F2}x");
        }
        Report(report);
    }

    /// <summary>
    /// The cull is O(colliders x cameras), which is the part of this that scales badly. Splits per
    /// camera count so the cost of adding cameras is visible.
    /// </summary>
    [Test]
    public void ColliderCull_CostByCameraCount() {
        var report = new StringBuilder();
        report.AppendLine("=== cull cost by camera count (8192 colliders) ===");
        report.AppendLine($"job worker threads: {JobsUtility.JobWorkerCount}");
        report.AppendLine("cameras |  serial ms | parallel ms | speedup |   kept");
        report.AppendLine("--------+------------+-------------+---------+-------");

        const int count = 8192;
        foreach (var cameraCount in new[] { 1, 2, 4, 8, 16 }) {
            using var scenario = Scenario.Build(count, cameraCount);
            var job = scenario.CullJob();
            var batch = ProductionBatch(count);

            var serial = MedianMilliseconds(() => job.Run(count));
            var kept = scenario.SurvivingColliderCount();
            var parallel = MedianMilliseconds(() => job.Schedule(count, batch).Complete());

            report.AppendLine(
                $"{cameraCount,7} | {serial,10:F4} | {parallel,11:F4} | {serial / parallel,6:F2}x | {kept,6}");
        }
        Report(report);
    }

    /// <summary>
    /// Batch size trades scheduling overhead against how evenly the work spreads. Prints the curve
    /// so JiggleSettings.ColliderCullMinBatch can be set from data instead of taste.
    /// </summary>
    [Test]
    public void ColliderCull_CostByBatchSize() {
        var report = new StringBuilder();
        report.AppendLine("=== cull cost by batch size (8192 colliders, 2 cameras) ===");
        report.AppendLine($"job worker threads: {JobsUtility.JobWorkerCount}");
        report.AppendLine("batch |  parallel ms");
        report.AppendLine("------+-------------");

        const int count = 8192;
        using var scenario = Scenario.Build(count, cameraCount: 2);
        var job = scenario.CullJob();

        foreach (var batch in new[] { 8, 16, 32, 64, 128, 256, 1024 }) {
            var parallel = MedianMilliseconds(() => job.Schedule(count, batch).Complete());
            report.AppendLine($"{batch,5} | {parallel,11:F4}");
        }
        Report(report);
    }

    /// <summary>
    /// How much the cull stage costs when it is doing nothing, which is what a project with culling
    /// switched off pays for the entry buffer to exist at all.
    /// </summary>
    [Test]
    public void ColliderCull_CostWithCullingDisabled() {
        var report = new StringBuilder();
        report.AppendLine("=== cull stage with culling disabled (classification only) ===");
        report.AppendLine("colliders |  serial ms | parallel ms | speedup");
        report.AppendLine("----------+------------+-------------+--------");

        foreach (var count in ColliderCounts) {
            using var scenario = Scenario.Build(count, cameraCount: 0);
            var job = scenario.CullJob(frustumCull: false, distanceCull: false);
            var batch = ProductionBatch(count);

            var serial = MedianMilliseconds(() => job.Run(count));
            var parallel = MedianMilliseconds(() => job.Schedule(count, batch).Complete());

            report.AppendLine($"{count,9} | {serial,10:F4} | {parallel,11:F4} | {serial / parallel,5:F2}x");
        }
        Report(report);
    }
}

}
