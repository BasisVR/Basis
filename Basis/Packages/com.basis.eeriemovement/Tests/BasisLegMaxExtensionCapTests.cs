using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// THE LEG MAY NEVER BECOME A FREE-SPINNING STICK -- the knee's twin of BasisArmFullExtensionTests.
    ///
    /// ================================================================================================
    /// At an interior angle of 180 degrees the knee's distance from the hip->ankle axis is EXACTLY ZERO:
    ///
    ///     rho = upper*lower*sin(interior) / d        =>   rho(180) = 0
    ///
    /// The knee lies ON the axis; its circle has collapsed to a point, and the SWIVEL that places it can no
    /// longer position it. Measured on the real BasisLegSolveCore with 0.2 mm foot nudges, |d(knee)/d(foot)|
    /// sits at ~2-3 across the whole envelope and then SPIKES to ~36x at reach 1.000 -- a 1 mm foot-tracker
    /// jitter swings the knee 36 mm -- before dropping back to 0.5x the instant the foot passes full reach.
    ///
    /// AND THE LEG LIVES THERE. BasisLegSolveCore's own swivel comment: "standing lives there: footHeightOffset
    /// is clamped so the legs fully extend, which parks hip->ankle at ~= thigh + shin permanently." Standing is
    /// the most common pose in VR and it sits right on the singularity.
    ///
    /// ⭐ The arm proved the fix is nearly free in reverse. BasisLegSolveCore.MaxKneeInteriorDeg = 175 gives up
    /// ~0.5 mm of leg reach and buys a ~1.9 cm guaranteed knee lever arm. 175 (a 5 deg standing bend) is the
    /// measured relaxed / single-limb-stance knee angle (Perry gait; young-adult quiet stance ~2-5 deg), so it
    /// reads as natural, not as a crouch, while moving the interior=180 singularity out of the reachable set.
    /// ================================================================================================
    /// </summary>
    public class BasisLegMaxExtensionCapTests
    {
        const float k_Thigh = 0.45f, k_Shin = 0.42f, k_Leg = k_Thigh + k_Shin;
        static readonly Vector3 k_Hip = new Vector3(0.09f, 0.90f, 0f);
        static readonly Vector3 k_BendNormal = Vector3.right; // hips-right -> knee bends forward

        static BasisLegSolveResult SolveTo(Vector3 target)
        {
            BasisLegSolveInput i = default;
            i.Root = k_Hip;
            i.Mid = k_Hip + new Vector3(0.02f, -0.98f, 0.20f).normalized * k_Thigh;
            i.Tip = i.Mid + new Vector3(0.0f, -0.99f, 0.10f).normalized * k_Shin;
            i.RootRotation = Quaternion.identity;
            i.MidRotation = Quaternion.identity;
            i.TargetPosition = target;
            i.TargetRotation = Quaternion.identity;
            i.TargetOffset = Quaternion.identity;
            i.HintPosition = k_Hip + new Vector3(0.35f, -0.55f, 0.75f).normalized * (0.5f * k_Leg);
            i.HintWeight = 1f;
            i.BendNormal = k_BendNormal;
            BasisLegSolveCore.Solve(i, out BasisLegSolveResult r);
            return r;
        }

        static float LeverArm(in BasisLegSolveResult r)
        {
            Vector3 ac = r.FootSolved - k_Hip;
            if (ac.sqrMagnitude < 1e-8f) return 0f;
            Vector3 acN = ac.normalized;
            Vector3 ae = r.KneeSolved - k_Hip;
            return (ae - acN * Vector3.Dot(ae, acN)).magnitude;
        }

        // The lever arm the cap guarantees at its interior angle, from the two segment lengths.
        static float CapLeverArm()
        {
            float chord = LegChord(BasisLegSolveCore.MaxKneeInteriorDeg);
            return k_Thigh * k_Shin * Mathf.Sin(BasisLegSolveCore.MaxKneeInteriorDeg * Mathf.Deg2Rad) / chord;
        }

        static float LegChord(float interiorDeg)
        {
            float c = Mathf.Cos(interiorDeg * Mathf.Deg2Rad);
            return Mathf.Sqrt(k_Thigh * k_Thigh + k_Shin * k_Shin - 2f * k_Thigh * k_Shin * c);
        }

        /// <summary>
        /// ⭐ THE ONE THAT MATTERS. Standing parks the leg at full reach, and a user whose real legs are longer
        /// than their avatar's is beyond reach on every frame. Out there the knee must STILL have a lever arm
        /// to be positioned by, or the swivel becomes pure roll of the thigh and shin.
        /// </summary>
        [Test]
        public void TheKnee_AlwaysHasALeverArm_EvenFarBeyondTheLegsReach()
        {
            // The cap guarantees ~1.9 cm here; floor a hair under it so float noise cannot trip the gate.
            float floor = CapLeverArm() * 0.9f;

            var rng = new System.Random(90210);
            float worst = float.PositiveInfinity;
            Vector3 worstAt = default;
            for (int i = 0; i < 4000; i++)
            {
                Vector3 dir = new Vector3(
                    (float)(rng.NextDouble() * 2 - 1),
                    (float)(rng.NextDouble() * 2 - 1),
                    (float)(rng.NextDouble() * 2 - 1));
                if (dir.sqrMagnitude < 1e-4f) continue;
                float reach = Mathf.Lerp(0.50f, 1.50f, (float)rng.NextDouble());
                Vector3 target = k_Hip + dir.normalized * (reach * k_Leg);
                float rho = LeverArm(SolveTo(target));
                if (rho < worst) { worst = rho; worstAt = target; }
            }

            Assert.Greater(worst, floor,
                $"the knee's lever arm collapsed to {worst * 100f:F2} cm (target {worstAt}). At rho = 0 the knee " +
                "lies ON the hip->ankle axis: a pole cannot position it, only ROLL the leg about its own length. " +
                "MaxKneeInteriorDeg is what floors it.");
        }

        /// <summary>
        /// ⭐ THE BUG GATE (a ratchet). Sweep the foot straight through full extension in world millimetres and
        /// measure the knee travel per unit of foot travel. On the UNCAPPED solver this spikes to ~36x exactly
        /// at reach 1.000; the cap holds it bounded. Proven to FAIL if MaxKneeInteriorDeg is set back to >=180.
        /// </summary>
        [Test]
        public void KneeConditioning_StaysBounded_ThroughFullExtension()
        {
            const float eps = 0.0002f;      // 0.2 mm world nudge, so it can straddle the reach boundary
            const float gate = 15f;         // clean is ~2-7 with the cap; the uncapped spike is 36+
            float worst = 0f; float worstReach = 0f;

            foreach (Vector3 u in new[]
            {
                new Vector3(0.03f, -1.0f, 0.05f).normalized,   // straight down = standing
                new Vector3(0.05f, -0.91f, 0.41f).normalized,  // down-forward
                new Vector3(0.7f, -0.7f, 0.05f).normalized,    // out to the side
            })
            {
                for (float reach = 0.90f; reach <= 1.02f; reach += 0.002f)
                {
                    Vector3 target = k_Hip + u * (reach * k_Leg);
                    foreach (Vector3 ax in new[] { Vector3.right, Vector3.up, Vector3.forward })
                    {
                        var rp = SolveTo(target + ax * eps);
                        var rm = SolveTo(target - ax * eps);
                        float dKnee = (rp.KneeSolved - rm.KneeSolved).magnitude;
                        float dFoot = (rp.FootSolved - rm.FootSolved).magnitude;
                        float cond = dFoot > 1e-9f ? dKnee / dFoot : dKnee / (2f * eps);
                        if (cond > worst) { worst = cond; worstReach = reach; }
                    }
                }
            }

            Assert.Less(worst, gate,
                $"knee conditioning spiked to {worst:F1}x foot travel at reach {worstReach:F3} -- the free-spinning " +
                "stick at full extension. With MaxKneeInteriorDeg < 180 the knee keeps a lever arm and this stays bounded.");
        }

        /// <summary>The cap must cost essentially nothing inside the workspace: every reachable target still hit
        /// exactly, and at full stretch the foot falls only ~half a millimetre short.</summary>
        [Test]
        public void TheCap_CostsAlmostNothing_ForReachableTargets()
        {
            foreach (float reach in new[] { 0.30f, 0.50f, 0.70f, 0.90f, 0.95f, 0.98f })
            {
                Vector3 dir = new Vector3(0.05f, -0.95f, 0.30f).normalized;
                Vector3 target = k_Hip + dir * (reach * k_Leg);
                Assert.AreEqual(0f, Vector3.Distance(SolveTo(target).FootSolved, target), 1.5e-3f,
                    $"at {reach:P0} of reach the foot must land on target -- the cap must only bind at the very limit");
            }

            Vector3 far = k_Hip + new Vector3(0.05f, -0.95f, 0.30f).normalized * (1.0f * k_Leg);
            float shortBy = k_Leg - (SolveTo(far).FootSolved - k_Hip).magnitude;
            Assert.Less(shortBy, 0.003f,
                $"the foot fell {shortBy * 1000f:F1} mm short at full stretch; the cap should cost well under a millimetre");
        }

        /// <summary>Anatomy, not a tuning knob: pinned so a "let the leg lock straight" change has to delete this
        /// on purpose. 175 sits inside the measured relaxed-standing / single-limb-stance range (~2-10 deg flex).</summary>
        [Test]
        public void TheKnee_NeverLocksDeadStraight()
        {
            Assert.Less(BasisLegSolveCore.MaxKneeInteriorDeg, 179f,
                "a knee allowed to reach ~180 has NO lever arm there and every swivel becomes pure roll of the leg");
            Assert.Greater(BasisLegSolveCore.MaxKneeInteriorDeg, 168f,
                "below ~168 the standing bend becomes a visible crouch");

            Vector3 straightDown = k_Hip + Vector3.down * (2f * k_Leg); // twice its reach
            Assert.Less(SolveTo(straightDown).KneeAngleDeg, 178f,
                "driven at a target twice its reach the leg still locked dead straight");
        }
    }
}
