using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisArmHitchSlewTests
    {
        const float k_ArmLen = 0.54f, dragHz = 1.25f, handSpeedDegPerSec = 90f;
        const int hitchAt = 40, frames = 140;
        static readonly Vector3 rightShoulder = new Vector3(0.17f, 1.40f, 0f);
        static readonly Vector3 leftShoulder = new Vector3(-0.17f, 1.40f, 0f);
        static BasisSwivelFrame Frame() => BasisSwivelHintCore.BuildFrame(leftShoulder, rightShoulder, new Vector3(0f, 1.25f, 0f), new Vector3(0f, 1.50f, 0f));
        static Vector3 Dir(float azDeg, float elDeg)
        {
            float az = azDeg * Mathf.Deg2Rad, el = elDeg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(az) * Mathf.Cos(el), Mathf.Sin(el), Mathf.Cos(az) * Mathf.Cos(el));
        }
        static void RunHand(float[] dts, bool ceiling, out float worstHitchJumpDeg, out float worstSteadyStepDeg)
        {
            BasisSwivelFrame frame = Frame();
            Quaternion bodyRot = Quaternion.LookRotation(frame.Forward, frame.Up);
            bool seeded = false;
            Vector3 prevBend = default, prevAxis = default, prevDrag = default;
            float prevReach = 0f;
            worstHitchJumpDeg = 0f;
            worstSteadyStepDeg = 0f;

            float az = 100f;
            for (int i = 0; i < dts.Length; i++)
            {
                float dt = dts[i];
                az += handSpeedDegPerSec * dt;
                Vector3 hand = rightShoulder + Dir(az, -20f) * (0.65f * k_ArmLen);

                if (!BasisSwivelHintCore.ArmHint(frame, rightShoulder, hand, k_ArmLen, false, out Vector3 modelHint, out float cond))
                {
                    continue;
                }

                Vector3 curAxisV = hand - rightShoulder, rawBendV = modelHint - rightShoulder;
                float axLen = curAxisV.magnitude, rbLen = rawBendV.magnitude;
                if (!(axLen > 1e-5f) || !(rbLen > 1e-5f)) continue;

                Vector3 curAxis = curAxisV / axLen, rawBend = rawBendV / rbLen;
                float curReach = axLen / k_ArmLen;
                float armDt = ceiling ? Mathf.Min(dt, BasisElbowSwingCapCore.MaxSlewBudgetDt) : dt;
                float slew = ceiling ? BasisElbowSwingCapCore.SlewCapRad(dt) : 0f;
                Vector3 capped = seeded ? (Vector3)BasisElbowSwingCapCore.Apply(prevBend, prevAxis, curAxis, rawBend, BasisElbowSwingCapCore.MaxGain, curReach - prevReach, cond, slew) : rawBend;
                Vector3 dragged = capped;
                if (seeded)
                {
                    dragged = (Vector3)BasisElbowDragCore.Apply(prevDrag, Quaternion.identity, curAxis, capped, BasisElbowDragCore.Alpha(dragHz, armDt));

                    float step = Vector3.Angle(prevDrag, dragged);
                    if (i == hitchAt) worstHitchJumpDeg = Mathf.Max(worstHitchJumpDeg, step);
                    else worstSteadyStepDeg = Mathf.Max(worstSteadyStepDeg, step);
                }

                prevBend = capped; prevAxis = curAxis; prevReach = curReach;
                prevDrag = dragged; seeded = true;
            }
        }
        static float[] Timeline(float hitchMs)
        {
            var dts = new float[frames];
            for (int i = 0; i < frames; i++) dts[i] = 1f / 90f;
            if (hitchMs > 0f) dts[hitchAt] = hitchMs / 1000f;
            return dts;
        }
        static float[] SteadyTimeline(float fps)
        {
            var dts = new float[frames];
            for (int i = 0; i < frames; i++) dts[i] = 1f / fps;
            return dts;
        }
        [Test]
        public void AStalledFrame_CannotFlickTheElbow([Values(50f, 100f, 200f, 333f)] float hitchMs)
        {
            RunHand(Timeline(hitchMs), true, out float jump, out _);

            float allowed = BasisElbowSwingCapCore.MaxSlewDegPerSec * BasisElbowSwingCapCore.MaxSlewBudgetDt * 1.25f;
            Assert.Less(jump, allowed, $"a {hitchMs:F0} ms stall moved the elbow pole {jump:F1} degrees in ONE frame (ceiling " + $"{allowed:F1}). The gain cap is relative to the hand, so a long frame buys a " +"proportionally huge elbow budget -- that is the arms-flying-around flick.");
        }
        [Test]
        public void WithoutTheCeiling_AStallFlicksTheElbow()
        {
            RunHand(Timeline(333f), false, out float jump, out _);
            Assert.Greater(jump, 90f, $"the un-ceilinged chain only moved the elbow {jump:F1} degrees on a 333 ms stall -- it " + "measured 122 degrees when this gate was written. The defect this test pairs with is gone " +"or the trajectory no longer crosses the field core.");
        }
        [Test]
        public void TheCeiling_IsInert_AtOrdinaryFramerates([Values(90f, 72f, 45f, 30f)] float fps)
        {
            RunHand(SteadyTimeline(fps), true, out _, out float withCeiling);
            RunHand(SteadyTimeline(fps), false, out _, out float without);

            Assert.AreEqual(without, withCeiling, 1e-4f, $"at {fps:F0} fps the slew ceiling changed the elbow's motion ({withCeiling:F3} vs " + $"{without:F3} deg worst step). It is a stutter guard and must be inert wherever the gain " +"cap already binds.");
        }
        [Test]
        public void TheCeiling_KeepsTheBendUnitAndPerpendicular()
        {
            var rng = new System.Random(7788);
            for (int t = 0; t < 4000; t++)
            {
                Vector3 curAxis = new Vector3((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1)).normalized;
                Vector3 prevAxis = (curAxis + 0.3f * new Vector3((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1))).normalized;

                if (!BasisSwivelHintCore.ArmHint(Frame(), rightShoulder, rightShoulder + curAxis * (0.7f * k_ArmLen), k_ArmLen, false, out Vector3 hint, out float cond)) continue;
                Vector3 raw = (hint - rightShoulder).normalized, prev = Vector3.Cross(prevAxis, raw).normalized;
                if (prev.sqrMagnitude < 0.5f) continue;

                Vector3 b = BasisElbowSwingCapCore.Apply(prev, prevAxis, curAxis, raw, BasisElbowSwingCapCore.MaxGain, 0.01f, cond, BasisElbowSwingCapCore.SlewCapRad(1f / 90f));

                Assert.IsTrue(float.IsFinite(b.x) && float.IsFinite(b.y) && float.IsFinite(b.z), $"ceiling went non-finite at axis {curAxis}");
                Assert.AreEqual(1f, b.magnitude, 3e-3f, "ceilinged bend must be unit");
                Assert.AreEqual(0f, Vector3.Dot(curAxis, b), 3e-3f, "ceilinged bend must stay perpendicular to the arm");
            }
        }
    }
}
