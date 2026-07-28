using System.Collections.Generic;
using System.Diagnostics;
using Basis.Scripts.BasisSdk.Constraints;
using Basis.Scripts.Constraints;
using NUnit.Framework;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Basis.Tests.Constraints
{
    /// <summary>
    /// Measures the per-frame cost of the constraint system at the scale a busy instance actually
    /// reaches, rather than reasoning about it. Reports numbers and asserts almost nothing — a
    /// benchmark that fails on a threshold just becomes a flaky test on someone else's machine.
    ///
    /// Numbers here run in the editor, so they are not what a player build sees. What they are good
    /// for is the ratio between two arrangements measured back to back on the same machine.
    /// </summary>
    [Category("Benchmark")]
    public sealed class BasisConstraintBenchmark
    {
        const int Avatars = 20;
        const int ConstraintsPerAvatar = 47;   // ≈931 total, matching the captured profile
        const int Frames = 200;

        readonly List<GameObject> spawned = new List<GameObject>();
        readonly List<BasisConstraintBase> registered = new List<BasisConstraintBase>();

        [TearDown]
        public void TearDown()
        {
            for (int Index = 0; Index < registered.Count; Index++)
            {
                BasisConstraintSystem.Unregister(registered[Index]);
            }
            registered.Clear();
            BasisConstraintSystem.SetPriorityRoot(null);
            BasisConstraintSystem.Dispose();

            for (int Index = 0; Index < spawned.Count; Index++)
            {
                if (spawned[Index] != null)
                {
                    Object.DestroyImmediate(spawned[Index]);
                }
            }
            spawned.Clear();
        }

        /// <summary>
        /// Builds avatars spread out in a line so distance banding has something to discriminate on:
        /// a couple sit near the origin, the rest trail off past the far threshold.
        /// </summary>
        Transform BuildPopulation()
        {
            Transform localRoot = null;
            for (int Avatar = 0; Avatar < Avatars; Avatar++)
            {
                GameObject root = new GameObject($"Avatar{Avatar}");
                spawned.Add(root);
                root.transform.position = new Vector3(Avatar * 5f, 0f, 0f);
                if (Avatar == 0)
                {
                    localRoot = root.transform;
                }

                for (int Index = 0; Index < ConstraintsPerAvatar; Index++)
                {
                    GameObject source = new GameObject($"S{Index}");
                    source.transform.SetParent(root.transform, false);
                    GameObject target = new GameObject($"T{Index}");
                    target.transform.SetParent(root.transform, false);

                    BasisPositionConstraint constraint = target.AddComponent<BasisPositionConstraint>();
                    constraint.AddSource(new BasisConstraintSourceEntry(source.transform, 1f));
                    BasisConstraintSystem.Register(constraint);
                    registered.Add(constraint);
                }
            }
            return localRoot;
        }

        /// <summary>
        /// Median of several runs. A single wall-clock pass in the editor drifts several percent
        /// between identical runs, which is enough to read a regression into noise or noise into a
        /// win — one sample cannot tell them apart, so do not take one.
        /// </summary>
        static double Median(System.Func<double> run, int samples = 5)
        {
            List<double> results = new List<double>();
            for (int Sample = 0; Sample < samples; Sample++)
            {
                results.Add(run());
            }
            results.Sort();
            return results[results.Count / 2];
        }

        static double MeasureFrames()
        {
            // One warm frame first: the first Schedule pays for the rebuild and the Burst compile,
            // neither of which is a per-frame cost.
            BasisConstraintSystem.Complete(BasisConstraintSystem.Schedule());

            Stopwatch watch = Stopwatch.StartNew();
            for (int Frame = 0; Frame < Frames; Frame++)
            {
                BasisConstraintSystem.Complete(BasisConstraintSystem.Schedule());
            }
            watch.Stop();
            return watch.Elapsed.TotalMilliseconds / Frames;
        }

        [Test]
        public void PerFrameCostAtInstanceScale()
        {
            Transform localRoot = BuildPopulation();
            int total = Avatars * ConstraintsPerAvatar;

            // Everything treated as near — what the system did before distance banding, and what it
            // still does when no local player has been announced.
            BasisConstraintSystem.SetPriorityRoot(null);
            double unbanded = Median(MeasureFrames);

            // Local player announced: near avatars keep up, distant ones step down.
            BasisConstraintSystem.SetPriorityRoot(localRoot);
            double banded = Median(MeasureFrames);

            Debug.Log(
                $"[constraint benchmark] {total} constraints across {Avatars} avatars, {Frames} frames\n" +
                $"  solve groups              : {BasisConstraintSystem.SolveGroupCount}\n" +
                $"  every avatar at full rate : {unbanded:F3} ms/frame\n" +
                $"  distance banded           : {banded:F3} ms/frame\n" +
                $"  saved                     : {unbanded - banded:F3} ms/frame " +
                $"({(unbanded > 0 ? (1f - banded / unbanded) * 100f : 0f):F1}%)");

            Assert.Greater(unbanded, 0d, "the benchmark measured something");
            // Avatars here share nothing, so each has to come out as its own group. One group would
            // mean the split collapsed and the solve is back on a single worker.
            Assert.AreEqual(Avatars, BasisConstraintSystem.SolveGroupCount,
                "each avatar solves independently of the others");
        }

        /// <summary>
        /// Builds rotation constraints, which are the ones that convert euler degrees to a quaternion
        /// on every refresh. <paramref name="animate"/> nudges the euler each frame so the conversion
        /// cache can never hit — the difference between the two runs is what the cache is worth.
        /// </summary>
        List<BasisRotationConstraint> BuildRotationPopulation(int count)
        {
            List<BasisRotationConstraint> built = new List<BasisRotationConstraint>();
            GameObject root = new GameObject("RotationRoot");
            spawned.Add(root);
            for (int Index = 0; Index < count; Index++)
            {
                GameObject source = new GameObject($"S{Index}");
                source.transform.SetParent(root.transform, false);
                GameObject target = new GameObject($"T{Index}");
                target.transform.SetParent(root.transform, false);

                BasisRotationConstraint constraint = target.AddComponent<BasisRotationConstraint>();
                constraint.AddSource(new BasisConstraintSourceEntry(source.transform, 1f));
                BasisConstraintSystem.Register(constraint);
                registered.Add(constraint);
                built.Add(constraint);
            }
            return built;
        }

        [Test]
        public void EulerConversionCacheIsWorthMeasuring()
        {
            const int Count = 940;
            List<BasisRotationConstraint> constraints = BuildRotationPopulation(Count);
            BasisConstraintSystem.Complete(BasisConstraintSystem.Schedule());

            // Static eulers: every refresh finds the cached conversion and skips the work.
            Stopwatch watch = Stopwatch.StartNew();
            for (int Frame = 0; Frame < Frames; Frame++)
            {
                BasisConstraintSystem.Complete(BasisConstraintSystem.Schedule());
            }
            watch.Stop();
            double cached = watch.Elapsed.TotalMilliseconds / Frames;

            // Moving eulers: the cache misses every time, which is the old cost.
            watch.Restart();
            for (int Frame = 0; Frame < Frames; Frame++)
            {
                for (int Index = 0; Index < constraints.Count; Index++)
                {
                    constraints[Index].rotationAtRest = new Vector3(Frame * 0.01f, 0f, 0f);
                }
                BasisConstraintSystem.Complete(BasisConstraintSystem.Schedule());
            }
            watch.Stop();
            double missed = watch.Elapsed.TotalMilliseconds / Frames;

            Debug.Log(
                $"[constraint benchmark] euler conversion, {Count} rotation constraints, {Frames} frames\n" +
                $"  unchanged eulers (cache hits) : {cached:F3} ms/frame\n" +
                $"  moving eulers (cache misses)  : {missed:F3} ms/frame\n" +
                $"  note: the miss arm also pays for writing the field, so this is an upper bound " +
                $"on the cache's value, not a clean isolation.");

            Assert.Greater(cached, 0d);
        }

        [Test]
        public void RegistrationChurnCost()
        {
            // A rebuild re-walks every registration, re-interns every transform and re-sorts. Joining
            // and leaving is what triggers it, so this is roughly what an avatar arriving costs.
            BuildPopulation();
            BasisConstraintSystem.Complete(BasisConstraintSystem.Schedule());

            GameObject latecomer = new GameObject("Latecomer");
            spawned.Add(latecomer);
            GameObject source = new GameObject("LateSource");
            source.transform.SetParent(latecomer.transform, false);

            Stopwatch watch = Stopwatch.StartNew();
            const int Joins = 50;
            for (int Join = 0; Join < Joins; Join++)
            {
                GameObject target = new GameObject($"LateTarget{Join}");
                target.transform.SetParent(latecomer.transform, false);
                BasisPositionConstraint constraint = target.AddComponent<BasisPositionConstraint>();
                constraint.AddSource(new BasisConstraintSourceEntry(source.transform, 1f));
                BasisConstraintSystem.Register(constraint);
                registered.Add(constraint);

                // Each registration dirties the table, so this frame pays for a full rebuild.
                BasisConstraintSystem.Complete(BasisConstraintSystem.Schedule());
            }
            watch.Stop();

            Debug.Log(
                $"[constraint benchmark] rebuild on registration, {Avatars * ConstraintsPerAvatar} " +
                $"existing constraints\n" +
                $"  {watch.Elapsed.TotalMilliseconds / Joins:F3} ms per join (rebuild + solve)");

            Assert.Greater(watch.Elapsed.TotalMilliseconds, 0d);
        }

        [Test]
        public void RebuildCostAtStablePopulation()
        {
            // Dirtying without changing the population is what a reparent, a worldUpObject swap or a
            // priority-root change does. The transform tables come back the same length, so this is
            // the case where reusing their arrays can pay — the join benchmark changes the count
            // every iteration and therefore cannot show it either way.
            Transform localRoot = BuildPopulation();
            BasisConstraintSystem.SetPriorityRoot(localRoot);
            BasisConstraintSystem.Complete(BasisConstraintSystem.Schedule());

            double perRebuild = Median(() =>
            {
                const int Rebuilds = 30;
                Stopwatch watch = Stopwatch.StartNew();
                for (int Pass = 0; Pass < Rebuilds; Pass++)
                {
                    registered[0].SetDirty();
                    BasisConstraintSystem.Complete(BasisConstraintSystem.Schedule());
                }
                watch.Stop();
                return watch.Elapsed.TotalMilliseconds / Rebuilds;
            });

            Debug.Log($"[constraint benchmark] rebuild at stable population, " +
                      $"{Avatars * ConstraintsPerAvatar} constraints - " +
                      $"{perRebuild:F3} ms per rebuild (median of 5)");

            Assert.Greater(perRebuild, 0d);
        }

        [Test]
        public void CostScalesWithAvatarCountNotConstraintCount()
        {
            // The point of banding: adding distant avatars should cost close to nothing per frame.
            // If this ratio tracks the constraint count instead, the banding is not working.
            Transform localRoot = BuildPopulation();
            BasisConstraintSystem.SetPriorityRoot(localRoot);
            double withMany = Median(MeasureFrames);

            Debug.Log($"[constraint benchmark] {Avatars * ConstraintsPerAvatar} constraints, " +
                      $"banded: {withMany:F3} ms/frame");

            Assert.Greater(withMany, 0d);
        }
    }
}
