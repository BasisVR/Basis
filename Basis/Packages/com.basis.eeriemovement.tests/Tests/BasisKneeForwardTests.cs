using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisKneeForwardTests
    {
        const float Thigh = 0.45f, Shin = 0.45f;
        const float DefaultCoupling = BasisKneeForwardCore.DefaultUprightCoupling;
        static readonly Vector3 Up = Vector3.up;         // player up (ceiling)
        static readonly Vector3 BodyFwd = Vector3.forward, HipsRight = Vector3.right;
        struct Row
        {
            public float ToeOutDeg;
            public float Upright01, FollowDeg, HintWeight;
            public float SolvedKneeAzimuthDeg;
        }
        // ----------------------------------------------------------------- the contract (asserted)
        [Test]
        public void Standing_KneeFollowsFoot_FootDominant_ButStillInboard()
        {
            // A standing leg follows the foot by the coupling fraction: foot-dominant at the default, but the knee
            // still stays a touch inboard of the foot (coupling < 1). A natural 7 deg foot -> ~5 deg knee, inside the
            // measured tibia-vs-pelvis band (1.65 +/- 5.58 deg).
            const float toe = 7f;
            var r = Eval(toeOutDeg: toe, coupling: DefaultCoupling);
            Assert.That(r.Upright01, Is.GreaterThan(0.95f), "a vertical standing leg should read fully upright.");
            Assert.That(r.FollowDeg, Is.EqualTo(DefaultCoupling * toe).Within(0.5f), $"follow should be ~coupling*toe = {DefaultCoupling * toe:0.00}; got {r.FollowDeg:0.00}.");
            Assert.That(r.FollowDeg, Is.LessThan(toe), "the knee must stay inboard of the foot (less toed-out).");
            Assert.That(DefaultCoupling, Is.GreaterThan(0.5f), "the default should let the foot DOMINATE the knee azimuth.");
        }
        [Test]
        public void FollowRisesMonotonically_WithFootToeOut_ThenClampsAtMax()
        {
            var rows = new List<Row>();
            float prev = -1f;
            foreach (float toe in new[] { 0f, 5f, 10f, 20f, 30f, 45f, 60f, 90f, 120f })
            {
                var r = Eval(toe, DefaultCoupling);
                rows.Add(r);
                Assert.That(r.FollowDeg, Is.GreaterThanOrEqualTo(prev - 0.01f), $"knee follow dropped as the foot toed out further (toe {toe}: {r.FollowDeg:0.0} < prev {prev:0.0}).");
                Assert.That(r.FollowDeg, Is.LessThanOrEqualTo(BasisKneeForwardCore.MaxFollowDeg + 0.01f), $"knee follow {r.FollowDeg:0.0} blew past the {BasisKneeForwardCore.MaxFollowDeg} deg cap.");
                Assert.That(r.FollowDeg, Is.LessThanOrEqualTo(toe + 0.5f), "knee must never toe out MORE than the foot.");
                prev = r.FollowDeg;
            }
            LogTable("Standing toe-out sweep (coupling 0.35)", rows);
        }
        [Test]
        public void Supine_RolledOutFoot_DoesNotYankTheKnee()
        {
            // THE REGRESSION: lying on your back, leg flat, foot rolled so the toe points sideways -- PERPENDICULAR to
            // the horizontal leg. An earlier gate keyed on the toe-vs-leg angle stayed wide open here and threw the
            // knee ~60 deg laterally. The leg-posture gate must shut the follow OFF: a supine leg is not toe-steered.
            Vector3 hip = new Vector3(0.1f, 0.2f, 0f);
            Vector3 foot = hip + Vector3.forward * ((Thigh + Shin) * 0.92f);   // leg lies flat (horizontal)

            BasisKneeForwardInput i;
            i.HipPosition = hip;
            i.FootPosition = foot;
            i.FootForwardDir = Vector3.right;   // toe rolled fully sideways -- the worst case for the old gate
            i.BodyForwardDir = Vector3.up;      // supine: belly faces the ceiling
            i.PlayerUp = Vector3.up;
            i.UpperLength = Thigh;
            i.Coupling = DefaultCoupling;
            i.Strength = 1f;

            BasisKneeForwardCore.Solve(i, out var r);
            Assert.That(r.Upright01, Is.LessThan(0.05f), "a flat (horizontal) leg must read as not-upright.");
            Assert.That(r.FollowDeg, Is.LessThan(1f), $"a supine leg's knee must not chase the rolled foot; got {r.FollowDeg:0.0} deg.");
        }
        [Test]
        public void Fade_WhenTheLegReclines_HandingOffToButterfly()
        {
            // The follow is gated on LEG POSTURE: full while the leg is upright, off once it goes horizontal (lying).
            // Recline the leg from vertical (standing) to flat (supine) and require the gate to fall monotonically.
            var rows = new List<Row>();
            float prevUpright = float.PositiveInfinity;
            foreach (float recline in new[] { 0f, 30f, 50f, 70f, 80f, 90f })
            {
                var i = MakeInput(toeOutDeg: 30f, coupling: DefaultCoupling, reclineDeg: recline, isLeft: false);
                BasisKneeForwardCore.Solve(i, out var r);
                rows.Add(new Row { ToeOutDeg = recline, Upright01 = r.Upright01, FollowDeg = r.FollowDeg, HintWeight = r.HintWeight });
                Assert.That(r.Upright01, Is.LessThanOrEqualTo(prevUpright + 0.01f), $"leg-verticality rose while reclining (recline {recline}).");
                prevUpright = r.Upright01;
            }
            LogTable("Recline sweep (toe 30 deg)", rows);

            Assert.That(rows[0].Upright01, Is.GreaterThan(0.95f), "a vertical (standing) leg reads fully upright.");
            var supine = rows[rows.Count - 1];
            Assert.That(supine.Upright01, Is.LessThan(0.05f), "a flat (supine) leg reads not-upright.");
            Assert.That(supine.FollowDeg, Is.LessThan(1f), "supine: the follow must have faded off.");
        }
        [Test]
        public void Disabled_WhenStrengthZero()
        {
            var i = MakeInput(toeOutDeg: 30f, coupling: DefaultCoupling, reclineDeg: 0f, isLeft: false);
            i.Strength = 0f;
            BasisKneeForwardCore.Solve(i, out var r);
            Assert.That(r.HintWeight, Is.EqualTo(0f).Within(1e-4f), "zero strength must emit no hint weight.");
        }
        [Test]
        public void Knee_SwingsToward_TheFoot_ThroughTheLegSolver()
        {
            // End-to-end: the SOLVED knee azimuth must track the foot toe-out (monotone) and sit inboard of it,
            // and the flat-ahead foot must leave the knee ~body-forward.
            var ahead = Eval(toeOutDeg: 0f, coupling: DefaultCoupling);
            Assert.That(Mathf.Abs(ahead.SolvedKneeAzimuthDeg), Is.LessThan(1.5f), $"a foot pointing straight ahead should leave the knee ~forward; got {ahead.SolvedKneeAzimuthDeg:0.0} deg.");

            float prev = float.NegativeInfinity;
            var rows = new List<Row>();
            foreach (float toe in new[] { 0f, 10f, 20f, 35f, 50f })
            {
                var r = Eval(toe, DefaultCoupling);
                rows.Add(r);
                Assert.That(IsFinite(r.SolvedKneeAzimuthDeg), Is.True, $"solved knee azimuth non-finite at toe {toe}.");
                Assert.That(r.SolvedKneeAzimuthDeg, Is.GreaterThanOrEqualTo(prev - 0.5f), $"solved knee swung back toward center as the foot toed out (toe {toe}).");
                Assert.That(r.SolvedKneeAzimuthDeg, Is.LessThan(toe + 1f), "solved knee toed out MORE than the foot.");
                prev = r.SolvedKneeAzimuthDeg;
            }
            LogTable("Solved-knee azimuth vs foot toe-out", rows);
            Assert.That(rows[rows.Count - 1].SolvedKneeAzimuthDeg, Is.GreaterThan(rows[0].SolvedKneeAzimuthDeg + 3f),"a strongly toed-out foot should visibly swing the solved knee.");
        }
        [Test]
        public void BothLegs_FollowSymmetrically()
        {
            var right = Eval(toeOutDeg: 30f, coupling: DefaultCoupling, isLeft: false);
            var left = Eval(toeOutDeg: 30f, coupling: DefaultCoupling, isLeft: true);
            Assert.That(left.FollowDeg, Is.EqualTo(right.FollowDeg).Within(0.5f), "legs followed by different angles.");
            Assert.That(left.SolvedKneeAzimuthDeg, Is.EqualTo(right.SolvedKneeAzimuthDeg).Within(0.5f),"legs swung the solved knee by different amounts.");
        }
        [Test]
        public void StrongerCoupling_FollowsFurther()
        {
            var gentle = Eval(toeOutDeg: 20f, coupling: 0.2f);
            var strong = Eval(toeOutDeg: 20f, coupling: 0.8f);
            Assert.That(strong.FollowDeg, Is.GreaterThan(gentle.FollowDeg + 3f), $"a stronger coupling should follow the foot further ({strong.FollowDeg:0.0} vs {gentle.FollowDeg:0.0}).");
            Assert.That(strong.FollowDeg, Is.LessThan(20f + 0.5f), "even strong coupling keeps the knee inboard of the foot.");
        }
        [Test]
        public void AllOutputs_StayFinite_AcrossTheFullSweep()
        {
            foreach (float recline in new[] { 0f, 30f, 60f, 90f })
            foreach (float toe in new[] { -40f, 0f, 25f, 70f, 130f })
            foreach (float coupling in new[] { 0f, 0.35f, 1f })
            {
                var i = MakeInput(toe, coupling, recline, isLeft: false);
                BasisKneeForwardCore.Solve(i, out var r);
                Assert.That(IsFinite(r.FollowDeg) && IsFinite(r.Upright01) && IsFinite(r.HintWeight) && IsFinite(r.BendDir.x) && IsFinite(r.KneeHint.x), Is.True, $"a knee-forward output went non-finite (recline {recline}, toe {toe}, coupling {coupling}).");
                Assert.That(r.HintWeight, Is.InRange(0f, 1f), "hint weight left [0,1].");
                Assert.That(r.BendDir.magnitude, Is.InRange(0.99f, 1.01f), "bend dir left the unit sphere.");
            }
        }
        // ----------------------------------------------------------------- harness
        static Row Eval(float toeOutDeg, float coupling, bool isLeft = false) => EvalReclined(0f, toeOutDeg, coupling, isLeft);
        static Row EvalReclined(float reclineDeg, float toeOutDeg, float coupling, bool isLeft = false)
        {
            var i = MakeInput(toeOutDeg, coupling, reclineDeg, isLeft);
            BasisKneeForwardCore.Solve(i, out var r);
            float az = SolveKneeAzimuth(i, r, isLeft);
            return new Row
            {
                ToeOutDeg = toeOutDeg,
                Upright01 = r.Upright01,
                FollowDeg = r.FollowDeg,
                HintWeight = r.HintWeight,
                SolvedKneeAzimuthDeg = az,
            };
        }
        // Build one leg. reclineDeg tilts the whole leg from vertical (0, standing) to horizontal (90, supine),
        // pivoting the foot forward from under the hip; the toe rides with the leg and toes out by toeOutDeg.
        static BasisKneeForwardInput MakeInput(float toeOutDeg, float coupling, float reclineDeg, bool isLeft)
        {
            float side = isLeft ? -1f : 1f;
            Vector3 hip = new Vector3(0.1f * side, 0.9f, 0f);
            float rec = reclineDeg * Mathf.Deg2Rad;
            Vector3 downLeg = new Vector3(0f, -Mathf.Cos(rec), Mathf.Sin(rec)); // vertical -> horizontal(+Z)
            float legLen = Thigh + Shin;
            Vector3 foot = hip + downLeg * (legLen * 0.92f);

            // Toe: body-forward yawed outward by toeOutDeg about player-up (the foot points out), then pitched with
            // the leg so a reclining foot's toe follows the shin toward the hip->foot axis.
            float toe = toeOutDeg * Mathf.Deg2Rad * side;
            Vector3 toeFlat = new Vector3(Mathf.Sin(toe), 0f, Mathf.Cos(toe));
            Quaternion pitchWithLeg = Quaternion.AngleAxis(reclineDeg, Vector3.right);
            Vector3 footFwd = (pitchWithLeg * toeFlat).normalized;
            BasisKneeForwardInput i;
            i.HipPosition = hip;
            i.FootPosition = foot;
            i.FootForwardDir = footFwd;
            i.BodyForwardDir = BodyFwd;
            i.PlayerUp = Up;
            i.UpperLength = Thigh;
            i.Coupling = coupling;
            i.Strength = 1f;
            return i;
        }
        // Push the core's pole through the real two-bone leg solver and read the SOLVED knee's azimuth off
        // body-forward, in the plane perpendicular to the hip->foot axis, signed so outward (toward the toe) is +.
        static float SolveKneeAzimuth(in BasisKneeForwardInput i, in BasisKneeForwardResult core, bool isLeft)
        {
            Vector3 axis = (i.FootPosition - i.HipPosition).normalized;
            Vector3 restPerp = Vector3.ProjectOnPlane(i.BodyForwardDir, axis).normalized;
            Vector3 restKnee = (i.HipPosition + i.FootPosition) * 0.5f + restPerp * 0.05f;
            BasisLegSolveInput li = default;
            li.Root = i.HipPosition;
            li.Mid = restKnee;
            li.Tip = i.FootPosition;
            li.RootRotation = Quaternion.identity;
            li.MidRotation = Quaternion.identity;
            li.TargetPosition = i.FootPosition;
            li.TargetRotation = Quaternion.identity;
            li.HintPosition = core.KneeHint;
            li.HintWeight = core.HintWeight;
            li.TargetOffset = Quaternion.identity;
            li.BendNormal = HipsRight;

            BasisLegSolveCore.Solve(li, out BasisLegSolveResult lr);

            Vector3 kneeDir = Vector3.ProjectOnPlane(lr.KneeSolved - i.HipPosition, axis);
            Vector3 bodyPerp = Vector3.ProjectOnPlane(i.BodyForwardDir, axis);
            if (kneeDir.sqrMagnitude < 1e-8f || bodyPerp.sqrMagnitude < 1e-8f) return 0f;
            kneeDir.Normalize();
            bodyPerp.Normalize();

            float ang = Vector3.Angle(bodyPerp, kneeDir);
            Vector3 outward = (isLeft ? -HipsRight : HipsRight);
            Vector3 outPerp = Vector3.ProjectOnPlane(outward, axis).normalized;
            return Vector3.Dot(kneeDir, outPerp) < 0f ? -ang : ang; // + = toed outward toward the foot
        }
        static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        static void LogTable(string title, IEnumerable<Row> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== {title} ===");
            sb.AppendLine("  toeOutDeg  upright01  followDeg  weight   solvedKneeAzimuthDeg");
            foreach (var m in rows)
            {
                sb.AppendLine(string.Format("  {0,6:0.0}     {1,5:0.00}     {2,5:0.0}    {3,4:0.00}    {4,7:0.0}", m.ToeOutDeg, m.Upright01, m.FollowDeg, m.HintWeight, m.SolvedKneeAzimuthDeg));
            }
            TestContext.WriteLine(sb.ToString());
        }
    }
}
