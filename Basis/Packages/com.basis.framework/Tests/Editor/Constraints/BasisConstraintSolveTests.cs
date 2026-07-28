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
        public void LockedHoldsUndrivenAxesAtTheirLivePose()
        {
            Transform source = NewTransform("Source", new Vector3(1f, 2f, 3f), Quaternion.identity);
            Transform target = NewTransform("Target", new Vector3(7f, 8f, 9f), Quaternion.identity);
            BasisPositionConstraint constraint = Constrain<BasisPositionConstraint>(target, source);
            constraint.translationAtRest = new Vector3(0f, -5f, -6f);
            constraint.translationAxis = BasisConstraintAxes.X;
            constraint.locked = true;

            Solve();

            AssertVector(new Vector3(1f, 8f, 9f), target.position,
                "X follows the source, Y and Z hold the pose the transform came in at");
        }

        [Test]
        public void UnlockedReturnsUndrivenAxesToTheRestPose()
        {
            Transform source = NewTransform("Source", new Vector3(1f, 2f, 3f), Quaternion.identity);
            Transform target = NewTransform("Target", new Vector3(7f, 8f, 9f), Quaternion.identity);
            BasisPositionConstraint constraint = Constrain<BasisPositionConstraint>(target, source);
            constraint.translationAtRest = new Vector3(0f, -5f, -6f);
            constraint.translationAxis = BasisConstraintAxes.X;
            constraint.locked = false;

            Solve();

            AssertVector(new Vector3(1f, -5f, -6f), target.position,
                "X follows the source, Y and Z fall back to the captured rest pose");
        }

        BasisMultiReferential NewReferential(params Transform[] members)
        {
            Transform host = members[0];
            BasisMultiReferential constraint = host.gameObject.AddComponent<BasisMultiReferential>();
            constraint.members = new List<Transform>(members);
            constraint.CaptureRest();
            BasisConstraintSystem.Register(constraint);
            registered.Add(constraint);
            return constraint;
        }

        [Test]
        public void ReferentialHoldsItsArrangementWhenTheDriverMoves()
        {
            Transform a = NewTransform("A", Vector3.zero, Quaternion.identity);
            Transform b = NewTransform("B", new Vector3(2f, 0f, 0f), Quaternion.identity);
            Transform c = NewTransform("C", new Vector3(0f, 3f, 0f), Quaternion.identity);
            BasisMultiReferential referential = NewReferential(a, b, c);
            referential.driver = 0;

            a.position = new Vector3(10f, 10f, 0f);
            Solve();

            AssertVector(new Vector3(12f, 10f, 0f), b.position, "B keeps its offset from the leader");
            AssertVector(new Vector3(10f, 13f, 0f), c.position, "C keeps its offset from the leader");
        }

        [Test]
        public void ReferentialFollowsANewDriverWithoutRebuilding()
        {
            Transform a = NewTransform("A", Vector3.zero, Quaternion.identity);
            Transform b = NewTransform("B", new Vector3(2f, 0f, 0f), Quaternion.identity);
            BasisMultiReferential referential = NewReferential(a, b);

            referential.driver = 0;
            a.position = new Vector3(5f, 0f, 0f);
            Solve();
            AssertVector(new Vector3(7f, 0f, 0f), b.position, "B follows A while A leads");

            // Hand leadership over mid-session. The driver is a scalar, so this needs no rebuild —
            // and the arrangement comes from the captured poses, so it must not drift.
            referential.driver = 1;
            b.position = new Vector3(0f, 8f, 0f);
            Solve();

            AssertVector(new Vector3(-2f, 8f, 0f), a.position,
                "now A follows B, holding the same arrangement in the other direction");
        }

        [Test]
        public void ReferentialLeavesItsDriverAlone()
        {
            Transform a = NewTransform("A", Vector3.zero, Quaternion.identity);
            Transform b = NewTransform("B", new Vector3(2f, 0f, 0f), Quaternion.identity);
            BasisMultiReferential referential = NewReferential(a, b);
            referential.driver = 0;

            a.position = new Vector3(4f, 4f, 4f);
            Solve();

            AssertVector(new Vector3(4f, 4f, 4f), a.position,
                "the leader is what everything else is measured against and is never written");
        }

        [Test]
        public void SwappingTheLocalAvatarRegroupsAndKeepsDriving()
        {
            // The real sequence when someone changes avatar: the old hierarchy is destroyed out from
            // under the solver while it still has that root grouped, and a new one is announced.
            Transform oldRoot = NewTransform("AvatarOld", Vector3.zero, Quaternion.identity);
            Transform oldSource = NewTransform("OldSource", new Vector3(3f, 0f, 0f), Quaternion.identity, oldRoot);
            Transform oldTarget = NewTransform("OldTarget", Vector3.zero, Quaternion.identity, oldRoot);
            BasisPositionConstraint oldConstraint = Constrain<BasisPositionConstraint>(oldTarget, oldSource);
            BasisConstraintSystem.SetPriorityRoot(oldRoot);

            Solve();
            AssertVector(new Vector3(3f, 0f, 0f), oldTarget.position, "the first avatar drives");

            // Tear the old one down the way an avatar swap does.
            BasisConstraintSystem.Unregister(oldConstraint);
            registered.Remove(oldConstraint);
            Object.DestroyImmediate(oldRoot.gameObject);

            Transform newRoot = NewTransform("AvatarNew", new Vector3(50f, 0f, 0f), Quaternion.identity);
            Transform newSource = NewTransform("NewSource", new Vector3(57f, 0f, 0f), Quaternion.identity, newRoot);
            Transform newTarget = NewTransform("NewTarget", new Vector3(50f, 0f, 0f), Quaternion.identity, newRoot);
            Constrain<BasisPositionConstraint>(newTarget, newSource);
            BasisConstraintSystem.SetPriorityRoot(newRoot);

            Solve();

            AssertVector(new Vector3(57f, 0f, 0f), newTarget.position,
                "the replacement avatar drives at full rate straight away, not on a distant cadence");
        }

        [Test]
        public void APriorityRootDestroyedWithoutBeingReplacedDoesNotWedge()
        {
            // Announced, then destroyed with nothing announced after it — a disconnect mid-swap.
            // Everything left should keep solving rather than stalling behind a dead reference.
            Transform goneRoot = NewTransform("Gone", Vector3.zero, Quaternion.identity);
            Transform survivor = NewTransform("Survivor", Vector3.zero, Quaternion.identity);
            Transform source = NewTransform("Source", new Vector3(6f, 0f, 0f), Quaternion.identity, survivor);
            Transform target = NewTransform("Target", Vector3.zero, Quaternion.identity, survivor);
            Constrain<BasisPositionConstraint>(target, source);

            BasisConstraintSystem.SetPriorityRoot(goneRoot);
            Solve();
            Object.DestroyImmediate(goneRoot.gameObject);

            Solve();
            Solve();

            AssertVector(new Vector3(6f, 0f, 0f), target.position,
                "a dead priority root falls back to treating everything as near, not to nothing");
        }

        [Test]
        public void DisablingAConstraintStopsItDrivingAndReEnablingResumes()
        {
            // Toggling `enabled` is a different path from constraintActive: it runs OnDisable, which
            // pulls the slot out of the table entirely. Vixxy toggles components this way.
            Transform source = NewTransform("Source", new Vector3(5f, 0f, 0f), Quaternion.identity);
            Transform target = NewTransform("Target", Vector3.zero, Quaternion.identity);
            BasisPositionConstraint constraint = Constrain<BasisPositionConstraint>(target, source);

            Solve();
            AssertVector(new Vector3(5f, 0f, 0f), target.position, "driving while enabled");

            constraint.enabled = false;
            BasisConstraintSystem.Unregister(constraint);
            target.position = Vector3.zero;
            Solve();
            AssertVector(Vector3.zero, target.position, "a disabled constraint drives nothing");

            constraint.enabled = true;
            BasisConstraintSystem.Register(constraint);
            Solve();
            AssertVector(new Vector3(5f, 0f, 0f), target.position, "and picks up again when re-enabled");
        }

        [Test]
        public void SeveralConstraintsOnOneObjectComposePerChannel()
        {
            // Position, rotation and scale stacked on one transform share a single write row; each
            // must land its own channel without clobbering the others.
            Transform source = NewTransform("Source", new Vector3(2f, 0f, 0f),
                Quaternion.AngleAxis(90f, Vector3.up));
            source.localScale = new Vector3(3f, 3f, 3f);
            Transform target = NewTransform("Target", Vector3.zero, Quaternion.identity);

            Constrain<BasisPositionConstraint>(target, source);
            Constrain<BasisRotationConstraint>(target, source);
            Constrain<BasisScaleConstraint>(target, source);

            Solve();

            AssertVector(new Vector3(2f, 0f, 0f), target.position, "position channel");
            Assert.Less(Quaternion.Angle(source.rotation, target.rotation), 0.5f, "rotation channel");
            AssertVector(new Vector3(3f, 3f, 3f), target.localScale, "scale channel");
        }

        [Test]
        public void DisablingOneOfSeveralOnAnObjectLeavesTheOthersDriving()
        {
            Transform source = NewTransform("Source", new Vector3(4f, 0f, 0f),
                Quaternion.AngleAxis(90f, Vector3.up));
            Transform target = NewTransform("Target", Vector3.zero, Quaternion.identity);

            BasisPositionConstraint position = Constrain<BasisPositionConstraint>(target, source);
            Constrain<BasisRotationConstraint>(target, source);

            Solve();
            AssertVector(new Vector3(4f, 0f, 0f), target.position, "both driving");

            position.enabled = false;
            BasisConstraintSystem.Unregister(position);
            target.position = Vector3.zero;
            Solve();

            AssertVector(Vector3.zero, target.position, "the disabled one stopped");
            Assert.Less(Quaternion.Angle(source.rotation, target.rotation), 0.5f,
                "while its neighbour on the same transform keeps driving its own channel");
        }

        [Test]
        public void DampedTransformKeepsItsLagAcrossARebuild()
        {
            Transform source = NewTransform("Source", Vector3.zero, Quaternion.identity);
            Transform target = NewTransform("Target", Vector3.zero, Quaternion.identity);
            BasisDampedTransform damped = Constrain<BasisDampedTransform>(target, source);
            damped.maintainAim = false;
            // Bind while the two sit together: the bind freezes their separation as neutral, so the
            // source has to move afterwards for there to be any lag to observe.
            damped.CaptureRest();
            damped.dampPosition = 0.9f;
            damped.dampRotation = 0.9f;
            source.position = new Vector3(20f, 0f, 0f);

            Solve();
            Solve();
            float travelled = target.position.x;
            Assert.Greater(travelled, 0f, "the lag has started moving");

            // Registering anything else marks the table dirty, which rebuilds every slot in the
            // session and reassigns their indices — an avatar joining does exactly this.
            Transform other = NewTransform("Other", Vector3.zero, Quaternion.identity);
            Constrain<BasisPositionConstraint>(other, source);

            // Stand in for an animator re-posing the bone: the live pose is no longer where the lag
            // left it, so continuing correctly can only come from the constraint's own memory.
            target.position = new Vector3(-100f, 0f, 0f);
            Solve();

            Assert.Greater(target.position.x, travelled - 1f,
                "the lag resumed from its own memory, not from the pose something else left behind");
            Assert.Less(target.position.x, 20f, "and it is still lagging, not snapped onto the source");
        }

        [Test]
        public void ACycleIsBrokenRatherThanSpinningForever()
        {
            // Two constraints each sourcing the other's target: no valid order exists.
            Transform first = NewTransform("First", new Vector3(1f, 0f, 0f), Quaternion.identity);
            Transform second = NewTransform("Second", new Vector3(2f, 0f, 0f), Quaternion.identity);
            Constrain<BasisPositionConstraint>(first, second);
            Constrain<BasisPositionConstraint>(second, first);

            // The break is reported once per rebuild; the point of the test is that it terminates
            // and still produces a pose rather than hanging or leaving the table half-solved.
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            try
            {
                Solve();
                Solve();
            }
            finally
            {
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;
            }

            Assert.IsFalse(float.IsNaN(first.position.x), "a broken cycle still leaves a usable pose");
            Assert.IsFalse(float.IsNaN(second.position.x));
        }

        [Test]
        public void OverrideTransformDrivenByASourceTracksItsMovementSinceBind()
        {
            Transform source = NewTransform("Source", Vector3.zero, Quaternion.identity);
            Transform target = NewTransform("Target", new Vector3(5f, 0f, 0f), Quaternion.identity);
            BasisOverrideTransform constraint = Constrain<BasisOverrideTransform>(target, source);
            constraint.useSource = true;
            constraint.space = BasisOverrideTransform.Space.Pivot;
            constraint.CaptureSourceBind();

            // Bind captured with the source at rest, so before it moves there is nothing to apply.
            Solve();
            AssertVector(new Vector3(5f, 0f, 0f), target.position,
                "a source sitting at its bind contributes no movement");

            source.localPosition = new Vector3(0f, 3f, 0f);
            Solve();

            Assert.AreEqual(3f, target.position.y, 1e-2f,
                "the source's movement since bind carries onto the target, not its absolute pose");
        }

        [Test]
        public void ConstraintsChainThroughEachOtherInOneFrame()
        {
            // A drives B drives C: all three must settle in a single solve, which is the whole point
            // of ordering by dependency rather than by hierarchy depth.
            Transform a = NewTransform("A", new Vector3(7f, 0f, 0f), Quaternion.identity);
            Transform b = NewTransform("B", Vector3.zero, Quaternion.identity);
            Transform c = NewTransform("C", Vector3.zero, Quaternion.identity);
            Constrain<BasisPositionConstraint>(c, b);
            Constrain<BasisPositionConstraint>(b, a);

            Solve();

            AssertVector(new Vector3(7f, 0f, 0f), c.position,
                "the far end of the chain sees the whole chain solved this frame");
        }

        [Test]
        public void ConstraintsThatDriveEachOtherSolveInOneGroup()
        {
            // The same A drives B drives C, read as a grouping question: the solve runs groups at
            // once, so anything that has to observe another's result cannot be split away from it.
            Transform a = NewTransform("A", new Vector3(7f, 0f, 0f), Quaternion.identity);
            Transform b = NewTransform("B", Vector3.zero, Quaternion.identity);
            Transform c = NewTransform("C", Vector3.zero, Quaternion.identity);
            Constrain<BasisPositionConstraint>(c, b);
            Constrain<BasisPositionConstraint>(b, a);

            Solve();

            Assert.AreEqual(1, BasisConstraintSystem.SolveGroupCount,
                "a dependent chain stays in one group however its objects are arranged");
        }

        [Test]
        public void UnrelatedConstraintsSolveAsSeparateGroups()
        {
            Transform firstSource = NewTransform("S1", new Vector3(3f, 0f, 0f), Quaternion.identity);
            Transform secondSource = NewTransform("S2", new Vector3(0f, 4f, 0f), Quaternion.identity);
            Constrain<BasisPositionConstraint>(
                NewTransform("T1", Vector3.zero, Quaternion.identity), firstSource);
            Constrain<BasisPositionConstraint>(
                NewTransform("T2", Vector3.zero, Quaternion.identity), secondSource);

            Solve();

            Assert.AreEqual(2, BasisConstraintSystem.SolveGroupCount,
                "two constraints sharing nothing solve independently");
        }

        [Test]
        public void ASourceNobodyDrivesDoesNotMergeTheGroupsReadingIt()
        {
            // A shared anchor everything aims at — a world marker, a held prop — is read by many and
            // written by none. Treating a read as a dependency would fold every reader into one group
            // and put the whole solve back on a single worker, which is the case this pins down.
            Transform anchor = NewTransform("Anchor", new Vector3(0f, 0f, 9f), Quaternion.identity);
            Transform first = NewTransform("T1", Vector3.zero, Quaternion.identity);
            Transform second = NewTransform("T2", Vector3.zero, Quaternion.identity);
            Constrain<BasisPositionConstraint>(first, anchor);
            Constrain<BasisPositionConstraint>(second, anchor);

            Solve();

            Assert.AreEqual(2, BasisConstraintSystem.SolveGroupCount,
                "reading a transform nothing drives is not a dependency");
            AssertVector(new Vector3(0f, 0f, 9f), first.position, "first reader still lands");
            AssertVector(new Vector3(0f, 0f, 9f), second.position, "second reader still lands");
        }

        [Test]
        public void StackedConstraintsOnOneObjectShareItsGroup()
        {
            // Two slots writing one results row cannot be split: they merge per channel into a single
            // write, and a merge is not something two workers can each do half of.
            Transform source = NewTransform("Source", new Vector3(2f, 0f, 0f), Quaternion.Euler(0f, 30f, 0f));
            Transform target = NewTransform("Target", Vector3.zero, Quaternion.identity);
            Constrain<BasisPositionConstraint>(target, source);
            Constrain<BasisRotationConstraint>(target, source);

            Solve();

            Assert.AreEqual(1, BasisConstraintSystem.SolveGroupCount,
                "slots sharing a target share a group");
        }

        [Test]
        public void BlendConstraintLandsMidwayBetweenItsTwoSources()
        {
            Transform a = NewTransform("A", Vector3.zero, Quaternion.identity);
            Transform b = NewTransform("B", new Vector3(4f, 8f, -2f), Quaternion.identity);
            Transform target = NewTransform("Target", Vector3.zero, Quaternion.identity);
            BasisBlendConstraint constraint = Constrain<BasisBlendConstraint>(target, a, b);
            constraint.positionWeight = 0.5f;

            Solve();

            AssertVector(new Vector3(2f, 4f, -1f), target.position, "position");
        }

        [Test]
        public void BlendConstraintTakesTheFirstSourceAtZeroAndTheSecondAtOne()
        {
            Transform a = NewTransform("A", new Vector3(-3f, 0f, 0f), Quaternion.identity);
            Transform b = NewTransform("B", new Vector3(5f, 0f, 0f), Quaternion.identity);
            Transform target = NewTransform("Target", Vector3.zero, Quaternion.identity);
            BasisBlendConstraint constraint = Constrain<BasisBlendConstraint>(target, a, b);

            // Also covers the scalar refresh path: retuning the blend between solves must land
            // without a structural rebuild.
            constraint.positionWeight = 0f;
            Solve();
            AssertVector(new Vector3(-3f, 0f, 0f), target.position, "position at blend 0");

            constraint.positionWeight = 1f;
            Solve();
            AssertVector(new Vector3(5f, 0f, 0f), target.position, "position at blend 1");
        }

        [Test]
        public void BlendConstraintWithPositionBlendingOffLeavesPositionAlone()
        {
            Transform a = NewTransform("A", new Vector3(1f, 1f, 1f), Quaternion.identity);
            Transform b = NewTransform("B", new Vector3(9f, 9f, 9f), Quaternion.identity);
            Transform target = NewTransform("Target", new Vector3(7f, 8f, 9f), Quaternion.identity);
            BasisBlendConstraint constraint = Constrain<BasisBlendConstraint>(target, a, b);
            constraint.blendPosition = false;

            Solve();

            AssertVector(new Vector3(7f, 8f, 9f), target.position,
                "position is untouched when position blending is off");
        }

        [Test]
        public void BlendConstraintWithASingleSourceLeavesTheTransformAlone()
        {
            Transform only = NewTransform("Only", new Vector3(4f, 4f, 4f), Quaternion.identity);
            Transform target = NewTransform("Target", new Vector3(7f, 8f, 9f), Quaternion.identity);
            Constrain<BasisBlendConstraint>(target, only);

            Solve();

            AssertVector(new Vector3(7f, 8f, 9f), target.position,
                "a blend needs two ends; one source is not enough to drive anything");
        }

        /// <summary>
        /// A bent two-bone limb along +X: root at the origin, elbow up at (1,1,0), tip back down at
        /// (2,0,0). Both bones are sqrt(2) long, so the limb can reach 2*sqrt(2) at full stretch.
        /// </summary>
        BasisTwoBoneIK NewLimb(out Transform root, out Transform mid, out Transform tip, Transform target)
        {
            root = NewTransform("Root", Vector3.zero, Quaternion.identity);
            mid = NewTransform("Mid", new Vector3(1f, 1f, 0f), Quaternion.identity, root);
            tip = NewTransform("Tip", new Vector3(2f, 0f, 0f), Quaternion.identity, mid);
            return Constrain<BasisTwoBoneIK>(tip, target);
        }

        /// <summary>A straight four-bone chain along +X, one unit per link, reaching 3 at full stretch.</summary>
        BasisChainIK NewChain(out Transform chainRoot, out Transform tip, Transform target)
        {
            chainRoot = NewTransform("Chain0", Vector3.zero, Quaternion.identity);
            Transform cursor = chainRoot;
            for (int Index = 1; Index <= 3; Index++)
            {
                cursor = NewTransform($"Chain{Index}", new Vector3(Index, 0f, 0f), Quaternion.identity, cursor);
            }
            tip = cursor;
            BasisChainIK constraint = Constrain<BasisChainIK>(tip, target);
            constraint.root = chainRoot;
            return constraint;
        }

        [Test]
        public void ChainIKReachesAReachableTarget()
        {
            Transform target = NewTransform("Target", new Vector3(1f, 2f, 0f), Quaternion.identity);
            NewChain(out _, out Transform tip, target);

            Solve();

            Assert.Less(Vector3.Distance(tip.position, target.position), 0.05f,
                "a target inside the chain's reach should be reached");
        }

        [Test]
        public void ChainIKStraightensTowardAnUnreachableTarget()
        {
            Transform target = NewTransform("Target", new Vector3(0f, 40f, 0f), Quaternion.identity);
            NewChain(out Transform chainRoot, out Transform tip, target);

            Solve();

            Assert.AreEqual(3f, Vector3.Distance(chainRoot.position, tip.position), 0.05f,
                "out of range the chain lays out straight at full length");
            Assert.Less(Vector3.Angle(tip.position - chainRoot.position, Vector3.up), 1f,
                "and points at the target");
        }

        [Test]
        public void ChainIKKeepsItsRootPlanted()
        {
            Transform target = NewTransform("Target", new Vector3(1f, 2f, 0f), Quaternion.identity);
            NewChain(out Transform chainRoot, out _, target);

            Solve();

            AssertVector(Vector3.zero, chainRoot.position, "the root never moves, only rotates");
        }

        [Test]
        public void ChainIKAtZeroWeightLeavesTheChainAlone()
        {
            Transform target = NewTransform("Target", new Vector3(1f, 2f, 0f), Quaternion.identity);
            BasisChainIK constraint = NewChain(out _, out Transform tip, target);
            constraint.weight = 0f;

            Solve();

            AssertVector(new Vector3(3f, 0f, 0f), tip.position, "untouched at zero weight");
        }

        [Test]
        public void TwoBoneIKPutsTheTipOnItsTarget()
        {
            Transform target = NewTransform("Target", new Vector3(1.5f, 1f, 0f), Quaternion.identity);
            BasisTwoBoneIK ik = NewLimb(out _, out _, out Transform tip, target);
            ik.hintWeight = 0f;

            Solve();

            Assert.Less(Vector3.Distance(tip.position, target.position), 0.01f,
                "a reachable target should be reached exactly");
        }

        [Test]
        public void TwoBoneIKStretchesTowardAnUnreachableTargetWithoutBreaking()
        {
            Transform target = NewTransform("Target", new Vector3(50f, 0f, 0f), Quaternion.identity);
            BasisTwoBoneIK ik = NewLimb(out Transform root, out _, out Transform tip, target);
            ik.hintWeight = 0f;

            Solve();

            float reach = Vector3.Distance(root.position, tip.position);
            Assert.AreEqual(2f * Mathf.Sqrt(2f), reach, 0.05f,
                "out of range the limb straightens to full length instead of tearing apart");
            Assert.Less(Vector3.Angle(tip.position - root.position, Vector3.right), 1f,
                "and points at the target");
        }

        [Test]
        public void TwoBoneIKAtZeroWeightLeavesTheLimbAlone()
        {
            Transform target = NewTransform("Target", new Vector3(1.5f, 1f, 0f), Quaternion.identity);
            BasisTwoBoneIK ik = NewLimb(out _, out Transform mid, out Transform tip, target);
            ik.weight = 0f;

            Solve();

            AssertVector(new Vector3(1f, 1f, 0f), mid.position, "elbow is untouched at zero weight");
            AssertVector(new Vector3(2f, 0f, 0f), tip.position, "tip is untouched at zero weight");
        }

        [Test]
        public void TwoBoneIKHintDecidesWhichWayTheJointBreaks()
        {
            Transform target = NewTransform("Target", new Vector3(1.5f, 0f, 0f), Quaternion.identity);
            Transform hint = NewTransform("Hint", new Vector3(1f, -5f, 0f), Quaternion.identity);
            BasisTwoBoneIK ik = NewLimb(out _, out Transform mid, out _, target);
            ik.AddSource(new BasisConstraintSourceEntry(hint, 1f));
            ik.hintWeight = 1f;

            Solve();

            Assert.Less(mid.position.y, 0f,
                "a hint below the limb should pull the elbow down through the straight pose");
        }

        [Test]
        public void TwistCorrectionPassesAShareOfTheSourceRoll()
        {
            Transform source = NewTransform("Source", Vector3.zero, Quaternion.identity);
            Transform target = NewTransform("Target", Vector3.zero, Quaternion.identity);
            BasisTwistCorrection constraint = Constrain<BasisTwistCorrection>(target, source);
            constraint.twistAxis = BasisTwistCorrection.TwistAxis.X;
            constraint.twistWeight = 0.5f;
            constraint.CaptureRest();

            source.localRotation = Quaternion.AngleAxis(90f, Vector3.right);
            Solve();

            float roll = Quaternion.Angle(Quaternion.identity, target.localRotation);
            Assert.AreEqual(45f, roll, 1f, "half the source's 90 degree roll reaches the node");
        }

        [Test]
        public void TwistCorrectionWithNegativeWeightCountersTheRoll()
        {
            Transform source = NewTransform("Source", Vector3.zero, Quaternion.identity);
            Transform target = NewTransform("Target", Vector3.zero, Quaternion.identity);
            BasisTwistCorrection constraint = Constrain<BasisTwistCorrection>(target, source);
            constraint.twistAxis = BasisTwistCorrection.TwistAxis.X;
            constraint.twistWeight = -1f;
            constraint.CaptureRest();

            source.localRotation = Quaternion.AngleAxis(60f, Vector3.right);
            Solve();

            // Countering means rolling the opposite way, so the x component flips sign.
            Assert.Less(target.localRotation.x, 0f,
                "a negative share rolls against the source, not with it");
            Assert.AreEqual(60f, Quaternion.Angle(Quaternion.identity, target.localRotation), 1f,
                "and by the same magnitude at full share");
        }

        [Test]
        public void TwistCorrectionIgnoresRollOffItsChosenAxis()
        {
            Transform source = NewTransform("Source", Vector3.zero, Quaternion.identity);
            Transform target = NewTransform("Target", Vector3.zero, Quaternion.identity);
            BasisTwistCorrection constraint = Constrain<BasisTwistCorrection>(target, source);
            constraint.twistAxis = BasisTwistCorrection.TwistAxis.X;
            constraint.twistWeight = 1f;
            constraint.CaptureRest();

            source.localRotation = Quaternion.AngleAxis(90f, Vector3.up);
            Solve();

            Assert.Less(Quaternion.Angle(Quaternion.identity, target.localRotation), 1f,
                "a pure yaw carries no twist about X, so the node stays put");
        }

        [Test]
        public void DampedTransformWithNoDampingConvergesOnItsSource()
        {
            Transform source = NewTransform("Source", Vector3.zero, Quaternion.identity);
            Transform target = NewTransform("Target", Vector3.zero, Quaternion.identity);
            BasisDampedTransform constraint = Constrain<BasisDampedTransform>(target, source);
            constraint.maintainAim = false;
            constraint.CaptureRest();
            constraint.dampPosition = 0f;
            constraint.dampRotation = 0f;

            source.position = new Vector3(10f, 0f, 0f);
            // First solve only seeds the lag memory from the live pose; motion starts after that.
            Solve();
            for (int Index = 0; Index < 30; Index++)
            {
                Solve();
            }

            Assert.Less(Vector3.Distance(target.position, source.position), 0.05f,
                "undamped, the target should end up on its source");
        }

        [Test]
        public void DampedTransformLagsBehindItsSource()
        {
            Transform source = NewTransform("Source", Vector3.zero, Quaternion.identity);
            Transform target = NewTransform("Target", Vector3.zero, Quaternion.identity);
            BasisDampedTransform constraint = Constrain<BasisDampedTransform>(target, source);
            constraint.maintainAim = false;
            constraint.CaptureRest();
            constraint.dampPosition = 0.9f;
            constraint.dampRotation = 0.9f;

            source.position = new Vector3(10f, 0f, 0f);
            Solve();
            Solve();

            float travelled = Vector3.Distance(target.position, Vector3.zero);
            Assert.Greater(travelled, 0f, "a damped transform still moves toward its source");
            Assert.Less(travelled, 10f, "but heavily damped it must not arrive in one step");
        }

        [Test]
        public void DampedTransformAtFullDampingHoldsStill()
        {
            Transform source = NewTransform("Source", Vector3.zero, Quaternion.identity);
            Transform target = NewTransform("Target", new Vector3(1f, 2f, 3f), Quaternion.identity);
            BasisDampedTransform constraint = Constrain<BasisDampedTransform>(target, source);
            constraint.maintainAim = false;
            constraint.CaptureRest();
            constraint.dampPosition = 1f;
            constraint.dampRotation = 1f;

            source.position = new Vector3(50f, 50f, 50f);
            Solve();
            Solve();
            Solve();

            AssertVector(new Vector3(1f, 2f, 3f), target.position,
                "fully damped resists entirely and never leaves where it started");
        }

        [Test]
        public void OverrideTransformOnExplicitValuesNeedsNoSource()
        {
            Transform target = NewTransform("Target", new Vector3(1f, 1f, 1f), Quaternion.identity);
            BasisOverrideTransform constraint = Constrain<BasisOverrideTransform>(target);
            constraint.space = BasisOverrideTransform.Space.World;
            constraint.position = new Vector3(5f, 6f, 7f);

            Solve();

            AssertVector(new Vector3(5f, 6f, 7f), target.position,
                "an override on explicit values drives with no source attached");
        }

        [Test]
        public void OverrideTransformInPivotSpaceComposesOntoTheCurrentPose()
        {
            Transform target = NewTransform("Target", new Vector3(2f, 0f, 0f), Quaternion.identity);
            BasisOverrideTransform constraint = Constrain<BasisOverrideTransform>(target);
            constraint.space = BasisOverrideTransform.Space.Pivot;
            constraint.position = new Vector3(3f, 0f, 0f);

            Solve();

            AssertVector(new Vector3(5f, 0f, 0f), target.position,
                "pivot adds to the pose the transform already had");
        }

        [Test]
        public void OverrideTransformAtHalfChannelWeightLandsHalfway()
        {
            Transform target = NewTransform("Target", Vector3.zero, Quaternion.identity);
            BasisOverrideTransform constraint = Constrain<BasisOverrideTransform>(target);
            constraint.position = new Vector3(10f, 0f, 0f);
            constraint.positionWeight = 0.5f;

            Solve();

            AssertVector(new Vector3(5f, 0f, 0f), target.position,
                "an override blends from the current pose, not from a rest pose");
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
