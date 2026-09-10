using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisArmSolveCoreTests
    {
        const float dt = 1f / 90f, upper = 0.28f, lower = 0.26f;
        static readonly Vector3 shoulder = new Vector3(0.18f, 1.40f, 0f), head = new Vector3(0f, 1.62f, 0f);
        static BasisArmSolveInput Input(bool isLeft, Vector3 target, Quaternion targetRot)
        {
            BasisArmSolveInput i = BasisArmSolveInput.Defaults(isLeft);
            float side = isLeft ? -1f : 1f;
            i.Shoulder = new Vector3(shoulder.x * side, shoulder.y, shoulder.z);
            i.RestElbow = i.Shoulder + new Vector3(upper * side, 0f, 0f);
            i.RestHand = i.RestElbow + new Vector3(lower * side, 0f, 0f);
            i.RestHandRotation = Quaternion.identity;
            i.TargetPosition = target;
            i.TargetRotation = targetRot;
            i.HasHead = true;
            i.HeadPosition = head;
            i.TorsoCapsule = true;
            i.TorsoA = new Vector3(0f, 0.95f, 0f);
            i.TorsoB = new Vector3(0f, 1.50f, 0f);
            i.TorsoRadius = 0.11f;
            i.Dt = dt;
            return i;
        }
        static Quaternion HandRot(bool isLeft, Vector3 target, float rollDeg)
        {
            Vector3 fwd = target - new Vector3(shoulder.x * (isLeft ? -1f : 1f), shoulder.y, shoulder.z);
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            Vector3 restFwd = isLeft ? Vector3.left : Vector3.right;
            return Quaternion.AngleAxis(rollDeg, fwd.normalized) * Quaternion.FromToRotation(restFwd, fwd.normalized);
        }
        static IEnumerable<Vector3> ReachGrid(bool isLeft)
        {
            float side = isLeft ? -1f : 1f;
            Vector3 s = new Vector3(shoulder.x * side, shoulder.y, shoulder.z);
            for (int e = -80; e <= 80; e += 20)
                for (int a = -150; a <= 150; a += 30)
                    for (int r = 0; r < 3; r++)
                    {
                        float reach = 0.45f + 0.2f * r, el = e * Mathf.Deg2Rad, az = a * Mathf.Deg2Rad;
                        Vector3 dir = new Vector3(Mathf.Cos(el) * Mathf.Sin(az) * side, Mathf.Sin(el), Mathf.Cos(el) * Mathf.Cos(az));
                        yield return s + dir * (reach * (upper + lower));
                    }
        }
        [Test]
        public void Frame_IsOrthonormal_AndContinuous_ThroughArmsDownAndOverhead()
        {
            Vector3 up = Vector3.up, fwd = Vector3.forward, outward = Vector3.right, prevEx = Vector3.zero;
            float worstStep = 0f;
            for (int k = 0; k <= 360; k += 2)
            {
                float a = k * Mathf.Deg2Rad;
                Vector3 axis = new Vector3(0.15f, Mathf.Cos(a), Mathf.Sin(a)).normalized;
                BasisArmSolveCore.Frame(axis, up, fwd, outward, out Vector3 ex, out Vector3 ey);
                Assert.That(ex.magnitude, Is.EqualTo(1f).Within(1e-4f));
                Assert.That(Mathf.Abs(Vector3.Dot(ex, axis)), Is.LessThan(1e-4f), "ex must be perpendicular to the arm axis");
                Assert.That(Mathf.Abs(Vector3.Dot(ey, axis)), Is.LessThan(1e-4f));
                Assert.That(Mathf.Abs(Vector3.Dot(ex, ey)), Is.LessThan(1e-4f));
                if (k > 0) worstStep = Mathf.Max(worstStep, Vector3.Angle(prevEx, ex));
                prevEx = ex;
            }
            Assert.That(worstStep, Is.LessThan(12f), $"the swivel frame jumped {worstStep:F1} deg between 2 deg steps of a vertical sweep through arms-down and overhead");
        }
        [Test]
        public void Solve_HandLandsOnTarget_AndBoneLengthsHold()
        {
            foreach (bool isLeft in new[] { false, true })
                foreach (Vector3 target in ReachGrid(isLeft))
                {
                    BasisArmSolveInput i = Input(isLeft, target, HandRot(isLeft, target, 0f));
                    BasisArmState state = default;
                    BasisArmSolveCore.Solve(i, ref state, out BasisArmSolveResult r);
                    Assert.That(r.Valid, Is.True);
                    if (r.ReachRatio < 0.9f) Assert.That(Vector3.Distance(r.Hand, target), Is.LessThan(1e-3f), $"hand missed {target}");
                    Assert.That(Vector3.Distance(r.Elbow, i.Shoulder), Is.EqualTo(upper).Within(1e-3f));
                    Assert.That(Vector3.Distance(r.Hand, r.Elbow), Is.EqualTo(lower).Within(1e-3f));
                    Assert.That(r.ElbowDeg, Is.GreaterThan(BasisArmSolveCore.MinElbowInteriorDeg - 0.5f));
                    Assert.That(r.ElbowDeg, Is.LessThan(180f));
                }
        }
        [Test]
        public void Solve_HeadTargetRule_ElbowHangsForChestHand_AndFlaresForRaisedHand()
        {
            Vector3 chestHand = new Vector3(0.10f, 1.25f, 0.30f);
            BasisArmSolveInput i = Input(false, chestHand, HandRot(false, chestHand, 0f));
            BasisArmState state = default;
            BasisArmSolveCore.Solve(i, ref state, out BasisArmSolveResult r);
            Vector3 mid = (i.Shoulder + chestHand) * 0.5f;
            Assert.That(r.Elbow.y, Is.LessThan(mid.y - 0.05f), "a hand in front of the chest hangs the elbow below the arm line");
            Vector3 raised = new Vector3(0.42f, 1.72f, 0.10f);
            i = Input(false, raised, HandRot(false, raised, 0f));
            state = default;
            BasisArmSolveCore.Solve(i, ref state, out r);
            Assert.That(r.Elbow.x, Is.GreaterThan(i.Shoulder.x + 0.05f), "a hand raised beside the head flares the elbow outward");
        }
        [Test]
        public void Solve_WristRoll_NeverFlipsTheElbow()
        {
            Vector3 target = new Vector3(0.25f, 1.20f, 0.38f);
            BasisArmState state = default;
            float prev = 0f, worst = 0f;
            Vector3 prevElbow = Vector3.zero;
            float worstJump = 0f;
            for (int f = 0; f <= 720; f++)
            {
                BasisArmSolveInput i = Input(false, target, HandRot(false, target, f * 0.5f));
                BasisArmSolveCore.Solve(i, ref state, out BasisArmSolveResult r);
                if (f > 0)
                {
                    worst = Mathf.Max(worst, Mathf.Abs(Mathf.DeltaAngle(prev, r.SwivelDeg)));
                    worstJump = Mathf.Max(worstJump, Vector3.Distance(prevElbow, r.Elbow));
                }
                prev = r.SwivelDeg;
                prevElbow = r.Elbow;
            }
            Assert.That(worst, Is.LessThanOrEqualTo(BasisArmSolveInput.Defaults(false).MaxRateDeg * dt + 0.01f), "swivel exceeded its rate limit during a full wrist roll");
            Assert.That(worstJump, Is.LessThan(0.03f), $"elbow jumped {worstJump * 100f:F1} cm in one frame during a wrist roll");
        }
        [Test]
        public void Solve_HandSweep_ThroughArmsDownAndBehind_IsContinuous()
        {
            BasisArmState state = default;
            Vector3 prevElbow = Vector3.zero;
            float worstJump = 0f;
            int frames = 600;
            for (int f = 0; f <= frames; f++)
            {
                float t = (float)f / frames, a = Mathf.Lerp(-40f, 200f, t) * Mathf.Deg2Rad;
                Vector3 target = shoulder + new Vector3(0.12f + 0.25f * Mathf.Abs(Mathf.Sin(a)), -0.45f * Mathf.Cos(a) - 0.05f, 0.45f * Mathf.Sin(a));
                BasisArmSolveInput i = Input(false, target, HandRot(false, target, 0f));
                BasisArmSolveCore.Solve(i, ref state, out BasisArmSolveResult r);
                if (f > 0) worstJump = Mathf.Max(worstJump, Vector3.Distance(prevElbow, r.Elbow));
                prevElbow = r.Elbow;
            }
            Assert.That(worstJump, Is.LessThan(0.03f), $"elbow jumped {worstJump * 100f:F1} cm in one frame while the hand swept from the front, past the hip, to behind the back");
        }
        [Test]
        public void Solve_JointLimits_ReduceWristAndForearmViolations()
        {
            float withLimits = 0f, without = 0f;
            int n = 0;
            foreach (Vector3 target in ReachGrid(false))
                foreach (float roll in new[] { -150f, -60f, 0f, 60f, 150f })
                {
                    BasisArmSolveInput i = Input(false, target, HandRot(false, target, roll));
                    BasisArmState state = default;
                    BasisArmSolveCore.Solve(i, ref state, out BasisArmSolveResult a);
                    i.JointLimits = false;
                    state = default;
                    BasisArmSolveCore.Solve(i, ref state, out BasisArmSolveResult b);
                    withLimits += Violation(a, i.Limits);
                    without += Violation(b, i.Limits);
                    n++;
                }
            Assert.That(n, Is.GreaterThan(0));
            Assert.That(withLimits, Is.LessThan(without * 0.6f), $"joint limits did not reduce range-of-motion violations: {withLimits / n:F1} deg with vs {without / n:F1} deg without");
        }
        static float Violation(in BasisArmSolveResult r, in BasisArmLimits l)
        {
            float v = 0f;
            v += Mathf.Max(0f, -l.SupinationMaxDeg - 90f - r.PronationDeg) + Mathf.Max(0f, r.PronationDeg - (l.PronationMaxDeg - 90f));
            v += Mathf.Max(0f, -l.WristExtensionMaxDeg - r.WristFlexDeg) + Mathf.Max(0f, r.WristFlexDeg - l.WristFlexionMaxDeg);
            v += Mathf.Max(0f, -l.WristUlnarMaxDeg - r.WristDevDeg) + Mathf.Max(0f, r.WristDevDeg - l.WristRadialMaxDeg);
            v += Mathf.Max(0f, -l.HumeralInternalMaxDeg - r.HumeralDeg) + Mathf.Max(0f, r.HumeralDeg - l.HumeralExternalMaxDeg);
            return v;
        }
        [Test]
        public void Solve_MirroredInputs_GiveMirroredElbows()
        {
            foreach (Vector3 target in ReachGrid(false))
            {
                Vector3 mirrored = new Vector3(-target.x, target.y, target.z);
                BasisArmSolveInput r = Input(false, target, HandRot(false, target, 0f)), l = Input(true, mirrored, HandRot(true, mirrored, 0f));
                BasisArmState sr = default, sl = default;
                BasisArmSolveCore.Solve(r, ref sr, out BasisArmSolveResult rr);
                BasisArmSolveCore.Solve(l, ref sl, out BasisArmSolveResult rl);
                Assert.That(Vector3.Distance(new Vector3(-rr.Elbow.x, rr.Elbow.y, rr.Elbow.z), rl.Elbow), Is.LessThan(0.02f), $"left and right elbows are not mirror images at {target}");
            }
        }
        [Test]
        public void Solve_ElbowTracker_PlacesTheElbowOnTheTrackerSide()
        {
            Vector3 target = new Vector3(0.30f, 1.20f, 0.35f);
            BasisArmSolveInput i = Input(false, target, HandRot(false, target, 0f));
            i.HasHint = true;
            i.HintPosition = new Vector3(0.55f, 1.42f, 0.05f);
            BasisArmState state = default;
            for (int f = 0; f < 30; f++) BasisArmSolveCore.Solve(i, ref state, out _);
            BasisArmSolveCore.Solve(i, ref state, out BasisArmSolveResult r);
            Vector3 axis = (target - i.Shoulder).normalized, hint = i.HintPosition - i.Shoulder, elbow = r.Elbow - i.Shoulder;
            hint -= axis * Vector3.Dot(hint, axis);
            elbow -= axis * Vector3.Dot(elbow, axis);
            Assert.That(Vector3.Angle(hint, elbow), Is.LessThan(8f), "a tracked elbow must sit on the tracker's side of the arm");
        }
        [Test]
        public void Solve_TorsoClearance_KeepsElbowOutOfTheChest()
        {
            Vector3 target = new Vector3(-0.15f, 1.30f, 0.22f);
            BasisArmSolveInput i = Input(false, target, HandRot(false, target, 0f));
            BasisArmState state = default;
            BasisArmSolveCore.Solve(i, ref state, out BasisArmSolveResult r);
            Vector3 ab = i.TorsoB - i.TorsoA;
            float t = Mathf.Clamp01(Vector3.Dot(r.Elbow - i.TorsoA, ab) / ab.sqrMagnitude), dist = Vector3.Distance(r.Elbow, i.TorsoA + ab * t);
            Assert.That(dist, Is.GreaterThan(i.TorsoRadius * 0.9f), $"elbow sits {dist * 100f:F1} cm from the torso axis, inside the chest capsule");
        }
        [Test]
        public void Pose_RotationsReproduceTheSolvedElbowAndHand()
        {
            foreach (Vector3 target in ReachGrid(false))
            {
                BasisArmSolveInput i = Input(false, target, HandRot(false, target, 30f));
                BasisArmState state = default;
                BasisArmSolveCore.Solve(i, ref state, out BasisArmSolveResult r);
                BasisArmSolveCore.Pose(i, r, Quaternion.identity, Quaternion.identity, out Quaternion upperRot, out Quaternion lowerRot);
                Vector3 elbow = i.Shoulder + upperRot * (i.RestElbow - i.Shoulder), hand = elbow + lowerRot * (i.RestHand - i.RestElbow);
                Assert.That(Vector3.Distance(elbow, r.Elbow), Is.LessThan(1e-3f));
                Assert.That(Vector3.Distance(hand, r.Hand), Is.LessThan(1e-3f));
                Vector3 hinge = upperRot * Vector3.up, planeNormal = Vector3.Cross(r.Elbow - i.Shoulder, r.Hand - r.Elbow);
                if (planeNormal.sqrMagnitude > 1e-6f) Assert.That(Mathf.Abs(Vector3.Dot(hinge.normalized, (r.Hand - r.Elbow).normalized)), Is.LessThan(0.2f), "the upper arm hinge axis must stay perpendicular to the forearm");
            }
        }
        [Test]
        public void Shoulder_ElevationRisesWithTheHand_AndStaysCapped()
        {
            BasisShoulderSolveInput i = default;
            i.ShoulderPos = new Vector3(0.03f, 1.45f, 0f);
            i.UpperArmPos = new Vector3(0.18f, 1.42f, 0f);
            i.ArmLength = upper + lower;
            i.TorsoUp = Vector3.up;
            i.TorsoForward = Vector3.forward;
            i.TorsoOut = Vector3.right;
            i.ShrugEnabled = true;
            i.ElevationFactor = 1f;
            i.ProtractionFactor = 1f;
            i.MaxDeg = 30f;
            float prev = -1f;
            for (int e = -90; e <= 180; e += 15)
            {
                float a = e * Mathf.Deg2Rad;
                i.HandTargetPos = i.UpperArmPos + new Vector3(Mathf.Sin(a) * 0.9f, -Mathf.Cos(a), 0f) * (upper + lower) * 0.9f;
                BasisShoulderSolveCore.Solve(i, out BasisShoulderSolveResult r);
                Assert.That(r.Apply, Is.True);
                Assert.That(r.ElevationDeg, Is.GreaterThanOrEqualTo(prev - 1e-3f), "clavicle elevation must not fall as the hand rises");
                Assert.That(r.ElevationDeg, Is.InRange(0f, i.MaxDeg + 1e-3f));
                Assert.That(Mathf.Abs(r.ProtractionDeg), Is.LessThanOrEqualTo(i.MaxDeg + 1e-3f));
                if (e <= 30) Assert.That(r.ElevationDeg, Is.EqualTo(0f).Within(1e-3f), "no girdle elevation in the setting phase below 30 deg");
                prev = r.ElevationDeg;
            }
            Assert.That(prev, Is.GreaterThan(20f), "an overhead hand must raise the clavicle");
        }
    }
}
