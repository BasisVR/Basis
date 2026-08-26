using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisLegTwistSmoothingTests
    {
        const float Upper = 0.45f, Lower = 0.45f; // equal thigh/shin
        const float HintForward = 0.30f;          // knee hint forward of the knee
        const float Dt = 1f / 90f;                // VR frame time
        static float MaxReach => Upper + Lower;
        static readonly Vector3 Right = Vector3.right, Fwd = Vector3.forward, Up = Vector3.up;
        // Standing hips-yaw jitter (deg): zero-mean, high frequency -- the residual tracking noise of a
        // player trying to hold still. The derivative low-pass sees ~0 mean velocity so the cutoff stays at
        // its floor and the swivel is heavily smoothed.
        static float Jitter(float t) => 4f * (0.6f * Mathf.Sin(2f * Mathf.PI * 5f * t) + 0.4f * Mathf.Sin(2f * Mathf.PI * 11f * t + 1.3f));
        // Tracked-knee path cutoffs -- MUST mirror BasisFullIKConstraintJob.k_TrackedKneeSwivel* (lock-step).
        // Low floor (kills the pole-amplified tracker jitter at rest) + large beta (opens fast so deliberate
        // shin motion isn't lagged), unlike the standing path's 1 Hz / 0.05.
        const float TrackedMinCutoffHz = 1.5f, TrackedBeta = 0.20f, TrackedDerivHz = 1.0f;
        // Knee-tracker positional noise (metres): zero-mean, high frequency -- a physical tracker's residual
        // jitter. Applied along the swivel tangent, the leg solve's short pole lever arm amplifies these few
        // millimetres into degrees of knee swivel; that is the "sway" this fix targets.
        static float HintJitter(float t) => 0.010f * (0.6f * Mathf.Sin(2f * Mathf.PI * 5f * t) + 0.4f * Mathf.Sin(2f * Mathf.PI * 11f * t + 1.3f));
        // ----------------------------------------------------------------- filter gate (synthetic)
        [Test]
        public void Filter_RejectsStandingJitter()
        {
            // ±4 deg of high-freq swivel jitter, foot/leg otherwise still. The One-Euro must collapse it.
            int steps = 270; // 3 s
            var raw = new List<float>(steps);
            var smooth = new List<float>(steps);
            BasisSwivelFilterState s = default;
            bool seeded = false;
            for (int i = 0; i < steps; i++)
            {
                float t = i * Dt, swivel = Jitter(t);
                if (!seeded) { s = BasisSwivelFilterCore.Seed(swivel); seeded = true; }
                else s = BasisSwivelFilterCore.Step(s, swivel, Dt);
                raw.Add(swivel);
                smooth.Add(s.Smooth);
            }
            float rawP2P = P2P(raw, 45);     // skip 0.5 s settle
            float smoothP2P = P2P(smooth, 45);
            TestContext.WriteLine($"Filter jitter: raw {rawP2P:0.0} deg p2p -> smoothed {smoothP2P:0.0} deg p2p ({smoothP2P / Mathf.Max(rawP2P, 1e-3f):P0})");

            Assert.That(rawP2P, Is.GreaterThan(3f), "synthetic jitter should give a visible raw swivel range (test wiring).");
            Assert.That(smoothP2P, Is.LessThan(rawP2P * 0.4f), $"One-Euro should cut standing swivel jitter to <40% of raw; got {smoothP2P:0.0}/{rawP2P:0.0} deg.");
        }
        [Test]
        public void Filter_TracksARealTurn_WithoutFreezing()
        {
            // A genuine body turn (steady yaw) must carry the swivel with the filter -- it must not be
            // mistaken for jitter and frozen. Steady-state lag must stay small.
            const float turnRateDeg = 60f;
            int steps = 180; // 2 s
            var raw = new List<float>(steps);
            var smooth = new List<float>(steps);
            BasisSwivelFilterState s = default;
            bool seeded = false;
            float maxLag = 0f;
            for (int i = 0; i < steps; i++)
            {
                float t = i * Dt, swivel = turnRateDeg * t;
                if (!seeded) { s = BasisSwivelFilterCore.Seed(swivel); seeded = true; }
                else s = BasisSwivelFilterCore.Step(s, swivel, Dt);
                raw.Add(swivel);
                smooth.Add(s.Smooth);
                if (t > 0.5f) maxLag = Mathf.Max(maxLag, Mathf.Abs(swivel - s.Smooth));
            }
            float rawChange = raw[steps - 1] - raw[45], smoothChange = smooth[steps - 1] - smooth[45];
            TestContext.WriteLine($"Filter turn @ {turnRateDeg}deg/s: raw d{rawChange:0.0} -> smoothed d{smoothChange:0.0} deg, steady lag {maxLag:0.0} deg");

            Assert.That(smoothChange, Is.GreaterThan(rawChange * 0.8f), $"smoothed swivel must follow a real turn (got {smoothChange:0.0} of {rawChange:0.0} deg) -- not frozen.");
            Assert.That(maxLag, Is.LessThan(20f), $"turn-tracking lag {maxLag:0.0} deg too high (over-smoothed).");
        }
        // ----------------------------------------------------------------- solver gate (BasisLegSolveCore)
        [Test]
        public void Solver_StandingYawJitter_IsSmoothed()
        {
            // Drive the real leg solver at standing extensions with a hips-yaw-jittering bend frame (bend
            // normal + knee hint both yaw, as both derive from the hips when standing). Measure the raw
            // knee-swivel excursion, then the same One-Euro pass; the filter must collapse it where the leg
            // is actually touchy. Thresholds calibrated to the deterministic sweep below -- re-check on first run.
            float worstRaw = 0f, worstRawSmoothed = 0f, worstExt = 0f;
            var rows = new List<(float ext, float raw, float smooth)>();
            foreach (float ext in new[] { 0.94f, 0.96f, 0.97f, 0.98f, 0.99f })
            {
                Drive(ext, Jitter, 270, out float rawP2P, out float smoothP2P, out _);
                rows.Add((ext, rawP2P, smoothP2P));
                if (rawP2P > worstRaw) { worstRaw = rawP2P; worstRawSmoothed = smoothP2P; worstExt = ext; }
            }
            LogRows("standing yaw jitter ±4deg", rows);

            Assert.That(worstRaw, Is.GreaterThan(2f), $"standing yaw jitter should visibly roll the near-straight leg somewhere in 0.94..0.99 (worst raw {worstRaw:0.0} deg @ ext {worstExt:0.00}); if not, the bend frame isn't reaching the knee.");
            Assert.That(worstRawSmoothed, Is.LessThan(worstRaw * 0.6f), $"knee-swivel smoothing should cut the worst standing twist below 60% of raw (got {worstRawSmoothed:0.0}/{worstRaw:0.0} deg @ ext {worstExt:0.00}).");
        }
        [Test]
        public void Solver_RealTurn_StillTracks()
        {
            // The same solver path under a real turn: the smoothed knee swivel must still follow the body so
            // the legs don't lag when you actually rotate.
            Drive(0.97f, t => 60f * t, 180, out float rawP2P, out float smoothP2P, out float maxLag);
            TestContext.WriteLine($"Solver turn @60deg/s ext0.97: raw {rawP2P:0.0} -> smoothed {smoothP2P:0.0} deg p2p, lag {maxLag:0.0} deg");
            Assert.That(smoothP2P, Is.GreaterThan(rawP2P * 0.7f), $"smoothed knee swivel must track a real turn (got {smoothP2P:0.0} of {rawP2P:0.0} deg).");
        }
        // -------------------------------------------- tracked-knee hint gate (pole-amplified tracker noise)
        // These cover the OTHER SmoothKneeSwivel entry point: a player with a physical knee/lower-leg tracker.
        // There the hint is the tracked shin (not a computed foot-driver pole), and the leg solve reduces it to
        // a pole DIRECTION with a short lever arm, so a few mm of tracker jitter become degrees of knee swivel
        // -- the "legs sway when moving" report. The fix routes that leg through the same output-swivel One-Euro
        // but with the more-responsive Tracked* cutoffs (low floor kills the jitter, big beta keeps real shin
        // motion from lagging). Thresholds are hand-set for the deterministic drive below -- re-check on first run.
        [Test]
        public void TrackedFilter_RejectsAmplifiedHintJitter()
        {
            // The pole-amplified swivel jitter, run through the tracked-knee filter, must still be visibly cut
            // even though its floor is lower (more responsive) than the standing path.
            int steps = 270;
            var raw = new List<float>(steps);
            var smooth = new List<float>(steps);
            BasisSwivelFilterState s = default;
            bool seeded = false;
            for (int i = 0; i < steps; i++)
            {
                float t = i * Dt;
                float swivel = Jitter(t); // reuse the ±4 deg high-freq shape as a stand-in for amplified swivel jitter
                if (!seeded) { s = BasisSwivelFilterCore.Seed(swivel); seeded = true; }
                else s = BasisSwivelFilterCore.Step(s, swivel, Dt, TrackedMinCutoffHz, TrackedBeta, TrackedDerivHz);
                raw.Add(swivel);
                smooth.Add(s.Smooth);
            }
            float rawP2P = P2P(raw, 45), smoothP2P = P2P(smooth, 45);
            TestContext.WriteLine($"Tracked filter jitter: raw {rawP2P:0.0} -> smoothed {smoothP2P:0.0} deg p2p ({smoothP2P / Mathf.Max(rawP2P, 1e-3f):P0})");
            Assert.That(smoothP2P, Is.LessThan(rawP2P * 0.6f), $"tracked-knee One-Euro should still cut rest jitter below 60% of raw; got {smoothP2P:0.0}/{rawP2P:0.0} deg. If not, the floor is too high to reject tracker noise.");
        }
        [Test]
        public void TrackedFilter_TracksDeliberateShinMotion_TighterThanStanding()
        {
            // The whole reason for the higher beta: a knee tracker is a real signal. A deliberate shin swivel
            // must track with LESS lag than the standing path would give (else we've just relabelled the lag).
            const float rateDeg = 60f;
            BasisSwivelFilterState tracked = default, standing = default;
            bool seeded = false;
            float trackedLag = 0f, standingLag = 0f;
            for (int i = 0; i < 180; i++)
            {
                float t = i * Dt, swivel = rateDeg * t;
                if (!seeded) { tracked = BasisSwivelFilterCore.Seed(swivel); standing = BasisSwivelFilterCore.Seed(swivel); seeded = true; }
                else
                {
                    tracked = BasisSwivelFilterCore.Step(tracked, swivel, Dt, TrackedMinCutoffHz, TrackedBeta, TrackedDerivHz);
                    standing = BasisSwivelFilterCore.Step(standing, swivel, Dt); // default (standing) cutoffs
                }
                if (t > 0.5f)
                {
                    trackedLag = Mathf.Max(trackedLag, Mathf.Abs(swivel - tracked.Smooth));
                    standingLag = Mathf.Max(standingLag, Mathf.Abs(swivel - standing.Smooth));
                }
            }
            TestContext.WriteLine($"Deliberate shin @ {rateDeg}deg/s steady lag: tracked {trackedLag:0.0} vs standing {standingLag:0.0} deg");
            Assert.That(trackedLag, Is.LessThan(standingLag), $"tracked cutoffs must lag a real shin swivel less than the standing floor ({trackedLag:0.0} vs {standingLag:0.0} deg).");
            Assert.That(trackedLag, Is.LessThan(12f), $"tracked shin-tracking lag {trackedLag:0.0} deg too high.");
        }
        [Test]
        public void TrackedSolver_HintJitter_IsSmoothed()
        {
            // Drive the real leg solver at a bent (walking) pose with a jittering KNEE-TRACKER hint and a fixed
            // bend frame (the body isn't turning -- the sway is pure hint noise). The short pole lever arm must
            // amplify the few-mm hint jitter into a visible raw knee swivel, and the tracked-knee One-Euro must
            // collapse it.
            RunTracked(0.72f, (h, k, f, t) => k + SwivelTangent(h, k, f) * HintJitter(t), 270, out float rawP2P, out float smoothP2P, out _);
            TestContext.WriteLine($"Tracked solver hint-jitter (bent 0.72): raw {rawP2P:0.0} -> smoothed {smoothP2P:0.0} deg p2p ({smoothP2P / Mathf.Max(rawP2P, 1e-3f):P0})");
            Assert.That(rawP2P, Is.GreaterThan(1f), $"a few mm of hint jitter should amplify into a visible raw knee swivel (got {rawP2P:0.0} deg); if not, the pole lever arm / drive wiring is off.");
            Assert.That(smoothP2P, Is.LessThan(rawP2P * 0.65f), $"tracked-knee smoothing should cut the amplified sway below 65% of raw (got {smoothP2P:0.0}/{rawP2P:0.0} deg).");
        }
        [Test]
        public void TrackedSolver_DeliberateShinSwing_StillTracks()
        {
            // Same solver path, but now the hint deliberately swivels the knee (the user rotating their shin).
            // The smoothed knee must follow so the tracked knee doesn't feel laggy.
            RunTracked(0.72f, (h, k, f, t) => HintSwiveled(h, k, f, 40f * t), 180, out float rawP2P, out float smoothP2P, out float maxLag);
            TestContext.WriteLine($"Tracked solver shin-swing @40deg/s (bent 0.72): raw {rawP2P:0.0} -> smoothed {smoothP2P:0.0} deg p2p, lag {maxLag:0.0} deg");
            Assert.That(smoothP2P, Is.GreaterThan(rawP2P * 0.75f), $"smoothed knee swivel must track a deliberate shin swing (got {smoothP2P:0.0} of {rawP2P:0.0} deg).");
            Assert.That(maxLag, Is.LessThan(15f), $"deliberate-swing tracking lag {maxLag:0.0} deg too high.");
        }
        [Test]
        public void Characterize_PrintTables()
        {
            var rows = new List<(float ext, float raw, float smooth)>();
            foreach (float ext in new[] { 0.90f, 0.94f, 0.96f, 0.97f, 0.98f, 0.99f, 0.995f })
            {
                Drive(ext, Jitter, 270, out float rawP2P, out float smoothP2P, out _);
                rows.Add((ext, rawP2P, smoothP2P));
            }
            LogRows("knee swivel under ±4deg standing yaw jitter, per standing extension", rows);
            Assert.Pass("see table");
        }
        [Test]
        public void AllOutputs_StayFinite()
        {
            foreach (float ext in new[] { 0.90f, 0.97f, 0.999f })
            {
                Drive(ext, Jitter, 120, out float rawP2P, out float smoothP2P, out float lag);
                Assert.That(IsFinite(rawP2P) && IsFinite(smoothP2P) && IsFinite(lag), Is.True, $"ext {ext}: non-finite metric.");
            }
        }
        // ----------------------------------------------------------------- harness
        // Solve the standing leg over a yawing bend frame, run the live One-Euro on the output knee swivel,
        // and return raw/smoothed peak-to-peak swivel (steady window) plus the worst steady tracking lag.
        static void Drive(float ratio, System.Func<float, float> yawDeg, int steps, out float rawP2P, out float smoothP2P, out float maxLag)
        {
            BuildStanding(ratio, out Vector3 hip, out Vector3 knee, out Vector3 foot);
            Vector3 plant = foot;

            var raw = new List<float>(steps);
            var smooth = new List<float>(steps);
            BasisSwivelFilterState s = default;
            bool seeded = false;
            maxLag = 0f;
            for (int i = 0; i < steps; i++)
            {
                float t = i * Dt;
                Quaternion yaw = Quaternion.AngleAxis(yawDeg(t), Up);
                BasisLegSolveInput li = default;
                li.Root = hip;
                li.Mid = knee;
                li.Tip = foot;
                li.RootRotation = Quaternion.identity;
                li.MidRotation = Quaternion.identity;
                li.TargetPosition = plant;
                li.TargetRotation = Quaternion.identity;
                li.TargetOffset = Quaternion.identity;
                li.HintPosition = knee + (yaw * Fwd) * HintForward;
                li.HintWeight = 1f;
                li.BendNormal = yaw * Right;

                BasisLegSolveCore.Solve(li, out BasisLegSolveResult r);
                float swivel = ComputeSwivel(hip, r.KneeSolved, plant);
                if (!seeded) { s = BasisSwivelFilterCore.Seed(swivel); seeded = true; }
                else s = BasisSwivelFilterCore.Step(s, swivel, Dt);
                raw.Add(swivel);
                smooth.Add(s.Smooth);
                if (t > 0.5f) maxLag = Mathf.Max(maxLag, Mathf.Abs(swivel - s.Smooth));
            }
            rawP2P = P2P(raw, 45);
            smoothP2P = P2P(smooth, 45);
        }
        // Drive the leg solver with a moving KNEE-TRACKER hint (fixed bend frame -- no body turn), run the
        // tracked-knee One-Euro (Tracked* cutoffs) on the output swivel, and return raw/smoothed peak-to-peak
        // swivel (steady window) plus worst steady tracking lag. hintAt(hip,knee,foot,t) supplies the hint pos.
        static void RunTracked(float ratio, System.Func<Vector3, Vector3, Vector3, float, Vector3> hintAt, int steps, out float rawP2P, out float smoothP2P, out float maxLag)
        {
            BuildStanding(ratio, out Vector3 hip, out Vector3 knee, out Vector3 foot);
            var raw = new List<float>(steps);
            var smooth = new List<float>(steps);
            BasisSwivelFilterState s = default;
            bool seeded = false;
            maxLag = 0f;
            for (int i = 0; i < steps; i++)
            {
                float t = i * Dt;
                BasisLegSolveInput li = default;
                li.Root = hip;
                li.Mid = knee;
                li.Tip = foot;
                li.RootRotation = Quaternion.identity;
                li.MidRotation = Quaternion.identity;
                li.TargetPosition = foot;         // foot planted on target (as with a tracked foot)
                li.TargetRotation = Quaternion.identity;
                li.TargetOffset = Quaternion.identity;
                li.HintPosition = hintAt(hip, knee, foot, t);
                li.HintWeight = 1f;
                li.BendNormal = Right;            // fixed: the sway is hint noise, not a body turn
                BasisLegSolveCore.Solve(li, out BasisLegSolveResult r);
                float swivel = ComputeSwivel(hip, r.KneeSolved, foot);
                if (!seeded) { s = BasisSwivelFilterCore.Seed(swivel); seeded = true; }
                else s = BasisSwivelFilterCore.Step(s, swivel, Dt, TrackedMinCutoffHz, TrackedBeta, TrackedDerivHz);
                raw.Add(swivel);
                smooth.Add(s.Smooth);
                if (t > 0.5f) maxLag = Mathf.Max(maxLag, Mathf.Abs(swivel - s.Smooth));
            }
            rawP2P = P2P(raw, 45);
            smoothP2P = P2P(smooth, 45);
        }
        // Unit "sideways" direction at the knee: perpendicular to both the hip->foot axis and the pole, i.e.
        // the direction a hint displacement rotates the knee about the leg axis (pure swivel).
        static Vector3 SwivelTangent(Vector3 hip, Vector3 knee, Vector3 foot)
        {
            Vector3 axis = (foot - hip).normalized, poleDir = (knee - hip);
            poleDir -= axis * Vector3.Dot(poleDir, axis);
            if (poleDir.sqrMagnitude < 1e-8f) return Vector3.Cross(axis, Fwd).normalized;
            poleDir.Normalize();
            return Vector3.Cross(axis, poleDir).normalized;
        }
        // The knee hint swivelled deg degrees about the hip->foot axis (a deliberate shin rotation).
        static Vector3 HintSwiveled(Vector3 hip, Vector3 knee, Vector3 foot, float deg)
        {
            Vector3 axis = (foot - hip).normalized, pole = knee - hip;
            float along = Vector3.Dot(pole, axis);
            Vector3 perp = pole - axis * along, rotated = Quaternion.AngleAxis(deg, axis) * perp;
            return hip + axis * along + rotated;
        }
        // Knee swivel about the hip->foot axis, referenced off forward -- identical to the live SmoothKneeSwivel.
        static float ComputeSwivel(Vector3 hip, Vector3 knee, Vector3 foot)
        {
            Vector3 ac = foot - hip;
            if (ac.sqrMagnitude < 1e-8f) return 0f;
            Vector3 axis = ac.normalized, refDir = Fwd - axis * Vector3.Dot(Fwd, axis);
            if (refDir.sqrMagnitude < 1e-8f) refDir = Right - axis * Vector3.Dot(Right, axis);
            Vector3 pole = (knee - hip);
            pole -= axis * Vector3.Dot(pole, axis);
            if (refDir.sqrMagnitude < 1e-8f || pole.sqrMagnitude < 1e-8f) return 0f;
            return Vector3.SignedAngle(refDir.normalized, pole, axis);
        }
        static float P2P(List<float> xs, int skip)
        {
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            for (int i = skip; i < xs.Count; i++) { min = Mathf.Min(min, xs[i]); max = Mathf.Max(max, xs[i]); }
            return (max >= min) ? max - min : 0f;
        }
        static void BuildStanding(float ratio, out Vector3 hip, out Vector3 knee, out Vector3 foot)
        {
            float chord = ratio * MaxReach;
            foot = Vector3.zero;
            hip = new Vector3(0f, chord, 0f);
            knee = RestKnee(hip, foot);
        }
        static Vector3 RestKnee(Vector3 hip, Vector3 foot)
        {
            Vector3 chord = foot - hip;
            float d = Mathf.Min(chord.magnitude, MaxReach * 0.999f);
            Vector3 along = chord.normalized;
            float proj = (Upper * Upper + d * d - Lower * Lower) / (2f * d);
            float h = Mathf.Sqrt(Mathf.Max(0f, Upper * Upper - proj * proj));
            Vector3 perp = Vector3.Cross(along, Right).normalized;
            if (Vector3.Dot(perp, Fwd) < 0f) perp = -perp;
            return hip + along * proj + perp * h;
        }
        static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        static void LogRows(string title, List<(float ext, float raw, float smooth)> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== {title} ===");
            sb.AppendLine(" standExt   rawP2P  smoothP2P   ratio");
            foreach (var r in rows)
                sb.AppendLine(string.Format("  {0:0.000}   {1,6:0.0}d  {2,6:0.0}d   {3,5:0.00}", r.ext, r.raw, r.smooth, r.smooth / Mathf.Max(r.raw, 1e-3f)));
            TestContext.WriteLine(sb.ToString());
        }
    }
}
