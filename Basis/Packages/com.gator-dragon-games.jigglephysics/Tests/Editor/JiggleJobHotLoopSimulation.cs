using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;
using Random = Unity.Mathematics.Random;

namespace GatorDragonGames.JigglePhysics.Tests {

/// <summary>
/// Timing harness for the four Burst jobs that make up the per-frame jiggle cost, plus the
/// bit-for-bit equivalence dump that guards them.
///
/// Serial numbers, from Run() rather than Schedule(): the question these answer is how much work the
/// job body does, not how well it spreads. Schedule() folds in worker wake-up and load imbalance,
/// which move for reasons that have nothing to do with the code under test.
///
/// The equivalence half exists because every optimisation here is supposed to be a pure
/// restructuring — same arithmetic, same order, same results to the last bit. DumpEquivalenceState
/// runs a fixed scene for a fixed number of steps and writes every output float to a stable path, so
/// two runs across a change can be diffed. The simulation is chaotic and the dump is taken after 60
/// steps, so even a one-ulp divergence has had time to grow into something visible: a clean diff is
/// strong evidence, not a weak one.
///
/// Editor timings. Relative, not absolute.
/// </summary>
[TestFixture]
[Category("Performance")]
[Explicit("Timing benchmark. Run it deliberately, it is too slow for the normal suite.")]
internal unsafe class JiggleJobHotLoopSimulation {
    private const int WarmupIterations = 5;
    private const int MeasuredIterations = 25;

    /// <summary>Where DumpEquivalenceState writes. Fixed so two runs can be diffed outside Unity.</summary>
    private static string DumpPath => Path.Combine(Path.GetTempPath(), "jiggle_equivalence.bin");

    private bool previousSynchronousCompilation;

    /// <summary>
    /// Burst compiles asynchronously in the editor, so an un-warmed job runs as managed IL and reads
    /// as a 10x cliff partway down a table. Simulate is big enough for that window to span rows.
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

    private static void Report(StringBuilder report) {
        Debug.Log(report.ToString());
        TestContext.Out.WriteLine(report.ToString());
    }

    /// <summary>
    /// Median of `action`, with `between` run untimed before every sample. The simulate job mutates
    /// its trees in place, so without restoring them between samples the later samples measure
    /// drifted (and eventually sanitised) trees rather than the job.
    /// </summary>
    private static double MedianMilliseconds(Action action, Action between = null) {
        for (int i = 0; i < WarmupIterations; i++) {
            between?.Invoke();
            action();
        }
        var samples = new double[MeasuredIterations];
        var stopwatch = new Stopwatch();
        for (int i = 0; i < MeasuredIterations; i++) {
            between?.Invoke();
            stopwatch.Restart();
            action();
            stopwatch.Stop();
            samples[i] = stopwatch.Elapsed.TotalMilliseconds;
        }
        Array.Sort(samples);
        return samples[MeasuredIterations / 2];
    }

    // ------------------------------------------------------------------------------- the scene

    /// <summary>
    /// A crowd of jiggle chains sitting inside a field of avatar-shaped scene colliders, with the
    /// broad phase populated the way JiggleJobBroadPhase would populate it. Chains are tilted off
    /// vertical so a tree's xz extent covers several cells — the per-point grid walk is one of the
    /// things being measured, and a chain hanging straight down touches one cell whatever happens.
    /// </summary>
    private sealed class Crowd : IDisposable {
        public NativeArray<JiggleTreeJobData> trees;
        public NativeArray<JiggleTransform> inputPoses;
        public NativeArray<PoseData> outputPoses;
        public NativeArray<JiggleCollider> sceneColliders;
        public NativeArray<JiggleCollider> personalColliders;
        public NativeHashMap<int2, JiggleGridCell> broadPhaseMap;
        public NativeReference<JiggleGridCell> globalCell;
        public NativeArray<FixedList128Bytes<JiggleGrabConstraint>> grabsPerTree;

        public int treeCount;
        public int pointsPerTree;
        public int sceneColliderCount;

        private readonly List<IntPtr> allocations = new List<IntPtr>();
        private readonly List<int2> gridKeys = new List<int2>();
        private JiggleSimulatedPoint[][] snapshots;
        private PoseData[] outputSnapshot;

        private const int PersonalCollidersPerTree = 2;
        private const int SceneCollidersPerTree = 8;

        /// <summary>
        /// Every tree on top of every other tree, sharing one set of colliders in one broad phase
        /// cell. Per-tree work is unchanged — the same number of candidate colliders survives the
        /// same tests — but the collider array and the cell map stop growing with the crowd. Run
        /// against <see cref="Build"/> at matching tree counts, it separates "cost per point rises
        /// because the collider field got big" from "cost per point rises because the per-tree
        /// buffers got big".
        /// </summary>
        public static Crowd BuildStacked(int treeCount, int bonesPerTree = 8, float elasticitySoften = 0.5f) {
            return Build(treeCount, bonesPerTree, worldRadius: 0f, elasticitySoften: elasticitySoften,
                sharedColliders: true);
        }

        public static Crowd Build(int treeCount, int bonesPerTree = 8, float worldRadius = 20f,
            uint seed = 0x5F3759DF, float elasticitySoften = 0.5f, bool sharedColliders = false,
            bool interleaveColliders = false) {
            var pointsPerTree = bonesPerTree + 2;
            var crowd = new Crowd {
                treeCount = treeCount,
                pointsPerTree = pointsPerTree,
                sceneColliderCount = treeCount * SceneCollidersPerTree,
            };

            crowd.trees = new NativeArray<JiggleTreeJobData>(treeCount, Allocator.Persistent);
            crowd.inputPoses = new NativeArray<JiggleTransform>(treeCount * pointsPerTree, Allocator.Persistent);
            crowd.outputPoses = new NativeArray<PoseData>(treeCount * pointsPerTree, Allocator.Persistent);
            if (sharedColliders) {
                crowd.sceneColliderCount = SceneCollidersPerTree;
            }
            crowd.sceneColliders = new NativeArray<JiggleCollider>(crowd.sceneColliderCount, Allocator.Persistent);
            crowd.personalColliders =
                new NativeArray<JiggleCollider>(treeCount * PersonalCollidersPerTree, Allocator.Persistent);
            crowd.broadPhaseMap = new NativeHashMap<int2, JiggleGridCell>(4096, Allocator.Persistent);
            crowd.globalCell = new NativeReference<JiggleGridCell>(
                new JiggleGridCell(JiggleJobBroadPhase.MAX_COLLIDERS), Allocator.Persistent);
            crowd.grabsPerTree = new NativeArray<FixedList128Bytes<JiggleGrabConstraint>>(treeCount, Allocator.Persistent);
            crowd.snapshots = new JiggleSimulatedPoint[treeCount][];

            // Production-shaped parameters: every term the solver has is live, because a benchmark
            // that leaves branches switched off measures a solver nobody runs.
            var parameters = JiggleTestFactory.Params(
                rootElasticity: 1f, angleElasticity: 0.6f, lengthElasticity: 0.8f,
                elasticitySoften: elasticitySoften,
                gravityMultiplier: 1f, blend: 1f, airDrag: 0.12f, drag: 0.1f, ignoreRootMotion: 0.4f,
                collisionRadius: 0.03f, angleLimited: true, angleLimit: 0.6f, angleLimitSoften: 0.3f);

            var random = new Random(seed);
            var inverseCellSize = JiggleSettings.InverseBroadPhaseCellSize;

            for (int t = 0; t < treeCount; t++) {
                var origin = new float3(
                    random.NextFloat(-worldRadius, worldRadius), 1.2f,
                    random.NextFloat(-worldRadius, worldRadius));
                var direction = math.normalize(new float3(
                    random.NextFloat(-0.7f, 0.7f), -1f, random.NextFloat(-0.7f, 0.7f)));

                var source = JiggleTestTree.Chain(bonesPerTree, origin, direction, 0.08f, parameters);
                var offset = t * pointsPerTree;
                for (int i = 0; i < pointsPerTree; i++) {
                    crowd.inputPoses[offset + i] = source.inputPoses[i];
                    crowd.outputPoses[offset + i] = new PoseData {
                        pose = new JiggleTransform {
                            isVirtual = !source.points[i].hasTransform,
                            position = source.points[i].pose,
                            rotation = quaternion.identity,
                            scale = new float3(1f),
                        },
                        rootPosition = origin,
                        rootOffset = float3.zero,
                        rootSnapStrength = 0.5f,
                    };
                }

                var jobData = new JiggleTreeJobData(t, offset, t * PersonalCollidersPerTree,
                    PersonalCollidersPerTree, source.points, source.parameters, source.children);
                crowd.allocations.Add((IntPtr)jobData.points);
                crowd.allocations.Add((IntPtr)jobData.parameters);
                crowd.allocations.Add((IntPtr)jobData.childrenIndices);
                crowd.trees[t] = jobData;

                crowd.snapshots[t] = new JiggleSimulatedPoint[pointsPerTree];
                Array.Copy(source.points, crowd.snapshots[t], pointsPerTree);

                for (int p = 0; p < PersonalCollidersPerTree; p++) {
                    crowd.personalColliders[t * PersonalCollidersPerTree + p] = JiggleTestFactory.Sphere(
                        origin + random.NextFloat3(new float3(-0.2f), new float3(0.2f)), 0.05f);
                }

                // Avatar-shaped scene colliders around each chain, registered into whichever cell
                // they land in, the way the broad phase job would.
                for (int c = 0; c < SceneCollidersPerTree; c++) {
                    var centre = origin + random.NextFloat3(new float3(-0.4f, -0.6f, -0.4f), new float3(0.4f, 0.2f, 0.4f));
                    // Stacked: one set of colliders, registered once, that every tree then finds in
                    // the single cell they all share.
                    // Interleaved: a tree's eight colliders land a stride apart instead of side by
                    // side, so the same eight reads span eight cache lines rather than sharing two.
                    // Same colliders, same tests, same results — only the addresses differ.
                    var index = sharedColliders ? c
                        : interleaveColliders ? c * treeCount + t
                        : t * SceneCollidersPerTree + c;
                    if (sharedColliders && t > 0) {
                        continue;
                    }
                    crowd.sceneColliders[index] = (c % 4) == 0
                        ? JiggleTestFactory.Capsule(centre, 0.06f, 0.3f)
                        : JiggleTestFactory.Sphere(centre, 0.04f);
                    crowd.AddGridCollider(JiggleGridCell.GetKeyForPosition(centre, inverseCellSize), index);
                }
            }

            // A couple of world colliders every tree tests every point against, which is what a
            // ground plane or a room capsule ends up being.
            var globalCollider = crowd.globalCell.Value;
            globalCollider.colliderIndices[0] = 0;
            globalCollider.count = 1;
            crowd.globalCell.Value = globalCollider;

            crowd.outputSnapshot = new PoseData[crowd.outputPoses.Length];
            crowd.outputPoses.CopyTo(crowd.outputSnapshot);
            return crowd;
        }

        private void AddGridCollider(int2 key, int colliderIndex) {
            if (!broadPhaseMap.TryGetValue(key, out var cell)) {
                cell = new JiggleGridCell(JiggleJobBroadPhase.MAX_COLLIDERS);
                broadPhaseMap.Add(key, cell);
                gridKeys.Add(key);
            }
            if (cell.count >= JiggleJobBroadPhase.MAX_COLLIDERS) {
                return;
            }
            cell.colliderIndices[cell.count] = colliderIndex;
            cell.count++;
            broadPhaseMap[key] = cell;
        }

        /// <summary>Restores the trees and output poses to their built state.</summary>
        public void Reset() {
            for (int t = 0; t < treeCount; t++) {
                var tree = trees[t];
                fixed (JiggleSimulatedPoint* source = snapshots[t]) {
                    UnsafeUtility.MemCpy(tree.points, source, sizeof(JiggleSimulatedPoint) * tree.pointCount);
                }
            }
            outputPoses.CopyFrom(outputSnapshot);
        }

        public JiggleJobSimulate SimulateJob(int substeps = 1) {
            var job = new JiggleJobSimulate();
            job.SetFixedDeltaTime(0.02f);
            job.inverseCellSize = JiggleSettings.InverseBroadPhaseCellSize;
            job.maxTreeCellSpan = JiggleSettings.MaxTreeCellSpan;
            job.substeps = substeps;
            job.gravity = new float3(0f, -9.81f, 0f);
            job.timeStamp = 0.0;
            job.sceneColliderCount = sceneColliderCount;
            job.inputPoses = inputPoses;
            job.outputPoses = outputPoses;
            job.jiggleTrees = trees;
            job.personalColliders = personalColliders;
            job.sceneColliders = sceneColliders;
            job.broadPhaseMap = broadPhaseMap;
            job.globalCell = globalCell;
            job.grabsPerTree = grabsPerTree;
            job.grabConstraintCount = 0;
            return job;
        }

        public void Dispose() {
            foreach (var key in gridKeys) {
                if (broadPhaseMap.TryGetValue(key, out var cell)) {
                    cell.Dispose();
                }
            }
            gridKeys.Clear();
            if (globalCell.IsCreated) {
                var cell = globalCell.Value;
                cell.Dispose();
                globalCell.Dispose();
            }
            if (grabsPerTree.IsCreated) grabsPerTree.Dispose();
            if (broadPhaseMap.IsCreated) broadPhaseMap.Dispose();
            if (sceneColliders.IsCreated) sceneColliders.Dispose();
            if (personalColliders.IsCreated) personalColliders.Dispose();
            if (trees.IsCreated) trees.Dispose();
            if (outputPoses.IsCreated) outputPoses.Dispose();
            if (inputPoses.IsCreated) inputPoses.Dispose();
            foreach (var pointer in allocations) {
                UnsafeUtility.Free((void*)pointer, Allocator.Persistent);
            }
            allocations.Clear();
        }
    }

    // -------------------------------------------------------------------------------- simulate

    [Test]
    public void Simulate_Throughput() {
        var report = new StringBuilder();
        report.AppendLine("=== JiggleJobSimulate, serial (Run) over a crowd ===");
        report.AppendLine("trees | bones | points | substeps | total ms | us per point");
        report.AppendLine("------+-------+--------+----------+----------+-------------");

        // Swept wide on purpose: per-point cost that climbs with the crowd is the working set falling
        // out of cache, not the solver getting slower, and that distinguishes a memory problem (fix
        // the 216 byte point struct) from an arithmetic one (fix the maths).
        foreach (var treeCount in new[] { 256, 2048, 8192 }) {
            foreach (var substeps in new[] { 1, 2 }) {
                // elasticitySoften 0 is what ToJigglePointParameters actually emits unless the
                // advanced toggle is on, so this is the shape of a shipped rig.
                using var crowd = Crowd.Build(treeCount, elasticitySoften: 0f);
                var job = crowd.SimulateJob(substeps);
                var points = treeCount * crowd.pointsPerTree;
                var total = MedianMilliseconds(() => job.Run(treeCount), crowd.Reset);
                var pointBytes = points * sizeof(JiggleSimulatedPoint) / 1024.0 / 1024.0;
                report.AppendLine(
                    $"{treeCount,5} | {8,5} | {points,6} | {substeps,8} | {total,8:F4} | {total * 1000.0 / points,12:F4}" +
                    $"  ({pointBytes,6:F2} MB of points)");
            }
        }
        Report(report);
    }

    /// <summary>
    /// The same crowd with the collision field emptied, so the split between the constraint solve
    /// and everything collision costs is visible rather than inferred.
    /// </summary>
    [Test]
    public void Simulate_CollisionShare() {
        var report = new StringBuilder();
        report.AppendLine("=== JiggleJobSimulate: cost with and without colliders in range ===");
        report.AppendLine("trees | with colliders ms | no colliders ms | collision share");
        report.AppendLine("------+-------------------+-----------------+----------------");

        foreach (var treeCount in new[] { 256, 2048 }) {
            using var crowd = Crowd.Build(treeCount);
            var job = crowd.SimulateJob();
            var withColliders = MedianMilliseconds(() => job.Run(treeCount), crowd.Reset);

            // Same trees, same code path, nothing to depenetrate against: the grid walk still runs
            // and still finds its cells, the colliders in them are just switched off.
            for (int i = 0; i < crowd.sceneColliderCount; i++) {
                var collider = crowd.sceneColliders[i];
                collider.enabled = false;
                crowd.sceneColliders[i] = collider;
            }
            for (int i = 0; i < crowd.personalColliders.Length; i++) {
                var collider = crowd.personalColliders[i];
                collider.enabled = false;
                crowd.personalColliders[i] = collider;
            }
            var withoutColliders = MedianMilliseconds(() => job.Run(treeCount), crowd.Reset);

            report.AppendLine($"{treeCount,5} | {withColliders,17:F4} | {withoutColliders,15:F4} | " +
                $"{(withColliders - withoutColliders) / withColliders * 100.0,14:F1}%");
        }
        Report(report);
    }

    // --------------------------------------------------------------------------- interpolation

    [Test]
    public void Interpolation_Throughput() {
        var report = new StringBuilder();
        report.AppendLine("=== interpolation jobs, serial (Run) ===");
        report.AppendLine("points | input interp ms | output interp ms");
        report.AppendLine("-------+-----------------+-----------------");

        foreach (var count in new[] { 8192, 32768 }) {
            var previousInputs = new NativeArray<JiggleTransform>(count, Allocator.Persistent);
            var currentInputs = new NativeArray<JiggleTransform>(count, Allocator.Persistent);
            var simulateInputs = new NativeArray<JiggleTransform>(count, Allocator.Persistent);
            var previousPoses = new NativeArray<PoseData>(count, Allocator.Persistent);
            var currentPoses = new NativeArray<PoseData>(count, Allocator.Persistent);
            var rootPositions = new NativeArray<float3>(count, Allocator.Persistent);
            var interpolated = new NativeArray<JiggleTransform>(count, Allocator.Persistent);
            try {
                var random = new Random(0x9E3779B9);
                for (int i = 0; i < count; i++) {
                    var a = RandomPose(ref random);
                    var b = RandomPose(ref random);
                    previousInputs[i] = a;
                    currentInputs[i] = b;
                    previousPoses[i] = new PoseData {
                        pose = a, rootPosition = a.position, rootOffset = float3.zero, rootSnapStrength = 0.5f,
                    };
                    currentPoses[i] = new PoseData {
                        pose = b, rootPosition = b.position, rootOffset = new float3(0.01f), rootSnapStrength = 0.5f,
                    };
                    rootPositions[i] = a.position;
                }

                var inputJob = new JiggleJobInputInterpolation {
                    previousInputs = previousInputs, currentInputs = currentInputs,
                    outputInterpolatedPoses = simulateInputs,
                    previousTimeStamp = 0.0, timeStamp = 0.02, currentTime = 0.013,
                };
                var outputJob = new JiggleJobInterpolation {
                    previousPoses = previousPoses, currentPoses = currentPoses,
                    realRootPositions = rootPositions, outputInterpolatedPoses = interpolated,
                    previousTimeStamp = 0.0, timeStamp = 0.02, currentTime = 0.013,
                };
                outputJob.SetFixedDeltaTime(0.02f);

                var inputMs = MedianMilliseconds(() => inputJob.Run(count));
                var outputMs = MedianMilliseconds(() => outputJob.Run(count));
                report.AppendLine($"{count,6} | {inputMs,15:F4} | {outputMs,16:F4}");
            } finally {
                previousInputs.Dispose();
                currentInputs.Dispose();
                simulateInputs.Dispose();
                previousPoses.Dispose();
                currentPoses.Dispose();
                rootPositions.Dispose();
                interpolated.Dispose();
            }
        }
        Report(report);
    }

    private static JiggleTransform RandomPose(ref Random random) {
        return new JiggleTransform {
            isVirtual = false,
            position = random.NextFloat3(new float3(-10f), new float3(10f)),
            rotation = random.NextQuaternionRotation(),
            scale = new float3(1f),
        };
    }

    // ------------------------------------------------------------------------- transform write

    [Test]
    public void TransformWrite_Throughput() {
        var report = new StringBuilder();
        report.AppendLine("=== JiggleJobTransformWrite over real transform hierarchies ===");
        report.AppendLine("bones | chains | schedule+complete ms | us per bone");
        report.AppendLine("------+--------+----------------------+------------");

        foreach (var chainCount in new[] { 256, 1024 }) {
            const int BonesPerChain = 8;
            var scene = new JiggleBoneScene();
            var count = chainCount * BonesPerChain;
            var access = new TransformAccessArray(count);
            var interpolated = new NativeArray<JiggleTransform>(count, Allocator.Persistent);
            var previousLocal = new NativeArray<JiggleTransform>(count, Allocator.Persistent);
            try {
                var random = new Random(0x5F3759DF);
                for (int c = 0; c < chainCount; c++) {
                    var root = scene.Chain(BonesPerChain, 0.08f, $"w{c}b");
                    var bones = JiggleBoneScene.Descend(root, BonesPerChain);
                    for (int b = 0; b < BonesPerChain; b++) {
                        access.Add(bones[b]);
                        var index = c * BonesPerChain + b;
                        interpolated[index] = RandomPose(ref random);
                        previousLocal[index] = JiggleTestFactory.Pose(float3.zero);
                    }
                }

                var job = new JiggleJobTransformWrite {
                    inputInterpolatedPoses = interpolated, previousLocalPoses = previousLocal,
                };
                var ms = MedianMilliseconds(() => job.Schedule(access).Complete());
                report.AppendLine($"{count,5} | {chainCount,6} | {ms,20:F4} | {ms * 1000.0 / count,11:F4}");
            } finally {
                interpolated.Dispose();
                previousLocal.Dispose();
                access.Dispose();
                scene.Dispose();
            }
        }
        Report(report);
    }

    /// <summary>
    /// A probe, not shipping code: JiggleJobBulkTransformReadReset with the lossy-scale fetch removed
    /// and nothing else changed. That line pulls a whole localToWorldMatrix across and takes three
    /// square roots for a value the solver only ever uses as an average, so this prices it before
    /// anyone tries to be clever about it.
    /// </summary>
    [BurstCompile]
    private struct ReadResetWithoutScaleProbe : IJobParallelForTransform {
        public NativeArray<JiggleTransform> restPoseTransforms;
        [ReadOnly] public NativeArray<JiggleTransform> previousLocalTransforms;
        public NativeArray<JiggleTransform> simulateInputPoses;

        public void Execute(int index, TransformAccess transform) {
            if (!transform.isValid) {
                return;
            }
            var localTransform = previousLocalTransforms[index];
            if (!localTransform.isVirtual) {
                transform.GetLocalPositionAndRotation(out var localPosition, out var localRotation);
                var restTransform = restPoseTransforms[index];
                var positionChanged = (Vector3)localTransform.position != localPosition;
                var rotationChanged = (Quaternion)localTransform.rotation != localRotation;
                if (!positionChanged && !rotationChanged) {
                    transform.SetLocalPositionAndRotation(restTransform.position, restTransform.rotation);
                } else {
                    if (positionChanged) restTransform.position = localPosition;
                    if (rotationChanged) restTransform.rotation = localRotation;
                    restPoseTransforms[index] = restTransform;
                }
            }

            var jiggleTransform = simulateInputPoses[index];
            if (jiggleTransform.isVirtual) {
                return;
            }
            transform.GetPositionAndRotation(out var position, out var rotation);
            jiggleTransform.position = position;
            jiggleTransform.rotation = rotation;
            simulateInputPoses[index] = jiggleTransform;
        }
    }

    /// <summary>
    /// The read-reset job, which is the other half of the per-frame transform traffic: it reads each
    /// bone's local pose, decides whether the animator moved it, restores the rest pose where it did
    /// not, and reads the world pose back out for the simulate job. Split so the two halves — the
    /// reset decision and the world read — can be told apart.
    /// </summary>
    [Test]
    public void BulkTransformReadReset_Throughput() {
        var report = new StringBuilder();
        report.AppendLine("=== JiggleJobBulkTransformReadReset over real transform hierarchies ===");
        report.AppendLine("bones | chains | all virtual ms | real bones ms | no lossyScale ms | us per bone");
        report.AppendLine("------+--------+----------------+---------------+------------------+------------");

        foreach (var chainCount in new[] { 256, 1024 }) {
            const int BonesPerChain = 8;
            var scene = new JiggleBoneScene();
            var count = chainCount * BonesPerChain;
            var access = new TransformAccessArray(count);
            var restPoses = new NativeArray<JiggleTransform>(count, Allocator.Persistent);
            var previousLocal = new NativeArray<JiggleTransform>(count, Allocator.Persistent);
            var simulateInputs = new NativeArray<JiggleTransform>(count, Allocator.Persistent);
            try {
                for (int c = 0; c < chainCount; c++) {
                    var root = scene.Chain(BonesPerChain, 0.08f, $"r{c}b");
                    var bones = JiggleBoneScene.Descend(root, BonesPerChain);
                    for (int b = 0; b < BonesPerChain; b++) {
                        access.Add(bones[b]);
                        var index = c * BonesPerChain + b;
                        bones[b].GetLocalPositionAndRotation(out var localPosition, out var localRotation);
                        var pose = new JiggleTransform {
                            isVirtual = false, position = localPosition, rotation = localRotation,
                            scale = new float3(1f),
                        };
                        restPoses[index] = pose;
                        previousLocal[index] = pose;
                        simulateInputs[index] = pose;
                    }
                }

                var job = new JiggleJobBulkTransformReadReset {
                    restPoseTransforms = restPoses, previousLocalTransforms = previousLocal,
                    simulateInputPoses = simulateInputs,
                };
                var realMs = MedianMilliseconds(() => job.Schedule(access).Complete());

                var probe = new ReadResetWithoutScaleProbe {
                    restPoseTransforms = restPoses, previousLocalTransforms = previousLocal,
                    simulateInputPoses = simulateInputs,
                };
                var noScaleMs = MedianMilliseconds(() => probe.Schedule(access).Complete());

                // Same job, every slot virtual: both halves early out, so what is left is the
                // per-bone dispatch floor the job cannot go below.
                for (int i = 0; i < count; i++) {
                    var virtualPose = previousLocal[i];
                    virtualPose.isVirtual = true;
                    previousLocal[i] = virtualPose;
                    var virtualInput = simulateInputs[i];
                    virtualInput.isVirtual = true;
                    simulateInputs[i] = virtualInput;
                }
                var virtualMs = MedianMilliseconds(() => job.Schedule(access).Complete());

                report.AppendLine($"{count,5} | {chainCount,6} | {virtualMs,14:F4} | {realMs,13:F4} | " +
                    $"{noScaleMs,16:F4} | {realMs * 1000.0 / count,11:F4}");
            } finally {
                restPoses.Dispose();
                previousLocal.Dispose();
                simulateInputs.Dispose();
                access.Dispose();
                scene.Dispose();
            }
        }
        Report(report);
    }


    /// <summary>
    /// Where the cost that scales with crowd size actually lives. Both columns do the same per-tree
    /// work; only the spread column grows the collider array and the cell map. If the stacked column
    /// stays flat while the spread column climbs, the misses are in the collision lookups and
    /// compacting them is worth doing. If both climb together, they are in the per-tree buffers and
    /// the collider layout is not the problem.
    /// </summary>
    [Test]
    public void Simulate_LocalityProbe() {
        var report = new StringBuilder();
        report.AppendLine("=== where does per-point cost grow: collider field, or per-tree buffers? ===");
        report.AppendLine("trees | spread us/point | stacked us/point | spread growth | stacked growth");
        report.AppendLine("------+-----------------+------------------+---------------+---------------");

        double spreadBase = 0, stackedBase = 0;
        foreach (var treeCount in new[] { 256, 2048, 8192 }) {
            double spread, stacked;
            using (var crowd = Crowd.Build(treeCount, elasticitySoften: 0f)) {
                var job = crowd.SimulateJob();
                spread = MedianMilliseconds(() => job.Run(treeCount), crowd.Reset)
                    * 1000.0 / (treeCount * crowd.pointsPerTree);
            }
            using (var crowd = Crowd.BuildStacked(treeCount, elasticitySoften: 0f)) {
                var job = crowd.SimulateJob();
                stacked = MedianMilliseconds(() => job.Run(treeCount), crowd.Reset)
                    * 1000.0 / (treeCount * crowd.pointsPerTree);
            }
            if (spreadBase == 0) {
                spreadBase = spread;
                stackedBase = stacked;
            }
            report.AppendLine($"{treeCount,5} | {spread,15:F4} | {stacked,16:F4} | " +
                $"{spread / spreadBase,12:F2}x | {stacked / stackedBase,13:F2}x");
        }
        Report(report);
    }


    /// <summary>
    /// Follow-up to the locality probe: is the collider cost fixable by layout, or is it just bytes
    /// that have to be read? Both columns read the same eight colliders per tree and run the same
    /// tests; only their addresses differ. If interleaving is materially worse than adjacent, packing
    /// each tree's candidates together is worth doing. If the two match, the array is already as
    /// compact as it can be and the only lever left is touching fewer bytes per collider.
    /// </summary>
    [Test]
    public void Simulate_ColliderLayoutProbe() {
        var report = new StringBuilder();
        report.AppendLine("=== collider layout: adjacent per tree vs interleaved ===");
        report.AppendLine("trees | adjacent us/point | interleaved us/point | penalty");
        report.AppendLine("------+-------------------+----------------------+--------");

        foreach (var treeCount in new[] { 2048, 8192 }) {
            double adjacent, interleaved;
            using (var crowd = Crowd.Build(treeCount, elasticitySoften: 0f)) {
                var job = crowd.SimulateJob();
                adjacent = MedianMilliseconds(() => job.Run(treeCount), crowd.Reset)
                    * 1000.0 / (treeCount * crowd.pointsPerTree);
            }
            using (var crowd = Crowd.Build(treeCount, elasticitySoften: 0f, interleaveColliders: true)) {
                var job = crowd.SimulateJob();
                interleaved = MedianMilliseconds(() => job.Run(treeCount), crowd.Reset)
                    * 1000.0 / (treeCount * crowd.pointsPerTree);
            }
            report.AppendLine($"{treeCount,5} | {adjacent,17:F4} | {interleaved,20:F4} | {interleaved / adjacent,6:F2}x");
        }
        Report(report);
    }


    /// <summary>
    /// What it costs to *schedule* a transform job, on the main thread, before any work happens.
    /// This is the number behind "culling the avatars to zero metres did not help": collider culling
    /// leaves every bone enrolled, and the pose chain schedules three TransformAccessArray jobs plus
    /// a parallel job every frame whatever the colliders are doing. If schedule cost tracks the
    /// enrolled transform count, the only lever is enrolling fewer transforms — not cheaper jobs.
    ///
    /// Also splits a stable array from one whose slots were rewritten since the last schedule, since
    /// the in-place commit path writes slots and Unity may rebuild its batch layout when it sees a
    /// modified array.
    /// </summary>
    [Test]
    public void TransformJob_ScheduleCost() {
        var report = new StringBuilder();
        report.AppendLine("=== main-thread cost of scheduling a transform job (before any work) ===");
        report.AppendLine("bones | chains | schedule ms | after slot writes ms | schedule+complete ms");
        report.AppendLine("------+--------+-------------+----------------------+---------------------");

        foreach (var chainCount in new[] { 256, 1024, 4096 }) {
            const int BonesPerChain = 8;
            var scene = new JiggleBoneScene();
            var count = chainCount * BonesPerChain;
            var access = new TransformAccessArray(count);
            var interpolated = new NativeArray<JiggleTransform>(count, Allocator.Persistent);
            var previousLocal = new NativeArray<JiggleTransform>(count, Allocator.Persistent);
            try {
                var random = new Random(0x5F3759DF);
                var bonesFlat = new Transform[count];
                for (int c = 0; c < chainCount; c++) {
                    var root = scene.Chain(BonesPerChain, 0.08f, $"s{c}b");
                    var bones = JiggleBoneScene.Descend(root, BonesPerChain);
                    for (int b = 0; b < BonesPerChain; b++) {
                        var index = c * BonesPerChain + b;
                        access.Add(bones[b]);
                        bonesFlat[index] = bones[b];
                        interpolated[index] = RandomPose(ref random);
                        previousLocal[index] = JiggleTestFactory.Pose(float3.zero);
                    }
                }

                var job = new JiggleJobTransformWrite {
                    inputInterpolatedPoses = interpolated, previousLocalPoses = previousLocal,
                };

                // Schedule only: the handle is completed outside the timed region.
                var stopwatch = new Stopwatch();
                var scheduleSamples = new double[MeasuredIterations];
                for (int i = 0; i < WarmupIterations; i++) {
                    job.Schedule(access).Complete();
                }
                for (int i = 0; i < MeasuredIterations; i++) {
                    stopwatch.Restart();
                    var handle = job.Schedule(access);
                    stopwatch.Stop();
                    handle.Complete();
                    scheduleSamples[i] = stopwatch.Elapsed.TotalMilliseconds;
                }
                Array.Sort(scheduleSamples);
                var scheduleMs = scheduleSamples[MeasuredIterations / 2];

                // Same, but a handful of slots are rewritten first, the way an in-place commit does.
                var dirtySamples = new double[MeasuredIterations];
                for (int i = 0; i < MeasuredIterations; i++) {
                    for (int w = 0; w < 8; w++) {
                        var slot = (i * 8 + w) % count;
                        access[slot] = bonesFlat[slot];
                    }
                    stopwatch.Restart();
                    var handle = job.Schedule(access);
                    stopwatch.Stop();
                    handle.Complete();
                    dirtySamples[i] = stopwatch.Elapsed.TotalMilliseconds;
                }
                Array.Sort(dirtySamples);
                var dirtyMs = dirtySamples[MeasuredIterations / 2];

                var totalMs = MedianMilliseconds(() => job.Schedule(access).Complete());
                report.AppendLine($"{count,5} | {chainCount,6} | {scheduleMs,11:F4} | {dirtyMs,20:F4} | {totalMs,20:F4}");
            } finally {
                interpolated.Dispose();
                previousLocal.Dispose();
                access.Dispose();
                scene.Dispose();
            }
        }
        Report(report);
    }

    // ------------------------------------------------------------------------------ equivalence

    /// <summary>
    /// Runs a fixed scene through the simulate and interpolation jobs for a fixed number of steps and
    /// writes every resulting float to <see cref="DumpPath"/>, with a checksum in the test output for
    /// the case where only the log survives. Diff the file across a change: these jobs are supposed to
    /// be restructured, never re-derived, so the correct diff is zero bytes.
    /// </summary>
    [Test]
    public void DumpEquivalenceState() {
        const int TreeCount = 128;
        const int Steps = 60;

        using var crowd = Crowd.Build(TreeCount, bonesPerTree: 8, seed: 0xA5A5A5A5);
        crowd.Reset();

        var job = crowd.SimulateJob(substeps: 2);
        var pointCount = TreeCount * crowd.pointsPerTree;
        var interpolated = new NativeArray<JiggleTransform>(pointCount, Allocator.Persistent);
        var rootPositions = new NativeArray<float3>(pointCount, Allocator.Persistent);
        var previousPoses = new NativeArray<PoseData>(pointCount, Allocator.Persistent);
        var simulateInputs = new NativeArray<JiggleTransform>(pointCount, Allocator.Persistent);
        var inputsPrevious = new NativeArray<JiggleTransform>(pointCount, Allocator.Persistent);

        try {
            crowd.inputPoses.CopyTo(inputsPrevious);
            for (int i = 0; i < pointCount; i++) {
                rootPositions[i] = crowd.outputPoses[i].rootPosition;
            }

            var values = new List<float>(pointCount * 32);
            for (int step = 0; step < Steps; step++) {
                // Input interpolation feeds the simulate job the same way the frame does.
                var inputJob = new JiggleJobInputInterpolation {
                    previousInputs = inputsPrevious, currentInputs = crowd.inputPoses,
                    outputInterpolatedPoses = simulateInputs,
                    previousTimeStamp = step * 0.02, timeStamp = (step + 1) * 0.02,
                    currentTime = step * 0.02 + 0.013,
                };
                inputJob.Run(pointCount);

                crowd.outputPoses.CopyTo(previousPoses);
                job.timeStamp = (step + 1) * 0.02;
                job.inputPoses = simulateInputs;
                job.Run(TreeCount);

                var interpolationJob = new JiggleJobInterpolation {
                    previousPoses = previousPoses, currentPoses = crowd.outputPoses,
                    realRootPositions = rootPositions, outputInterpolatedPoses = interpolated,
                    previousTimeStamp = step * 0.02, timeStamp = (step + 1) * 0.02,
                    currentTime = step * 0.02 + 0.013,
                };
                interpolationJob.SetFixedDeltaTime(0.02f);
                interpolationJob.Run(pointCount);
            }

            for (int i = 0; i < pointCount; i++) {
                var pose = interpolated[i];
                values.Add(pose.position.x); values.Add(pose.position.y); values.Add(pose.position.z);
                values.Add(pose.rotation.value.x); values.Add(pose.rotation.value.y);
                values.Add(pose.rotation.value.z); values.Add(pose.rotation.value.w);

                var data = crowd.outputPoses[i];
                values.Add(data.rootPosition.x); values.Add(data.rootPosition.y); values.Add(data.rootPosition.z);
                values.Add(data.rootOffset.x); values.Add(data.rootOffset.y); values.Add(data.rootOffset.z);
                values.Add(data.rootSnapStrength);
            }
            for (int t = 0; t < TreeCount; t++) {
                var tree = crowd.trees[t];
                for (int p = 0; p < tree.pointCount; p++) {
                    var point = tree.points[p];
                    values.Add(point.position.x); values.Add(point.position.y); values.Add(point.position.z);
                    values.Add(point.lastPosition.x); values.Add(point.lastPosition.y); values.Add(point.lastPosition.z);
                    values.Add(point.workingPosition.x); values.Add(point.workingPosition.y); values.Add(point.workingPosition.z);
                    values.Add(point.desiredLengthToParent);
                    values.Add(point.worldRadius);
                }
            }

            var bytes = new byte[values.Count * sizeof(float)];
            Buffer.BlockCopy(values.ToArray(), 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(DumpPath, bytes);

            // FNV-1a over the raw bytes, so a mismatch is visible in the log alone.
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < bytes.Length; i++) {
                hash ^= bytes[i];
                hash *= 1099511628211UL;
            }

            var report = new StringBuilder();
            report.AppendLine("=== equivalence dump ===");
            report.AppendLine($"path:     {DumpPath}");
            report.AppendLine($"floats:   {values.Count} over {TreeCount} trees x {crowd.pointsPerTree} points, {Steps} steps");
            report.AppendLine($"checksum: {hash:X16}");
            Report(report);

            // A run that diverges into NaN would dump a stable checksum of garbage, so assert the
            // scene is still alive before trusting any of it.
            for (int i = 0; i < values.Count; i++) {
                Assert.IsTrue(math.isfinite(values[i]), $"value {i} in the dump is not finite");
            }
        } finally {
            interpolated.Dispose();
            rootPositions.Dispose();
            previousPoses.Dispose();
            simulateInputs.Dispose();
            inputsPrevious.Dispose();
        }
    }
}

}
