using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Constraints;
using Basis.Scripts.Constraints;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Animations.Rigging;

namespace Basis.Tests.Constraints
{
    /// <summary>
    /// Cover for rewriting Unity and Animation Rigging constraints onto the Basis solver. Compiling
    /// only proves the field names were read correctly; these check that what comes out the far side
    /// drives the same way the original did, that the original is gone, and that the rig goes with it.
    /// </summary>
    public sealed class BasisConstraintConversionTests
    {
        const float Tolerance = 1e-3f;

        readonly List<GameObject> spawned = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (int Index = 0; Index < spawned.Count; Index++)
            {
                if (spawned[Index] != null)
                {
                    Object.DestroyImmediate(spawned[Index]);
                }
            }
            spawned.Clear();
        }

        GameObject New(string name, Vector3 position, Transform parent = null)
        {
            GameObject go = new GameObject(name);
            spawned.Add(go);
            go.transform.SetParent(parent, false);
            go.transform.position = position;
            return go;
        }

        // ── Unity's built-in constraints ──────────────────────────────────────────────────────────

        [Test]
        public void BuiltInPositionConstraintCarriesItsFieldsAcross()
        {
            GameObject source = New("Source", new Vector3(1f, 2f, 3f));
            GameObject target = New("Target", Vector3.zero);

            PositionConstraint original = target.AddComponent<PositionConstraint>();
            original.weight = 0.75f;
            original.translationAtRest = new Vector3(4f, 5f, 6f);
            original.translationOffset = new Vector3(7f, 8f, 9f);
            original.translationAxis = Axis.X | Axis.Z;
            original.AddSource(new ConstraintSource { sourceTransform = source.transform, weight = 0.5f });

            BasisConstraintConversion.ConvertHierarchy(target);

            Assert.IsNull(target.GetComponent<PositionConstraint>(),
                "the original must not survive, or it would drive the transform alongside the replacement");

            BasisPositionConstraint converted = target.GetComponent<BasisPositionConstraint>();
            Assert.IsNotNull(converted, "a Basis equivalent should have taken its place");
            Assert.AreEqual(0.75f, converted.weight, Tolerance, "weight");
            Assert.AreEqual(new Vector3(4f, 5f, 6f), converted.translationAtRest, "rest pose");
            Assert.AreEqual(new Vector3(7f, 8f, 9f), converted.translationOffset, "offset");
            Assert.AreEqual(BasisConstraintAxes.X | BasisConstraintAxes.Z, converted.translationAxis,
                "the axis mask carries over, excluded axes included");
            Assert.AreEqual(1, converted.sourceCount, "source count");
            Assert.AreSame(source.transform, converted.GetSource(0).sourceTransform, "source transform");
            Assert.AreEqual(0.5f, converted.GetSource(0).weight, Tolerance, "source weight");
        }

        [Test]
        public void BuiltInParentConstraintKeepsItsPerSourceOffsets()
        {
            GameObject a = New("A", new Vector3(1f, 0f, 0f));
            GameObject b = New("B", new Vector3(0f, 1f, 0f));
            GameObject target = New("Target", Vector3.zero);

            ParentConstraint original = target.AddComponent<ParentConstraint>();
            original.AddSource(new ConstraintSource { sourceTransform = a.transform, weight = 1f });
            original.AddSource(new ConstraintSource { sourceTransform = b.transform, weight = 1f });
            original.SetTranslationOffset(0, new Vector3(5f, 0f, 0f));
            original.SetTranslationOffset(1, new Vector3(0f, 6f, 0f));

            BasisConstraintConversion.ConvertHierarchy(target);

            BasisParentConstraint converted = target.GetComponent<BasisParentConstraint>();
            Assert.IsNotNull(converted);
            Assert.AreEqual(2, converted.sourceCount, "both sources carried over");
            Assert.AreEqual(new Vector3(5f, 0f, 0f), converted.translationOffsets[0],
                "per-source offsets are indexed by source order and must stay aligned");
            Assert.AreEqual(new Vector3(0f, 6f, 0f), converted.translationOffsets[1]);
        }

        [Test]
        public void ConvertedConstraintActuallyDrivesTheTransform()
        {
            GameObject source = New("Source", new Vector3(3f, 4f, 5f));
            GameObject target = New("Target", Vector3.zero);

            PositionConstraint original = target.AddComponent<PositionConstraint>();
            original.AddSource(new ConstraintSource { sourceTransform = source.transform, weight = 1f });
            original.constraintActive = true;

            BasisConstraintConversion.ConvertHierarchy(target);

            BasisPositionConstraint converted = target.GetComponent<BasisPositionConstraint>();
            BasisConstraintSystem.Register(converted);
            try
            {
                BasisConstraintSystem.Complete(BasisConstraintSystem.Schedule());

                Assert.AreEqual(3f, target.transform.position.x, 1e-2f, "x");
                Assert.AreEqual(4f, target.transform.position.y, 1e-2f, "y");
                Assert.AreEqual(5f, target.transform.position.z, 1e-2f, "z");
            }
            finally
            {
                BasisConstraintSystem.Unregister(converted);
                BasisConstraintSystem.Dispose();
            }
        }

        // ── Animation Rigging ─────────────────────────────────────────────────────────────────────

        [Test]
        public void TwoBoneIKConstraintCarriesItsChainAndTarget()
        {
            GameObject root = New("Root", Vector3.zero);
            GameObject mid = New("Mid", new Vector3(1f, 1f, 0f), root.transform);
            GameObject tip = New("Tip", new Vector3(2f, 0f, 0f), mid.transform);
            GameObject target = New("Target", new Vector3(1.5f, 1f, 0f));
            GameObject hint = New("Hint", new Vector3(1f, 3f, 0f));

            GameObject rig = New("Rig", Vector3.zero);
            TwoBoneIKConstraint original = rig.AddComponent<TwoBoneIKConstraint>();
            TwoBoneIKConstraintData data = original.data;
            data.root = root.transform;
            data.mid = mid.transform;
            data.tip = tip.transform;
            data.target = target.transform;
            data.hint = hint.transform;
            data.targetPositionWeight = 0.8f;
            data.targetRotationWeight = 0.6f;
            data.hintWeight = 0.4f;
            original.data = data;
            original.weight = 0.9f;

            BasisConstraintConversion.ConvertHierarchy(rig);

            // The replacement lives on the tip, not on the object that held the rig constraint.
            BasisTwoBoneIK converted = tip.GetComponent<BasisTwoBoneIK>();
            Assert.IsNotNull(converted, "two-bone IK is rehomed onto the tip bone it drives");
            Assert.AreSame(mid.transform, converted.mid, "mid bone");
            Assert.AreSame(root.transform, converted.root, "root bone");
            Assert.AreEqual(0.9f, converted.weight, Tolerance, "constraint weight");
            Assert.AreEqual(0.8f, converted.targetPositionWeight, Tolerance, "target position weight");
            Assert.AreEqual(0.6f, converted.targetRotationWeight, Tolerance, "target rotation weight");
            Assert.AreEqual(0.4f, converted.hintWeight, Tolerance, "hint weight");
            Assert.AreEqual(2, converted.sourceCount, "target and hint both become sources");
            Assert.AreSame(target.transform, converted.GetSource(0).sourceTransform, "target is source 0");
            Assert.AreSame(hint.transform, converted.GetSource(1).sourceTransform, "hint is source 1");
        }

        [Test]
        public void TwistCorrectionBecomesOneConstraintPerTwistNode()
        {
            GameObject twistSource = New("TwistSource", Vector3.zero);
            GameObject nodeA = New("NodeA", new Vector3(1f, 0f, 0f));
            GameObject nodeB = New("NodeB", new Vector3(2f, 0f, 0f));

            GameObject rig = New("Rig", Vector3.zero);
            TwistCorrection original = rig.AddComponent<TwistCorrection>();
            TwistCorrectionData data = original.data;
            data.sourceObject = twistSource.transform;
            data.twistAxis = TwistCorrectionData.Axis.X;
            // Counter-twist has to be authored by assigning the field, not through the constructor:
            // WeightedTransform's constructor runs Clamp01, which would quietly swallow the negative
            // weight that TwistCorrection's own [WeightRange(-1, 1)] explicitly allows.
            WeightedTransform counterTwist = new WeightedTransform(nodeB.transform, 0f);
            counterTwist.weight = -0.5f;

            WeightedTransformArray nodes = new WeightedTransformArray(0);
            nodes.Add(new WeightedTransform(nodeA.transform, 0.25f));
            nodes.Add(counterTwist);
            data.twistNodes = nodes;
            original.data = data;

            BasisConstraintConversion.ConvertHierarchy(rig);

            BasisTwistCorrection a = nodeA.GetComponent<BasisTwistCorrection>();
            BasisTwistCorrection b = nodeB.GetComponent<BasisTwistCorrection>();
            Assert.IsNotNull(a, "each twist node gets its own constraint");
            Assert.IsNotNull(b);
            Assert.AreEqual(0.25f, a.twistWeight, Tolerance, "each node keeps its own share");
            Assert.AreEqual(-0.5f, b.twistWeight, Tolerance,
                "including a negative share, which counters the twist rather than following it");
            Assert.AreSame(twistSource.transform, a.GetSource(0).sourceTransform, "shared twist source");
        }

        [Test]
        public void MultiReferentialBecomesOneConstraintHoldingTheWholeSet()
        {
            GameObject driver = New("Driver", Vector3.zero);
            GameObject followerA = New("FollowerA", new Vector3(1f, 0f, 0f));
            GameObject followerB = New("FollowerB", new Vector3(0f, 1f, 0f));

            GameObject rig = New("Rig", Vector3.zero);
            MultiReferentialConstraint original = rig.AddComponent<MultiReferentialConstraint>();
            MultiReferentialConstraintData data = original.data;
            data.sourceObjects = new List<Transform>
            {
                driver.transform, followerA.transform, followerB.transform,
            };
            data.driver = 0;
            original.data = data;

            BasisConstraintConversion.ConvertHierarchy(rig);

            // One constraint holds the whole set rather than a follow-the-leader constraint on each
            // non-driver, so the driver index stays live and can change at runtime.
            BasisMultiReferential converted = driver.GetComponent<BasisMultiReferential>();
            Assert.IsNotNull(converted, "the set is held by a single constraint");
            Assert.AreEqual(3, converted.members.Count, "every member is carried across");
            Assert.AreEqual(0, converted.driver, "and the authored driver index with them");
            Assert.AreSame(driver.transform, converted.members[0], "member order is preserved, since " +
                "the driver index addresses into it");
            Assert.AreSame(followerA.transform, converted.members[1]);
            Assert.AreSame(followerB.transform, converted.members[2]);
            Assert.AreEqual(3, converted.BindPositions.Length,
                "and the arrangement is captured for all of them, not just the followers");
        }

        [Test]
        public void DampedTransformCarriesItsDampingAcross()
        {
            GameObject source = New("Source", Vector3.zero);
            GameObject target = New("Target", new Vector3(1f, 0f, 0f));

            GameObject rig = New("Rig", Vector3.zero);
            DampedTransform original = rig.AddComponent<DampedTransform>();
            DampedTransformData data = original.data;
            data.constrainedObject = target.transform;
            data.sourceObject = source.transform;
            data.dampPosition = 0.3f;
            data.dampRotation = 0.7f;
            data.maintainAim = false;
            original.data = data;

            BasisConstraintConversion.ConvertHierarchy(rig);

            BasisDampedTransform converted = target.GetComponent<BasisDampedTransform>();
            Assert.IsNotNull(converted);
            Assert.AreEqual(0.3f, converted.dampPosition, Tolerance, "damp position");
            Assert.AreEqual(0.7f, converted.dampRotation, Tolerance, "damp rotation");
            Assert.IsFalse(converted.maintainAim, "maintain aim");
        }

        [Test]
        public void TwistChainBecomesOneConstraintPerBoneWithTheCurveAlreadySampled()
        {
            GameObject b0 = New("B0", Vector3.zero);
            GameObject b1 = New("B1", new Vector3(1f, 0f, 0f), b0.transform);
            GameObject b2 = New("B2", new Vector3(2f, 0f, 0f), b1.transform);
            GameObject rootTarget = New("RootTarget", Vector3.zero);
            GameObject tipTarget = New("TipTarget", Vector3.zero);

            GameObject rig = New("Rig", Vector3.zero);
            TwistChainConstraint original = rig.AddComponent<TwistChainConstraint>();
            TwistChainConstraintData data = original.data;
            data.root = b0.transform;
            data.tip = b2.transform;
            data.rootTarget = rootTarget.transform;
            data.tipTarget = tipTarget.transform;
            data.curve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
            original.data = data;

            BasisConstraintConversion.ConvertHierarchy(rig);

            BasisTwistChain first = b0.GetComponent<BasisTwistChain>();
            BasisTwistChain middle = b1.GetComponent<BasisTwistChain>();
            BasisTwistChain last = b2.GetComponent<BasisTwistChain>();
            Assert.IsNotNull(first, "every bone in the chain gets its own constraint");
            Assert.IsNotNull(middle);
            Assert.IsNotNull(last);

            Assert.AreEqual(0f, first.blend, Tolerance, "the root end is pinned to the root target");
            Assert.AreEqual(1f, last.blend, Tolerance, "the tip end is pinned to the tip target");
            Assert.AreEqual(0.5f, middle.blend, Tolerance,
                "a linear curve puts the midpoint bone halfway between the two ends");

            Assert.AreEqual(2, middle.sourceCount, "both ends are sources");
            Assert.AreSame(rootTarget.transform, middle.GetSource(0).sourceTransform, "root end is source 0");
            Assert.AreSame(tipTarget.transform, middle.GetSource(1).sourceTransform, "tip end is source 1");
        }

        [Test]
        public void AConstraintWithMissingReferencesIsCountedAndStillRemoved()
        {
            // Uploaded content routinely ships constraints wired to objects that no longer exist.
            GameObject rig = New("Rig", Vector3.zero);
            TwoBoneIKConstraint broken = rig.AddComponent<TwoBoneIKConstraint>();

            BasisConstraintConversion.Report report = BasisConstraintConversion.ConvertHierarchy(rig);

            Assert.AreEqual(0, report.Converted, "there was nothing usable to convert");
            Assert.AreEqual(1, report.Unsupported, "and that is reported rather than passing silently");
            Assert.IsTrue(broken == null,
                "the original still goes: leaving it would cost a per-frame update for a rig that " +
                "is no longer there");
        }

        [Test]
        public void ConversionSurvivesAConstraintWithNoSources()
        {
            GameObject target = New("Target", new Vector3(1f, 2f, 3f));
            target.AddComponent<PositionConstraint>();

            BasisConstraintConversion.Report report = BasisConstraintConversion.ConvertHierarchy(target);

            BasisPositionConstraint converted = target.GetComponent<BasisPositionConstraint>();
            Assert.IsNotNull(converted, "a sourceless constraint still converts");
            Assert.AreEqual(0, converted.sourceCount, "it just has nothing driving it");
            Assert.AreEqual(1, report.Converted);
        }

        [Test]
        public void ConvertedConstraintsStillOrderByDependency()
        {
            // B is constrained to A, C to B. Conversion must preserve that the chain resolves in one
            // frame — the ordering is derived from sources, so it has to survive the rewrite.
            GameObject a = New("A", new Vector3(9f, 0f, 0f));
            GameObject b = New("B", Vector3.zero);
            GameObject c = New("C", Vector3.zero);

            // constraintActive defaults to false on Unity's constraints, and conversion carries that
            // across faithfully — an inactive original stays inactive.
            PositionConstraint bToA = b.AddComponent<PositionConstraint>();
            bToA.AddSource(new ConstraintSource { sourceTransform = a.transform, weight = 1f });
            bToA.constraintActive = true;
            PositionConstraint cToB = c.AddComponent<PositionConstraint>();
            cToB.AddSource(new ConstraintSource { sourceTransform = b.transform, weight = 1f });
            cToB.constraintActive = true;

            GameObject holder = New("Holder", Vector3.zero);
            b.transform.SetParent(holder.transform, true);
            c.transform.SetParent(holder.transform, true);
            BasisConstraintConversion.ConvertHierarchy(holder);

            BasisPositionConstraint convertedB = b.GetComponent<BasisPositionConstraint>();
            BasisPositionConstraint convertedC = c.GetComponent<BasisPositionConstraint>();
            BasisConstraintSystem.Register(convertedB);
            BasisConstraintSystem.Register(convertedC);
            try
            {
                BasisConstraintSystem.Complete(BasisConstraintSystem.Schedule());

                Assert.AreEqual(9f, c.transform.position.x, 1e-2f,
                    "C sees B already solved in the same frame it was solved");
            }
            finally
            {
                BasisConstraintSystem.Unregister(convertedB);
                BasisConstraintSystem.Unregister(convertedC);
                BasisConstraintSystem.Dispose();
            }
        }

        [Test]
        public void ConvertedTwoBoneIKActuallyReachesItsTarget()
        {
            GameObject root = New("Root", Vector3.zero);
            GameObject mid = New("Mid", new Vector3(1f, 1f, 0f), root.transform);
            GameObject tip = New("Tip", new Vector3(2f, 0f, 0f), mid.transform);
            GameObject target = New("Target", new Vector3(1.5f, 1f, 0f));

            GameObject rig = New("Rig", Vector3.zero);
            TwoBoneIKConstraint original = rig.AddComponent<TwoBoneIKConstraint>();
            TwoBoneIKConstraintData data = original.data;
            data.root = root.transform;
            data.mid = mid.transform;
            data.tip = tip.transform;
            data.target = target.transform;
            data.targetPositionWeight = 1f;
            data.targetRotationWeight = 0f;
            data.hintWeight = 0f;
            original.data = data;
            original.weight = 1f;

            BasisConstraintConversion.ConvertHierarchy(rig);

            BasisTwoBoneIK converted = tip.GetComponent<BasisTwoBoneIK>();
            BasisConstraintSystem.Register(converted);
            try
            {
                BasisConstraintSystem.Complete(BasisConstraintSystem.Schedule());

                Assert.Less(Vector3.Distance(tip.transform.position, target.transform.position), 0.05f,
                    "the converted limb reaches, end to end through the real solve and write");
            }
            finally
            {
                BasisConstraintSystem.Unregister(converted);
                BasisConstraintSystem.Dispose();
            }
        }

        [Test]
        public void SeveralConstraintsOnOneObjectAllConvert()
        {
            GameObject source = New("Source", new Vector3(1f, 2f, 3f));
            GameObject target = New("Target", Vector3.zero);

            PositionConstraint position = target.AddComponent<PositionConstraint>();
            position.AddSource(new ConstraintSource { sourceTransform = source.transform, weight = 1f });
            RotationConstraint rotation = target.AddComponent<RotationConstraint>();
            rotation.AddSource(new ConstraintSource { sourceTransform = source.transform, weight = 1f });

            BasisConstraintConversion.Report report = BasisConstraintConversion.ConvertHierarchy(target);

            Assert.AreEqual(2, report.Converted, "stacking position and rotation is routine");
            Assert.IsNotNull(target.GetComponent<BasisPositionConstraint>());
            Assert.IsNotNull(target.GetComponent<BasisRotationConstraint>());
            Assert.IsNull(target.GetComponent<PositionConstraint>(), "neither original survives");
            Assert.IsNull(target.GetComponent<RotationConstraint>());
        }

        // ── Bindings that name a constraint by type ───────────────────────────────────────────────

        /// <summary>
        /// Builds a real Vixxy control driving a constraint. The subjects array is internal, so the
        /// test sets it the same way the remap reads it — the point is that the walk finds the binding
        /// through Vixxy's actual nested structure, not a stand-in shaped like it.
        /// </summary>
        static HVR.Vixxy.HVRVixxyControl NewVixxyControlDriving(
            GameObject host, GameObject targetObject, string fullClassName, string propertyName)
        {
            HVR.Vixxy.HVRVixxyControl control = host.AddComponent<HVR.Vixxy.HVRVixxyControl>();

            HVR.Vixxy.HVRVixxyPropertyFloat property = new HVR.Vixxy.HVRVixxyPropertyFloat
            {
                fullClassName = fullClassName,
                propertyName = propertyName,
            };
            HVR.Vixxy.HVRVixxySubject subject = new HVR.Vixxy.HVRVixxySubject
            {
                targets = new[] { targetObject },
                properties = new List<HVR.Vixxy.HVRVixxyPropertyBase> { property },
            };

            typeof(HVR.Vixxy.HVRVixxyControl)
                .GetField("subjects", System.Reflection.BindingFlags.Instance
                                      | System.Reflection.BindingFlags.NonPublic)
                .SetValue(control, new[] { subject });
            return control;
        }

        static string FirstBoundClassName(HVR.Vixxy.HVRVixxyControl control)
        {
            object subjects = typeof(HVR.Vixxy.HVRVixxyControl)
                .GetField("subjects", System.Reflection.BindingFlags.Instance
                                      | System.Reflection.BindingFlags.NonPublic)
                .GetValue(control);
            HVR.Vixxy.HVRVixxySubject[] typed = (HVR.Vixxy.HVRVixxySubject[])subjects;
            return typed[0].properties[0].fullClassName;
        }

        [Test]
        public void AVixxyBindingOnAConvertedConstraintIsRepointed()
        {
            GameObject source = New("Source", new Vector3(1f, 0f, 0f));
            GameObject target = New("Target", Vector3.zero);
            PositionConstraint original = target.AddComponent<PositionConstraint>();
            original.AddSource(new ConstraintSource { sourceTransform = source.transform, weight = 1f });
            original.constraintActive = true;

            GameObject host = New("VixxyHost", Vector3.zero);
            target.transform.SetParent(host.transform, true);
            HVR.Vixxy.HVRVixxyControl control = NewVixxyControlDriving(
                host, target, "UnityEngine.Animations.PositionConstraint", "weight");

            BasisConstraintConversion.Report report = BasisConstraintConversion.ConvertHierarchy(host);

            Assert.AreEqual("Basis.Scripts.BasisSdk.Constraints.BasisPositionConstraint",
                FirstBoundClassName(control),
                "the binding follows the constraint onto its replacement, or the toggle would " +
                "resolve to a component that no longer exists and silently do nothing");
            Assert.AreEqual(1, report.Rebound, "and the rebind is reported");
            Assert.IsNull(target.GetComponent<PositionConstraint>(), "the original is still removed");
        }

        [Test]
        public void AVixxyBindingOnSomethingElseIsLeftAlone()
        {
            GameObject target = New("Target", Vector3.zero);
            target.AddComponent<MeshRenderer>();

            GameObject host = New("VixxyHost", Vector3.zero);
            target.transform.SetParent(host.transform, true);
            HVR.Vixxy.HVRVixxyControl control = NewVixxyControlDriving(
                host, target, "UnityEngine.MeshRenderer", "enabled");

            BasisConstraintConversion.Report report = BasisConstraintConversion.ConvertHierarchy(host);

            Assert.AreEqual("UnityEngine.MeshRenderer", FirstBoundClassName(control),
                "a binding that never named a constraint must not be touched");
            Assert.AreEqual(0, report.Rebound);
        }

        [Test]
        public void ARiggingBindingIsRepointedToo()
        {
            GameObject host = New("VixxyHost", Vector3.zero);
            GameObject target = New("Target", Vector3.zero, host.transform);
            HVR.Vixxy.HVRVixxyControl control = NewVixxyControlDriving(
                host, target, "UnityEngine.Animations.Rigging.TwoBoneIKConstraint", "weight");

            BasisConstraintConversion.ConvertHierarchy(host);

            Assert.AreEqual("Basis.Scripts.BasisSdk.Constraints.BasisTwoBoneIK",
                FirstBoundClassName(control),
                "Animation Rigging bindings are repointed as well as the built-in ones");
        }

        // ── Stripping the rig ─────────────────────────────────────────────────────────────────────

        [Test]
        public void TheRigItselfIsRemoved()
        {
            GameObject root = New("Root", Vector3.zero);
            root.AddComponent<Animator>();
            root.AddComponent<RigBuilder>();
            GameObject rig = New("Rig", Vector3.zero, root.transform);
            rig.AddComponent<Rig>();
            GameObject bone = New("Bone", Vector3.zero, rig.transform);
            bone.AddComponent<RigTransform>();

            BasisConstraintConversion.Report report = BasisConstraintConversion.ConvertHierarchy(root);

            Assert.IsNull(root.GetComponent<RigBuilder>(), "the rig builder and its playable graph go");
            Assert.IsNull(rig.GetComponent<Rig>(), "the rig goes");
            Assert.IsNull(bone.GetComponent<RigTransform>(), "and its scaffolding transforms go");
            Assert.AreEqual(3, report.Removed, "all three are counted as removed");
            Assert.IsNotNull(root.GetComponent<Animator>(),
                "the animator is not ours to remove — the local player still drives one");
        }

        [Test]
        public void ContentWithNoConstraintsIsLeftAlone()
        {
            GameObject root = New("Root", Vector3.zero);
            root.AddComponent<MeshRenderer>();

            BasisConstraintConversion.Report report = BasisConstraintConversion.ConvertHierarchy(root);

            Assert.IsFalse(report.DidAnything, "nothing to do, and nothing reported");
            Assert.IsNotNull(root.GetComponent<MeshRenderer>(), "unrelated components are untouched");
        }
    }
}
