using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisElbowSwingCapTests
    {
        const float k_MaxGain = BasisElbowSwingCapCore.MaxGain;
        static float3 Dir(float azDeg, float elDeg)
        {
            float az = azDeg * Mathf.Deg2Rad, el = elDeg * Mathf.Deg2Rad;
            return new float3(Mathf.Sin(az) * Mathf.Cos(el), Mathf.Sin(el), Mathf.Cos(az) * Mathf.Cos(el));
        }
        static float3 FieldBend(float3 dir)   // the OLD field's raw bend (what ships as default), perp to dir
            => BasisElbowFieldModel.BendDirection(dir, BasisElbowFieldModel.Elbow(dir), out _);
        static float3[] Run(float3[] hand, float maxGain)
        {
            var outb = new float3[hand.Length];
            float3 prevBend = default, prevAxis = default; bool init = false;
            for (int i = 0; i < hand.Length; i++)
            {
                float3 axis = math.normalize(hand[i]), raw = FieldBend(axis);
                float3 capped = !init ? raw : BasisElbowSwingCapCore.Apply(prevBend, prevAxis, axis, raw, maxGain);
                outb[i] = capped; prevBend = capped; prevAxis = axis; init = true;
            }
            return outb;
        }
        static float Transport(float3 prev, float3 axis, out float3 tp)
        {
            tp = prev - axis * math.dot(prev, axis);
            float l = math.length(tp);
            if (l > 1e-6f) tp /= l;
            return l;
        }
        [Test]
        public void TheCap_BoundsBendRotation_ToTheHumanGain_ThroughTheCore()
        {
            // fine sweep across the down-and-back core so a sample lands on it
            int n = 500;
            var hand = new float3[n];
            for (int i = 0; i < n; i++) hand[i] = Dir(Mathf.Lerp(90f, 175f, i / (float)(n - 1)), -20f);

            float3[] capped = Run(hand, k_MaxGain);
            float worstGain = 0f;
            for (int i = 1; i < n; i++)
            {
                // atan2 angle metric, matching the core's own budget arithmetic: Vector3.Angle's acos
                // loses ~1.3% at these 0.17-degree steps (acos ulp amplification near dot=1), which is
                // wider than this assert's 1% slack and reads as a phantom cap leak.
                float3 prevAxisN = math.normalize(hand[i - 1]), axis = math.normalize(hand[i]);
                float dHand = math.degrees(math.atan2(math.length(math.cross(prevAxisN, axis)), math.dot(prevAxisN, axis)));
                if (dHand < 1e-4f) continue;
                Transport(capped[i - 1], axis, out float3 tp);
                float rot = math.degrees(math.atan2(math.length(math.cross(tp, capped[i])), math.dot(tp, capped[i])));
                worstGain = Mathf.Max(worstGain, rot / dHand);
            }
            Assert.LessOrEqual(worstGain, k_MaxGain + 0.05f, $"the capped bend rotated {worstGain:F1}x the hand through the core -- the cap ({k_MaxGain}x) " +"did not bind. That is the reach-behind snap leaking through.");
        }
        [Test]
        public void TheCap_IsExactlyTheField_OnOrdinaryReaching()
        {
            int n = 200;
            var hand = new float3[n];
            for (int i = 0; i < n; i++) hand[i] = Dir(Mathf.Lerp(-40f, 50f, i / (float)(n - 1)), 10f);  // front sweep
            float3[] capped = Run(hand, k_MaxGain);
            for (int i = 0; i < n; i++)
            {
                float3 raw = FieldBend(math.normalize(hand[i]));
                Assert.AreEqual(0f, math.distance(raw, capped[i]), 1e-5f, $"the cap changed the bend at a non-core pose (frame {i}) -- it must be a bit-exact no-op " +"where the field is slower than the cap, or it lags ordinary reaching");
            }
        }
        [Test]
        public void TheCap_IsFramerateIndependent()
        {
            float3[] Sweep(int n)
            {
                var h = new float3[n];
                for (int i = 0; i < n; i++) h[i] = Dir(Mathf.Lerp(90f, 175f, i / (float)(n - 1)), -20f);
                return h;
            }
            float3[] a = Run(Sweep(120), k_MaxGain);       // "72 fps"
            float3[] b = Run(Sweep(239), k_MaxGain);       // "144 fps" (shares every other sample)
            float worst = 0f;
            for (int i = 0; i < a.Length; i++)
                worst = Mathf.Max(worst, Vector3.Angle((Vector3)a[i], (Vector3)b[2 * i]));
            Assert.Less(worst, 1.5f, $"the same reach-behind sweep gave poses {worst:F1} deg apart at 1x vs 2x frame density -- a " +"gain cap must be framerate-independent");
        }
        [Test]
        public void TheCap_ReacquiresTheField_FromAStalePole()
        {
            float3 axisStart = math.normalize(Dir(10f, -10f)), raw0 = FieldBend(axisStart);
            float3 wrong = math.normalize(math.cross(axisStart, raw0));   // 90 deg off, perpendicular, on the circle
            float3 prevBend = wrong, prevAxis = axisStart;
            float dev = 180f;
            for (int i = 1; i <= 60; i++)
            {
                float3 axis = math.normalize(Dir(10f + i * 0.8f, -10f));   // a steady ordinary reach
                float3 raw = FieldBend(axis);
                float3 capped = BasisElbowSwingCapCore.Apply(prevBend, prevAxis, axis, raw, k_MaxGain);
                dev = Vector3.Angle((Vector3)capped, (Vector3)raw);
                prevBend = capped; prevAxis = axis;
            }
            Assert.Less(dev, 2f, $"after 60 frames of ordinary reaching the capped bend was still {dev:F1} deg " +"off the field -- it must re-acquire, not hold a stale pole");
        }
        [Test]
        public void TheCappedBend_StaysUnitAndPerpendicular()
        {
            var rng = new System.Random(1234);
            for (int t = 0; t < 5000; t++)
            {
                float3 curAxis = math.normalize(new float3((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1)));
                float3 prevAxis = math.normalize(curAxis + 0.1f * new float3((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1)));
                float3 raw = FieldBend(curAxis), pRaw = FieldBend(prevAxis);
                if (!math.all(math.isfinite(raw)) || !math.all(math.isfinite(pRaw))) continue;
                float3 b = BasisElbowSwingCapCore.Apply(pRaw, prevAxis, curAxis, raw, k_MaxGain);
                Assert.IsTrue(math.all(math.isfinite(b)), $"cap went non-finite at axis {curAxis}");
                Assert.AreEqual(1f, math.length(b), 3e-3f, "capped bend must be unit");
                Assert.AreEqual(0f, math.dot(curAxis, b), 3e-3f, "capped bend must be perpendicular to the arm");
            }
        }
    }
}
