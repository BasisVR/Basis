using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Jobs;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GatorDragonGames.JigglePhysics.Tests {

/// <summary>
/// Timing harness for the main thread commit — the JiggleJobs.Simulate.Commit sample, which is
/// JiggleMemoryBus.CommitTrees() plus CommitColliders().
///
/// The interesting question is not what a commit costs once, it is what a commit costs on a frame
/// where nothing structural happened except the usual trickle: the collider distance LOD adding and
/// dropping a handful of colliders, and the tree regeneration budget rebuilding its four trees. That
/// is the steady state, and both commits run every frame in it. So every table here sweeps the
/// standing set size against a fixed per-frame churn: if the cost tracks the standing set rather
/// than the churn, the commit is paying for work it did not do.
///
/// Editor timings, so treat them as relative.
/// </summary>
[TestFixture]
[Category("Performance")]
[Explicit("Timing benchmark. Run it deliberately, it is too slow for the normal suite.")]
internal class JiggleCommitPerformanceSimulation {
    private const int WarmupIterations = 8;
    private const int MeasuredIterations = 33;

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

    // ------------------------------------------------------------ TransformAccessArray primitives

    /// <summary>
    /// The in place commit path writes changed slots straight into the front access array, one
    /// indexer store per slot, on the assumption that a store is O(1). If it is not — if Unity
    /// rebuilds its hierarchy grouping per store — then in place is quietly O(changed * length) and
    /// is worse than the sliced rebuild it replaced. This table answers that: per op cost across a
    /// 32x span of array lengths, flat or not.
    /// </summary>
    [Test]
    public void TransformAccessArray_PerOpCostByLength() {
        var report = new StringBuilder();
        report.AppendLine("=== TransformAccessArray per-op cost vs array length ===");
        report.AppendLine("length |   set us |   add us | dispose+new ms");
        report.AppendLine("-------+----------+----------+---------------");

        var scene = new JiggleBoneScene();
        try {
            const int DistinctTransforms = 256;
            var pool = new Transform[DistinctTransforms];
            for (int i = 0; i < DistinctTransforms; i++) {
                pool[i] = scene.Spawn($"taa{i}");
            }

            foreach (var length in new[] { 1024, 4096, 16384, 65536 }) {
                var array = new TransformAccessArray(length);
                for (int i = 0; i < length; i++) {
                    array.Add(pool[i % DistinctTransforms]);
                }

                const int OpBatch = 512;
                var setBatch = MedianMilliseconds(() => {
                    for (int i = 0; i < OpBatch; i++) {
                        array[(i * 7919) % length] = pool[i % DistinctTransforms];
                    }
                });

                // Adds grow the array, so they are measured on a scratch copy that is thrown away
                // and rebuilt to `length` between samples rather than on the array under test.
                var addScratch = new TransformAccessArray(length + OpBatch);
                for (int i = 0; i < length; i++) {
                    addScratch.Add(pool[i % DistinctTransforms]);
                }
                var addBatch = MedianMilliseconds(() => {
                    for (int i = 0; i < OpBatch; i++) {
                        addScratch.Add(pool[i % DistinctTransforms]);
                    }
                    for (int i = 0; i < OpBatch; i++) {
                        addScratch.RemoveAtSwapBack(addScratch.length - 1);
                    }
                });
                addScratch.Dispose();

                var capacity = array.capacity;
                var recreate = MedianMilliseconds(() => {
                    array.Dispose();
                    array = new TransformAccessArray(capacity);
                });

                report.AppendLine(
                    $"{length,6} | {setBatch * 1000.0 / OpBatch,8:F3} | {addBatch * 1000.0 / OpBatch,8:F3} | {recreate,14:F4}");
                array.Dispose();
            }
        } finally {
            scene.Dispose();
        }
        Report(report);
    }

    /// <summary>
    /// The access array is grouped by hierarchy root internally, so the cost of a single slot store
    /// may track how many distinct roots are enrolled rather than how many entries there are. That
    /// distinction decides the fix: the bus enrolls one hierarchy per rig, so a world of many small
    /// rigs is the worst case for a per-slot store and nothing like a benchmark that reuses a
    /// handful of transforms.
    /// </summary>
    [Test]
    public void TransformAccessArray_SetCostByDistinctHierarchies() {
        var report = new StringBuilder();
        report.AppendLine("=== per-slot store cost vs distinct hierarchies (16384 entries) ===");
        report.AppendLine("distinct roots |   set us");
        report.AppendLine("---------------+---------");

        const int Length = 16384;
        var scene = new JiggleBoneScene();
        try {
            foreach (var distinct in new[] { 1, 64, 1024, 16384 }) {
                var pool = new Transform[distinct];
                for (int i = 0; i < distinct; i++) {
                    pool[i] = scene.Spawn($"h{distinct}_{i}");
                }
                var array = new TransformAccessArray(Length);
                for (int i = 0; i < Length; i++) {
                    array.Add(pool[i % distinct]);
                }

                const int OpBatch = 256;
                var batch = MedianMilliseconds(() => {
                    for (int i = 0; i < OpBatch; i++) {
                        array[(i * 7919) % Length] = pool[i % distinct];
                    }
                });
                array.Dispose();
                report.AppendLine($"{distinct,14} | {batch * 1000.0 / OpBatch,8:F3}");
            }
        } finally {
            scene.Dispose();
        }
        Report(report);
    }

    /// <summary>
    /// Whole-array rebuild costs, which is what the alternative to per-slot stores looks like:
    /// filling a fresh array one Add at a time (what the sliced rebuild does) against handing the
    /// whole list over in one SetTransforms call.
    /// </summary>
    [Test]
    public void TransformAccessArray_BulkBuildCost() {
        var report = new StringBuilder();
        report.AppendLine("=== whole-array build: N x Add vs one SetTransforms (distinct transforms) ===");
        report.AppendLine("length | build by Add ms | SetTransforms ms | speedup");
        report.AppendLine("-------+-----------------+------------------+--------");

        var scene = new JiggleBoneScene();
        try {
            foreach (var length in new[] { 2048, 8192, 32768 }) {
                var pool = new Transform[length];
                for (int i = 0; i < length; i++) {
                    pool[i] = scene.Spawn($"b{length}_{i}");
                }

                var array = new TransformAccessArray(length);
                var byAdd = MedianMilliseconds(() => {
                    array.Dispose();
                    array = new TransformAccessArray(length);
                    for (int i = 0; i < length; i++) {
                        array.Add(pool[i]);
                    }
                });
                var bulk = MedianMilliseconds(() => {
                    array.SetTransforms(pool);
                });
                array.Dispose();

                report.AppendLine($"{length,6} | {byAdd,15:F4} | {bulk,16:F4} | {byAdd / bulk,6:F1}x");
            }
        } finally {
            scene.Dispose();
        }
        Report(report);
    }

    // ------------------------------------------------------------------------ collider steady state

    private sealed class ColliderChurn : IDisposable {
        public JiggleBoneScene scene = new JiggleBoneScene();
        public JiggleMemoryBus bus = new JiggleMemoryBus();
        public readonly List<JiggleColliderSerializable> groupA = new List<JiggleColliderSerializable>();
        public readonly List<JiggleColliderSerializable> groupB = new List<JiggleColliderSerializable>();

        /// <summary>
        /// `standing` colliders that never move, plus two churn groups of `churn` each. The LOD pass
        /// swaps colliders in and out in both directions at once, so the ping pong drops one group
        /// while picking the other back up and the committed count stays flat.
        /// </summary>
        public static ColliderChurn Build(int standing, int churn) {
            var harness = new ColliderChurn();
            var pending = new List<JiggleColliderSerializable>(standing);
            for (int i = 0; i < standing; i++) {
                pending.Add(JiggleSceneFactory.SphereCollider(harness.scene.Spawn($"standing{i}")));
            }
            for (int i = 0; i < churn; i++) {
                harness.groupA.Add(JiggleSceneFactory.SphereCollider(harness.scene.Spawn($"churnA{i}")));
                harness.groupB.Add(JiggleSceneFactory.SphereCollider(harness.scene.Spawn($"churnB{i}")));
            }
            harness.bus.ScheduleAddBatch(pending);
            harness.bus.ScheduleAddBatch(harness.groupA);
            for (int i = 0; i < 64; i++) {
                harness.bus.CommitColliders();
            }
            return harness;
        }

        private bool aIsIn = true;

        /// <summary>One LOD trickle frame: drop the group that is in, pick up the group that is out.</summary>
        public void Frame() {
            var dropping = aIsIn ? groupA : groupB;
            var adding = aIsIn ? groupB : groupA;
            aIsIn = !aIsIn;
            bus.ScheduleRemoveBatch(dropping);
            bus.ScheduleAddBatch(adding);
            bus.CommitColliders();
        }

        public void Dispose() {
            bus?.Dispose();
            scene?.Dispose();
        }
    }

    /// <summary>
    /// The steady state cost of CommitColliders. Sweeps the standing collider set against a fixed
    /// churn: any growth down a column is cost the commit pays for colliders that did not change.
    /// </summary>
    [Test]
    public void CommitColliders_SteadyStateChurn() {
        var report = new StringBuilder();
        report.AppendLine("=== CommitColliders per frame, LOD trickle over a standing set ===");
        report.AppendLine("standing | churn/frame | commit ms | ms per churned collider");
        report.AppendLine("---------+-------------+-----------+------------------------");

        foreach (var standing in new[] { 512, 2048, 8192, 32768 }) {
            foreach (var churn in new[] { 8, 64 }) {
                using var harness = ColliderChurn.Build(standing, churn);
                JiggleRuntimeStatics.Boot();
                var perFrame = MedianMilliseconds(harness.Frame);
                report.AppendLine(
                    $"{standing,8} | {churn,11} | {perFrame,9:F4} | {perFrame / churn,22:F5}");
            }
        }
        Report(report);
    }

    /// <summary>
    /// The same standing sets with nothing queued at all, which is what an instance with the LOD
    /// pass idle pays. A commit with no pending work should be free; anything here is a scan the
    /// idle path runs unconditionally.
    /// </summary>
    [Test]
    public void CommitColliders_IdleCost() {
        var report = new StringBuilder();
        report.AppendLine("=== CommitColliders per frame with nothing queued ===");
        report.AppendLine("standing | idle commit ms");
        report.AppendLine("---------+---------------");

        foreach (var standing in new[] { 512, 2048, 8192, 32768 }) {
            using var harness = ColliderChurn.Build(standing, 0);
            var perFrame = MedianMilliseconds(() => harness.bus.CommitColliders());
            report.AppendLine($"{standing,8} | {perFrame,14:F5}");
        }
        Report(report);
    }

    // ---------------------------------------------------------------------------- tree steady state

    private sealed class TreeChurn : IDisposable {
        public JiggleBoneScene scene = new JiggleBoneScene();
        public JiggleMemoryBus bus = new JiggleMemoryBus();
        public readonly List<JiggleTree> trees = new List<JiggleTree>();
        private int cursor;
        private readonly int regenerationsPerFrame;

        public static TreeChurn Build(int treeCount, int bonesPerTree, int regenerationsPerFrame) {
            var harness = new TreeChurn(regenerationsPerFrame);
            for (int i = 0; i < treeCount; i++) {
                var root = harness.scene.Chain(bonesPerTree, 0.25f, $"t{i}b");
                var tree = JigglePhysics.CreateJiggleTree(JiggleSceneFactory.Rig(root), null);
                harness.trees.Add(tree);
                harness.bus.ScheduleAdd(tree);
            }
            // Capacity growth bails out of a commit, so this pumps until the whole set is resident.
            for (int i = 0; i < 128; i++) {
                harness.bus.CommitTrees();
            }
            Assert.AreEqual(treeCount, harness.bus.treeCount, "harness failed to commit its trees");
            return harness;
        }

        private TreeChurn(int regenerationsPerFrame) {
            this.regenerationsPerFrame = regenerationsPerFrame;
        }

        /// <summary>
        /// One regeneration budget's worth of rebuilt trees. A regenerated tree arrives as a remove
        /// paired with an add of the same rootID, which is what JiggleTree.Set queues.
        /// </summary>
        public void Frame() {
            for (int i = 0; i < regenerationsPerFrame; i++) {
                var tree = trees[cursor];
                cursor = (cursor + 1) % trees.Count;
                bus.ScheduleRemove(tree);
                bus.ScheduleAdd(tree);
            }
            bus.CommitTrees();
        }

        public void Dispose() {
            bus?.Dispose();
            for (int i = 0; i < trees.Count; i++) {
                JiggleSceneFactory.FreeStruct(trees[i]);
            }
            scene?.Dispose();
        }
    }

    /// <summary>
    /// The steady state cost of CommitTrees: the regeneration budget is four trees a frame no matter
    /// how many rigs are resident, so this should be flat across the sweep.
    /// </summary>
    [Test]
    public void CommitTrees_SteadyStateRegeneration() {
        var report = new StringBuilder();
        report.AppendLine("=== CommitTrees per frame, regeneration budget over a resident set ===");
        report.AppendLine("resident trees | bones | regen/frame | commit ms");
        report.AppendLine("---------------+-------+-------------+----------");

        var costs = new List<double>();
        foreach (var treeCount in new[] { 128, 512, 2048 }) {
            using var harness = TreeChurn.Build(treeCount, 6, 4);
            JiggleRuntimeStatics.Boot();
            var perFrame = MedianMilliseconds(harness.Frame);
            costs.Add(perFrame);
            report.AppendLine($"{treeCount,14} | {6,5} | {4,11} | {perFrame,8:F4}");
        }
        report.AppendLine();
        report.AppendLine($"16x the resident set for the same churn cost {costs[2] / costs[0]:F1}x more.");
        report.AppendLine("Flat is the design: a commit should cost what changed, not what is resident.");
        Report(report);

        // The historical cause of a regression here is diagnostics creeping back into
        // FinishTreeCommit: a walk of every tree, every point and every transform, which measured
        // 3.5ms at 2048 rigs against 0.10ms without it. It came back twice via the JIGGLE_VALIDATE
        // scripting define before that code was deleted outright.
        Assert.Less(costs[2], costs[0] * 8.0,
            "CommitTrees is scaling with the resident set rather than with what changed. " +
            "First suspect: a per-frame walk of everything resident inside FinishTreeCommit.");
    }

    [Test]
    public void CommitTrees_IdleCost() {
        var report = new StringBuilder();
        report.AppendLine("=== CommitTrees per frame with nothing queued ===");
        report.AppendLine("resident trees | idle commit ms");
        report.AppendLine("---------------+---------------");

        foreach (var treeCount in new[] { 128, 512, 2048 }) {
            using var harness = TreeChurn.Build(treeCount, 6, 0);
            var perFrame = MedianMilliseconds(() => harness.bus.CommitTrees());
            report.AppendLine($"{treeCount,14} | {perFrame,14:F5}");
        }
        Report(report);
    }

    // ------------------------------------------------------------------------------ the dummy pool

    /// <summary>
    /// PreRemoveTree parks a pooled dummy in every slot a departing rig vacated, so a removal pays
    /// for whatever the pool has to create. The pool is keyed by slot index and is sparse: it creates
    /// one GameObject per slot actually asked for. It used to be a dense list, where asking for slot
    /// N created every GameObject below N as well — a spike proportional to the arena rather than to
    /// the rig, inside the commit. This prices both, so the difference is on the record.
    /// </summary>
    [Test]
    public void DummyTransformPool_GrowthCost() {
        var report = new StringBuilder();
        report.AppendLine("=== dummy transform pool growth (inside PreRemoveTree / collider retire) ===");

        JiggleRuntimeStatics.Boot();
        var stopwatch = new Stopwatch();
        const int BonesPerRig = 8;
        var slot = 1 << 20;
        foreach (var arena in new[] { 1024, 4096, 16384 }) {
            // What a rig removal actually asks for: one dummy per vacated slot, at a slot index that
            // says nothing about how big the arena is. Fresh indices every pass so nothing is warm.
            stopwatch.Restart();
            for (int i = 0; i < BonesPerRig; i++) {
                JiggleMemoryBus.GetDummyTransform(slot + i);
            }
            stopwatch.Stop();
            slot += BonesPerRig;
            var sparse = stopwatch.Elapsed.TotalMilliseconds;

            // What the dense pool would have cost for that same removal: every GameObject below the
            // slot too. Built standalone rather than through the bus, which no longer does this.
            stopwatch.Restart();
            var dense = new List<Transform>(arena);
            for (int i = 0; i < arena; i++) {
                dense.Add(new GameObject($"denseDummy{i}") { hideFlags = HideFlags.HideAndDontSave }.transform);
            }
            stopwatch.Stop();
            var denseCost = stopwatch.Elapsed.TotalMilliseconds;
            for (int i = 0; i < dense.Count; i++) {
                Object.DestroyImmediate(dense[i].gameObject);
            }

            report.AppendLine(
                $"rig of {BonesPerRig} bones removed at a slot inside a {arena,6}-slot arena: " +
                $"{sparse,7:F3} ms sparse vs {denseCost,8:F3} ms dense");
        }
        report.AppendLine("(sparse is per vacated slot and flat; dense tracked the arena, once per new high slot)");
        Report(report);
    }

    // -------------------------------------------------------------------------------- the fragmenter

    /// <summary>
    /// Every collider add and remove goes through the fragmenter, whose Free is a linear walk of the
    /// free list plus an insert, times an editor-only double free check that walks it again. Collider
    /// churn is what fragments that list, so this sweeps free-list length against op cost.
    /// </summary>
    [Test]
    public void MemoryFragmenter_OpCostByFragmentation() {
        var report = new StringBuilder();
        report.AppendLine("=== fragmenter op cost vs free-list length ===");
        report.AppendLine("capacity | holes | alloc+free us");
        report.AppendLine("---------+-------+--------------");

        foreach (var capacity in new[] { 2048, 8192, 32768 }) {
            foreach (var holeStride in new[] { 2, 8 }) {
                var fragmenter = new JiggleMemoryFragmenter(capacity);
                // Allocate everything, then free every `holeStride`-th slot to build the free list.
                for (int i = 0; i < capacity; i++) {
                    fragmenter.TryAllocate(1, out _);
                }
                var holes = 0;
                for (int i = 0; i < capacity; i += holeStride) {
                    fragmenter.Free(i, 1);
                    holes++;
                }

                // The arena is nearly full, so an allocation can legitimately fail; freeing the -1 it
                // hands back would trip the double-free guard on a benchmark bug rather than a real one.
                const int OpBatch = 64;
                var scratch = new int[OpBatch];
                var cost = MedianMilliseconds(() => {
                    for (int i = 0; i < OpBatch; i++) {
                        if (!fragmenter.TryAllocate(1, out scratch[i])) scratch[i] = -1;
                    }
                    for (int i = 0; i < OpBatch; i++) {
                        if (scratch[i] >= 0) fragmenter.Free(scratch[i], 1);
                    }
                });
                var freeCost = MedianMilliseconds(() => {
                    for (int i = 0; i < OpBatch; i++) {
                        if (!fragmenter.TryAllocate(1, out scratch[i])) scratch[i] = -1;
                    }
                    for (int i = 0; i < OpBatch; i++) {
                        if (scratch[i] >= 0) fragmenter.Free(scratch[i], 1);
                    }
                });
                report.AppendLine(
                    $"{capacity,8} | {holes,5} | {Math.Min(cost, freeCost) * 1000.0 / OpBatch,13:F3}");
            }
        }
        Report(report);
    }
}

}
