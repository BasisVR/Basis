using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisArmFullExtensionTests
    {
        const float arm = 0.60f, upper = 0.30f, k_Fore = 0.30f;
        static readonly Vector3 shoulder = new Vector3(0.17f, 1.40f, 0f);
        static BasisArmSolveResult SolveTo(Vector3 target)
        {
            BasisArmSolveInput i = default;
            i.Shoulder = shoulder;
            i.Elbow = shoulder + new Vector3(0.25f, -0.75f, 0.20f).normalized * upper;
            i.Hand = i.Elbow + new Vector3(0.10f, -0.30f, 0.90f).normalized * k_Fore;
            i.RootRotation = Quaternion.identity;
            i.MidRotation = Quaternion.identity;
            i.TargetPosition = target;
            i.TargetRotation = Quaternion.identity;
            i.TargetOffset = Quaternion.identity;
            i.HintPosition = shoulder + new Vector3(0.5f, -0.8f, -0.2f).normalized * (0.5f * arm);
            i.HintWeight = true;
            i.PlayerUp = Vector3.up;
            i.HintMaxStepDeg = float.MaxValue;
            i.HintIsTracker = false;

            BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);
            return r;
        }
        static float LeverArm(in BasisArmSolveResult r)
        {
            Vector3 ac = r.HandSolved - shoulder;
            if (ac.sqrMagnitude < 1e-8f) return 0f;
            Vector3 acN = ac.normalized, ae = r.ElbowSolved - shoulder;
            return (ae - acN * Vector3.Dot(ae, acN)).magnitude;
        }
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
                Vector3 dir = new Vector3((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1));
                if (dir.sqrMagnitude < 1e-4f) continue;

                float reach = Mathf.Lerp(0.50f, 1.50f, (float)rng.NextDouble());
                Vector3 target = shoulder + dir.normalized * (reach * arm);
                BasisArmSolveResult r = SolveTo(target);
                float rho = LeverArm(r);
                if (rho < worst) { worst = rho; worstAt = target; }
            }

            Assert.Greater(worst, floor, $"the elbow's lever arm collapsed to {worst * 100f:F2} cm (target {worstAt}). At rho = 0 the " + "elbow lies ON the shoulder->hand axis: a pole cannot position it, only ROLL the arm about " + "its own length. That is the free-spinning stick, and it is what a user with longer arms " +"than their avatar gets on EVERY frame. MaxElbowAngleDeg is what floors it.");
        }
        [Test]
        public void TheCap_CostsNothing_ForAnyTargetTheAvatarCanActuallyReach()
        {
            foreach (float reach in new[] { 0.30f, 0.50f, 0.70f, 0.90f, 0.95f, 0.98f, 0.99f })
            {
                Vector3 dir = new Vector3(0.30f, -0.20f, 0.93f).normalized, target = shoulder + dir * (reach * arm);
                BasisArmSolveResult r = SolveTo(target);

                Assert.AreEqual(0f, Vector3.Distance(r.HandSolved, target), 1e-3f, $"at {reach:P0} of reach the hand must land EXACTLY on its target -- the elbow cap must " +"only ever bind at the very limit, never inside the workspace");
            }

            // at the limit it gives up millimetres, and that is the entire price
            Vector3 far = shoulder + new Vector3(0.30f, -0.20f, 0.93f).normalized * (1.0f * arm);
            BasisArmSolveResult rf = SolveTo(far);
            float shortBy = arm - (rf.HandSolved - shoulder).magnitude;
            Assert.Less(shortBy, 0.005f, $"the hand fell {shortBy * 1000f:F1} mm short at full stretch. The cap should cost ~2 mm; " +"if it costs centimetres the angle is far too low and the arm will look permanently bent");
        }
        [Test]
        public void TheElbow_NeverLocksDeadStraight()
        {
            Assert.Less(BasisArmSolveCore.MaxElbowAngleDeg, 178f, "an elbow allowed to reach ~180 degrees has NO lever arm there, and everything the solver " +"asks of it becomes pure roll of the arm about its own axis");
            Assert.Greater(BasisArmSolveCore.MaxElbowAngleDeg, 160f, "below ~160 degrees the permanent bend becomes visible and the arm looks like it can never " +"straighten");

            Vector3 straightOut = shoulder + Vector3.right * (2f * arm);   // way out of reach
            BasisArmSolveResult r = SolveTo(straightOut);
            Assert.Less(r.ElbowAngleDeg, 178f, $"driven at a target twice its reach the arm still locked to {r.ElbowAngleDeg:F1} degrees");
        }
    }
}
