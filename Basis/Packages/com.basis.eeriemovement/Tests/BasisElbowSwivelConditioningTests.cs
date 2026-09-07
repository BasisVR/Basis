using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public sealed class BasisElbowSwivelConditioningTests
    {
        const float k_Dt = 1f / 90f;
        // Realistic right arm. Deliberately UNEQUAL segments (a forearm is shorter than an upper arm), unlike the
        // knee test's symmetric leg -- the conditioning must not depend on the two bones matching.
        const float upperLen = 0.28f, lowerLen = 0.26f;
        const float stepDeg = 60f;   // a step change is the worst case for a One-Euro
        // The live ELBOW cutoffs, i.e. BasisSwivelFilterCore's own defaults, which is what SmoothElbowSwivel passes.
        // Note the elbow's floor is ALREADY the heavy 1 Hz standing floor -- so unlike the tracked knee, the floor
        // was never the problem here. What opens the gate on noise is beta, and beta alone is what conditioning scales.
        const float elbowMinCutoffHz = BasisSwivelFilterCore.MinCutoffHz;   // 1.0
        const float elbowBeta = BasisSwivelFilterCore.Beta;                 // 0.05
        const float elbowDerivCutoffHz = BasisSwivelFilterCore.DerivCutoffHz;
        static BasisSwivelSmootherInput MakeArm(float reach, float swivelDeg, bool conditionOnPole)
        {
            float full = upperLen + lowerLen, d = reach * full;
            Vector3 root = Vector3.zero;
            Vector3 tip = new Vector3(0f, 0f, d);      // hand straight ahead
            Vector3 axis = Vector3.forward;

            // Standard two-bone placement: distance along the axis, then the perpendicular lever arm.
            float along = (upperLen * upperLen - lowerLen * lowerLen + d * d) / (2f * d);
            float lever = Mathf.Sqrt(Mathf.Max(0f, upperLen * upperLen - along * along));
            Vector3 refDir = Vector3.down, perp = Quaternion.AngleAxis(swivelDeg, axis) * refDir;
            Vector3 mid = root + axis * along + perp * lever;

            return new BasisSwivelSmootherInput
            {
                Root = root,
                Mid = mid,
                Tip = tip,
                BodyRotation = Quaternion.identity,
                ReferenceLocal = Vector3.down,
                FallbackLocal = Vector3.zero,
                Dt = k_Dt,
                MinCutoffHz = elbowMinCutoffHz,
                Beta = elbowBeta,
                DerivCutoffHz = elbowDerivCutoffHz,
                ConditionOnPole = conditionOnPole,
                SingularMinCutoffHz = BasisSwivelFilterCore.MinCutoffHz,
                GuardAnteriorHalfSpace = false,   // see HalfSpaceGuard_CannotTransferToTheArm below
            };
        }
        static float StepResponse(float reach, bool conditionOnPole)
        {
            BasisSwivelSmootherInput seed = MakeArm(reach, 0f, conditionOnPole);
            BasisSwivelSmootherCore.Solve(seed, out BasisSwivelSmootherResult seeded);
            Assert.IsTrue(seeded.Seeded, "seed frame must establish filter state");

            BasisSwivelSmootherInput step = MakeArm(reach, stepDeg, conditionOnPole);
            step.State = seeded.State;
            step.Seeded = true;

            BasisSwivelSmootherCore.Solve(step, out BasisSwivelSmootherResult r);
            Assert.IsTrue(r.WriteState, "post-seed frame must advance the filter");
            Assert.Greater(Mathf.Abs(r.RawSwivelDeg), 1f, "the step must actually register in the raw swivel");

            return Mathf.Clamp01(Mathf.Abs(r.SmoothSwivelDeg) / Mathf.Abs(r.RawSwivelDeg));
        }
        static float ConditioningAt(float reach)
        {
            BasisSwivelSmootherCore.Solve(MakeArm(reach, 0f, true), out BasisSwivelSmootherResult r);
            return r.Conditioning;
        }
        [Test]
        public void Conditioning_CollapsesAsTheArmStraightens()
        {
            // Crossing the threshold, not merely approaching it: 0.999 is the pose a reaching arm actually sits in.
            float straight = ConditioningAt(0.999f), bent = ConditioningAt(0.70f);

            Assert.Less(straight, 0.06f, $"a near-straight arm must be near-singular (got {straight:F4})");
            Assert.Greater(bent, 0.40f, $"a bent elbow must be well-conditioned (got {bent:F4})");
        }
        [Test]
        public void ElbowStep_IsDamped_WhenTheArmIsStraight()
        {
            float resp = StepResponse(0.999f, conditionOnPole: true);
            Assert.Less(resp, 0.20f, $"at the singularity the pole is noise, so the filter must not chase it (got {resp:P0} of the step in one frame)");
        }
        [Test]
        public void ElbowStep_StaysResponsive_WhenTheElbowIsBent()
        {
            // The fix must not simply glue the elbow in place. A real reach at a bent elbow still has to track --
            // this is the gate that fails if conditioning is applied too aggressively.
            float resp = StepResponse(0.70f, conditionOnPole: true);
            Assert.Greater(resp, 0.35f, $"a bent elbow carries real pole information and must still track it (got {resp:P0})");
        }
        [Test]
        public void BentElbow_IsStrictlyMoreResponsiveThanStraightArm()
        {
            float straight = StepResponse(0.999f, conditionOnPole: true);
            float bent = StepResponse(0.70f, conditionOnPole: true);
            Assert.Greater(bent, straight + 0.20f, $"responsiveness must scale with how much the pole is worth (straight {straight:P0} vs bent {bent:P0})");
        }
        [Test]
        public void Legacy_UnconditionedElbow_SnapsAtFullExtension()
        {
            float legacy = StepResponse(0.999f, conditionOnPole: false);
            float conditioned = StepResponse(0.999f, conditionOnPole: true);

            Assert.Greater(legacy, 0.45f, $"the legacy elbow filter is expected to snap at the singularity -- that IS the defect (got {legacy:P0}). " +"If this starts passing, the One-Euro was retuned and these gates must be re-derived.");
            Assert.Less(conditioned, legacy - 0.30f, $"conditioning must materially damp the snap (legacy {legacy:P0} vs conditioned {conditioned:P0})");
        }
        // ---------------------------------------------------------------------------------------------------------
        // The knee guard does NOT transfer. This test exists to stop someone completing the symmetry by reflex.
        // ---------------------------------------------------------------------------------------------------------
        [Test]
        public void HalfSpaceGuard_CannotTransferToTheArm()
        {
            Vector3 shoulder = Vector3.zero;

            // Ordinary right-arm poses. Body frame: forward +Z, up +Y, right +X.
            (string name, Vector3 elbow, Vector3 hand)[] poses =
            {
                ("arm hanging",        new Vector3(0.03f, -0.27f, -0.05f), new Vector3(0.06f, -0.52f,  0.04f)),
                ("reach forward",      new Vector3(0.10f, -0.20f,  0.15f), new Vector3(0.08f, -0.05f,  0.52f)),
                ("hand behind head",   new Vector3(0.26f,  0.10f,  0.02f), new Vector3(-0.02f, 0.26f, -0.10f)),
                ("hand across chest",  new Vector3(0.22f, -0.16f,  0.04f), new Vector3(-0.18f, -0.06f, 0.16f)),
                ("arm overhead",       new Vector3(0.12f,  0.24f, -0.04f), new Vector3(0.04f,  0.52f,  0.02f)),
                ("hand behind back",   new Vector3(0.10f, -0.24f, -0.08f), new Vector3(-0.10f, -0.34f, -0.22f)),
                ("elbow high",         new Vector3(0.24f,  0.04f, -0.02f), new Vector3(0.30f, -0.20f,  0.18f)),
            };

            // Each pose's elbow direction, as an angle about that pose's OWN shoulder->hand axis. The reference used
            // to read the angle off is arbitrary and cancels out of a span, which is precisely the point.
            float[] deg = new float[poses.Length];
            for (int i = 0; i < poses.Length; i++)
            {
                Vector3 axis = (poses[i].hand - shoulder).normalized;
                Vector3 elbowDir = Vector3.ProjectOnPlane(poses[i].elbow - shoulder, axis);
                Vector3 reference = Vector3.ProjectOnPlane(Vector3.forward, axis);

                Assert.Greater(elbowDir.sqrMagnitude, 1e-8f, $"{poses[i].name}: elbow must be off the arm axis");
                Assert.Greater(reference.sqrMagnitude, 1e-8f, $"{poses[i].name}: reference must lie in the swivel plane");

                deg[i] = Vector3.SignedAngle(reference.normalized, elbowDir.normalized, axis);
            }

            System.Array.Sort(deg);

            // Largest gap between neighbours on the circle (including the wrap from the last back to the first).
            float largestGap = deg[0] + 360f - deg[poses.Length - 1];
            for (int i = 1; i < poses.Length; i++)
            {
                largestGap = Mathf.Max(largestGap, deg[i] - deg[i - 1]);
            }

            float span = 360f - largestGap;

            Assert.Greater(span, 180f, $"legitimate arm poses span {span:F1} deg of the elbow circle. A half-space guard admits only a " + "180 deg arc, so NO reference direction can contain them -- pasting the knee's anterior guard onto " + "the elbow would clamp poses the user actually makes, which is worse than the bug it targets. " + "The arm's real invariant is a humeral ROM limit, not a body-frame half-space. Do not 'finish the " +"symmetry' here.");
        }
    }
}
