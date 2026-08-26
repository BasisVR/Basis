using NUnit.Framework;
using UnityEngine;
using Basis.IK;
using Basis.Scripts.Common;
namespace Basis.Tests.IK
{
    public class BasisBodyFitNetworkingTests
    {
        const float Eps = 1e-5f;
        // ── Wire scale -> fit result ────────────────────────────────────────────
        [Test]
        public void ToFitResult_RealScales_RoundTripAndReportFitted()
        {
            BasisBodyFitResult fit = BasisBodyFitNetworking.ToFitResult(1.0625f, 0.9375f, 1.125f);

            Assert.AreEqual(1.0625f, fit.ArmScale, Eps);
            Assert.AreEqual(0.9375f, fit.LegScale, Eps);
            Assert.AreEqual(1.125f, fit.TorsoScale, Eps);
            Assert.IsTrue(fit.HasArmFit);
            Assert.IsTrue(fit.HasBodyFit);
        }
        [Test]
        public void ToFitResult_Identity_ReportsUnfitted()
        {
            BasisBodyFitResult fit = BasisBodyFitNetworking.ToFitResult(1f, 1f, 1f);

            Assert.IsFalse(fit.HasArmFit, "an identity arm scale is not a deformation");
            Assert.IsFalse(fit.HasBodyFit, "an identity leg/torso pair is not a deformation");
            Assert.IsTrue(fit.IsIdentity);
        }
        [Test]
        public void ToFitResult_ArmsFittedBodyNot_KeepsTheHalvesIndependent()
        {
            BasisBodyFitResult fit = BasisBodyFitNetworking.ToFitResult(1.1f, 1f, 1f);

            Assert.IsTrue(fit.HasArmFit);
            Assert.IsFalse(fit.HasBodyFit);
        }
        [Test]
        public void ToFitResult_DegenerateScales_FallBackToIdentity()
        {
            BasisBodyFitResult fit = BasisBodyFitNetworking.ToFitResult(0f, float.NaN, -1f);

            Assert.AreEqual(1f, fit.ArmScale, Eps);
            Assert.AreEqual(1f, fit.LegScale, Eps);
            Assert.AreEqual(1f, fit.TorsoScale, Eps);
            Assert.IsTrue(fit.IsIdentity);
        }
        [Test]
        public void ToFitResult_OutOfBandScale_IsClampedToTheValidBand()
        {
            BasisBodyFitResult fit = BasisBodyFitNetworking.ToFitResult(1e9f, 1e-9f, 1f);

            Assert.AreEqual(1.5f, fit.ArmScale, Eps);
            Assert.AreEqual(0.5f, fit.LegScale, Eps);
        }
        [Test]
        public void SolvedFit_SurvivesTheWireUnchanged()
        {
            var measurements = new BasisBodyFitMeasurements
            {
                PlayerEyeHeight = 1.75f,
                PlayerArmSpan = 1.82f,
                PlayerHipHeight = 0.98f,
                AvatarEyeHeight = 1.60f,
                AvatarArmSpan = 1.65f,
                AvatarHipHeight = 0.90f,
                AvatarLegSpan = 0.84f,
                AvatarSpineSpan = 0.55f,
                AvatarShoulderWidth = 0.35f,
            };
            BasisBodyFitResult solved = BasisBodyFitCore.Solve(measurements, 0.15f);
            Assume.That(solved.IsIdentity, Is.False, "test needs a fit that actually deforms");

            // Mirrors what the transmitter puts on the wire. The byte-level round trip is pinned
            // server-side (BodyFitMessageWireTests); this pins that no scale is lost in between.
            float wireArm = solved.HasArmFit ? solved.ArmScale : 1f, wireLeg = solved.HasBodyFit ? solved.LegScale : 1f;
            float wireTorso = solved.HasBodyFit ? solved.TorsoScale : 1f;
            BasisBodyFitResult received = BasisBodyFitNetworking.ToFitResult(wireArm, wireLeg, wireTorso);

            Assert.AreEqual(solved.ArmScale, received.ArmScale, Eps);
            Assert.AreEqual(solved.LegScale, received.LegScale, Eps);
            Assert.AreEqual(solved.TorsoScale, received.TorsoScale, Eps);
        }
        // ── Shared bone/scale table ─────────────────────────────────────────────
        class Rig
        {
            public readonly BasisTransformMapping Mapping = new BasisTransformMapping();
            readonly GameObject root;
            public Rig(bool withTwists)
            {
                root = new GameObject("fit-test-rig");

                Mapping.leftLowerArm = Bone("leftLowerArm");
                Mapping.leftHand = Bone("leftHand");
                Mapping.RightLowerArm = Bone("rightLowerArm");
                Mapping.rightHand = Bone("rightHand");

                Mapping.leftUpperArmTwist = Bone("leftUpperArmTwist");
                Mapping.leftLowerArmTwist = Bone("leftLowerArmTwist");
                Mapping.RightUpperArmTwist = Bone("rightUpperArmTwist");
                Mapping.RightLowerArmTwist = Bone("rightLowerArmTwist");
                Mapping.HasleftUpperArmTwist = withTwists;
                Mapping.HasleftLowerArmTwist = withTwists;
                Mapping.HasRightUpperArmTwist = withTwists;
                Mapping.HasRightLowerArmTwist = withTwists;

                Mapping.LeftLowerLeg = Bone("leftLowerLeg");
                Mapping.leftFoot = Bone("leftFoot");
                Mapping.RightLowerLeg = Bone("rightLowerLeg");
                Mapping.rightFoot = Bone("rightFoot");

                Mapping.spine = Bone("spine");
                Mapping.chest = Bone("chest");
                Mapping.Upperchest = Bone("upperChest");
                Mapping.neck = Bone("neck");
                Mapping.head = Bone("head");
            }
            Transform Bone(string name)
            {
                var go = new GameObject(name);
                go.transform.SetParent(root.transform);
                return go.transform;
            }
            public void Destroy() => Object.DestroyImmediate(root);
        }
        [Test]
        public void CollectBones_FillsEverySlot_WhenTheRigIsComplete()
        {
            var rig = new Rig(withTwists: true);
            try
            {
                var bones = new Transform[BasisBodyFitApply.BoneCount];
                BasisBodyFitApply.CollectBones(rig.Mapping, bones);

                CollectionAssert.DoesNotContain(bones, null);
                CollectionAssert.AllItemsAreUnique(bones);
            }
            finally
            {
                rig.Destroy();
            }
        }
        [Test]
        public void CollectBones_MissingTwists_LeaveHolesWithoutShiftingOtherSlots()
        {
            var withTwists = new Rig(withTwists: true);
            var withoutTwists = new Rig(withTwists: false);
            try
            {
                var a = new Transform[BasisBodyFitApply.BoneCount];
                var b = new Transform[BasisBodyFitApply.BoneCount];
                BasisBodyFitApply.CollectBones(withTwists.Mapping, a);
                BasisBodyFitApply.CollectBones(withoutTwists.Mapping, b);

                for (int i = 0; i < BasisBodyFitApply.BoneCount; i++)
                {
                    bool isTwistSlot = i == 2 || i == 3 || i == 6 || i == 7;
                    if (isTwistSlot)
                    {
                        Assert.IsNull(b[i], $"slot {i} should be empty without twist detection");
                    }
                    else
                    {
                        Assert.AreEqual(a[i].name, b[i].name, $"slot {i} moved when twists were absent");
                    }
                }
            }
            finally
            {
                withTwists.Destroy();
                withoutTwists.Destroy();
            }
        }
        [Test]
        public void CollectScales_MapsEachSegmentGroupToItsOwnScale()
        {
            BasisBodyFitResult fit = BasisBodyFitNetworking.ToFitResult(1.1f, 0.9f, 1.05f);
            var scales = new float[BasisBodyFitApply.BoneCount];
            BasisBodyFitApply.CollectScales(in fit, scales);

            for (int i = 0; i < 8; i++)
            {
                Assert.AreEqual(1.1f, scales[i], Eps, $"arm slot {i}");
            }
            for (int i = 8; i < 12; i++)
            {
                Assert.AreEqual(0.9f, scales[i], Eps, $"leg slot {i}");
            }
            for (int i = 12; i < BasisBodyFitApply.BoneCount; i++)
            {
                Assert.AreEqual(1.05f, scales[i], Eps, $"torso slot {i}");
            }
        }
        [Test]
        public void CollectScales_UnfittedHalf_StaysAtOne()
        {
            // Arms fitted, body not — the body slots must not inherit the arm scale.
            BasisBodyFitResult fit = BasisBodyFitNetworking.ToFitResult(1.1f, 1f, 1f);
            var scales = new float[BasisBodyFitApply.BoneCount];
            BasisBodyFitApply.CollectScales(in fit, scales);

            for (int i = 8; i < BasisBodyFitApply.BoneCount; i++)
            {
                Assert.AreEqual(1f, scales[i], Eps, $"slot {i} should be undeformed");
            }
        }
        [Test]
        public void RestTimesScale_IsIdempotentAcrossRepeatedApplies()
        {
            var rig = new Rig(withTwists: true);
            try
            {
                var bones = new Transform[BasisBodyFitApply.BoneCount];
                BasisBodyFitApply.CollectBones(rig.Mapping, bones);

                var rest = new Vector3[BasisBodyFitApply.BoneCount];
                for (int i = 0; i < bones.Length; i++)
                {
                    bones[i].localPosition = new Vector3(0f, 0.25f + i * 0.01f, 0f);
                    rest[i] = bones[i].localPosition;
                }

                BasisBodyFitResult fit = BasisBodyFitNetworking.ToFitResult(1.1f, 0.9f, 1.05f);
                var scales = new float[BasisBodyFitApply.BoneCount];
                BasisBodyFitApply.CollectScales(in fit, scales);

                for (int pass = 0; pass < 5; pass++)
                {
                    for (int i = 0; i < bones.Length; i++)
                    {
                        bones[i].localPosition = rest[i] * scales[i];
                    }
                }

                for (int i = 0; i < bones.Length; i++)
                {
                    Assert.AreEqual(rest[i].y * scales[i], bones[i].localPosition.y, Eps, $"slot {i} drifted across repeated applies");
                }
            }
            finally
            {
                rig.Destroy();
            }
        }
        [Test]
        public void IdentityFit_RestoresTheAuthoredBind()
        {
            var rig = new Rig(withTwists: true);
            try
            {
                var bones = new Transform[BasisBodyFitApply.BoneCount];
                BasisBodyFitApply.CollectBones(rig.Mapping, bones);

                var rest = new Vector3[BasisBodyFitApply.BoneCount];
                for (int i = 0; i < bones.Length; i++)
                {
                    bones[i].localPosition = new Vector3(0f, 0.3f, 0f);
                    rest[i] = bones[i].localPosition;
                }

                var scales = new float[BasisBodyFitApply.BoneCount];

                BasisBodyFitResult fitted = BasisBodyFitNetworking.ToFitResult(1.1f, 0.9f, 1.05f);
                BasisBodyFitApply.CollectScales(in fitted, scales);
                for (int i = 0; i < bones.Length; i++)
                {
                    bones[i].localPosition = rest[i] * scales[i];
                }

                BasisBodyFitResult off = BasisBodyFitNetworking.ToFitResult(1f, 1f, 1f);
                BasisBodyFitApply.CollectScales(in off, scales);
                for (int i = 0; i < bones.Length; i++)
                {
                    bones[i].localPosition = rest[i] * scales[i];
                }

                for (int i = 0; i < bones.Length; i++)
                {
                    Assert.AreEqual(rest[i].y, bones[i].localPosition.y, Eps, $"slot {i} not restored");
                }
            }
            finally
            {
                rig.Destroy();
            }
        }
    }
}
