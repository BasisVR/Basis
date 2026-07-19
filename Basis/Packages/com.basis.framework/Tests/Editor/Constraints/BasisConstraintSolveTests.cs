using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Constraints;
using Basis.Scripts.Constraints;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Constraints
{
    /// <summary>
    /// End-to-end cover for the batched constraint solver: real components, real transforms, the
    /// real sample/solve/write jobs. Each case asserts against the transform after a full
    /// <see cref="BasisConstraintSystem.Schedule"/> / <see cref="BasisConstraintSystem.Complete"/>
    /// pair, so a regression anywhere in the flatten → solve → write chain shows up here rather
    /// than only in a scene.
    ///
    /// Registration is driven explicitly instead of through <c>OnEnable</c>: the system installs its
    /// cross-assembly subscription from a <c>[RuntimeInitializeOnLoadMethod]</c>, which never runs in
    /// an EditMode test. <see cref="BasisConstraintSystem.Register"/> is idempotent, so these tests
    /// behave identically if a prior play session already wired the subscription up.
    /// </summary>
    public sealed class BasisConstraintSolveTests
    {
        const float Tolerance = 1e-3f;

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

        Transform NewTransform(string name, Vector3 position, Quaternion rotation, Transform parent = null)
        {
            GameObject go = new GameObject(name);
            spawned.Add(go);
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(position, rotation);
            return go.transform;
        }

        T Constrain<T>(Transform target, params Transform[] sources) where T : BasisConstraintBase
        {
            T constraint = target.gameObject.AddComponent<T>();
            for (int Index = 0; Index < sources.Length; Index++)
            {
                constraint.AddSource(new BasisConstraintSourceEntry(sources[Index], 1f));
            }
            BasisConstraintSystem.Register(constraint);
            registered.Add(constraint);
            return constraint;
        }

        static void Solve()
        {
            BasisConstraintSystem.Complete(BasisConstraintSystem.Schedule());
        }

        static void AssertVector(Vector3 expected, Vector3 actual, string what)
        {
            Assert.AreEqual(expected.x, actual.x, Tolerance, $"{what}.x");
            Assert.AreEqual(expected.y, actual.y, Tolerance, $"{what}.y");
            Assert.AreEqual(expected.z, actual.z, Tolerance, $"{what}.z");
        }

        [Test]
        public void PositionConstraintLandsOnItsSource()
        {
            Transform source = NewTransform("Source", new Vector3(1f, 2f, 3f), Quaternion.identity);
            Transform target = NewTransform("Target", Vector3.zero, Quaternion.identity);
            Constrain<BasisPositionConstraint>(target, source);

            Solve();

            AssertVector(new Vector3(1f, 2f, 3f), target.position, "position");
        }

        [Test]
        public void PositionConstraintBlendsHalfwayAtHalfWeight()
        {
            Transform source = NewTransform("Source", new Vector3(1f, 2f, 3f), Quaternion.identity);
            Transform target = NewTransform("Target", Vector3.zero, Quaternion.identity);
            BasisPositionConstraint constraint = Constrain<BasisPositionConstraint>(target, source);
            constraint.translationAtRest = Vector3.zero;
            constraint.weight = 0.5f;

            Solve();

            AssertVector(new Vector3(0.5f, 1f, 1.5f), target.position, "position");
        }

        [Test]
        public void TwoEqualSourcesBlendToTheirMidpoint()
        {
            Transform left = NewTransform("Left", new Vector3(-2f, 0f, 0f), Quaternion.identity);
            Transform right = NewTransform("Right", new Vector3(4f, 0f, 0f), Quaternion.identity);
            Transform target = NewTransform("Target", Vector3.zero, Quaternion.identity);
            Constrain<BasisPositionConstraint>(target, left, right);

            Solve();

            AssertVector(new Vector3(1f, 0f, 0f), target.position, "position");
        }

        [Test]
        public void MaskedAxesAreLeftUntouched()
        {
            Transform source = NewTransform("Source", new Vector3(1f, 2f, 3f), Quaternion.identity);
            Transform target = NewTransform("Target", new Vector3(0f, 7f, 9f), Quaternion.identity);
            BasisPositionConstraint constraint = Constrain<BasisPositionConstraint>(target, source);
            constraint.translationAxis = BasisConstraintAxes.X;

            Solve();

            // X follows the source; Y and Z keep the pose the transform arrived with, and the weight
            // blend must not drag them toward translationAtRest either.
            AssertVector(new Vector3(1f, 7f, 9f), target.position, "position");
        }

        [Test]
        public void ZeroWeightedSourcesWriteNothing()
        {
            Transform source = NewTransform("Source", new Vector3(1f, 2f, 3f), Quaternion.identity);
            Transform target = NewTransform("Target", new Vector3(9f, 9f, 9f), Quaternion.identity);
            BasisPositionConstraint constraint = Constrain<BasisPositionConstraint>(target, source);
            constraint.SetSource(0, new BasisConstraintSourceEntry(source, 0f));

            Solve();

            // Nothing drives the constraint, so it must leave the transform alone rather than snap
            // it to the at-rest pose.
            AssertVector(new Vector3(9f, 9f, 9f), target.position, "position");
        }

        [Test]
        public void InactiveConstraintWritesNothing()
        {
            Transform source = NewTransform("Source", new Vector3(1f, 2f, 3f), Quaternion.identity);
            Transform target = NewTransform("Target", new Vector3(5f, 5f, 5f), Quaternion.identity);
            BasisPositionConstraint constraint = Constrain<BasisPositionConstraint>(target, source);
            constraint.constraintActive = false;

            Solve();

            AssertVector(new Vector3(5f, 5f, 5f), target.position, "position");
        }

        [Test]
        public void RotationConstraintMatchesItsSource()
        {
            Quaternion sourceRotation = Quaternion.Euler(0f, 90f, 0f);
            Transform source = NewTransform("Source", Vector3.zero, sourceRotation);
            Transform target = NewTransform("Target", Vector3.zero, Quaternion.identity);
            Constrain<BasisRotationConstraint>(target, source);

            Solve();

            Assert.Less(Quaternion.Angle(sourceRotation, target.rotation), 0.1f, "rotation");
        }

        [Test]
        public void ScaleConstraintMatchesItsSource()
        {
            Transform source = NewTransform("Source", Vector3.zero, Quaternion.identity);
            source.localScale = new Vector3(2f, 3f, 4f);
            Transform target = NewTransform("Target", Vector3.zero, Quaternion.identity);
            Constrain<BasisScaleConstraint>(target, source);

            Solve();

            AssertVector(new Vector3(2f, 3f, 4f), target.localScale, "localScale");
        }

        [Test]
        public void ParentConstraintAppliesItsPerSourceOffset()
        {
            Transform source = NewTransform("Source", new Vector3(5f, 0f, 0f), Quaternion.identity);
            Transform target = NewTransform("Target", Vector3.zero, Quaternion.identity);
            BasisParentConstraint constraint = Constrain<BasisParentConstraint>(target, source);
            constraint.translationOffsets[0] = new Vector3(1f, 0f, 0f);

            Solve();

            AssertVector(new Vector3(6f, 0f, 0f), target.position, "position");
        }

        [Test]
        public void LookAtConstraintPointsForwardAtItsSource()
        {
            Transform source = NewTransform("Source", new Vector3(5f, 0f, 0f), Quaternion.identity);
            Transform target = NewTransform("Target", Vector3.zero, Quaternion.identity);
            Constrain<BasisLookAtConstraint>(target, source);

            Solve();

            AssertVector(new Vector3(1f, 0f, 0f), target.forward, "forward");
        }

        [Test]
        public void AimConstraintPointsItsChosenAxisAtTheSource()
        {
            Transform source = NewTransform("Source", new Vector3(0f, 0f, 5f), Quaternion.identity);
            Transform target = NewTransform("Target", Vector3.zero, Quaternion.identity);
            BasisAimConstraint constraint = Constrain<BasisAimConstraint>(target, source);
            constraint.aimVector = Vector3.right;

            Solve();

            // +X is the aimed axis, so it should end up pointing at the source, not +Z.
            AssertVector(new Vector3(0f, 0f, 1f), target.right, "right");
        }

        [Test]
        public void DeeperConstraintReadsTheShallowerOneSolvedThisFrame()
        {
            // A drives B (depth 1); B drives C (depth 2). Depth ordering must solve B first and
            // refresh its world row, so C lands on A's position in the same frame rather than
            // trailing by one.
            Transform root = NewTransform("Root", Vector3.zero, Quaternion.identity);
            Transform anchor = NewTransform("Anchor", new Vector3(4f, 0f, 0f), Quaternion.identity);

            Transform b = NewTransform("B", Vector3.zero, Quaternion.identity, root);
            Transform intermediate = NewTransform("Intermediate", Vector3.zero, Quaternion.identity, root);
            Transform c = NewTransform("C", Vector3.zero, Quaternion.identity, intermediate);

            // Registered deepest-first on purpose: only the depth sort can put them right.
            Constrain<BasisPositionConstraint>(c, b);
            Constrain<BasisPositionConstraint>(b, anchor);

            Solve();

            AssertVector(new Vector3(4f, 0f, 0f), b.position, "b.position");
            AssertVector(new Vector3(4f, 0f, 0f), c.position, "c.position");
        }

        [Test]
        public void ConstraintUnderAMovedParentSolvesInLocalSpace()
        {
            // The write path is local-space, so a target whose parent is itself offset and rotated
            // must still land on the source in world space.
            Transform parent = NewTransform("Parent", new Vector3(10f, 0f, 0f), Quaternion.Euler(0f, 90f, 0f));
            Transform source = NewTransform("Source", new Vector3(1f, 2f, 3f), Quaternion.identity);
            Transform target = NewTransform("Target", Vector3.zero, Quaternion.identity, parent);
            Constrain<BasisPositionConstraint>(target, source);

            Solve();

            AssertVector(new Vector3(1f, 2f, 3f), target.position, "position");
        }

        [Test]
        public void ShallowConstraintStillSeesADeepDependencySolvedThisFrame()
        {
            // The case hierarchy depth alone gets wrong: a root-level object (depth 0) sourcing a
            // deep bone (depth 3) that is itself constrained. Sorting by depth would run the shallow
            // one first and leave it a frame behind forever; the dependency sort has to override it.
            Transform anchor = NewTransform("Anchor", new Vector3(7f, 0f, 0f), Quaternion.identity);

            Transform a = NewTransform("A", Vector3.zero, Quaternion.identity);
            Transform b = NewTransform("B", Vector3.zero, Quaternion.identity, a);
            Transform deepBone = NewTransform("DeepBone", Vector3.zero, Quaternion.identity, b);

            Transform prop = NewTransform("Prop", Vector3.zero, Quaternion.identity);

            Constrain<BasisPositionConstraint>(prop, deepBone);
            Constrain<BasisPositionConstraint>(deepBone, anchor);

            Solve();

            AssertVector(new Vector3(7f, 0f, 0f), deepBone.position, "deepBone.position");
            AssertVector(new Vector3(7f, 0f, 0f), prop.position, "prop.position");
        }

        [Test]
        public void StackedPositionAndRotationConstraintsBothApply()
        {
            // Two constraints on one GameObject is routine in Unity. They share a single row in the
            // write array and merge per channel — if they each got their own row, the parallel write
            // job would have two threads racing over the same transform and one would be lost.
            Quaternion sourceRotation = Quaternion.Euler(0f, 90f, 0f);
            Transform source = NewTransform("Source", new Vector3(1f, 2f, 3f), sourceRotation);
            Transform target = NewTransform("Target", Vector3.zero, Quaternion.identity);
            Constrain<BasisPositionConstraint>(target, source);
            Constrain<BasisRotationConstraint>(target, source);

            Solve();

            AssertVector(new Vector3(1f, 2f, 3f), target.position, "position");
            Assert.Less(Quaternion.Angle(sourceRotation, target.rotation), 0.1f, "rotation");
        }

        [Test]
        public void RegistrationCountsTrackTheLiveComponents()
        {
            Transform source = NewTransform("Source", Vector3.zero, Quaternion.identity);
            Transform target = NewTransform("Target", Vector3.zero, Quaternion.identity);
            BasisPositionConstraint constraint = Constrain<BasisPositionConstraint>(target, source);

            Solve();
            Assert.AreEqual(1, BasisConstraintSystem.SlotCount, "slot count after register");

            BasisConstraintSystem.Unregister(constraint);
            registered.Remove(constraint);

            Solve();
            Assert.AreEqual(0, BasisConstraintSystem.SlotCount, "slot count after unregister");
        }
    }
}
