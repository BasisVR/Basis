using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// "When we reach behind with our arms there is snapping."
    ///
    /// ================================================================================================
    /// WHAT THE SNAP WAS, AND WHY THESE TESTS SWEEP AZIMUTH.
    ///
    /// The no-tracker elbow bend is a UNIT TANGENT field on the sphere of hand directions, so by
    /// Poincare-Hopf it must carry zeros of total index 2. BasisElbowFieldModel (a global polynomial)
    /// split that into two index-1 zeros, and one of them sat in REACHABLE workspace: down-and-back,
    /// azimuth ~130 deg, ~20 deg below level -- hand out behind the hip, arm bent. Reaching behind.
    ///
    /// BasisArmBigSwingFlipTests already sweeps MERIDIANS (up-down swings) and permits a hard rotation
    /// only inside a collapsed-lever core. But the reach-behind core is crossed on a HORIZONTAL path --
    /// swinging the hand around from the front to behind you -- which no meridian sweep touches. That is
    /// the coverage gap these tests close: constant-elevation AZIMUTH sweeps, front to behind.
    ///
    /// The fix (BasisElbowStereoModel, default via BasisElbowFieldModel.UseStereoField) carries a SINGLE
    /// index-2 zero and places it straight across the body, inside the torso, where no hand can point.
    /// The entire reachable workspace is then zero-free. These tests pin both halves: the snap is gone in
    /// reach, AND the one unavoidable zero is unreachable. Measured on 199,528 real frames the reach-behind
    /// worst step fell 33 deg/frame -> 0.3, and the worst reachable elbow gain ~106x -> 7x.
    /// ================================================================================================
    /// </summary>
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

        /// <summary>
        /// ⭐⭐ THE COMPLAINT, GATED. Swing the hand from straight ahead around to behind you at a fixed
        /// height, in fine steps, and measure how far the bend rotates per step. This is the exact path
        /// the user traverses reaching behind, and it is the one BasisArmBigSwingFlipTests' meridians miss.
        ///
        /// On BasisElbowFieldModel this sweep rotated the hint 33 degrees in a single 0.25-degree hand step
        /// at reach 0.65, elevation -20 (hand behind the hip). Here it must stay smooth at every height,
        /// because the stereo field has no zero anywhere the hand can reach. Gate 8 deg; measured worst ~0.7.
        /// </summary>
        [Test]
        public void ReachingBehind_DoesNotSnapTheElbow_OnAnyHorizontalSwing(
            [Values(-30f, -20f, -10f, 0f, 10f, 20f)] float elevDeg)
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

            Assert.Less(worst, gate,
                $"reaching behind at elevation {elevDeg:F0} deg rotated the elbow bend {worst:F1} deg in one " +
                $"0.25-degree hand step near azimuth {worstAz:F0}. That is the reach-behind snap. The polynomial " +
                "field it replaced measured 33 deg on exactly this swing; the stereo field has no reachable zero " +
                "to cross.");
        }

        /// <summary>
        /// ⭐⭐ THE TOPOLOGY, PINNED. A tangent field must have a zero (index total 2); the whole point of
        /// the stereo model is that its ONE zero lives in the torso. So sweep the entire sphere and demand
        /// the bend's conditioning (its lever) never collapses ANYWHERE the hand can actually go. The only
        /// place it is allowed to fall is deep across the body -- x well negative -- where a hand would have
        /// to pass through the chest to point, which is unreachable at every reach.
        ///
        /// If a future re-fit (or a different zero placement) let a collapse back into reachable space, this
        /// fails -- which is precisely the regression that reintroduces the snap.
        /// </summary>
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
            Assert.Greater(worstReachable, floor,
                $"the elbow bend collapsed to lever {worstReachable:F3} at a REACHABLE direction {worstAt} " +
                "(not across the body). A collapse in reach is a snap waiting to happen -- the stereo field's " +
                "single zero is supposed to sit only inside the torso.");
            Assert.IsTrue(sawCollapse,
                $"the field must still HAVE its one zero (it is topologically required); it was expected deep " +
                "across the body. Not finding it means the construction changed and the guarantee is void.");
            Assert.Less(collapseAt.x, -0.5f,
                $"the zero was found at {collapseAt}, not across the body. It must stay in the torso to be " +
                "unreachable.");
        }

        /// <summary>
        /// The bend must be a UNIT vector PERPENDICULAR to the shoulder->hand axis for EVERY input, including
        /// targets far beyond the avatar's reach and degenerate ones -- that is what puts the hint on the
        /// elbow's circle by construction, so the solver needs no fade to drag it back.
        /// </summary>
        [Test]
        public void TheBend_IsAlwaysUnit_AndPerpendicular_EvenBeyondReachAndAtDegeneracies()
        {
            var rng = new System.Random(20260718);
            for (int i = 0; i < 20000; i++)
            {
                float3 tip = new float3(
                    (float)(rng.NextDouble() * 6.0 - 3.0),
                    (float)(rng.NextDouble() * 6.0 - 3.0),
                    (float)(rng.NextDouble() * 6.0 - 3.0));
                if (math.length(tip) < 1e-4f) continue;
                float3 b = BasisElbowStereoModel.BendDirection(tip, out float cond);
                Assert.IsTrue(math.all(math.isfinite(b)), $"bend not finite at {tip}");
                Assert.IsTrue(math.isfinite(cond), $"conditioning not finite at {tip}");
                Assert.AreEqual(1f, math.length(b), 3e-3f, $"bend must be UNIT at {tip}");
                Assert.AreEqual(0f, math.dot(math.normalize(tip), b), 3e-3f,
                    $"bend must be PERPENDICULAR to shoulder->hand at {tip}");
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

        /// <summary>
        /// A big up-down swing (a meridian) must stay as smooth as it was -- the reach-behind fix must not
        /// re-introduce a snap on the vertical swings BasisArmBigSwingFlipTests already guards. Measured worst
        /// step here is under 1 degree at every azimuth.
        /// </summary>
        [Test]
        public void BigUpDownSwings_StaySmooth([Values(-45f, 0f, 45f, 90f, 135f)] float azDeg)
        {
            const int steps = 640;
            float worst = 0f;
            float3 prev = Dir(azDeg, 84f);
            float3 prevBend = Bend(prev);
            for (int s = 1; s <= steps; s++)
            {
                float el = Mathf.Lerp(84f, -84f, s / (float)steps);
                float3 b = Bend(Dir(azDeg, el));
                worst = Mathf.Max(worst, Vector3.Angle(new Vector3(prevBend.x, prevBend.y, prevBend.z),
                                                       new Vector3(b.x, b.y, b.z)));
                prevBend = b;
            }
            Assert.Less(worst, 8f, $"an up-down swing at azimuth {azDeg:F0} snapped {worst:F1} deg/step");
        }

        /// <summary>
        /// The live entry the solver uses (BasisSwivelHintCore.ArmHint -- frame, mirror, field, tuck) must
        /// keep the two arms EXACT mirrors: a sign slip here is "one elbow fine, the other inverted", which
        /// this model's ancestors got wrong twice (once by 145 degrees). Holds for whichever field is the
        /// active default (BasisElbowFieldModel.UseStereoField); it exercises the whole live path.
        /// </summary>
        [Test]
        public void LiveArmHint_MirrorsLeftToRight()
        {
            BasisSwivelFrame frame = BasisSwivelHintCore.BuildFrame(
                new Vector3(-0.17f, 1.40f, 0f), new Vector3(0.17f, 1.40f, 0f),
                new Vector3(0f, 1.25f, 0f), new Vector3(0f, 1.50f, 0f));

            var rng = new System.Random(7);
            Vector3 rSh = new Vector3(0.17f, 1.40f, 0f), lSh = new Vector3(-0.17f, 1.40f, 0f);
            for (int i = 0; i < 3000; i++)
            {
                Vector3 off = new Vector3(
                    (float)(rng.NextDouble() * 1.1 - 0.55),
                    (float)(rng.NextDouble() * 1.1 - 0.55),
                    (float)(rng.NextDouble() * 1.1 - 0.55));
                if (off.sqrMagnitude < 0.03f) continue;
                Vector3 mirrored = new Vector3(-off.x, off.y, off.z);

                Assert.IsTrue(BasisSwivelHintCore.ArmHint(frame, rSh, rSh + off, k_ArmLen, false,
                                                          out Vector3 hintR, out float condR));
                Assert.IsTrue(BasisSwivelHintCore.ArmHint(frame, lSh, lSh + mirrored, k_ArmLen, true,
                                                          out Vector3 hintL, out float condL));
                Vector3 poleR = hintR - rSh, poleL = hintL - lSh;
                Assert.AreEqual(-poleL.x, poleR.x, 2e-3f, "elbows' OUTWARD offset must mirror");
                Assert.AreEqual(poleL.y, poleR.y, 2e-3f, "elbows' height must match");
                Assert.AreEqual(poleL.z, poleR.z, 2e-3f, "elbows' forward offset must match");
                Assert.AreEqual(condL, condR, 2e-3f, "conditioning must match across the mirror");
            }
        }
    }
}
