using NUnit.Framework;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// THE ARM MAY NEVER BECOME A FREE-SPINNING STICK.
    ///
    /// ================================================================================================
    /// At 180 degrees the elbow's distance from the shoulder->hand axis is EXACTLY ZERO:
    ///
    ///     rho = sqrt(upper^2 - (d/2)^2),   d = 2*L*sin(theta/2)      =>   rho(180) = 0
    ///
    /// The elbow lies ON the axis. A pole can no longer POSITION it -- every point on its circle is the
    /// same point -- so everything the hint, the swivel, the anatomy guard and the elbow protect ask for
    /// is paid out as ROLL of the upper arm and forearm about their own long axis. Every roll and flip
    /// singularity this codebase has chased, across three separate files, lives below rho ~ 1.3 cm.
    ///
    /// AND THE ARM LIVES THERE. A relaxed human arm hangs at ~170-175 deg, already 99.6% of full reach.
    /// Any target at or past the avatar's reach is clamped by TriangleAngle to a flat 180 -- and a user
    /// whose real arms are longer than their avatar's is past it ON EVERY FRAME.
    ///
    /// ⚠️ CALIBRATION CANNOT FIX THIS, AND THAT IS THE POINT OF THIS FILE. Reach is STATIONARY in the
    /// elbow angle near straight (dr/dtheta -> 0), so a length error maps to an angle error with
    /// exploding gain: 1 mm -> 6.6 deg, 2 mm -> 9.4 deg, and a PERFECT 0 mm calibration lands the target
    /// exactly AT reach, which is theta = 180 and rho = 0. The singularity sits precisely where the user
    /// is trying to reach. No calibration, however good, keeps the arm off it.
    ///
    /// ⭐ The same stationarity makes the fix nearly free in reverse: 180 -> 170 deg gives up 2.3 mm of
    /// reach and buys 2.61 cm of guaranteed lever arm. A 12x trade. See BasisArmSolveCore.MaxElbowAngleDeg.
    /// ================================================================================================
    /// </summary>
    public class BasisArmFullExtensionTests
    {
        const float k_Arm = 0.60f, k_Upper = 0.30f, k_Fore = 0.30f;
        static readonly Vector3 k_Shoulder = new Vector3(0.17f, 1.40f, 0f);

        static BasisArmSolveResult SolveTo(Vector3 target)
        {
            BasisArmSolveInput i = default;
            i.Shoulder = k_Shoulder;
            i.Elbow = k_Shoulder + new Vector3(0.25f, -0.75f, 0.20f).normalized * k_Upper;
            i.Hand = i.Elbow + new Vector3(0.10f, -0.30f, 0.90f).normalized * k_Fore;
            i.RootRotation = Quaternion.identity;
            i.MidRotation = Quaternion.identity;
            i.TargetPosition = target;
            i.TargetRotation = Quaternion.identity;
            i.TargetOffset = Quaternion.identity;
            i.HintPosition = k_Shoulder + new Vector3(0.5f, -0.8f, -0.2f).normalized * (0.5f * k_Arm);
            i.HintWeight = true;
            i.PlayerUp = Vector3.up;
            i.HintMaxStepDeg = float.MaxValue;
            i.HintIsTracker = false;

            BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);
            return r;
        }

        static float LeverArm(in BasisArmSolveResult r)
        {
            Vector3 ac = r.HandSolved - k_Shoulder;
            if (ac.sqrMagnitude < 1e-8f) return 0f;
            Vector3 acN = ac.normalized;
            Vector3 ae = r.ElbowSolved - k_Shoulder;
            return (ae - acN * Vector3.Dot(ae, acN)).magnitude;
        }

        /// <summary>
        /// ⭐ THE ONE THAT MATTERS. The hand target routinely sails past the avatar's reach -- ANYONE whose
        /// real arms are longer than their avatar's is out there on every single frame, and no calibration
        /// removes that. Out there the elbow must STILL have a lever arm to be positioned by.
        ///
        /// Drives the target from half reach to 1.5x reach, in every direction. rho must never collapse.
        /// </summary>
        [Test]
        public void TheElbow_AlwaysHasALeverArm_EvenFarBeyondTheAvatarsReach()
        {
            // 2 cm on a 0.6 m arm. Every roll/flip singularity in this system lives below ~1.3 cm.
            const float floor = 0.020f;

            var rng = new System.Random(8675309);
            float worst = float.PositiveInfinity;
            Vector3 worstAt = default;

            for (int i = 0; i < 3000; i++)
            {
                Vector3 dir = new Vector3(
                    (float)(rng.NextDouble() * 2 - 1),
                    (float)(rng.NextDouble() * 2 - 1),
                    (float)(rng.NextDouble() * 2 - 1));
                if (dir.sqrMagnitude < 1e-4f) continue;

                float reach = Mathf.Lerp(0.50f, 1.50f, (float)rng.NextDouble());
                Vector3 target = k_Shoulder + dir.normalized * (reach * k_Arm);

                BasisArmSolveResult r = SolveTo(target);
                float rho = LeverArm(r);
                if (rho < worst) { worst = rho; worstAt = target; }
            }

            Assert.Greater(worst, floor,
                $"the elbow's lever arm collapsed to {worst * 100f:F2} cm (target {worstAt}). At rho = 0 the " +
                "elbow lies ON the shoulder->hand axis: a pole cannot position it, only ROLL the arm about " +
                "its own length. That is the free-spinning stick, and it is what a user with longer arms " +
                "than their avatar gets on EVERY frame. MaxElbowAngleDeg is what floors it.");
        }

        /// <summary>
        /// And the cap must cost essentially nothing. Reach is stationary in the elbow angle near straight,
        /// so 10 degrees of guaranteed bend is bought for MILLIMETRES -- and only at the very limit. Any
        /// target the avatar can actually reach must still be reached EXACTLY.
        /// </summary>
        [Test]
        public void TheCap_CostsNothing_ForAnyTargetTheAvatarCanActuallyReach()
        {
            foreach (float reach in new[] { 0.30f, 0.50f, 0.70f, 0.90f, 0.95f, 0.98f, 0.99f })
            {
                Vector3 dir = new Vector3(0.30f, -0.20f, 0.93f).normalized;
                Vector3 target = k_Shoulder + dir * (reach * k_Arm);

                BasisArmSolveResult r = SolveTo(target);

                Assert.AreEqual(0f, Vector3.Distance(r.HandSolved, target), 1e-3f,
                    $"at {reach:P0} of reach the hand must land EXACTLY on its target -- the elbow cap must " +
                    "only ever bind at the very limit, never inside the workspace");
            }

            // at the limit it gives up millimetres, and that is the entire price
            Vector3 far = k_Shoulder + new Vector3(0.30f, -0.20f, 0.93f).normalized * (1.0f * k_Arm);
            BasisArmSolveResult rf = SolveTo(far);
            float shortBy = k_Arm - (rf.HandSolved - k_Shoulder).magnitude;
            Assert.Less(shortBy, 0.005f,
                $"the hand fell {shortBy * 1000f:F1} mm short at full stretch. The cap should cost ~2 mm; " +
                "if it costs centimetres the angle is far too low and the arm will look permanently bent");
        }

        /// <summary>
        /// The cap is anatomy, not a tuning knob: a human elbow does not lock out at 180 under load, and an
        /// avatar whose arm snaps dead straight reads as robotic. Pinned so a well-meaning "let the arm
        /// straighten fully" change has to come and delete this test on purpose.
        /// </summary>
        [Test]
        public void TheElbow_NeverLocksDeadStraight()
        {
            Assert.Less(BasisArmSolveCore.MaxElbowAngleDeg, 178f,
                "an elbow allowed to reach ~180 degrees has NO lever arm there, and everything the solver " +
                "asks of it becomes pure roll of the arm about its own axis");
            Assert.Greater(BasisArmSolveCore.MaxElbowAngleDeg, 160f,
                "below ~160 degrees the permanent bend becomes visible and the arm looks like it can never " +
                "straighten");

            Vector3 straightOut = k_Shoulder + Vector3.right * (2f * k_Arm);   // way out of reach
            BasisArmSolveResult r = SolveTo(straightOut);
            Assert.Less(r.ElbowAngleDeg, 178f,
                $"driven at a target twice its reach the arm still locked to {r.ElbowAngleDeg:F1} degrees");
        }
    }
}
