using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisLimbRollConditioningTests
    {
        const float ThighLen = 0.42f, ShinLen = 0.42f;
        const float LegReach = ThighLen + ShinLen;
        const float UpperLen = 0.30f, ForeLen = 0.30f;
        const float ArmReach = UpperLen + ForeLen;
        static readonly Vector3 LegBoneAxis = Vector3.down;    // hip → knee in the thigh's own frame
        static readonly Vector3 ArmBoneAxis = Vector3.right;   // shoulder → elbow, a T-pose arm
        static readonly Vector3 BendNormal = Vector3.right;    // a knee bends in the sagittal plane
        static readonly Vector3 Root = Vector3.zero;
        const float RollGateDeg = 2.0f;
        static readonly Vector3[] Nudges =
        {
            new Vector3(0.001f, 0f, 0f), new Vector3(-0.001f, 0f, 0f),
            new Vector3(0f, 0.001f, 0f), new Vector3(0f, -0.001f, 0f),
            new Vector3(0f, 0f, 0.001f), new Vector3(0f, 0f, -0.001f),
        };
        static IEnumerable<float> Extensions()
        {
            for (float e = 0.95f; e < 0.99f; e += 0.01f) yield return e;
            for (float e = 0.99f; e <= 1.0021f; e += 0.0005f) yield return e;
        }
        static float RollDeg(Quaternion before, Quaternion after, Vector3 boneLocalAxis)
        {
            Vector3 axis = (after * boneLocalAxis).normalized;
            Quaternion delta = after * Quaternion.Inverse(before);
            if (delta.w < 0f) delta = new Quaternion(-delta.x, -delta.y, -delta.z, -delta.w);
            Vector3 v = new Vector3(delta.x, delta.y, delta.z);
            float twist = 2f * Mathf.Atan2(Vector3.Dot(v, axis), delta.w) * Mathf.Rad2Deg;
            return Mathf.Abs(Mathf.DeltaAngle(0f, twist));
        }
        static void Pose(float flexDeg, float bulgeAzimDeg, float rollDeg, Vector3 boneAxis, Vector3 straight, float upper, float lower, out Vector3 mid, out Vector3 tip, out Quaternion rootRot, out Quaternion midRot)
        {
            float f = flexDeg * Mathf.Deg2Rad;
            Vector3 seed = Mathf.Abs(Vector3.Dot(straight, Vector3.forward)) > 0.9f ? Vector3.up : Vector3.forward;
            Vector3 bulge0 = Vector3.Cross(straight, Vector3.Cross(seed, straight)).normalized;
            Vector3 bulge = Quaternion.AngleAxis(bulgeAzimDeg, straight) * bulge0;
            Vector3 upperDir = (straight * Mathf.Cos(f) + bulge * Mathf.Sin(f)).normalized;
            Vector3 lowerDir = (straight * Mathf.Cos(f) - bulge * Mathf.Sin(f)).normalized;

            rootRot = Quaternion.FromToRotation(boneAxis, upperDir) * Quaternion.AngleAxis(rollDeg, boneAxis);
            midRot = Quaternion.FromToRotation(boneAxis, lowerDir) * Quaternion.AngleAxis(rollDeg, boneAxis);
            mid = Root + rootRot * boneAxis * upper;
            tip = mid + midRot * boneAxis * lower;
        }
        static Vector3 Anterior(Vector3 target)
        {
            Vector3 axis = (target - Root).normalized, a = Vector3.Cross(axis, BendNormal);
            return a.sqrMagnitude < 1e-8f ? Vector3.forward : a.normalized;
        }
        static Vector3 LegTarget(float extension, float elevDeg, float azimDeg, Vector3 nudge)
        {
            Vector3 dir = Quaternion.AngleAxis(azimDeg, Vector3.up) * (Quaternion.AngleAxis(elevDeg, Vector3.right) * Vector3.down);
            return Root + dir.normalized * (extension * LegReach) + nudge;
        }
        static BasisLegSolveResult SolveLeg(Vector3 target, float poleAzimDeg, float animFlexDeg, float animAzimDeg)
        {
            Pose(animFlexDeg, animAzimDeg, 12f, LegBoneAxis, Vector3.down, ThighLen, ShinLen, out Vector3 knee, out Vector3 ankle, out Quaternion rootRot, out Quaternion midRot);

            Vector3 axis = (target - Root).normalized, dir = Quaternion.AngleAxis(poleAzimDeg, axis) * Anterior(target);

            var input = new BasisLegSolveInput
            {
                Root = Root, Mid = knee, Tip = ankle,
                RootRotation = rootRot, MidRotation = midRot,
                TargetPosition = target,
                TargetRotation = Quaternion.identity, TargetOffset = Quaternion.identity,
                HintPosition = Root + axis * (0.5f * LegReach) + dir * (0.35f * LegReach),
                HintWeight = 1f, BendNormal = BendNormal,
            };
            BasisLegSolveCore.Solve(input, out BasisLegSolveResult r);
            return r;
        }
        static Vector3 ArmTarget(float extension, float elevDeg, float azimDeg, Vector3 nudge)
        {
            Vector3 dir = Quaternion.AngleAxis(azimDeg, Vector3.up) * (Quaternion.AngleAxis(elevDeg, Vector3.forward) * Vector3.right);
            return Root + dir.normalized * (extension * ArmReach) + nudge;
        }
        static BasisArmSolveResult SolveArm(Vector3 target, float poleAzimDeg, float animFlexDeg, float animAzimDeg)
        {
            Pose(animFlexDeg, animAzimDeg, 15f, ArmBoneAxis, Vector3.right, UpperLen, ForeLen, out Vector3 elbow, out Vector3 hand, out Quaternion rootRot, out Quaternion midRot);

            Vector3 axis = (target - Root).normalized, down = Vector3.down - axis * Vector3.Dot(Vector3.down, axis);
            down = down.sqrMagnitude < 1e-8f ? Vector3.forward : down.normalized;
            Vector3 dir = Quaternion.AngleAxis(poleAzimDeg, axis) * down;

            var input = new BasisArmSolveInput
            {
                Shoulder = Root, Elbow = elbow, Hand = hand,
                RootRotation = rootRot, MidRotation = midRot,
                TargetPosition = target,
                TargetRotation = Quaternion.identity, TargetOffset = Quaternion.identity,
                HintPosition = Root + axis * (0.5f * ArmReach) + dir * (0.3f * ArmReach),
                HintWeight = true, HintIsTracker = false,
                PlayerUp = Vector3.up,
                HintMaxStepDeg = float.MaxValue,   // exactly what BasisFullBodyIK passes the live rig
            };
            BasisArmSolveCore.Solve(input, out BasisArmSolveResult r);
            return r;
        }
        // ── THE DEFECT ────────────────────────────────────────────────────────────────────────────────────
        [Test]
        public void Leg_TheThighDoesNotRoll_WhenTheFootTargetIsNudgedAMillimetre()
        {
            float worst = 0f;
            string where = "";

            foreach (float ext in Extensions())
                foreach (float elev in new[] { -40f, 0f, 30f, 60f })
                    foreach (float azim in new[] { -25f, 0f, 25f })
                        foreach (float pole in new[] { -45f, 0f, 45f, 80f })
                            foreach (float flex in new[] { 3f, 30f })
                                foreach (float animAzim in new[] { 0f, 150f, 250f })
                                {
                                    BasisLegSolveResult a = SolveLeg(LegTarget(ext, elev, azim, Vector3.zero), pole, flex, animAzim);

                                    foreach (Vector3 nudge in Nudges)
                                    {
                                        BasisLegSolveResult b = SolveLeg(LegTarget(ext, elev, azim, nudge), pole, flex, animAzim);
                                        float roll = RollDeg(a.RootRotationSolved, b.RootRotationSolved, LegBoneAxis);
                                        if (roll > worst)
                                        {
                                            worst = roll;
                                            where = $"extension {ext:0.0000}, foot elev {elev:0}° azim {azim:0}°, pole {pole:0}°, animated knee at {animAzim:0}°";
                                        }
                                    }
                                }

            Assert.That(worst, Is.LessThan(RollGateDeg), $"A 1 mm nudge of the foot target rolled the thigh {worst:0.0}° about its own axis ({where}). " + $"The foot never left its target and the knee barely moved — this is the leg spinning inside its mesh.");
        }
        [Test]
        public void Arm_TheUpperArmDoesNotRoll_WhenTheHandTargetIsNudgedAMillimetre()
        {
            float worst = 0f;
            string where = "";

            foreach (float ext in Extensions())
                foreach (float elev in new[] { -40f, -15f, 0f, 25f, 50f })
                    foreach (float azim in new[] { -40f, 0f, 40f })
                        foreach (float pole in new[] { -60f, 0f, 60f })
                            foreach (float flex in new[] { 5f, 45f })
                                foreach (float animAzim in new[] { 0f, 90f, 200f })
                                {
                                    BasisArmSolveResult a = SolveArm(ArmTarget(ext, elev, azim, Vector3.zero), pole, flex, animAzim);

                                    foreach (Vector3 nudge in Nudges)
                                    {
                                        BasisArmSolveResult b = SolveArm(ArmTarget(ext, elev, azim, nudge), pole, flex, animAzim);
                                        float roll = RollDeg(a.RootRotationSolved, b.RootRotationSolved, ArmBoneAxis);
                                        if (roll > worst)
                                        {
                                            worst = roll;
                                            where = $"extension {ext:0.0000}, hand elev {elev:0}° azim {azim:0}°, pole {pole:0}°, animated elbow at {animAzim:0}°";
                                        }
                                    }
                                }

            Assert.That(worst, Is.LessThan(RollGateDeg), $"A 1 mm nudge of the hand target rolled the upper arm {worst:0.0}° about its own axis ({where}). " + $"A VR user reaching or pointing lives here: 57% of the mocap corpus is past 95% extension.");
        }
        // ── THE GUARDRAILS. Without these, "never swivel" passes everything above. ────────────────────────
        [Test]
        public void Leg_TheKneeStillLandsOnItsCommandedPole_AtEveryExtension()
        {
            // The animated knee deliberately starts bulging at 200° — the WRONG side — so a solver that quietly
            // stopped steering is caught landing on the animation instead of on the pole. Poles stay inside the
            // anterior guard's free band (|θ| ≤ 85°), where it is the exact identity, so the pole must be hit exactly.
            float worst = 0f;
            string where = "";

            for (float ext = 0.85f; ext <= 0.9995f; ext += 0.0025f)
                foreach (float pole in new[] { -45f, -20f, 0f, 20f, 45f })
                {
                    Vector3 target = LegTarget(ext, 0f, 0f, Vector3.zero);
                    BasisLegSolveResult r = SolveLeg(target, pole, 3f, 200f);
                    Vector3 axis = (target - Root).normalized, perp = Vector3.ProjectOnPlane(r.KneeSolved - Root, axis);
                    if (perp.sqrMagnitude < 1e-12f) continue;   // dead straight: the azimuth is genuinely undefined

                    float landed = Vector3.SignedAngle(Anterior(target), perp, axis);
                    float err = Mathf.Abs(Mathf.DeltaAngle(pole, landed));
                    if (err > worst) { worst = err; where = $"extension {ext:0.0000}, pole {pole:0}°"; }
                }

            Assert.That(worst, Is.LessThan(1.0f), $"The knee missed its commanded pole by {worst:0.00}° ({where}). The hint must keep FULL authority " + $"at every extension — surrendering it near straight is the defect the near-extension fade was deleted for.");
        }
        [Test]
        public void Leg_TheKneeNeverInverts_ThroughAFullPoleSweep_AtEveryExtension()
        {
            float worstDot = 1f;
            string where = "";

            for (float ext = 0.85f; ext <= 1.0001f; ext += 0.005f)
                for (float pole = -179f; pole <= 180f; pole += 5f)
                {
                    Vector3 target = LegTarget(ext, 0f, 0f, Vector3.zero);
                    BasisLegSolveResult r = SolveLeg(target, pole, 3f, 0f);
                    Vector3 axis = (target - Root).normalized, perp = Vector3.ProjectOnPlane(r.KneeSolved - Root, axis);
                    if (perp.sqrMagnitude < 1e-12f) continue;

                    float dot = Vector3.Dot(perp.normalized, Anterior(target));
                    if (dot < worstDot) { worstDot = dot; where = $"extension {ext:0.0000}, pole {pole:0}°"; }
                }

            Assert.That(worstDot, Is.GreaterThan(0f), $"The knee reached the POSTERIOR half-space (dot {worstDot:0.0000}, {where}). A knee is a hinge: " + $"behind the hip→ankle axis is not unnatural, it is anatomically unrepresentable.");
        }
        [Test]
        public void Leg_TheFootStaysOnItsTarget_AtEveryExtension()
        {
            float worst = 0f;

            // Measure the swivel's TANGENTIAL disturbance -- the foot's deviation perpendicular to the
            // hip->target ray -- not raw distance to target. Reach preservation is the swivel's job (it
            // rotates ABOUT the hip->foot axis, and the foot lies ON it, so it cannot move the foot). The
            // MAX-EXTENSION cap (BasisLegSolveCore.MaxKneeInteriorDeg) is a SEPARATE, intentional RADIAL
            // shortfall of a fraction of a mm just past full reach; it slides the foot straight along the
            // ray, contributing zero perpendicular error, so this metric sees the swivel alone -- which is
            // what the test is actually about.
            for (float ext = 0.60f; ext <= 1.0001f; ext += 0.005f)
                foreach (float elev in new[] { -30f, 0f, 45f })
                    foreach (float pole in new[] { -170f, -90f, -30f, 0f, 30f, 90f, 170f })
                        foreach (float flex in new[] { 1f, 10f, 40f })
                        {
                            Vector3 tgt = LegTarget(ext, elev, 0f, Vector3.zero);
                            BasisLegSolveResult r = SolveLeg(tgt, pole, flex, 0f);
                            Vector3 dir = tgt - Root;
                            float perp = dir.sqrMagnitude < 1e-12f ? 0f : Vector3.ProjectOnPlane(r.FootSolved - Root, dir.normalized).magnitude;
                            worst = Mathf.Max(worst, perp);
                        }

            Assert.That(worst * 1000f, Is.LessThan(0.5f), $"The foot slid {worst * 1000f:0.00} mm sideways off the hip→target ray. The swivel is a rotation " + $"ABOUT the hip→foot axis and the foot lies ON that axis, so this is supposed to be structurally impossible.");
        }
        [Test]
        public void Arm_TheHandStaysOnItsTarget_AtEveryExtension()
        {
            float worst = 0f;

            for (float ext = 0.60f; ext <= 0.999f; ext += 0.005f)
                foreach (float elev in new[] { -30f, 0f, 45f })
                    foreach (float pole in new[] { -170f, -60f, 0f, 60f, 170f })
                        foreach (float flex in new[] { 1f, 20f, 45f })
                            worst = Mathf.Max(worst, SolveArm(ArmTarget(ext, elev, 0f, Vector3.zero), pole, flex, 0f).HandError);

            Assert.That(worst * 1000f, Is.LessThan(0.5f), $"The hand left its target by {worst * 1000f:0.00} mm.");
        }
    }
}
