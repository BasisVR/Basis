using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisHipFrameSpringTests
    {
        [Test]
        public void Spring_ConvergesToStaticTarget()
        {
            Quaternion target = Quaternion.AngleAxis(40f, Vector3.up) * Quaternion.AngleAxis(15f, Vector3.right);
            Quaternion rot = Quaternion.identity;
            Vector3 vel = Vector3.zero;
            float dt = 1f / 90f;
            for (int i = 0; i < 600; i++)
                BasisHipFrameSpringCore.Step(rot, vel, target, dt, 8f, 1f, out rot, out vel);

            Assert.That(Quaternion.Angle(rot, target), Is.LessThan(0.5f),"the spring must settle on a static target (no steady-state error).");
        }
        [Test]
        public void Spring_IsStable_AcrossHzAndFps()
        {
            // Implicit Euler is unconditionally stable: it must never diverge across the Hz/fps grid (incl. low
            // fps where explicit Euler blows up), and well within the sim time it must reach the static target.
            float[] hzs = { 2f, 8f, 12f, 30f, 60f };
            float[] dts = { 1f / 120f, 1f / 90f, 1f / 30f, 1f / 10f };
            Quaternion target = Quaternion.AngleAxis(70f, new Vector3(0.2f, 1f, 0.1f).normalized);

            foreach (float hz in hzs)
            foreach (float dt in dts)
            {
                Quaternion rot = Quaternion.identity;
                Vector3 vel = Vector3.zero;
                int frames = Mathf.CeilToInt(40f / dt); // ~40 sim-seconds regardless of fps
                for (int i = 0; i < frames; i++)
                {
                    BasisHipFrameSpringCore.Step(rot, vel, target, dt, hz, 1f, out rot, out vel);
                    Assert.IsTrue(IsFinite(rot) && IsFinite(vel), $"diverged at hz={hz}, dt={dt:0.0000}, frame {i}.");
                }
                Assert.That(Quaternion.Angle(rot, target), Is.LessThan(2f), $"did not converge at hz={hz}, dt={dt:0.0000} (angle {Quaternion.Angle(rot, target):0.0}).");
            }
        }
        [Test]
        public void Spring_LagsStep_WithoutOvershoot()
        {
            Quaternion target = Quaternion.AngleAxis(50f, Vector3.up), rot = Quaternion.identity;
            Vector3 vel = Vector3.zero;
            float dt = 1f / 90f;

            // First frame: it must MOVE toward the target but not jump there -- a bounded lag.
            BasisHipFrameSpringCore.Step(rot, vel, target, dt, 8f, 1f, out rot, out vel);
            float afterOne = Quaternion.Angle(rot, target);
            Assert.That(afterOne, Is.LessThan(50f), "the spring must move toward the target.");
            Assert.That(afterOne, Is.GreaterThan(5f), "the spring must LAG (not snap to the target in one frame).");

            // Critically damped: the gap to the target only ever shrinks (monotone approach, no overshoot), and
            // the smoothed rotation never rotates PAST the target.
            float prevGap = afterOne;
            for (int i = 0; i < 400; i++)
            {
                BasisHipFrameSpringCore.Step(rot, vel, target, dt, 8f, 1f, out rot, out vel);
                float gap = Quaternion.Angle(rot, target);
                Assert.That(gap, Is.LessThanOrEqualTo(prevGap + 0.01f), $"overshoot at frame {i} (gap {gap:0.00} > prev {prevGap:0.00}).");
                Assert.That(Quaternion.Angle(rot, Quaternion.identity), Is.LessThanOrEqualTo(50f + 0.5f), $"rotated past the target (overshoot) at frame {i}.");
                prevGap = gap;
            }
        }
        [Test]
        public void Spring_RejectsHighFrequencyMoreThanLowFrequency()
        {
            // Drive the target as a pure yaw oscillation at the same amplitude but two frequencies. An 8 Hz
            // critically-damped spring passes the slow sway and strongly attenuates the fast jitter -- exactly
            // "hip movements don't affect the connecting bones as much". Peak output amplitude proves it.
            const float amplitude = 20f;
            float peakLow = PeakResponseDeg(1f, amplitude), peakHigh = PeakResponseDeg(25f, amplitude);

            Assert.That(peakLow, Is.GreaterThan(0.6f * amplitude), $"the slow sway should pass largely intact (peak {peakLow:0.0} of {amplitude}).");
            Assert.That(peakHigh, Is.LessThan(0.6f * peakLow), $"fast jitter must be attenuated far more than slow sway (high {peakHigh:0.0} vs low {peakLow:0.0}).");
        }
        // Steady-state peak |output yaw| (deg) for a sinusoidal yaw target of the given frequency/amplitude,
        // through the 8 Hz critically-damped spring. Measured over the back half (steady state).
        static float PeakResponseDeg(float freqHz, float amplitudeDeg)
        {
            float dt = 1f / 120f;
            int frames = Mathf.CeilToInt(4f / dt); // 4 seconds
            Quaternion rot = Quaternion.identity;
            Vector3 vel = Vector3.zero;
            float peak = 0f;
            for (int i = 0; i < frames; i++)
            {
                float t = i * dt;
                Quaternion target = Quaternion.AngleAxis(amplitudeDeg * Mathf.Sin(2f * Mathf.PI * freqHz * t), Vector3.up);
                BasisHipFrameSpringCore.Step(rot, vel, target, dt, 8f, 1f, out rot, out vel);
                if (i > frames / 2)
                    peak = Mathf.Max(peak, Quaternion.Angle(Quaternion.identity, rot));
            }
            return peak;
        }
        static bool IsFinite(Quaternion q) => !float.IsNaN(q.x) && !float.IsInfinity(q.x) && !float.IsNaN(q.y) && !float.IsInfinity(q.y) && !float.IsNaN(q.z) && !float.IsInfinity(q.z) && !float.IsNaN(q.w) && !float.IsInfinity(q.w);
        static bool IsFinite(Vector3 v) => !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
    }
}
