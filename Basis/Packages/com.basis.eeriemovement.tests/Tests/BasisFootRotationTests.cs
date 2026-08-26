using System.Collections.Generic;
using System.Text;
using Basis.IK.Mocap;
using NUnit.Framework;
using UnityEngine;
namespace Basis.Tests.IK
{
    public sealed class BasisFootRotationTests
    {
        static readonly float[] Scales = { 0.6f, 1.0f, 1.8f };
        // Includes EXTREME rates (600-1200 deg/s = a fast physical whip / snap turn) -- the anti-overlap
        // backstop must hold there, which the tracking+widening alone did not.
        static readonly float[] Rates = { 60f, 120f, 240f, 360f, 600f, 900f, 1200f };
        const float MaxCollidePct = 3f;    // feet overlap (gap < foot length) on at most this % of ticks
        const float MaxCrossMaxCm = 3f;    // a foot may cross to the wrong side of the other by at most this (scaled below)
        static string Row(float scale, float rate, bool stick, in BasisFootRotationResult r) => $"  {(stick ? "stick" : "phys"),-6} {scale,4:F1}x {rate,4:F0}deg/s | " + $"crossMax {r.CrossMaxCm,6:F1}cm  crossed {r.CrossedPct,5:F1}%  minGap {r.MinGapCm,5:F1}cm  " + $"collide {r.CollidePct,5:F1}%  bothStep {r.BothSteppingTicks}";
        [Test]
        public void Feet_DoNotStepOnEachOther_DuringFastRotation()
        {
            var report = new StringBuilder();
            report.AppendLine("In-place spin -- feet must not overlap (collide) or step in flight (bothStep):");
            var failures = new List<string>();

            foreach (float scale in Scales)
            {
                BasisFootSimParams p = BasisMocapFootQuality.BuildReferenceParams(scale);
                foreach (bool stick in new[] { false, true })
                {
                    foreach (float rate in Rates)
                    {
                        BasisFootRotationResult r = BasisMocapFootQuality.RunSpin(p, rate, stick);
                        report.AppendLine(Row(scale, rate, stick, r));

                        // COLLISION -- the literal "stepping on each other" -- is the real criterion, gated at
                        // EVERY rate (the anti-overlap backstop must hold at any spin). Scale-free (% of ticks).
                        if (r.CollidePct > MaxCollidePct)
                            failures.Add($"{(stick ? "stick" : "phys")} {scale:F1}x {rate:F0}deg/s: feet overlap {r.CollidePct:F1}% of ticks");
                        // "scissored relative to the facing" only matters at a turn slow enough to TRACK, and
                        // "slow enough" is DIMENSIONLESS (w*sqrt(L/g)) -- a fixed deg/s is a faster dimensionless
                        // spin for a giant (longer pendulum), so gating on raw deg/s is scale-wrong. Past ~2.0
                        // the facing outruns any single-support footwork, so scissor-without-overlap is fine.
                        float dimlessYaw = rate * Mathf.Deg2Rad * Mathf.Sqrt(0.87f * scale / 9.81f);
                        if (dimlessYaw <= 2.0f && r.CrossMaxCm > MaxCrossMaxCm * scale)
                            failures.Add($"{(stick ? "stick" : "phys")} {scale:F1}x {rate:F0}deg/s: feet scissor {r.CrossMaxCm:F1}cm to the wrong side (dimless {dimlessYaw:F2})");
                        // single support: a walking stepper must never lift both feet at once -- gated everywhere.
                        if (r.BothSteppingTicks > 0)
                            failures.Add($"{(stick ? "stick" : "phys")} {scale:F1}x {rate:F0}deg/s: both feet stepping ({r.BothSteppingTicks} ticks)");
                    }
                }
            }
            Debug.Log(report.ToString());
            if (failures.Count > 0) Assert.Fail("Fast-rotation crossover:\n  " + string.Join("\n  ", failures) + "\n\n" + report);
        }
        [Test]
        public void PlantedFoot_HoldsItsRotation_WhileTheBodyTurns()
        {
            var failures = new List<string>();
            foreach (float scale in Scales)
            {
                BasisFootSimParams p = BasisMocapFootQuality.BuildReferenceParams(scale);
                foreach (bool stick in new[] { false, true })
                {
                    BasisFootRotationResult r = BasisMocapFootQuality.RunSpin(p, 90f, stick);
                    Debug.Log($"[planted-hold {scale:F1}x {(stick ? "stick" : "phys")}] yaw-follow {r.PlantedYawFollowFrac:F2}");
                    if (r.PlantedYawFollowFrac > 0.5f)
                        failures.Add($"{(stick ? "stick" : "phys")} {scale:F1}x: planted foot follows the body {r.PlantedYawFollowFrac:F2} " +"(it pivots with the turn instead of holding in the world)");
                }
            }
            if (failures.Count > 0) Assert.Fail(string.Join("\n", failures));
        }
        [Test]
        public void Rotation_IsScaleInvariant()
        {
            var failures = new List<string>();
            BasisFootSimParams chibi = BasisMocapFootQuality.BuildReferenceParams(0.6f);
            BasisFootSimParams giant = BasisMocapFootQuality.BuildReferenceParams(1.8f);
            foreach (bool stick in new[] { false, true })
            {
                foreach (float rate in new[] { 120f, 240f, 360f, 600f })
                {
                    // Gaits are dynamically similar at equal DIMENSIONLESS yaw rate w*sqrt(L/g), NOT equal
                    // absolute deg/s -- a fixed absolute spin is a FASTER dimensionless spin for a giant (longer
                    // pendulum), so it legitimately handles it differently. Scale the rate by 1/sqrt(size) so
                    // both spin at the same dimensionless rate, then the dimensionless metrics must match.
                    BasisFootRotationResult c = BasisMocapFootQuality.RunSpin(chibi, rate / Mathf.Sqrt(0.6f), stick);
                    BasisFootRotationResult g = BasisMocapFootQuality.RunSpin(giant, rate / Mathf.Sqrt(1.8f), stick);
                    if (Mathf.Abs(c.CollidePct - g.CollidePct) > 5f)
                        failures.Add($"{(stick ? "stick" : "phys")} {rate:F0}deg/s: collide% chibi {c.CollidePct:F1} vs giant {g.CollidePct:F1} -- not scale-invariant");
                    float cGapL = c.MinGapCm / (0.6f * 87f), gGapL = g.MinGapCm / (1.8f * 87f);   // minGap / leg
                    if (Mathf.Abs(cGapL - gGapL) > 0.04f)
                        failures.Add($"{(stick ? "stick" : "phys")} {rate:F0}deg/s: minGap/L chibi {cGapL:F3} vs giant {gGapL:F3} -- not scale-invariant");
                }
            }
            if (failures.Count > 0) Assert.Fail(string.Join("\n", failures));
        }
        [Test]
        public void Rotation_IsFramerateIndependent()
        {
            BasisFootSimParams p = BasisMocapFootQuality.BuildReferenceParams(1.0f);
            var failures = new List<string>();
            foreach (bool stick in new[] { false, true })
            {
                foreach (float rate in new[] { 120f, 240f })
                {
                    BasisFootRotationResult r30 = BasisMocapFootQuality.RunSpin(p, rate, stick, dt: 1f / 30f);
                    BasisFootRotationResult r72 = BasisMocapFootQuality.RunSpin(p, rate, stick, dt: 1f / 72f);
                    BasisFootRotationResult r144 = BasisMocapFootQuality.RunSpin(p, rate, stick, dt: 1f / 144f);
                    foreach (var (label, r) in new[] { ("30fps", r30), ("72fps", r72), ("144fps", r144) })
                        if (r.CollidePct > MaxCollidePct)
                            failures.Add($"{(stick ? "stick" : "phys")} {rate:F0}deg/s @ {label}: feet overlap {r.CollidePct:F1}%");
                    float spread = Mathf.Max(r30.CrossMaxCm, r72.CrossMaxCm, r144.CrossMaxCm) - Mathf.Min(r30.CrossMaxCm, r72.CrossMaxCm, r144.CrossMaxCm);
                    if (spread > 6f)
                        failures.Add($"{(stick ? "stick" : "phys")} {rate:F0}deg/s: crossMax spans {spread:F1}cm across 30/72/144fps -- framerate-dependent");
                }
            }
            if (failures.Count > 0) Assert.Fail(string.Join("\n", failures));
        }
    }
}
