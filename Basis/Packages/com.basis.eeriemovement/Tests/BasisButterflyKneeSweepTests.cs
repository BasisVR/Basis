using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// "Butterfly knees when laying down" sweep for <see cref="BasisButterflyKneeCore"/> and its hand-off to the
    /// two-bone leg solver (<see cref="BasisLegSolveCore"/>).
    ///
    /// Scenario: lie on your back with FOOT trackers but no KNEE trackers, press the soles together (tilt the feet
    /// outward so the instep faces out) and pull them in toward the pelvis -- the knees should fall open laterally
    /// (the "butterfly"/cobbler pose). Foot tilt drives how far the knees open; pulling the feet in amplifies it;
    /// and the open angle is HARD-CLAMPED to the hip's natural max abduction (MaxOpenDeg) so the splay can't exceed
    /// what the hips physically allow.
    ///
    /// These are characterization + guard sweeps. A few just print tables; the asserted ones pin the contract:
    ///   - gated fully OFF when standing or when the feet are flat (no false splay during normal use),
    ///   - the open angle is monotonic in foot tilt and amplified by pull-in,
    ///   - it never exceeds MaxOpenDeg for ANY input (the clamp the user asked for),
    ///   - the knee actually swings OUTWARD through the real two-bone solver, finitely and bounded.
    /// The hint is fed to the leg solver exactly as BasisLocalRigDriver wires it (HintPosition + HintWeight,
    /// BendNormal = the hips' right axis = KneeBendPref), so the knee excursion is measured end-to-end.
    /// </summary>
    public class BasisButterflyKneeSweepTests
    {
        const float Thigh = 0.45f, Shin = 0.45f;
        const float MaxReach = Thigh + Shin;
        const float DefaultMax = BasisButterflyKneeCore.DefaultMaxOpenDeg;

        // Supine world frame: lying on the back, belly toward the ceiling.
        static readonly Vector3 Up = Vector3.up;            // player up (ceiling)
        static readonly Vector3 BellyUp = Vector3.up;       // hips-forward (belly) when supine -> faces the ceiling
        static readonly Vector3 BellyFwd = Vector3.forward; // hips-forward when standing upright (belly faces ahead)
        static readonly Vector3 HipsRight = Vector3.right;  // pelvis left-right axis; the KneeBendPref bend axis

        // Right leg, butterfly: hip a little right of + above the centerline; foot pulled toward center + pelvis.
        static readonly Vector3 RightHip = new Vector3(0.12f, 0.12f, 0f);
        static readonly Vector3 FootDirRight = new Vector3(-0.5f, 0f, 0.85f); // from hip toward centerline + head

        const float DeepFoldDist = 0.33f;   // foot pulled right in -> knee folded -> pull-in ~1
        const float ExtendedDist = 0.80f;   // leg nearly straight -> pull-in ~0.2

        struct Row
        {
            public float TiltDeg, Dist;
            public float Supine01, FootTilt01, PullIn01;
            public float OpenAngleDeg, HintWeight;
            public float KneeOutwardCm;
            public float KneeAngleDeg;
        }

        // ----------------------------------------------------------------- the contract (asserted)

        [Test]
        public void Clamp_OpenAngleNeverExceedsMax_AcrossEveryInput()
        {
            // The headline guarantee: whatever the pose, the knee splay is capped at the hip's natural max-open.
            // Push supine, tilt (incl. past the reference), pull-in (incl. fully folded), strength, and several
            // caps -- OpenAngleDeg must never exceed MaxOpenDeg, and the pole must sit at exactly that angle.
            foreach (float maxOpen in new[] { 30f, 45f, 60f, 75f })
            foreach (float supine in new[] { 0f, 0.5f, 1f })
            foreach (float dist in new[] { 0.22f, 0.33f, 0.55f, 0.88f })
            foreach (float tilt in new[] { 0f, 20f, 45f, 55f, 70f, 90f })
            foreach (float strength in new[] { 0f, 0.5f, 1f })
            {
                var i = MakeInput(supine, tilt, dist, maxOpen, strength, isLeft: false);
                BasisButterflyKneeCore.Solve(i, out var r);

                Assert.That(r.OpenAngleDeg, Is.LessThanOrEqualTo(maxOpen + 0.01f),
                    $"open angle {r.OpenAngleDeg:0.0} exceeded the {maxOpen:0} cap (supine {supine}, tilt {tilt}, dist {dist}, str {strength}).");
                Assert.That(r.OpenAngleDeg, Is.GreaterThanOrEqualTo(-0.01f), "open angle went negative.");

                // The pole must be placed at exactly OpenAngleDeg off the sagittal default, toward outward.
                float placed = HintAngleFromDefault(i, r);
                Assert.That(placed, Is.EqualTo(r.OpenAngleDeg).Within(0.5f),
                    $"pole placed at {placed:0.0} deg but reported {r.OpenAngleDeg:0.0} deg.");
            }
        }

        [Test]
        public void Gated_Off_WhenStandingOrFeetFlat()
        {
            // Standing upright with the feet fully tilted -> no butterfly (the supine gate is closed).
            var standing = MakeInput(supineDot: 0f, tiltDeg: 90f, dist: DeepFoldDist, maxOpenDeg: DefaultMax, strength: 1f, isLeft: false);
            BasisButterflyKneeCore.Solve(standing, out var rStand);
            Assert.That(rStand.HintWeight, Is.EqualTo(0f).Within(1e-4f), "butterfly engaged while standing.");
            Assert.That(rStand.OpenAngleDeg, Is.EqualTo(0f).Within(1e-4f), "knee opened while standing.");

            // Fully supine but feet flat (soles down) -> no butterfly (nothing tells the knees to open).
            var flat = MakeInput(supineDot: 1f, tiltDeg: 0f, dist: DeepFoldDist, maxOpenDeg: DefaultMax, strength: 1f, isLeft: false);
            BasisButterflyKneeCore.Solve(flat, out var rFlat);
            Assert.That(rFlat.HintWeight, Is.EqualTo(0f).Within(1e-4f), "butterfly engaged with flat feet.");
            Assert.That(rFlat.OpenAngleDeg, Is.EqualTo(0f).Within(1e-4f), "knee opened with flat feet.");

            // Disabled (strength 0) at full pose -> no butterfly.
            var off = MakeInput(supineDot: 1f, tiltDeg: 90f, dist: DeepFoldDist, maxOpenDeg: DefaultMax, strength: 0f, isLeft: false);
            BasisButterflyKneeCore.Solve(off, out var rOff);
            Assert.That(rOff.HintWeight, Is.EqualTo(0f).Within(1e-4f), "butterfly engaged while disabled.");
        }

        [Test]
        public void Upright_ButterflyEngages_WithSupineFloor_ButStillNeedsFootTilt()
        {
            // "Butterfly Knees While Upright": sitting cross-legged (torso vertical, NOT supine) with the soles
            // pressed together should still open the knees once SupineFloor relaxes the on-your-back requirement.
            var upright = MakeInput(supineDot: 0f, tiltDeg: 90f, dist: DeepFoldDist, maxOpenDeg: DefaultMax, strength: 1f, isLeft: false);
            upright.SupineFloor = 1f;
            BasisButterflyKneeCore.Solve(upright, out var rUp);
            Assert.That(rUp.HintWeight, Is.GreaterThan(0f), "upright butterfly should engage when SupineFloor is set.");
            Assert.That(rUp.OpenAngleDeg, Is.GreaterThan(DefaultMax * 0.9f), "upright + full tilt should open the knee near the cap.");
            Assert.That(rUp.OpenAngleDeg, Is.LessThanOrEqualTo(DefaultMax + 0.01f), "upright open angle must still respect the cap.");

            // The foot-tilt gate is what keeps normal standing/walking safe: flat feet upright -> still nothing,
            // even with the floor fully open.
            var flatUpright = MakeInput(supineDot: 0f, tiltDeg: 0f, dist: DeepFoldDist, maxOpenDeg: DefaultMax, strength: 1f, isLeft: false);
            flatUpright.SupineFloor = 1f;
            BasisButterflyKneeCore.Solve(flatUpright, out var rFlat);
            Assert.That(rFlat.HintWeight, Is.EqualTo(0f).Within(1e-4f), "flat feet upright must not engage even with SupineFloor on.");
            Assert.That(rFlat.OpenAngleDeg, Is.EqualTo(0f).Within(1e-4f), "flat feet upright must not open the knee.");
        }

        [Test]
        public void OpenAngle_RisesMonotonically_WithFootTilt_WhenSupine()
        {
            // The control the user described: tilting the feet outward opens the knees. Supine + folded; sweep the
            // tilt and require the open angle to climb monotonically and reach the cap once the tilt is past ref.
            var rows = new List<Row>();
            float prev = -1f;
            foreach (float tilt in new[] { 0f, 10f, 20f, 30f, 40f, 50f, 55f, 70f, 90f })
            {
                var r = Eval(supine: 1f, tilt: tilt, dist: DeepFoldDist, maxOpen: DefaultMax, strength: 1f, isLeft: false);
                rows.Add(r);
                Assert.That(r.OpenAngleDeg, Is.GreaterThanOrEqualTo(prev - 0.01f),
                    $"open angle dropped as the foot tilted further out (tilt {tilt}: {r.OpenAngleDeg:0.0} < prev {prev:0.0}).");
                prev = r.OpenAngleDeg;
            }
            LogTable("Foot tilt sweep (supine, folded)", rows);

            var full = rows[rows.Count - 1];
            Assert.That(full.OpenAngleDeg, Is.GreaterThan(DefaultMax * 0.9f),
                $"a full outward tilt should open the knee near the {DefaultMax:0} deg cap; got {full.OpenAngleDeg:0.0}.");
        }

        [Test]
        public void PullingFeetIn_AmplifiesTheOpenAngle()
        {
            // "the closer you pull them in, the stronger" -- at the same foot tilt, a folded knee opens further
            // than a near-straight one.
            float tilt = 40f;
            var folded = Eval(supine: 1f, tilt: tilt, dist: DeepFoldDist, maxOpen: DefaultMax, strength: 1f, isLeft: false);
            var extended = Eval(supine: 1f, tilt: tilt, dist: ExtendedDist, maxOpen: DefaultMax, strength: 1f, isLeft: false);

            Assert.That(folded.PullIn01, Is.GreaterThan(extended.PullIn01), "folded leg should read a higher pull-in.");
            Assert.That(folded.OpenAngleDeg, Is.GreaterThan(extended.OpenAngleDeg + 1f),
                $"pulling the feet in should open the knee further: folded {folded.OpenAngleDeg:0.0} vs extended {extended.OpenAngleDeg:0.0}.");
        }

        [Test]
        public void Knee_SwingsOutward_Monotonically_ThroughTheLegSolver()
        {
            // End-to-end: push the core's pole through the real two-bone solver and confirm the SOLVED knee swings
            // outward as the feet tilt -- and that the flat-foot baseline has no outward splay.
            var baseline = Eval(supine: 1f, tilt: 0f, dist: DeepFoldDist, maxOpen: DefaultMax, strength: 1f, isLeft: false);

            float prev = float.NegativeInfinity;
            var rows = new List<Row>();
            foreach (float tilt in new[] { 0f, 15f, 30f, 45f, 60f, 75f, 90f })
            {
                var r = Eval(supine: 1f, tilt: tilt, dist: DeepFoldDist, maxOpen: DefaultMax, strength: 1f, isLeft: false);
                rows.Add(r);
                Assert.That(IsFinite(r.KneeOutwardCm), Is.True, $"knee position non-finite at tilt {tilt}.");
                Assert.That(r.KneeOutwardCm, Is.GreaterThanOrEqualTo(prev - 0.05f),
                    $"solved knee moved inward as the foot tilted further (tilt {tilt}: {r.KneeOutwardCm:0.0}cm < prev {prev:0.0}cm).");
                prev = r.KneeOutwardCm;
            }
            LogTable("Solved-knee outward excursion (supine, folded)", rows);

            var full = rows[rows.Count - 1];
            Assert.That(full.KneeOutwardCm, Is.GreaterThan(baseline.KneeOutwardCm + 3f),
                $"full butterfly should push the knee clearly outward vs the flat-foot baseline ({full.KneeOutwardCm:0.0}cm vs {baseline.KneeOutwardCm:0.0}cm).");
        }

        [Test]
        public void Knee_OutwardExcursion_IsBounded_ByTheCap()
        {
            // The cap is real end-to-end: a bigger MaxOpenDeg lets the knee swing further out, and a smaller cap
            // holds it back. Same pose, three caps -> strictly increasing outward excursion.
            var tight = Eval(supine: 1f, tilt: 90f, dist: DeepFoldDist, maxOpen: 30f, strength: 1f, isLeft: false);
            var mid = Eval(supine: 1f, tilt: 90f, dist: DeepFoldDist, maxOpen: 55f, strength: 1f, isLeft: false);
            var wide = Eval(supine: 1f, tilt: 90f, dist: DeepFoldDist, maxOpen: 80f, strength: 1f, isLeft: false);

            Assert.That(tight.OpenAngleDeg, Is.LessThan(mid.OpenAngleDeg).And.LessThan(wide.OpenAngleDeg),
                "a tighter cap must yield a smaller open angle.");
            Assert.That(mid.KneeOutwardCm, Is.GreaterThan(tight.KneeOutwardCm),
                $"a wider cap should let the knee swing further out ({mid.KneeOutwardCm:0.0}cm vs {tight.KneeOutwardCm:0.0}cm).");
            Assert.That(wide.KneeOutwardCm, Is.GreaterThan(mid.KneeOutwardCm),
                $"a wider cap should let the knee swing further out ({wide.KneeOutwardCm:0.0}cm vs {mid.KneeOutwardCm:0.0}cm).");
        }

        [Test]
        public void BothLegs_OpenSymmetrically()
        {
            // Left and right behave identically (mirror the X axis) so the pose stays symmetric.
            var rightLeg = Eval(supine: 1f, tilt: 50f, dist: DeepFoldDist, maxOpen: DefaultMax, strength: 1f, isLeft: false);
            var leftLeg = Eval(supine: 1f, tilt: 50f, dist: DeepFoldDist, maxOpen: DefaultMax, strength: 1f, isLeft: true);

            Assert.That(leftLeg.OpenAngleDeg, Is.EqualTo(rightLeg.OpenAngleDeg).Within(0.5f), "legs opened by different angles.");
            Assert.That(leftLeg.HintWeight, Is.EqualTo(rightLeg.HintWeight).Within(0.01f), "legs engaged by different amounts.");
            Assert.That(leftLeg.KneeOutwardCm, Is.EqualTo(rightLeg.KneeOutwardCm).Within(0.3f), "legs swung out by different amounts.");
        }

        [Test]
        public void AllOutputs_StayFinite_AcrossTheFullSweep()
        {
            foreach (float supine in new[] { 0f, 0.4f, 0.7f, 1f })
            foreach (float tilt in new[] { 0f, 25f, 55f, 95f })
            foreach (float dist in new[] { 0.20f, 0.45f, 0.70f, 0.92f })
            {
                var r = Eval(supine, tilt, dist, DefaultMax, 1f, isLeft: false);
                Assert.That(IsFinite(r.OpenAngleDeg) && IsFinite(r.HintWeight) && IsFinite(r.KneeOutwardCm) && IsFinite(r.KneeAngleDeg),
                    Is.True, $"a butterfly output went non-finite (supine {supine}, tilt {tilt}, dist {dist}).");
                Assert.That(r.HintWeight, Is.InRange(0f, 1f), "hint weight left [0,1].");
            }
        }

        [Test]
        public void Characterize_PrintAllSweepTables()
        {
            foreach (float dist in new[] { DeepFoldDist, 0.55f, ExtendedDist })
            {
                var rows = new List<Row>();
                foreach (float tilt in new[] { 0f, 15f, 30f, 45f, 55f, 70f, 90f })
                    rows.Add(Eval(supine: 1f, tilt: tilt, dist: dist, maxOpen: DefaultMax, strength: 1f, isLeft: false));
                LogTable($"supine sweep @ hip->foot {dist:0.00}m (reach {dist / MaxReach:0.00})", rows);
            }
            Assert.Pass("see attached sweep tables");
        }

        // ----------------------------------------------------------------- harness

        static Row Eval(float supine, float tilt, float dist, float maxOpen, float strength, bool isLeft)
        {
            var i = MakeInput(supine, tilt, dist, maxOpen, strength, isLeft);
            BasisButterflyKneeCore.Solve(i, out var r);
            SolveLeg(i, r, out Vector3 kneeSolved, out float kneeAngle);
            return new Row
            {
                TiltDeg = tilt,
                Dist = dist,
                Supine01 = r.Supine01,
                FootTilt01 = r.FootTilt01,
                PullIn01 = r.PullIn01,
                OpenAngleDeg = r.OpenAngleDeg,
                HintWeight = r.HintWeight,
                KneeOutwardCm = Vector3.Dot(kneeSolved - i.HipPosition, i.OutwardDir.normalized) * 100f,
                KneeAngleDeg = kneeAngle,
            };
        }

        static BasisButterflyKneeInput MakeInput(float supineDot, float tiltDeg, float dist, float maxOpenDeg, float strength, bool isLeft)
        {
            Vector3 outward = isLeft ? -HipsRight : HipsRight;
            Vector3 hip = isLeft ? new Vector3(-RightHip.x, RightHip.y, RightHip.z) : RightHip;
            Vector3 footDir = (isLeft ? new Vector3(-FootDirRight.x, FootDirRight.y, FootDirRight.z) : FootDirRight).normalized;

            // Belly between forward (upright) and up (supine) so dot(belly, up) == supineDot exactly.
            Vector3 belly = (BellyFwd * Mathf.Sqrt(Mathf.Max(0f, 1f - supineDot * supineDot)) + BellyUp * supineDot).normalized;

            BasisButterflyKneeInput i;
            i.HipPosition = hip;
            i.FootPosition = hip + footDir * dist;
            i.FootInstepDir = Instep(tiltDeg, outward); // soles-together tilt: instep leans toward outward
            i.OutwardDir = outward;
            i.DefaultBendDir = BellyUp;                 // relaxed supine knee points toward the ceiling
            i.PlayerUp = Up;
            i.TorsoFacingDir = belly;
            i.UpperLength = Thigh;
            i.LowerLength = Shin;
            i.MaxOpenDeg = maxOpenDeg;
            i.Strength = strength;
            i.SupineFloor = 0f;                         // default: laying-down gated (upright tests opt in)
            return i;
        }

        // Instep normal for a foot tilted `tiltDeg` outward: rotate the foot "up" (soles-down) toward the outward
        // direction. dot(instep, outward) = sin(tiltDeg), exactly the signal the core reads.
        static Vector3 Instep(float tiltDeg, Vector3 outward)
        {
            float a = tiltDeg * Mathf.Deg2Rad;
            return (Up * Mathf.Cos(a) + outward.normalized * Mathf.Sin(a)).normalized;
        }

        // Feed the core's pole through the real two-bone leg solver the way BasisLocalRigDriver does. Output knee
        // position is independent of the input bone rotations, so those are left identity.
        static void SolveLeg(in BasisButterflyKneeInput i, in BasisButterflyKneeResult core, out Vector3 kneeSolved, out float kneeAngleDeg)
        {
            Vector3 axis = (i.FootPosition - i.HipPosition).normalized;
            Vector3 restPerp = Vector3.ProjectOnPlane(i.DefaultBendDir, axis);
            restPerp = restPerp.sqrMagnitude > 1e-6f ? restPerp.normalized : Vector3.ProjectOnPlane(Vector3.forward, axis).normalized;
            Vector3 restKnee = (i.HipPosition + i.FootPosition) * 0.5f + restPerp * 0.05f;

            BasisLegSolveInput li = default;
            li.Root = i.HipPosition;
            li.Mid = restKnee;
            li.Tip = i.FootPosition;
            li.RootRotation = Quaternion.identity;
            li.MidRotation = Quaternion.identity;
            li.TargetPosition = i.FootPosition;   // foot is tracked -> the IK target is its position
            li.TargetRotation = Quaternion.identity;
            li.HintPosition = core.KneeHint;
            li.HintWeight = core.HintWeight;
            li.TargetOffset = Quaternion.identity;
            li.BendNormal = HipsRight;            // KneeBendPref (the default sagittal bend axis)

            BasisLegSolveCore.Solve(li, out BasisLegSolveResult lr);
            kneeSolved = lr.KneeSolved;
            kneeAngleDeg = lr.KneeAngleDeg;
        }

        // Angle of the placed pole off the sagittal default, in the plane perpendicular to the leg axis.
        static float HintAngleFromDefault(in BasisButterflyKneeInput i, in BasisButterflyKneeResult r)
        {
            Vector3 axis = (i.FootPosition - i.HipPosition).normalized;
            Vector3 mid = (i.HipPosition + i.FootPosition) * 0.5f;
            Vector3 hintPerp = Vector3.ProjectOnPlane(r.KneeHint - mid, axis);
            Vector3 defPerp = Vector3.ProjectOnPlane(i.DefaultBendDir, axis);
            if (hintPerp.sqrMagnitude < 1e-8f || defPerp.sqrMagnitude < 1e-8f) return 0f;
            return Vector3.Angle(defPerp.normalized, hintPerp.normalized);
        }

        static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        static void LogTable(string title, IEnumerable<Row> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== {title} ===");
            sb.AppendLine("  tiltDeg  supine  footTilt  pullIn   openDeg  weight   kneeOut   kneeFlexDeg");
            foreach (var m in rows)
            {
                sb.AppendLine(string.Format(
                    "  {0,5:0.0}    {1,4:0.00}   {2,5:0.00}    {3,4:0.00}   {4,5:0.0}    {5,4:0.00}   {6,6:0.0}cm   {7,5:0.0}",
                    m.TiltDeg, m.Supine01, m.FootTilt01, m.PullIn01, m.OpenAngleDeg, m.HintWeight, m.KneeOutwardCm, m.KneeAngleDeg));
            }
            TestContext.WriteLine(sb.ToString());
        }
    }
}
