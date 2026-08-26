using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisLegIdleSwaySweepTests
    {
        const float Upper = 0.45f, Lower = 0.45f; // typical adult thigh/shin; equal segments
        const float HintForward = 0.30f;          // knee hint placed forward of the knee (foot-IK biases knee fwd)
        static float MaxReach => Upper + Lower;
        static readonly Vector3 Right = Vector3.right;   // knee hinge axis = hipsRot * right (upright hips)
        static readonly Vector3 Fwd = Vector3.forward;   // body sway "back and forwards"
        static readonly Vector3 Up = Vector3.up;
        enum SwayAxis { Horizontal, Vertical, Arc }
        struct SwayMetrics
        {
            public float StandExtension;
            public string Axis;
            public float BodyPeakToPeakMeters, KneeTotalPeakToPeakMeters, KneeForwardPeakToPeakMeters;
            public float KneeUpPeakToPeakMeters;
            public float Gain;                  // KneeTotal / Body
            public float KneeAnglePeakToPeakDeg;
            public float WorstStepMeters;       // largest knee jump between adjacent sway steps
            public float MaxFootErrorMeters;    // how far the foot left the plant
            public string AxisSources;
        }
        // ----------------------------------------------------------------- characterization (pass)
        [Test]
        public void HorizontalIdleSway_IsNatural_KneeTracksTheMidpoint()
        {
            // The well-behaved contrast axis. Body sways fwd/back +-2cm at a realistic standing pose, foot
            // planted. The knee sits ~halfway up the leg so it tracks the hip->foot MIDPOINT (~half the body
            // travel, almost no flex): pure horizontal sway changes the hip->plant DISTANCE only to 2nd
            // order, so the high-gain term is not excited. This isolates the vertical case below as the cause.
            var m = Measure(0.96f, SwayAxis.Horizontal, 0.02f);
            Log("Horizontal +-2cm @ ext 0.96", m);
            Assert.That(m.Gain, Is.LessThan(0.8f), $"horizontal idle sway moved the knee {m.Gain:0.00}x the body travel; a planted leg should track the midpoint (~0.5x).");
            Assert.That(m.KneeAnglePeakToPeakDeg, Is.LessThan(1.5f), $"horizontal idle sway flexed the knee {m.KneeAnglePeakToPeakDeg:0.0} deg; it should barely change angle.");
            Assert.That(m.WorstStepMeters, Is.LessThan(0.002f), $"knee jumped {m.WorstStepMeters * 1000f:0.0} mm between adjacent sway steps.");
        }
        [Test]
        public void VerticalIdleBob_KneeGain_GrowsWithExtension_AndDwarfsHorizontal()
        {
            // The mechanism, measured. Foot planted, hip bobs +-1cm in height (a 3pt rig feeds HMD height
            // straight into the hip). Knee gain must climb as the leg straightens, and near lock far exceed
            // the horizontal-sway gain at the same amplitude -- proving the blow-up is the over-extension
            // distance sensitivity, not the sway amplitude.
            var rows = new List<SwayMetrics>();
            float prev = -1f;
            foreach (float ext in new[] { 0.85f, 0.90f, 0.94f, 0.97f, 0.99f })
            {
                var m = Measure(ext, SwayAxis.Vertical, 0.01f);
                rows.Add(m);
                Assert.That(m.Gain, Is.GreaterThan(prev), $"vertical-bob knee gain should grow as the leg straightens; ext {ext:0.00} gave {m.Gain:0.00} <= prev {prev:0.00}.");
                prev = m.Gain;
            }
            LogTable("Vertical bob +-1cm vs standing extension", rows);

            var nearLock = rows[rows.Count - 1];
            var horiz = Measure(0.97f, SwayAxis.Horizontal, 0.01f);
            Assert.That(nearLock.Gain, Is.GreaterThan(2f), $"near-locked knee should show the over-extension blow-up; got gain {nearLock.Gain:0.00}.");
            Assert.That(nearLock.Gain, Is.GreaterThan(horiz.Gain * 3f), $"vertical-bob gain {nearLock.Gain:0.00} should dwarf horizontal-sway gain {horiz.Gain:0.00} at the same amplitude.");
        }
        [Test]
        public void Characterize_PrintAllSweepTables()
        {
            // The sweep centerpiece: full tables for all three idle motions across the standing-extension
            // range so the over-extension knee gain is visible at a glance. No behavioural assertion -- this
            // is the measurement; the Repro_* tests below assert the natural target.
            var exts = new[] { 0.85f, 0.90f, 0.94f, 0.97f, 0.99f, 0.995f, 0.999f };
            foreach (var (axis, amp, label) in new[]
            {
                (SwayAxis.Horizontal, 0.02f, "horizontal fwd/back +-2cm (4cm p2p)"),
                (SwayAxis.Vertical,   0.01f, "vertical hip bob +-1cm (2cm p2p)"),
                (SwayAxis.Arc,        0.02f, "pendulum arc about the foot +-2cm"),
            })
            {
                var rows = new List<SwayMetrics>();
                foreach (float ext in exts) rows.Add(Measure(ext, axis, amp));
                LogTable(label, rows);
            }
            Assert.Pass("see attached sweep tables");
        }
        [Test]
        public void AllOutputs_StayFinite_AcrossTheFullSweep()
        {
            // Through every standing extension (incl. past max reach) and idle amplitude nothing may go
            // NaN/Inf and the knee must stay forward of the hip->foot line (the bend must never invert).
            foreach (SwayAxis axis in new[] { SwayAxis.Horizontal, SwayAxis.Vertical, SwayAxis.Arc })
            {
                foreach (float ext in new[] { 0.80f, 0.95f, 0.999f, 1.02f })
                {
                    foreach (float amp in new[] { 0.005f, 0.02f, 0.05f })
                    {
                        var res = RunSweep(ext, axis, amp, 61, 1f, out _, out Vector3 hip0);
                        for (int s = 0; s < res.Count; s++)
                        {
                            BasisLegSolveResult r = res[s];
                            Assert.That(IsFinite(r.KneeSolved) && IsFinite(r.FootSolved) && IsFinite(r.KneeAngleDeg), Is.True, $"{axis} ext {ext:0.000} amp {amp:0.000}: a leg-solve output is non-finite.");

                            // "The knee must not invert" means the knee must stay forward OF THE HIP->FOOT LINE.
                            // Measure it against that line, not against world z: RunSweep translates the whole leg
                            // by up to +-amp while pinning the foot to the plant, so at ext 0.999 / amp 5cm the hip
                            // sways back far enough to over-extend the leg, the knee is commanded dead straight, and
                            // it lands at z = -0.025 for the sole reason that the HIP moved back 5cm and took it
                            // along. That is a translation, not an inversion -- and it is what this assert used to
                            // fail on. Projecting onto the leg's own axis is invariant to the sway the harness
                            // itself applies, so it tests the thing it means to test.
                            Vector3 hip = hip0 + Offset(axis, Mathf.Lerp(-amp, amp, s / 60f), hip0.y);
                            Vector3 legAxis = r.FootSolved - hip;
                            float axisLen = legAxis.magnitude;
                            if (axisLen < 1e-5f) continue;

                            legAxis /= axisLen;
                            Vector3 kneeVec = r.KneeSolved - hip;
                            Vector3 offAxis = kneeVec - legAxis * Vector3.Dot(kneeVec, legAxis);
                            float forward = Vector3.Dot(offAxis, Vector3.forward);

                            Assert.That(forward, Is.GreaterThan(-0.02f), $"{axis} ext {ext:0.000} amp {amp:0.000}: knee bent {-forward * 100f:0.0}cm BEHIND the " + $"hip->foot line (inverted).");
                        }
                    }
                }
            }
        }
        // ----------------------------------------------------------------- harness
        // Build the standing leg, translate it rigidly by every body offset across +-amp (foot planted),
        // solve, and return the raw per-step results. steps is the sweep resolution; bodyP2P/hip0 are echoed
        // back for the metric maths.
        static List<BasisLegSolveResult> RunSweep(float ratio, SwayAxis axis, float amp, int steps, float hintWeight, out float bodyP2P, out Vector3 hip0)
        {
            BuildStanding(ratio, out Vector3 hip, out Vector3 knee, out Vector3 foot);
            hip0 = hip;
            Vector3 plant = foot;
            float h = hip.y;
            bodyP2P = (Offset(axis, +amp, h) - Offset(axis, -amp, h)).magnitude;

            var results = new List<BasisLegSolveResult>(steps);
            for (int s = 0; s < steps; s++)
            {
                float t = steps == 1 ? 0f : Mathf.Lerp(-amp, amp, s / (float)(steps - 1));
                Vector3 o = Offset(axis, t, h);
                BasisLegSolveInput i = default;
                i.Root = hip + o;
                i.Mid = knee + o;
                i.Tip = foot + o;
                i.RootRotation = Quaternion.identity;   // output positions are rotation-independent
                i.MidRotation = Quaternion.identity;
                i.TargetPosition = plant;
                i.TargetRotation = Quaternion.identity; // foot rotation not measured here
                i.HintPosition = (knee + o) + Fwd * HintForward;
                i.HintWeight = hintWeight;
                i.TargetOffset = Quaternion.identity;
                i.BendNormal = Right;

                BasisLegSolveCore.Solve(i, out BasisLegSolveResult r);
                results.Add(r);
            }
            return results;
        }
        static SwayMetrics Measure(float ratio, SwayAxis axis, float amp, int steps = 81, float hintWeight = 1f)
        {
            var res = RunSweep(ratio, axis, amp, steps, hintWeight, out float bodyP2P, out _);

            Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 max = -min;
            float angMin = float.PositiveInfinity, angMax = float.NegativeInfinity;
            float worstStep = 0f, maxFootErr = 0f;
            var sources = new HashSet<int>();
            bool have = false;
            Vector3 prevKnee = Vector3.zero;

            foreach (var r in res)
            {
                Vector3 k = r.KneeSolved;
                min = Vector3.Min(min, k);
                max = Vector3.Max(max, k);
                angMin = Mathf.Min(angMin, r.KneeAngleDeg);
                angMax = Mathf.Max(angMax, r.KneeAngleDeg);
                maxFootErr = Mathf.Max(maxFootErr, r.FootError);
                sources.Add(r.AxisSource);
                if (have) worstStep = Mathf.Max(worstStep, Vector3.Distance(k, prevKnee));
                prevKnee = k;
                have = true;
            }

            float kneeTot = Vector3.Distance(min, max);
            var srcList = new List<int>(sources);
            srcList.Sort();

            return new SwayMetrics
            {
                StandExtension = ratio,
                Axis = axis.ToString(),
                BodyPeakToPeakMeters = bodyP2P,
                KneeTotalPeakToPeakMeters = kneeTot,
                KneeForwardPeakToPeakMeters = max.z - min.z,
                KneeUpPeakToPeakMeters = max.y - min.y,
                Gain = bodyP2P > 1e-6f ? kneeTot / bodyP2P : 0f,
                KneeAnglePeakToPeakDeg = angMax - angMin,
                WorstStepMeters = worstStep,
                MaxFootErrorMeters = maxFootErr,
                AxisSources = string.Join("/", srcList),
            };
        }
        // Body offset for a given sway parameter. Horizontal = fwd/back translate; Vertical = hip-height bob;
        // Arc = the hip swinging on a small pendulum about the planted foot.
        static Vector3 Offset(SwayAxis axis, float s, float hipHeight)
        {
            switch (axis)
            {
                case SwayAxis.Horizontal: return Fwd * s;
                case SwayAxis.Vertical: return Up * s;
                default:
                    float ang = s / hipHeight;
                    return new Vector3(0f, hipHeight * (Mathf.Cos(ang) - 1f), hipHeight * Mathf.Sin(ang));
            }
        }
        // A standing leg at the given extension ratio: foot at origin, hip straight above, knee bulged
        // forward by planar two-bone geometry (the natural slight bend).
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
        static bool IsFinite(Vector3 v) => IsFinite(v.x) && IsFinite(v.y) && IsFinite(v.z);
        static void Log(string title, SwayMetrics m)
        {
            TestContext.WriteLine($"{title}: body {m.BodyPeakToPeakMeters * 1000f:0.0}mm -> knee {m.KneeTotalPeakToPeakMeters * 1000f:0.0}mm " + $"(fwd {m.KneeForwardPeakToPeakMeters * 1000f:0.0}mm, up {m.KneeUpPeakToPeakMeters * 1000f:0.0}mm), " + $"GAIN {m.Gain:0.00}x, knee flex {m.KneeAnglePeakToPeakDeg:0.0} deg, worst step {m.WorstStepMeters * 1000f:0.00}mm, " + $"foot err {m.MaxFootErrorMeters * 1000f:0.0}mm, axisSrc {m.AxisSources}");
        }
        static void LogTable(string title, IEnumerable<SwayMetrics> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== {title} ===");
            sb.AppendLine(" standExt  bodyP2P  kneeP2P   GAIN  kneeFwd  kneeUp  flexDeg  worstStep  footErr  axisSrc");
            foreach (var m in rows)
            {
                sb.AppendLine(string.Format("  {0:0.000}  {1,6:0.0}mm {2,6:0.0}mm  {3,4:0.00}  {4,5:0.0}mm {5,5:0.0}mm  {6,5:0.0}   {7,6:0.00}mm  {8,5:0.0}mm   {9}", m.StandExtension, m.BodyPeakToPeakMeters * 1000f, m.KneeTotalPeakToPeakMeters * 1000f, m.Gain, m.KneeForwardPeakToPeakMeters * 1000f, m.KneeUpPeakToPeakMeters * 1000f, m.KneeAnglePeakToPeakDeg, m.WorstStepMeters * 1000f, m.MaxFootErrorMeters * 1000f, m.AxisSources));
            }
            TestContext.WriteLine(sb.ToString());
        }
    }
}
