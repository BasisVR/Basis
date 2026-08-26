using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisArmReachBehindTests
    {
        const float k_ArmLen = 0.54f;
        static float3 Dir(float azDeg, float elevDeg)
        {
            float az = azDeg * Mathf.Deg2Rad, el = elevDeg * Mathf.Deg2Rad;
            // az=0 FORWARD(+z), az=+90 OUTWARD(+x); elev<0 DOWN. Same convention as BasisArmBigSwingFlipTests.
            return new float3(Mathf.Sin(az) * Mathf.Cos(el), Mathf.Sin(el), Mathf.Cos(az) * Mathf.Cos(el));
        }
        static float3 Bend(float3 dir) => BasisElbowStereoModel.BendDirection(dir, out _);
        [Test]
        public void ReachingBehind_DoesNotSnapTheElbow_OnAnyHorizontalSwing([Values(-30f, -20f, -10f, 0f, 10f, 20f)] float elevDeg)
        {
            const int steps = 700;
            const float gate = 8f;
            float worst = 0f; float worstAz = 0f;
            float3 prev = Bend(Dir(0f, elevDeg));

            for (int s = 1; s <= steps; s++)
            {
                float az = Mathf.Lerp(0f, 178f, s / (float)steps);   // front -> behind
                float3 b = Bend(Dir(az, elevDeg));
                float stepDeg = Vector3.Angle(new Vector3(prev.x, prev.y, prev.z), new Vector3(b.x, b.y, b.z));
                if (stepDeg > worst) { worst = stepDeg; worstAz = az; }
                prev = b;
            }

            Assert.Less(worst, gate, $"reaching behind at elevation {elevDeg:F0} deg rotated the elbow bend {worst:F1} deg in one " + $"0.25-degree hand step near azimuth {worstAz:F0}. That is the reach-behind snap. The polynomial " + "field it replaced measured 33 deg on exactly this swing; the stereo field has no reachable zero " +"to cross.");
        }
        [Test]
        public void TheReachableWorkspace_HasNoBendCollapse_TheOneZeroIsInTheTorso()
        {
            const float floor = 0.25f;   // measured min over reachable ~0.55; the collapse at the zero is ~0
            int reachableSamples = 0;
            float worstReachable = 1f; float3 worstAt = default;
            bool sawCollapse = false; float3 collapseAt = default;

            for (int azI = -180; azI < 180; azI += 2)
            {
                for (int elI = -88; elI <= 88; elI += 2)
                {
                    float3 dir = Dir(azI, elI);
                    BasisElbowStereoModel.BendDirection(dir, out float cond);

                    // "reachable-ish": exclude only the deep across-body cone (hand through the torso).
                    bool acrossTorso = dir.x < -0.5f;
                    if (cond < 0.10f) { sawCollapse = true; collapseAt = dir; }

                    if (!acrossTorso)
                    {
                        reachableSamples++;
                        if (cond < worstReachable) { worstReachable = cond; worstAt = dir; }
                    }
                }
            }

            Assert.Greater(reachableSamples, 1000, "sanity: the sweep must actually cover reachable space");
            Assert.Greater(worstReachable, floor, $"the elbow bend collapsed to lever {worstReachable:F3} at a REACHABLE direction {worstAt} " + "(not across the body). A collapse in reach is a snap waiting to happen -- the stereo field's " +"single zero is supposed to sit only inside the torso.");
            Assert.IsTrue(sawCollapse, $"the field must still HAVE its one zero (it is topologically required); it was expected deep " +"across the body. Not finding it means the construction changed and the guarantee is void.");
            Assert.Less(collapseAt.x, -0.5f, $"the zero was found at {collapseAt}, not across the body. It must stay in the torso to be " +"unreachable.");
        }
        [Test]
        public void TheBend_IsAlwaysUnit_AndPerpendicular_EvenBeyondReachAndAtDegeneracies()
        {
            var rng = new System.Random(20260718);
            for (int i = 0; i < 20000; i++)
            {
                float3 tip = new float3((float)(rng.NextDouble() * 6.0 - 3.0), (float)(rng.NextDouble() * 6.0 - 3.0), (float)(rng.NextDouble() * 6.0 - 3.0));
                if (math.length(tip) < 1e-4f) continue;
                float3 b = BasisElbowStereoModel.BendDirection(tip, out float cond);
                Assert.IsTrue(math.all(math.isfinite(b)), $"bend not finite at {tip}");
                Assert.IsTrue(math.isfinite(cond), $"conditioning not finite at {tip}");
                Assert.AreEqual(1f, math.length(b), 3e-3f, $"bend must be UNIT at {tip}");
                Assert.AreEqual(0f, math.dot(math.normalize(tip), b), 3e-3f, $"bend must be PERPENDICULAR to shoulder->hand at {tip}");
            }

            foreach (float3 nasty in new[]
            {
                float3.zero, new float3(0f, -1f, 0f), new float3(0f, 1f, 0f),
                new float3(-0.97339949f, 0.20492621f, -0.10246310f),   // exactly on the zero
                new float3(1e-20f, 0f, 0f), new float3(-1f, 0f, 0f),
            })
            {
                float3 b = BasisElbowStereoModel.BendDirection(nasty, out _);
                Assert.IsTrue(math.all(math.isfinite(b)), $"bend went non-finite at {nasty}");
                Assert.AreEqual(1f, math.length(b), 3e-3f, $"bend must stay unit even at {nasty}");
            }
        }
        [Test]
        public void BigUpDownSwings_StaySmooth([Values(-45f, 0f, 45f, 90f, 135f)] float azDeg)
        {
            const int steps = 640;
            float worst = 0f;
            float3 prev = Dir(azDeg, 84f), prevBend = Bend(prev);
            for (int s = 1; s <= steps; s++)
            {
                float el = Mathf.Lerp(84f, -84f, s / (float)steps);
                float3 b = Bend(Dir(azDeg, el));
                worst = Mathf.Max(worst, Vector3.Angle(new Vector3(prevBend.x, prevBend.y, prevBend.z), new Vector3(b.x, b.y, b.z)));
                prevBend = b;
            }
            Assert.Less(worst, 8f, $"an up-down swing at azimuth {azDeg:F0} snapped {worst:F1} deg/step");
        }
        [Test]
        public void LiveArmHint_MirrorsLeftToRight()
        {
            BasisSwivelFrame frame = BasisSwivelHintCore.BuildFrame(new Vector3(-0.17f, 1.40f, 0f), new Vector3(0.17f, 1.40f, 0f), new Vector3(0f, 1.25f, 0f), new Vector3(0f, 1.50f, 0f));

            var rng = new System.Random(7);
            Vector3 rSh = new Vector3(0.17f, 1.40f, 0f), lSh = new Vector3(-0.17f, 1.40f, 0f);
            for (int i = 0; i < 3000; i++)
            {
                Vector3 off = new Vector3((float)(rng.NextDouble() * 1.1 - 0.55), (float)(rng.NextDouble() * 1.1 - 0.55), (float)(rng.NextDouble() * 1.1 - 0.55));
                if (off.sqrMagnitude < 0.03f) continue;
                Vector3 mirrored = new Vector3(-off.x, off.y, off.z);

                Assert.IsTrue(BasisSwivelHintCore.ArmHint(frame, rSh, rSh + off, k_ArmLen, false, out Vector3 hintR, out float condR));
                Assert.IsTrue(BasisSwivelHintCore.ArmHint(frame, lSh, lSh + mirrored, k_ArmLen, true, out Vector3 hintL, out float condL));
                Vector3 poleR = hintR - rSh, poleL = hintL - lSh;
                Assert.AreEqual(-poleL.x, poleR.x, 2e-3f, "elbows' OUTWARD offset must mirror");
                Assert.AreEqual(poleL.y, poleR.y, 2e-3f, "elbows' height must match");
                Assert.AreEqual(poleL.z, poleR.z, 2e-3f, "elbows' forward offset must match");
                Assert.AreEqual(condL, condR, 2e-3f, "conditioning must match across the mirror");
            }
        }
    }
}
