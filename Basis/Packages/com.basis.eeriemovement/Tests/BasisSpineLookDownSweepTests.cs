using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisSpineLookDownSweepTests
    {
        // Production defaults, matching BasisFullBodyIK.SetDefaults().
        const float SpineBendPitch = 0.45f, SpineBendYaw = 0.10f, SpineBendRoll = 0.35f;
        const float UpperBendPitch = 0.25f, UpperBendYaw = 0.30f, UpperBendRoll = 0.20f;
        const float SpineMaxForwardDeg = 60f, SpineMaxBackwardDeg = 25f, SpineMaxLateralDeg = 25f;
        const float SquishBoost = 0.5f;
        const float BendTwistCoupling = 0.15f; // k_BendTwistCoupling
        const float RestLen = 1f;
        // ----------------------------------------------------------------- wiring sanity
        [Test]
        public void TwistFollowsHeadYaw_AtLevelGaze()
        {
            // Baseline the whole suite rests on: with the head facing 30 deg to one side at level gaze the
            // spine/upper-chest twist that way (yaw weight * the 30 deg facing). If this is zero/flipped the
            // continuity assertions below would be guarding a twist that was never applied.
            var r = Solve(pitch: 0f, yaw: 30f);
            Assert.That(r.WriteUpper && r.WriteSpine, Is.True, "spine/upper-chest bend was not written.");
            Assert.That(r.UpperEuler.y, Is.GreaterThan(1f), $"upper-chest did not twist toward a 30 deg head facing at level gaze (got {r.UpperEuler.y:0.00} deg).");
            Assert.That(Mathf.Sign(r.SpineEuler.y), Is.EqualTo(Mathf.Sign(r.UpperEuler.y)),"spine and upper-chest twisted opposite ways for the same head facing.");
        }
        // ----------------------------------------------------------------- the headline: no snap
        [Test]
        public void ChestTwist_DoesNotSnap_AsGazeCrossesVertical()
        {
            // The bug, exactly: nod the gaze from past-vertical-up to past-vertical-down in fine steps and
            // the applied spine/upper-chest yaw must never jump between consecutive steps. Pre-fix the upper-
            // chest yaw leaps 0 -> ~25 deg the instant the gaze crosses straight-down (the atan2 azimuth
            // flip clamped to the lateral limit); post-fade the worst step is a fraction of a degree.
            AssertNoYawSnap(yaw: 0f, maxStepDeg: 2f);
        }
        [Test]
        public void ChestTwist_DoesNotSnap_AcrossVertical_AtVariousHeadYaw()
        {
            // Same guard with the head also turned, since a real look-down is rarely dead-ahead. The fade is
            // keyed on gaze elevation (horizMag), not yaw, so every facing must stay continuous through the
            // pole. Measured worst consecutive step post-fix is ~0.7 deg at 30 deg yaw; 2 deg is a wide guard.
            foreach (float yaw in new[] { 5f, 15f, 30f, -20f })
            {
                AssertNoYawSnap(yaw, maxStepDeg: 2f);
            }
        }
        // ----------------------------------------------------------------- the fade must not over-correct
        [Test]
        public void OrdinaryLookDown_KeepsChestTwist()
        {
            // The fade may only engage near vertical -- ordinary look-down (down to ~70 deg, gaze at the
            // floor a stride ahead) must keep the full facing twist, or the torso would stop following the
            // head through normal poses. With a 25 deg head facing the upper-chest twist is unchanged from
            // level all the way to 68 deg of pitch (the fade only begins past ~70 deg).
            float level = Solve(pitch: 0f, yaw: 25f).UpperEuler.y;
            Assert.That(level, Is.GreaterThan(1f), "no twist at level gaze to compare against.");
            foreach (float pitch in new[] { 20f, 40f, 55f, 68f })
            {
                float here = Solve(pitch, yaw: 25f).UpperEuler.y;
                Assert.That(here, Is.EqualTo(level).Within(0.5f), $"look-down {pitch:0} deg changed the chest twist to {here:0.00} (was {level:0.00}); the fade engaged inside the normal range.");
            }
        }
        // ----------------------------------------------------------------- corrected behaviour past vertical
        [Test]
        public void LookingPastVertical_DoesNotFlickTheChestSideways()
        {
            // Looking straight down (and a touch past, chin tucked) must not throw a sideways twist into the
            // chest. Pre-fix this region is exactly where the azimuth flip parks the upper-chest at the full
            // lateral clamp; post-fade the facing twist is gone, so the only yaw left is the (here zero)
            // bend->twist coupling. Asserted for a dead-ahead and a turned gaze.
            foreach (float yaw in new[] { 0f, 20f })
            {
                foreach (float pitch in new[] { 91f, 93f, 95f })
                {
                    float upper = Mathf.Abs(Solve(pitch, yaw).UpperEuler.y);
                    float spine = Mathf.Abs(Solve(pitch, yaw).SpineEuler.y);
                    Assert.That(upper, Is.LessThan(3f), $"gaze {pitch:0} deg down (yaw {yaw:0}) flicked the upper-chest {upper:0.0} deg sideways.");
                    Assert.That(spine, Is.LessThan(3f), $"gaze {pitch:0} deg down (yaw {yaw:0}) flicked the spine {spine:0.0} deg sideways.");
                }
            }
        }
        // ----------------------------------------------------------------- realistic look-down (rot + pos)
        [Test]
        public void RealisticLookDown_BendsSmoothly_NoChestSnap()
        {
            // Closer to the live rig: as the gaze drops, the head target also slides down and forward (the
            // HMD swings off the neck pivot), so the forward bend engages alongside the twist. Sweep the
            // whole motion and require the applied spine/upper-chest delta to move continuously -- the bend
            // grows smoothly while the twist must not snap at the bottom of the nod.
            foreach (float yaw in new[] { 0f, 15f })
            {
                Vector3 prevSpine = Vector3.zero, prevUpper = Vector3.zero;
                bool have = false;
                for (float pitch = 0f; pitch <= 95f; pitch += 1f)
                {
                    float drop = 0.3f * Mathf.Sin(Mathf.Deg2Rad * Mathf.Min(pitch, 90f));
                    Vector3 head = new Vector3(0f, 1f - drop, drop); // stays above the hips throughout
                    var r = Solve(pitch, yaw, head);
                    if (have)
                    {
                        Assert.That((r.SpineEuler - prevSpine).magnitude, Is.LessThan(4f), $"spine bend jumped {(r.SpineEuler - prevSpine).magnitude:0.0} deg at pitch {pitch:0} (yaw {yaw:0}).");
                        Assert.That((r.UpperEuler - prevUpper).magnitude, Is.LessThan(4f), $"upper-chest bend jumped {(r.UpperEuler - prevUpper).magnitude:0.0} deg at pitch {pitch:0} (yaw {yaw:0}).");
                    }
                    prevSpine = r.SpineEuler; prevUpper = r.UpperEuler; have = true;
                }
            }
        }
        // ----------------------------------------------------------------- degenerate / safety
        [Test]
        public void AllOutputs_StayFinite_AcrossTheFullPitchSweep()
        {
            // Through the straight-up/straight-down singularities where horizMag collapses, nothing may go
            // NaN/Inf and the bend must stay in a sane range. The lateral clamp bounds yaw/roll at 25 deg.
            foreach (float yaw in new[] { 0f, 20f, -35f })
            {
                for (float pitch = -120f; pitch <= 120f; pitch += 1f)
                {
                    var r = Solve(pitch, yaw);
                    Assert.That(IsFinite(r.SpineEuler) && IsFinite(r.UpperEuler) && IsFinite(r.TwistY), Is.True, $"pitch {pitch:0} (yaw {yaw:0}): a spine-bend output is non-finite.");
                    Assert.That(r.UpperEuler.magnitude, Is.LessThan(80f), $"pitch {pitch:0} (yaw {yaw:0}): upper-chest bend {r.UpperEuler.magnitude:0.0} deg is an inhuman contortion.");
                }
            }
        }
        // ----------------------------------------------------------------- surgical: fade is twist-only
        [Test]
        public void TwistFade_IsTwistOnly_LeavesTheForwardBendUntouched()
        {
            // The fade must only damp the head-facing TWIST -- never the position-derived forward/lateral
            // bend. Hold the head leaned forward (a real forward bend) and pitch the head rotation (which
            // drives the fade) through vertical: the forward/lateral bend must not move a hair, while the
            // twist fades. A refactor that let the fade multiply the wrong term would silently kill the
            // spine's forward bend the moment you look down -- this is the guard for that.
            Vector3 forwardHead = new Vector3(0f, 1f, 0.3f);
            var b = Solve(0f, yaw: 20f, head: forwardHead);
            Assert.That(b.SpineEuler.x, Is.GreaterThan(1f), "setup: no forward bend to check the fade against.");

            bool twistActuallyFaded = false;
            for (float rotPitch = 0f; rotPitch <= 95f; rotPitch += 5f)
            {
                var r = Solve(rotPitch, yaw: 20f, head: forwardHead);
                Assert.That(r.SpineEuler.x, Is.EqualTo(b.SpineEuler.x).Within(1e-3f), $"forward spine bend shifted ({b.SpineEuler.x:0.000}->{r.SpineEuler.x:0.000}) as the head pitched to {rotPitch:0}; the twist fade bled into the lean.");
                Assert.That(r.UpperEuler.x, Is.EqualTo(b.UpperEuler.x).Within(1e-3f), $"forward upper-chest bend shifted as the head pitched to {rotPitch:0}; the twist fade bled into the lean.");
                Assert.That(r.SpineEuler.z, Is.EqualTo(b.SpineEuler.z).Within(1e-3f), $"lateral spine bend shifted as the head pitched to {rotPitch:0}; the twist fade bled into the roll.");
                if (Mathf.Abs(r.TwistY) < 1f) twistActuallyFaded = true;
            }
            Assert.That(twistActuallyFaded, Is.True,"the twist never faded across the sweep -- the test isn't actually exercising the fade.");
        }
        [Test]
        public void NormalGazeRange_LeavesTheTwistUntouched()
        {
            // The fix must be surgical: out to ~65 deg of gaze pitch the applied twist is EXACTLY the raw
            // head-facing azimuth (the fade is fully off there). Pins the fade band so it can't drift down
            // into everyday poses, which would quietly drop the spine's natural follow-your-gaze twist.
            foreach (float yaw in new[] { 10f, 25f, -20f })
            {
                for (float pitch = -65f; pitch <= 65f; pitch += 5f)
                {
                    var r = Solve(pitch, yaw);
                    float raw = RawTwistDeg(pitch, yaw);
                    Assert.That(r.TwistY, Is.EqualTo(raw).Within(0.01f), $"pitch {pitch:0} (yaw {yaw:0}): applied twist {r.TwistY:0.00} != raw {raw:0.00}; the fade reaches into the normal gaze range.");
                }
            }
        }
        [Test]
        public void TwistFade_IsMonotonic_AsGazeApproachesVertical()
        {
            // For a fixed head facing, the applied twist may only ever DECREASE as the gaze pitches toward
            // vertical -- a non-monotonic fade (a bump) would read as the chest twitching mid-nod.
            foreach (float yaw in new[] { 15f, 30f, -25f })
            {
                float prev = float.PositiveInfinity;
                for (float pitch = 0f; pitch <= 89f; pitch += 1f)
                {
                    float twist = Mathf.Abs(Solve(pitch, yaw).UpperEuler.y);
                    Assert.That(twist, Is.LessThanOrEqualTo(prev + 1e-3f), $"twist grew ({prev:0.000}->{twist:0.000}) as the gaze pitched to {pitch:0} (yaw {yaw:0}); the fade must be monotonic.");
                    prev = twist;
                }
            }
        }
        // ----------------------------------------------------------------- helpers (test scaffolding)
        // Worst consecutive-step change in the applied spine AND upper-chest yaw over a fine pitch sweep
        // through both vertical poles; the head is held straight above the hips so only the facing twist
        // varies (bend pitch/roll stay zero), making any step purely the twist's (dis)continuity.
        static void AssertNoYawSnap(float yaw, float maxStepDeg)
        {
            float prevUpper = 0f, prevSpine = 0f;
            bool have = false;
            float worst = 0f, worstAt = 0f;
            for (float pitch = -95f; pitch <= 95f; pitch += 1f)
            {
                var r = Solve(pitch, yaw);
                if (have)
                {
                    float du = Mathf.Abs(r.UpperEuler.y - prevUpper), ds = Mathf.Abs(r.SpineEuler.y - prevSpine);
                    float step = Mathf.Max(du, ds);
                    if (step > worst) { worst = step; worstAt = pitch; }
                }
                prevUpper = r.UpperEuler.y; prevSpine = r.SpineEuler.y; have = true;
            }
            Assert.That(worst, Is.LessThan(maxStepDeg), $"chest twist snapped {worst:0.0} deg between consecutive 1 deg steps near pitch {worstAt:0} (yaw {yaw:0}); it should ease through vertical gaze.");
        }
        static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        static bool IsFinite(Vector3 v) => IsFinite(v.x) && IsFinite(v.y) && IsFinite(v.z);
        // The raw head-facing azimuth the fade damps, with hips/bind identity (matching the Solve builder)
        // so it is exactly the value the core computes before the fade -- used to assert the fade is off.
        static float RawTwistDeg(float pitch, float yaw)
        {
            Quaternion headRot = Quaternion.AngleAxis(yaw, Vector3.up) * Quaternion.AngleAxis(pitch, Vector3.right);
            Vector3 f = headRot * Vector3.forward;
            float horizSq = f.x * f.x + f.z * f.z;
            return horizSq < 1e-8f ? 0f : Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        }
        static BasisSpineBendResult Solve(float pitch, float yaw) => Solve(pitch, yaw, new Vector3(0f, 1f, 0f));
        // Build the production-default spine-bend input at a given gaze. Hips at the origin (identity
        // rotation + bind, so the bend frame is world-aligned); chest half-way up; head where the caller
        // puts it. Head rotation = yaw about up, then pitch about right -- positive pitch is look-down.
        static BasisSpineBendResult Solve(float pitch, float yaw, Vector3 head)
        {
            BasisSpineBendInput i;
            i.HipsRot = Quaternion.identity;
            i.HipsPos = Vector3.zero;
            i.ChestPos = new Vector3(0f, 0.5f, 0f);
            i.SmoothedHead = head;
            i.HipsBind = Quaternion.identity;
            i.HeadTargetRot = Quaternion.AngleAxis(yaw, Vector3.up) * Quaternion.AngleAxis(pitch, Vector3.right);
            i.SpineMaxForwardDeg = SpineMaxForwardDeg;
            i.SpineMaxBackwardDeg = SpineMaxBackwardDeg;
            i.SpineMaxLateralDeg = SpineMaxLateralDeg;
            i.SpineBendPitch = SpineBendPitch;
            i.SpineBendYaw = SpineBendYaw;
            i.SpineBendRoll = SpineBendRoll;
            i.UpperBendPitch = UpperBendPitch;
            i.UpperBendYaw = UpperBendYaw;
            i.UpperBendRoll = UpperBendRoll;
            i.AnatDifferentialStiffness = false;
            i.AnatPelvicTwistRouting = false;
            i.BendTwistCoupling = BendTwistCoupling;
            i.SquishBoost = SquishBoost;
            i.RestLen = RestLen;
            i.HasSpine = true;
            i.HasUpper = true;
            BasisSpineBendCore.Solve(i, out BasisSpineBendResult r);
            return r;
        }
    }
}
