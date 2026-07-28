using System;
using NUnit.Framework;
using Unity.Mathematics;
using Basis.Network.Core.Compression;

namespace Basis.Tests.Sync
{
    /// <summary>
    /// Locks the remote-avatar playback interpolation (BasisRemoteInterpolationCore):
    /// the Catmull-Rom spline that replaced linear-nlerp + the heavy per-bone 1€ filter.
    ///
    /// Guards the properties the fix depends on:
    ///   - the spline passes through the network snapshots (no bias),
    ///   - it is C1 at snapshot boundaries (kills the velocity corner = the wobble source),
    ///   - it survives the codec's sign canonicalization (no long-way whole-body flip),
    ///   - duplicated endpoints (startup / packet-loss underrun) stay bounded and finite,
    ///   - and, through the REAL bone codec, cubic tracks a moving joint markedly better
    ///     than the old linear path.
    /// </summary>
    public class BasisRemoteInterpolationTests
    {
        const float InvSqrt2 = 0.70710678118f;

        static quaternion AxisAngle(float3 axis, float deg) => quaternion.AxisAngle(math.normalize(axis), math.radians(deg));
        static float AngleDeg(quaternion a, quaternion b)
        {
            float d = math.abs(math.dot(math.normalize(a).value, math.normalize(b).value));
            return math.degrees(2f * math.acos(math.min(1f, d)));
        }

        static uint _rng;
        static float NextSigned() { _rng = _rng * 1664525u + 1013904223u; return (_rng >> 8) / (float)(1 << 24) * 2f - 1f; }
        static quaternion RndQ() => math.normalize(new quaternion(NextSigned(), NextSigned(), NextSigned(), NextSigned()));

        // ── The spline interpolates the control points (t=0 -> p1, t=1 -> p2) ──

        [Test]
        public void Position_PassesThroughEndpoints()
        {
            float3 p0 = new(-1, 0, 0), p1 = new(0, 1, 0), p2 = new(2, 1, 3), p3 = new(3, -1, 2);
            Assert.That(math.length(BasisRemoteInterpolationCore.Position(p0, p1, p2, p3, 0f) - p1), Is.LessThan(1e-5f));
            Assert.That(math.length(BasisRemoteInterpolationCore.Position(p0, p1, p2, p3, 1f) - p2), Is.LessThan(1e-5f));
        }

        [Test]
        public void Rotation_PassesThroughEndpoints()
        {
            _rng = 7u;
            for (int i = 0; i < 200; i++)
            {
                quaternion p0 = RndQ(), p1 = RndQ(), p2 = RndQ(), p3 = RndQ();
                // 0.05° tolerates float acos-near-1 noise; Catmull-Rom returns the endpoints exactly pre-normalize.
                Assert.That(AngleDeg(BasisRemoteInterpolationCore.Rotation(p0, p1, p2, p3, 0f), p1), Is.LessThan(0.05f));
                Assert.That(AngleDeg(BasisRemoteInterpolationCore.Rotation(p0, p1, p2, p3, 1f), p2), Is.LessThan(0.05f));
            }
        }

        // ── C1 continuity across a snapshot boundary: the whole point of the change ──

        [Test]
        public void Position_IsC1_AcrossBoundary()
        {
            // A curving trajectory sampled at 5 control points; the shared knot is p2.
            float3 P(int k) => new(k, math.sin(k * 0.7f), math.cos(k * 0.5f) * 0.5f);
            float3 p0 = P(0), p1 = P(1), p2 = P(2), p3 = P(3), p4 = P(4);
            const float h = 1e-3f;
            // velocity at the END of segment (p0..p3) and START of segment (p1..p4) — must match (C1).
            float3 vEnd = (BasisRemoteInterpolationCore.Position(p0, p1, p2, p3, 1f)
                         - BasisRemoteInterpolationCore.Position(p0, p1, p2, p3, 1f - h)) / h;
            float3 vStart = (BasisRemoteInterpolationCore.Position(p1, p2, p3, p4, h)
                           - BasisRemoteInterpolationCore.Position(p1, p2, p3, p4, 0f)) / h;
            Assert.That(math.length(vEnd - vStart), Is.LessThan(1e-2f), "position velocity is discontinuous at the knot");
        }

        [Test]
        public void Rotation_IsApproximatelyC1_AcrossBoundary()
        {
            quaternion Q(int k) => AxisAngle(new float3(0, 1, 0.2f), 15f * k) ; // steady turn, 15°/snapshot
            quaternion p0 = Q(0), p1 = Q(1), p2 = Q(2), p3 = Q(3), p4 = Q(4);
            const float h = 1e-3f;
            float wEnd = AngleDeg(BasisRemoteInterpolationCore.Rotation(p0, p1, p2, p3, 1f - h),
                                  BasisRemoteInterpolationCore.Rotation(p0, p1, p2, p3, 1f)) / h;
            float wStart = AngleDeg(BasisRemoteInterpolationCore.Rotation(p1, p2, p3, p4, 0f),
                                    BasisRemoteInterpolationCore.Rotation(p1, p2, p3, p4, h)) / h;
            // Component-CR + renormalize is only APPROXIMATELY C1; a linear-nlerp corner here would be a
            // large angular-velocity jump. Require the two one-sided speeds to agree within 8%.
            Assert.That(math.abs(wEnd - wStart) / math.max(wEnd, 1e-3f), Is.LessThan(0.08f));
        }

        // ── Immunity to the codec's sign canonicalization (the whole-body-flip bug) ──

        [Test]
        public void Rotation_SignFlippedNeighbours_TakeShortWay()
        {
            // p1->p2 is a small step, but the codec may hand us -p2 / -p0 / -p3 (largest component
            // forced positive). The old linear nlerp without a dot-check flipped the whole body 180°.
            quaternion p1 = AxisAngle(new float3(0, 1, 0), -80f);
            quaternion p2 = AxisAngle(new float3(0, 1, 0), -100f);
            quaternion truthMid = AxisAngle(new float3(0, 1, 0), -90f);

            quaternion nP0 = new quaternion(-p1.value);   // arbitrary sign-flips on the neighbours
            quaternion nP2 = new quaternion(-p2.value);
            quaternion nP3 = new quaternion(-p2.value);
            quaternion mid = BasisRemoteInterpolationCore.Rotation(nP0, p1, nP2, nP3, 0.5f);
            Assert.That(AngleDeg(mid, truthMid), Is.LessThan(1.0f), "sign-flipped neighbours must not blow up the blend");
        }

        // ── Fallbacks: duplicated endpoints (cold start / underrun) stay finite and bounded ──

        [Test]
        public void DuplicatedEndpoints_StayBoundedAndFinite()
        {
            quaternion p1 = AxisAngle(new float3(1, 0, 0), 20f);
            quaternion p2 = AxisAngle(new float3(1, 0, 0), 50f);
            for (float t = 0; t <= 1f; t += 0.05f)
            {
                // p0 == p1 and p3 == p2 (the receiver's duplicate-endpoint fallback)
                quaternion q = BasisRemoteInterpolationCore.Rotation(p1, p1, p2, p2, t);
                Assert.That(math.all(math.isfinite(q.value)), Is.True);
                Assert.That(math.abs(math.length(q.value) - 1f), Is.LessThan(1e-3f), "not unit length");
                // stays within the p1..p2 arc (no overshoot when endpoints are clamped)
                Assert.That(AngleDeg(q, p1) + AngleDeg(q, p2), Is.LessThan(AngleDeg(p1, p2) + 1.0f));
            }

            float3 a = new(0, 1, 0), b = new(0, 2, 1);
            float3 mid = BasisRemoteInterpolationCore.Position(a, a, b, b, 0.5f);
            Assert.That(math.length(mid - (a + b) * 0.5f), Is.LessThan(1e-5f), "clamped-endpoint midpoint should be the mean");
        }

        // ── The actual win, through the REAL bone codec: cubic tracks motion better than linear ──

        [Test]
        public void Cubic_BeatsLinear_TrackingAQuantizedMovingJoint()
        {
            const int bpc = 10;                 // HIGH-quality body joint
            const float maxRange = InvSqrt2;
            const double sendHz = 20.0, renderHz = 90.0, dur = 6.0, warmup = 1.0;
            const double freq = 1.2;            // joint oscillation Hz (brisk arm motion)
            const float amp = 40f;              // degrees

            quaternion Truth(double t) => AxisAngle(new float3(1, 0, 0), amp * (float)Math.Sin(2 * Math.PI * freq * t));

            quaternion Quantized(double t)
            {
                quaternion q = Truth(t);
                ulong packed = BasisBoneRotationCompression.EncodeSmallestThree(q.value.x, q.value.y, q.value.z, q.value.w, bpc, maxRange);
                BasisBoneRotationCompression.DecodeSmallestThree(packed, bpc, out float x, out float y, out float z, out float w, maxRange);
                return math.normalize(new quaternion(x, y, z, w));
            }

            double dtSend = 1.0 / sendHz, dtRender = 1.0 / renderHz;
            double linSum = 0, cubSum = 0; int n = 0;

            for (double t = warmup; t < dur; t += dtRender)
            {
                int k = (int)Math.Floor(t / dtSend);
                double localT = (t - k * dtSend) / dtSend;
                quaternion p0 = Quantized((k - 1) * dtSend);
                quaternion p1 = Quantized(k * dtSend);
                quaternion p2 = Quantized((k + 1) * dtSend);
                quaternion p3 = Quantized((k + 2) * dtSend);

                quaternion truth = Truth(t);
                quaternion lin = BasisRemoteInterpolationCore.NlerpShortest(p1, p2, (float)localT);
                quaternion cub = BasisRemoteInterpolationCore.Rotation(p0, p1, p2, p3, (float)localT);

                linSum += AngleDeg(lin, truth);
                cubSum += AngleDeg(cub, truth);
                n++;
            }

            double linErr = linSum / n, cubErr = cubSum / n;
            // Cubic should cut the tracking error clearly (harness showed ~2x); require at least 25% better.
            Assert.That(cubErr, Is.LessThan(linErr * 0.75),
                $"cubic {cubErr:F3}deg should beat linear {linErr:F3}deg by >25%");
        }

        // ── Bone-quantization shimmer low-pass ──

        [Test]
        public void OnePoleAlpha_MonotonicInCutoff_AndBounded()
        {
            float dt = 1f / 90f;
            float aLow = BasisRemoteInterpolationCore.OnePoleAlpha(5f, dt);
            float aMid = BasisRemoteInterpolationCore.OnePoleAlpha(15f, dt);
            float aHigh = BasisRemoteInterpolationCore.OnePoleAlpha(90f, dt);
            Assert.That(aLow, Is.GreaterThan(0f).And.LessThan(1f));
            Assert.That(aHigh, Is.GreaterThan(aMid).And.LessThan(1f), "higher cutoff = less smoothing = larger alpha");
            Assert.That(aMid, Is.GreaterThan(aLow));
            // ~15 Hz at 90 fps is roughly a half-blend (a light, ~1-frame smoother).
            Assert.That(aMid, Is.EqualTo(0.51f).Within(0.06f));
        }

        [Test]
        public void AdaptiveCutoff_MinimumWhenStill_OpensWithMotion()
        {
            quaternion a = AxisAngle(new float3(1, 0, 0), 10f);
            float still = BasisRemoteInterpolationCore.AdaptiveCutoff(a, a, 1.5f, 250f);
            Assert.That(still, Is.EqualTo(1.5f).Within(1e-3f), "identical snapshots => cutoff = min (heavy smoothing)");

            quaternion small = AxisAngle(new float3(1, 0, 0), 13f);   // 3° of motion this window
            float moving = BasisRemoteInterpolationCore.AdaptiveCutoff(a, small, 1.5f, 250f);
            Assert.That(moving, Is.GreaterThan(still + 5f), "real motion must open the cutoff (no lag/wobble)");

            quaternion big = AxisAngle(new float3(1, 0, 0), 40f);     // faster
            Assert.That(BasisRemoteInterpolationCore.AdaptiveCutoff(a, big, 1.5f, 250f), Is.GreaterThan(moving),
                "cutoff is monotonic in this window's motion");
        }

        [Test]
        public void LowPass_ConvergesToRaw_WhenTargetHeld()
        {
            float alpha = BasisRemoteInterpolationCore.OnePoleAlpha(15f, 1f / 90f);
            quaternion target = AxisAngle(new float3(0, 1, 0), 40f);
            quaternion f = AxisAngle(new float3(0, 1, 0), -10f);
            for (int i = 0; i < 200; i++) f = BasisRemoteInterpolationCore.LowPassStep(f, target, alpha);
            Assert.That(AngleDeg(f, target), Is.LessThan(0.05f), "a held target must be reached (no steady-state bias)");
        }

        [Test]
        public void LowPass_ReducesQuantizationShimmer_ButPassesSlowMotion()
        {
            const int bpc = 10; const float maxRange = InvSqrt2;
            float alpha = BasisRemoteInterpolationCore.OnePoleAlpha(15f, 1f / 90f);

            // Slow ramp through the quantizer: measure step-to-step angular jump (the shimmer),
            // raw vs low-passed, and the lag-aligned tracking error (must stay tiny = passband).
            quaternion prevRawQ = quaternion.identity, prevFiltQ = quaternion.identity, filt = quaternion.identity;
            double rawJump = 0, filtJump = 0; double trackErr = 0; int n = 0;
            bool seeded = false;
            for (int k = 0; k < 900; k++)
            {
                float deg = 6f * (k / 90f);                 // 6°/s ramp, 90 fps
                quaternion tru = AxisAngle(new float3(1, 0, 0), deg);
                ulong p = BasisBoneRotationCompression.EncodeSmallestThree(tru.value.x, tru.value.y, tru.value.z, tru.value.w, bpc, maxRange);
                BasisBoneRotationCompression.DecodeSmallestThree(p, bpc, out float x, out float y, out float z, out float w, maxRange);
                quaternion rawQ = math.normalize(new quaternion(x, y, z, w));

                if (!seeded) { filt = rawQ; prevRawQ = rawQ; prevFiltQ = rawQ; seeded = true; continue; }
                filt = BasisRemoteInterpolationCore.LowPassStep(filt, rawQ, alpha);

                if (k > 100)
                {
                    rawJump += AngleDeg(prevRawQ, rawQ);
                    filtJump += AngleDeg(prevFiltQ, filt);
                    trackErr += AngleDeg(filt, tru);   // low-pass tracks a ~5° lag on a 6°/s ramp -> sub-degree
                    n++;
                }
                prevRawQ = rawQ; prevFiltQ = filt;
            }
            double rawMean = rawJump / n, filtMean = filtJump / n, trackMean = trackErr / n;
            Assert.That(filtMean, Is.LessThan(rawMean * 0.7),
                $"filter should cut the step-to-step shimmer >30% (raw {rawMean:F4} -> filt {filtMean:F4} deg/frame)");
            Assert.That(trackMean, Is.LessThan(0.5),
                $"but must still track the slow ramp (mean tracking error {trackMean:F3} deg)");
        }

        // ── RingBuffer peek used to supply p3 without consuming the staged frame ──

        // ── Why the hips world pose uses LINEAR, not cubic (desktop WASD overshoot) ──

        [Test]
        public void UniformCubic_OvershootsASharpStop_LinearDoesNot()
        {
            // "walk then stop": approach a stop value then hold it (piecewise-linear, like keyboard
            // locomotion). The hold segment's start tangent (p2-p0)/2 is still forward, so uniform
            // Catmull-Rom leaves the stop going forward and comes back — a whole-body fore-aft twitch.
            float3 moving = new(0, 0, 0.07f), stop = new(0, 0, 0.14f);
            float3 cub = BasisRemoteInterpolationCore.Position(moving, stop, stop, stop, 0.5f);
            Assert.That(cub.z, Is.GreaterThan(0.141f), "uniform cubic overshoots the stop (hips would twitch fore-aft)");

            float3 lin = math.lerp(stop, stop, 0.5f);
            Assert.That(lin.z, Is.EqualTo(stop.z).Within(1e-5f), "linear never overshoots a stop");
            // and across the whole hold, linear stays put while cubic bulges past it
            for (float t = 0; t <= 1f; t += 0.1f)
                Assert.That(math.lerp(stop, stop, t).z, Is.EqualTo(stop.z).Within(1e-5f));
        }

        // ── Wire format: High bone quality raised 10→12 bits for body/limb joints (anti-shimmer) ──

        [Test]
        public void HighWireSize_Locked_AfterBitDepthBump()
        {
            // Body/limb joints (slots 0..18) at 12 bits, toes 5, fingers 6/5.
            // 19×(2+36) + 2×(2+15) + fingers = 1302 bits = 163 rotation bytes; +12 pos +22 tail +35 effector = 232.
            Assert.That(BasisBoneRotationCompression.RotationBytes(BasisAvatarBitPacking.BitQuality.High), Is.EqualTo(163),
                "High rotation-byte count changed — wire format + ServerVersion must move together");
            Assert.That(BasisAvatarBitPacking.ConvertToSize(BasisAvatarBitPacking.BitQuality.High), Is.EqualTo(232));
        }

        [Test]
        public void TwelveBits_HalvesQuantizationErrorVsTen()
        {
            _rng = 3u;
            float worst10 = 0, worst12 = 0;
            for (int i = 0; i < 30000; i++)
            {
                quaternion q = RndQ();
                worst10 = math.max(worst10, RoundTripErr(q, 10));
                worst12 = math.max(worst12, RoundTripErr(q, 12));
            }
            // Each extra bit halves the step; 12 vs 10 bits ≈ 4× finer → clearly smaller worst error.
            Assert.That(worst12, Is.LessThan(worst10 * 0.4f), $"12-bit {worst12:F4}° should be «4× under 10-bit {worst10:F4}°");
        }

        static float RoundTripErr(quaternion q, int bpc)
        {
            q = math.normalize(q);
            ulong p = BasisBoneRotationCompression.EncodeSmallestThree(q.value.x, q.value.y, q.value.z, q.value.w, bpc, InvSqrt2);
            BasisBoneRotationCompression.DecodeSmallestThree(p, bpc, out float x, out float y, out float z, out float w, InvSqrt2);
            return AngleDeg(q, math.normalize(new quaternion(x, y, z, w)));
        }

        // ── RingBuffer peek used to supply p3 without consuming the staged frame ──

        [Test]
        public void RingBuffer_PeekOldest_DoesNotConsume()
        {
            var rb = new BasisRingBuffer<int>(4);
            Assert.That(rb.TryPeekOldest(out _), Is.False, "empty peek must fail");
            rb.EnqueueOverwriteOldest(10);
            rb.EnqueueOverwriteOldest(20);
            Assert.That(rb.TryPeekOldest(out int p), Is.True);
            Assert.That(p, Is.EqualTo(10), "peek returns oldest");
            Assert.That(rb.Count, Is.EqualTo(2), "peek must not consume");
            rb.TryDequeueOldest(out int d);
            Assert.That(d, Is.EqualTo(10), "peeked element is still the next dequeue");
            Assert.That(rb.TryPeekOldest(out int p2), Is.True);
            Assert.That(p2, Is.EqualTo(20));
        }
    }
}
