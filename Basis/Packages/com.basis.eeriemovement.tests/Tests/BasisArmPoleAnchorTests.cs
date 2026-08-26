using System;
using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisArmPoleAnchorTests
    {
        const float upper = 0.30f, k_Fore = 0.30f;
        const float arm = upper + k_Fore;
        static readonly Vector3 root = Vector3.zero, boneAxis = Vector3.right;
        static Vector3 Target(float ext, Vector3 nudge)
        {
            Vector3 dir = Quaternion.AngleAxis(35f, Vector3.up) * (Quaternion.AngleAxis(20f, Vector3.forward) * Vector3.right);
            return root + dir.normalized * (ext * arm) + nudge;
        }
        static Vector3 UserElbow(Vector3 target, float swivelDeg, float userArm)
        {
            Vector3 sa = target - root;
            float d = sa.magnitude;
            Vector3 axis = sa / d;
            float up = userArm * 0.5f, fo = userArm * 0.5f;
            float a = (up * up - fo * fo + d * d) / (2f * d), radius = Mathf.Sqrt(Mathf.Max(up * up - a * a, 0f));
            Vector3 down = Vector3.down - axis * Vector3.Dot(Vector3.down, axis);
            down = down.sqrMagnitude < 1e-8f ? Vector3.forward : down.normalized;
            return root + axis * a + (Quaternion.AngleAxis(swivelDeg, axis) * down) * radius;
        }
        static Quaternion ForearmRot(Vector3 target, float swivelDeg, float userArm)
        {
            Vector3 axis = (target - root).normalized, fore0 = target - UserElbow(target, 0f, userArm);
            if (fore0.sqrMagnitude < 1e-10f) return Quaternion.AngleAxis(swivelDeg, axis);
            return Quaternion.AngleAxis(swivelDeg, axis) * Quaternion.FromToRotation(boneAxis, fore0.normalized);
        }
        static void AnimatedPose(out Vector3 elbow, out Vector3 hand, out Quaternion rootRot, out Quaternion midRot)
        {
            const float flex = 150f * Mathf.Deg2Rad;
            Vector3 straight = Vector3.right;
            Vector3 bulge0 = Vector3.Cross(straight, Vector3.Cross(Vector3.forward, straight)).normalized;
            Vector3 bulge = Quaternion.AngleAxis(40f, straight) * bulge0;
            Vector3 upperDir = (straight * Mathf.Cos(flex) + bulge * Mathf.Sin(flex)).normalized;
            Vector3 lowerDir = (straight * Mathf.Cos(flex) - bulge * Mathf.Sin(flex)).normalized;
            rootRot = Quaternion.FromToRotation(boneAxis, upperDir) * Quaternion.AngleAxis(15f, boneAxis);
            midRot = Quaternion.FromToRotation(boneAxis, lowerDir) * Quaternion.AngleAxis(15f, boneAxis);
            elbow = root + rootRot * boneAxis * upper;
            hand = elbow + midRot * boneAxis * k_Fore;
        }
        static float RollDeg(Quaternion before, Quaternion after)
        {
            Vector3 axis = (after * boneAxis).normalized;
            Quaternion delta = after * Quaternion.Inverse(before);
            if (delta.w < 0f) delta = new Quaternion(-delta.x, -delta.y, -delta.z, -delta.w);
            Vector3 v = new Vector3(delta.x, delta.y, delta.z);
            return Mathf.Abs(Mathf.DeltaAngle(0f, 2f * Mathf.Atan2(Vector3.Dot(v, axis), delta.w) * Mathf.Rad2Deg));
        }
        static float SwivelOf(Vector3 hand, Vector3 elbow)
        {
            Vector3 axis = (hand - root).normalized, down = Vector3.down - axis * Vector3.Dot(Vector3.down, axis);
            down = down.sqrMagnitude < 1e-8f ? Vector3.forward : down.normalized;
            Vector3 e = elbow - root;
            e -= axis * Vector3.Dot(e, axis);
            if (e.sqrMagnitude < 1e-12f) return float.NaN;
            e = e.normalized;
            float a = Mathf.Acos(Mathf.Clamp(Vector3.Dot(down, e), -1f, 1f)) * Mathf.Rad2Deg;
            return Vector3.Dot(axis, Vector3.Cross(down, e)) < 0f ? -a : a;
        }
        static float Gauss(System.Random rng)
        {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }
        struct Trace
        {
            public float WorstFrameRollDeg;
            public int Flips;
            public float TrackRmsDeg;
        }
        static Trace Series(float ext, float userArmRatio, bool anchored, int frames, float swivelStart, float swivelEnd, int seed, float warmExt = -1f, int warmFrames = 0)
        {
            var rng = new System.Random(seed);
            var t = new Trace();
            Vector3 anchorDir = Vector3.zero;
            Quaternion anchorRot = Quaternion.identity;
            bool hasAnchor = false, first = true;
            Quaternion prevRoot = Quaternion.identity;
            double sqSum = 0; int n = 0;
            int warm = warmExt > 0f ? warmFrames : 0;
            float userArm = arm * userArmRatio;

            for (int f = -warm; f < frames; f++)
            {
                bool warming = f < 0;
                float lerpT = frames > 1 ? (float)Mathf.Max(f, 0) / (frames - 1) : 0f;
                float swivel = warming ? swivelStart : Mathf.Lerp(swivelStart, swivelEnd, lerpT);
                Vector3 target = Target(warming ? warmExt : ext, new Vector3(Gauss(rng), Gauss(rng), Gauss(rng)) * 0.001f);
                Vector3 trueElbow = UserElbow(target, swivel, userArm);
                Vector3 hint = trueElbow + new Vector3(Gauss(rng), Gauss(rng), Gauss(rng)) * 0.001f;

                AnimatedPose(out Vector3 aElbow, out Vector3 aHand, out Quaternion rootRot, out Quaternion midRot);

                BasisArmSolveInput i = default;
                i.Shoulder = root; i.Elbow = aElbow; i.Hand = aHand;
                i.RootRotation = rootRot; i.MidRotation = midRot;
                i.TargetPosition = target;
                i.TargetRotation = Quaternion.identity; i.TargetOffset = Quaternion.identity;
                i.HintPosition = hint;
                i.HintRotation = ForearmRot(target, swivel, userArm);
                i.HasHintRotation = true;
                i.HintWeight = true;
                i.HintIsTracker = true;
                i.PlayerUp = Vector3.up;
                i.HintMaxStepDeg = float.MaxValue;
                if (anchored && hasAnchor)
                {
                    i.PrevPoleDir = anchorDir;
                    i.PrevHintRotation = anchorRot;
                    i.HasPrevPole = true;
                }

                BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);

                if (anchored && r.PoleAnchorValid)
                {
                    anchorDir = r.PoleDirUsed;
                    anchorRot = r.PoleRotUsed;
                    hasAnchor = true;
                }

                if (warming) { prevRoot = r.RootRotationSolved; first = true; continue; }

                float solved = SwivelOf(r.HandSolved, r.ElbowSolved);
                if (!float.IsNaN(solved))
                {
                    float d = Mathf.DeltaAngle(swivel, solved);
                    sqSum += (double)d * d; n++;
                }
                if (!first)
                {
                    float roll = RollDeg(prevRoot, r.RootRotationSolved);
                    if (roll > t.WorstFrameRollDeg) t.WorstFrameRollDeg = roll;
                    if (roll > 90f) t.Flips++;
                }
                prevRoot = r.RootRotationSolved;
                first = false;
            }
            t.TrackRmsDeg = n > 0 ? (float)Math.Sqrt(sqSum / n) : float.NaN;
            return t;
        }
        [Test]
        public void AStraightenedArmWithAnElbowTracker_DoesNotSpinTheHumerus()
        {
            foreach (float ext in new[] { 0.999f, 1.000f })
            {
                Trace control = Series(ext, 1.00f, anchored: false, 400, 35f, 35f, 12345);
                Trace fixedUp = Series(ext, 1.00f, anchored: true, 400, 35f, 35f, 12345);

                Assert.That(control.Flips, Is.GreaterThan(10), $"ANTI-TAUTOLOGY: the unanchored control must still reproduce the defect at ext {ext}. " +"It did not, so this test is no longer measuring anything.");
                Assert.That(fixedUp.Flips, Is.Zero, $"ext {ext}: anchored solve flipped {fixedUp.Flips} times (worst frame roll " + $"{fixedUp.WorstFrameRollDeg:0.00} deg).");
                Assert.That(fixedUp.WorstFrameRollDeg, Is.LessThan(20f), $"ext {ext}: worst single-frame humerus roll {fixedUp.WorstFrameRollDeg:0.00} deg.");
            }
        }
        [Test]
        public void AProportionMismatch_DoesNotDragTheSingularityIntoOrdinaryReaches()
        {
            Trace control = Series(0.990f, 0.98f, anchored: false, 400, 35f, 35f, 4242);
            Trace fixedUp = Series(0.990f, 0.98f, anchored: true, 400, 35f, 35f, 4242);

            Assert.That(control.Flips, Is.GreaterThan(10),"ANTI-TAUTOLOGY: the 0.98-ratio control must still flip.");
            Assert.That(fixedUp.Flips, Is.Zero, $"anchored solve flipped {fixedUp.Flips} times at a 0.98 arm ratio.");
        }
        [Test]
        public void AtFullExtension_TheElbowStillFollowsTheTracker()
        {
            Trace control = Series(0.999f, 1.00f, anchored: false, 300, 0f, 120f, 777);
            Trace fixedUp = Series(0.999f, 1.00f, anchored: true, 300, 0f, 120f, 777);

            Assert.That(fixedUp.TrackRmsDeg, Is.LessThan(20f), $"the elbow stopped following the tracker: {fixedUp.TrackRmsDeg:0.0} deg RMS from the " +"user's own elbow. A fix that merely freezes or world-downs the elbow lands here.");
            Assert.That(fixedUp.TrackRmsDeg, Is.LessThan(control.TrackRmsDeg), $"anchored tracking ({fixedUp.TrackRmsDeg:0.0} deg) must beat the unanchored control " + $"({control.TrackRmsDeg:0.0} deg), not merely be quiet.");
        }
        [Test]
        public void PastFullReach_TheTrackersRotationCarriesTheElbow()
        {
            Trace warmed = Series(1.020f, 1.00f, anchored: true, 300, 0f, 120f, 777, warmExt: 0.85f, warmFrames: 60);

            Assert.That(warmed.Flips, Is.Zero, "flipped past full reach after a normal approach.");
            Assert.That(warmed.TrackRmsDeg, Is.LessThan(25f), $"past reach the elbow tracked at only {warmed.TrackRmsDeg:0.0} deg RMS; the puck's " +"rotation is the only azimuth signal left and it is not being used.");
        }
        [Test]
        public void WhileThePoleIsWellConditioned_TheAnchorIsIgnoredExactly()
        {
            var rng = new System.Random(99);
            float worst = 0f;
            foreach (float ext in new[] { 0.50f, 0.70f, 0.85f, 0.93f })
            {
                for (int k = 0; k < 100; k++)
                {
                    float sw = (float)(rng.NextDouble() * 360.0 - 180.0);
                    Vector3 target = Target(ext, Vector3.zero), trueElbow = UserElbow(target, sw, arm);
                    AnimatedPose(out Vector3 aElbow, out Vector3 aHand, out Quaternion rootRot, out Quaternion midRot);

                    BasisArmSolveInput baseline = default;
                    baseline.Shoulder = root; baseline.Elbow = aElbow; baseline.Hand = aHand;
                    baseline.RootRotation = rootRot; baseline.MidRotation = midRot;
                    baseline.TargetPosition = target;
                    baseline.TargetRotation = Quaternion.identity; baseline.TargetOffset = Quaternion.identity;
                    baseline.HintPosition = trueElbow;
                    baseline.HintRotation = ForearmRot(target, sw, arm);
                    baseline.HasHintRotation = true;
                    baseline.HintWeight = true; baseline.HintIsTracker = true;
                    baseline.PlayerUp = Vector3.up; baseline.HintMaxStepDeg = float.MaxValue;

                    BasisArmSolveCore.Solve(baseline, out BasisArmSolveResult a);
                    Assert.That(a.PoleConditioning, Is.EqualTo(1f).Within(1e-6f), $"ext {ext} should be well-conditioned but reported {a.PoleConditioning:0.000}.");

                    BasisArmSolveInput poisoned = baseline;
                    poisoned.PrevPoleDir = Quaternion.AngleAxis(90f, (target - root).normalized) * a.PoleDirUsed;
                    poisoned.PrevHintRotation = baseline.HintRotation;
                    poisoned.HasPrevPole = true;
                    BasisArmSolveCore.Solve(poisoned, out BasisArmSolveResult b);

                    float d = (a.ElbowSolved - b.ElbowSolved).magnitude;
                    if (d > worst) worst = d;
                }
            }
            Assert.That(worst, Is.EqualTo(0f).Within(1e-9f), $"a wrong anchor moved a well-conditioned elbow by {worst * 1000f:0.000000} mm; it must be ignored exactly.");
        }
    }
}
