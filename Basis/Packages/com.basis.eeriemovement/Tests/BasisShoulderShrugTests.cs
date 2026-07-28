using NUnit.Framework;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// SHRUG DATA, MINED FROM THE HANDS (or better, the elbows). Arms down at the side, push the shoulders
    /// up: the controller RISES while the arm's DIRECTION barely changes — zero humeral swing, which is the
    /// one quantity the scapulohumeral solve is driven by, so the girdle never moved. What survives is the
    /// REACH DEFICIT: the driver ends up measurably closer to the chest-anchored clavicle than a straight
    /// hanging arm can reach.
    ///
    /// What these tests hold it to:
    ///   - the expected reach is the RAY-SPHERE intersection for the observed direction (the T-pose scalar
    ///     over-expects ~18% on a hanging arm; the law of cosines on the composite direction ~5% — both
    ///     would shrug permanently at idle). At an exact rest hang the deficit must be ZERO;
    ///   - a ~4 cm push-up reads as a strong girdle elevation that LIFTS the arm root, on both arms, with
    ///     no twist leak, and eases in continuously and monotonically;
    ///   - a hand on the hip (big deficit = elbow BEND) must read as NOTHING — the hand path fades back
    ///     out past the shrug band. The ELBOW path must NOT fade: an upper arm cannot bend itself, so a
    ///     tracker's deficit can only be girdle motion;
    ///   - ordinary reaches never engage it (hang gate, on the SEGMENT direction — the composite tilts
    ///     with the rise and would half-close the gate on the short elbow segment).
    /// </summary>
    public class BasisShoulderShrugTests
    {
        const float L = 0.70f;    // T-pose clavicle→hand
        const float C = 0.15f;    // clavicle length
        const float Le = 0.43f;   // T-pose clavicle→elbow
        static readonly Vector3 ClavR = new Vector3(0.05f, 1.40f, 0f);
        static readonly Vector3 RootR = ClavR + new Vector3(C, 0f, 0f);
        static readonly Vector3 HangHandR = RootR + new Vector3(0f, -(L - C), 0f);
        static readonly Vector3 HangElbowR = RootR + new Vector3(0f, -(Le - C), 0f);

        static BasisShoulderSolveInput Input(Vector3 hand, Vector3 elbow, bool hasElbow, float elbowLen)
        {
            BasisShoulderSolveInput s = default;
            s.ShoulderPos = ClavR;
            s.HandTargetPos = hand;
            s.ElbowPos = elbow;
            s.HasElbow = hasElbow;
            s.ChestRot = Quaternion.identity;
            s.TposeChestRot = Quaternion.identity;
            s.TposeShoulderRot = Quaternion.identity;
            s.TposeArmDirWorld = Vector3.right;
            s.TposeArmLength = L;
            s.TposeClavicleLength = C;
            s.TposeElbowLength = elbowLen;
            s.ShrugEnabled = true;
            s.ElevationFactor = 1f;
            s.ProtractionFactor = 1f;
            s.CoupleRatio = 0.4f;
            s.MaxShoulderDeg = 40f;
            s.TrackerFinal = Quaternion.identity;
            return s;
        }

        [Test]
        public void RestHang_MeasuresZeroDeficit_AndStaysStill()
        {
            BasisShoulderSolveCore.Solve(Input(HangHandR, HangElbowR, false, 0f), out BasisShoulderSolveResult r);
            Assert.That(r.Apply, Is.True);
            Assert.That(r.ShrugDeg, Is.EqualTo(0f), "an exact rest hang must measure zero deficit — the ray-sphere expectation");
            Assert.That(r.AppliedAngleDeg, Is.LessThan(1.5f), "the girdle must be ~still on a relaxed hanging arm");
        }

        [Test]
        public void PushUpFromTheSide_IsAShrug_ThatLiftsTheArmRoot()
        {
            BasisShoulderSolveCore.Solve(Input(HangHandR + Vector3.up * 0.04f, HangElbowR, false, 0f), out BasisShoulderSolveResult r);
            Assert.That(r.ShrugDeg, Is.InRange(24f, 44.5f), "a 4 cm push-up is a committed shrug");
            Assert.That((r.ShoulderRotation * Vector3.right).y, Is.GreaterThan(0.15f),
                "the girdle elevation must actually LIFT the right arm root");
            Assert.That(r.TwistLeakDeg, Is.LessThan(0.5f), "a shrug is a swing of the arm root, never a roll");
        }

        [Test]
        public void LeftArm_ShrugsMirrored()
        {
            BasisShoulderSolveInput s = Input(default, HangElbowR, false, 0f);
            s.ShoulderPos = new Vector3(-ClavR.x, ClavR.y, ClavR.z);
            s.HandTargetPos = new Vector3(-HangHandR.x, HangHandR.y + 0.04f, HangHandR.z);
            s.TposeArmDirWorld = Vector3.left;
            s.IsLeft = true;

            BasisShoulderSolveCore.Solve(s, out BasisShoulderSolveResult r);
            Assert.That(r.ShrugDeg, Is.GreaterThan(24f), "the left arm must mine the same shrug");
            Assert.That((r.ShoulderRotation * Vector3.left).y, Is.GreaterThan(0.15f),
                "and its girdle must lift its OWN arm root — one sign convention, mirrored by IsLeft");
        }

        [Test]
        public void HandOnHip_IsAnElbowBend_NotAShrug()
        {
            BasisShoulderSolveCore.Solve(Input(ClavR + new Vector3(0.20f, -0.35f, 0.05f), HangElbowR, false, 0f), out BasisShoulderSolveResult r);
            Assert.That(r.ShrugDeg, Is.EqualTo(0f), "a big deficit is an elbow bend — the hand path must let go entirely");
        }

        [Test]
        public void TenCentimetreLift_HandsOff_BackToTheArmChain()
        {
            BasisShoulderSolveCore.Solve(Input(HangHandR + Vector3.up * 0.10f, HangElbowR, false, 0f), out BasisShoulderSolveResult r);
            Assert.That(r.ShrugDeg, Is.LessThan(10f), "past the shrug band the rise belongs to the arm, not the girdle");
        }

        [Test]
        public void ElbowTracker_IsTheBetterWitness_AndNeverFades()
        {
            BasisShoulderSolveCore.Solve(Input(HangHandR + Vector3.up * 0.04f, HangElbowR + Vector3.up * 0.04f, true, Le), out BasisShoulderSolveResult r4);
            Assert.That(r4.ShrugDeg, Is.GreaterThan(24f), "the elbow tracker must mine the same 4 cm shrug");

            BasisShoulderSolveCore.Solve(Input(HangHandR, HangElbowR + Vector3.up * 0.12f, true, Le), out BasisShoulderSolveResult rBig);
            Assert.That(rBig.ShrugDeg, Is.GreaterThan(36f),
                "an upper arm cannot bend itself: a tracked elbow's deficit is girdle motion at ANY size — no bend fade");

            BasisShoulderSolveCore.Solve(Input(HangHandR, HangElbowR + Vector3.up * 0.06f, true, 0f), out BasisShoulderSolveResult rOff);
            Assert.That(rOff.ShrugDeg, Is.EqualTo(0f), "no baked elbow length (older callers) → the elbow path stands down");
        }

        [Test]
        public void TheBodyTrackingToggle_StandsTheWholeThingDown()
        {
            BasisShoulderSolveInput s = Input(HangHandR + Vector3.up * 0.04f, HangElbowR, false, 0f);
            s.ShrugEnabled = false;

            BasisShoulderSolveCore.Solve(s, out BasisShoulderSolveResult r);
            Assert.That(r.ShrugDeg, Is.EqualTo(0f), "the settings toggle must disable the shrug outright");
            Assert.That(r.AppliedAngleDeg, Is.LessThan(1.5f),
                "with the shrug off, a push-up from the side is back to the pre-feature girdle");
        }

        [Test]
        public void OrdinaryReach_NeverShrugs()
        {
            BasisShoulderSolveCore.Solve(Input(ClavR + new Vector3(0.10f, 0.05f, 0.62f), HangElbowR, false, 0f), out BasisShoulderSolveResult r);
            Assert.That(r.ShrugDeg, Is.EqualTo(0f), "the hang gate must keep the shrug out of ordinary reaches");
        }

        [Test]
        public void TheRise_EasesIn_Continuously()
        {
            float prev = -1f;
            for (float rise = 0f; rise <= 0.12f; rise += 0.002f)
            {
                BasisShoulderSolveCore.Solve(Input(HangHandR + Vector3.up * rise, HangElbowR, false, 0f), out BasisShoulderSolveResult r);
                if (prev >= 0f)
                {
                    Assert.That(Mathf.Abs(r.ShrugDeg - prev), Is.LessThan(5f),
                        $"the shrug must ease, not step (rise {rise * 100f:F1} cm)");
                    if (rise <= 0.055f)
                    {
                        Assert.That(r.ShrugDeg, Is.GreaterThanOrEqualTo(prev - 0.01f),
                            $"inside the band the shrug rises with the hand (rise {rise * 100f:F1} cm)");
                    }
                }
                Assert.That(r.AppliedAngleDeg, Is.LessThanOrEqualTo(40.5f), "the girdle clamp still owns the outcome");
                prev = r.ShrugDeg;
            }
        }
    }
}
