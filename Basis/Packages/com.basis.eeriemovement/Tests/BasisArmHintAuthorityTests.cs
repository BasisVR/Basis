using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public sealed class BasisArmHintAuthorityTests
    {
        const float upper = 0.30f, lower = 0.30f, k_ArmLen = upper + lower;
        static readonly Vector3 shoulder = new Vector3(0.17f, 1.40f, 0f);
        static BasisArmSolveInput Input(Vector3 animElbow, Vector3 animHand, Vector3 target, Vector3 hintPos)
        {
            BasisArmSolveInput i = default;
            i.Shoulder = shoulder;
            i.Elbow = animElbow;
            i.Hand = animHand;
            i.RootRotation = Quaternion.identity;
            i.MidRotation = Quaternion.identity;
            i.TargetPosition = target;
            i.TargetRotation = Quaternion.identity;
            i.TargetOffset = Quaternion.identity;
            i.HintPosition = hintPos;
            i.HintWeight = true;
            i.HintIsTracker = false;          // THE MODEL PATH -- this is the one that must have full authority
            i.HintMaxStepDeg = float.MaxValue; // the live job passes MaxValue (stateless solve, no rate limit)
            i.PlayerUp = Vector3.up;
            return i;
        }
        static Vector3 HintOnCircle(Vector3 target, float swivelDeg)
        {
            float3 bend = BasisArmSwivelModel.BendDirection(new float3(target.x - shoulder.x, target.y - shoulder.y, target.z - shoulder.z), new float3(0f, -1f, 0f),                      // body DOWN -- the model's swivel zero
                swivelDeg * Mathf.Deg2Rad);
            return shoulder + 0.5f * k_ArmLen * new Vector3(bend.x, bend.y, bend.z);
        }
        static Vector3 ElbowOnCircle(Vector3 target, float swivelDeg)
        {
            Vector3 sa = target - shoulder;
            float d = sa.magnitude;
            Vector3 axis = sa / d;

            // Standard two-bone circle: the elbow rides a circle of radius `radius`, centred `a` along the axis.
            float a = (upper * upper - lower * lower + d * d) / (2f * d);
            float radius = Mathf.Sqrt(Mathf.Max(upper * upper - a * a, 0f));
            Vector3 centre = shoulder + axis * a;

            // The same in-plane basis BasisArmSwivelModel.BendDirection uses, so `swivelDeg` means the same
            // thing here as it does to the model. Reference = body DOWN.
            Vector3 refDown = Vector3.down, u = (refDown - axis * Vector3.Dot(refDown, axis)).normalized;
            Vector3 v = Vector3.Cross(axis, u);
            float rad = swivelDeg * Mathf.Deg2Rad;
            return centre + radius * (u * Mathf.Cos(rad) + v * Mathf.Sin(rad));
        }
        [Test]
        public void TheHint_LandsTheElbowOnItsPole_AtEveryExtension()
        {
            const float commandedSwivel = 30f;   // elbow down and a bit back: where a real one hangs

            foreach (float reach in new[] { 0.50f, 0.80f, 0.95f, 0.97f, 0.99f, 0.995f, 0.999f })
            {
                // Hand out to the side and slightly forward -- the canonical VR reach.
                Vector3 dir = new Vector3(0.92f, 0f, 0.39f).normalized, target = shoulder + dir * (reach * k_ArmLen);
                Vector3 hintPos = HintOnCircle(target, commandedSwivel);

                // The ANIMATED arm starts with its elbow on the OPPOSITE side of its circle -- a REAL elbow at
                // the real bone distances, just on the wrong side. That is exactly what an idle animation hands
                // the solver for an extended arm, and it is the state the solver must be able to rescue.
                Vector3 animElbow = ElbowOnCircle(target, commandedSwivel + 180f), animHand = target;

                BasisArmSolveCore.Solve(Input(animElbow, animHand, target, hintPos), out BasisArmSolveResult r);

                // 1. The hand must land on its target. Everything else is meaningless if it does not.
                Assert.AreEqual(0f, Vector3.Distance(r.HandSolved, target), 2e-3f, $"the hand must reach its target at {reach:P1} extension");

                // 2. THE ELBOW MUST BE ON THE COMMANDED SIDE. Measured as the signed swivel about the
                //    shoulder->hand axis, which is the only thing the pole actually controls.
                Vector3 axis = (r.HandSolved - shoulder).normalized, got = r.ElbowSolved - shoulder;
                got -= axis * Vector3.Dot(got, axis);
                Vector3 want = hintPos - shoulder;
                want -= axis * Vector3.Dot(want, axis);

                Assert.Greater(want.sqrMagnitude, 1e-8f, "the commanded pole must be well-defined");

                // At full extension the elbow's circle has collapsed, so its POSITION barely moves -- but its
                // DIRECTION is exactly what the user sees as "which side is my elbow on", and it must be right.
                float offDeg = Vector3.Angle(got.normalized, want.normalized);
                Assert.Less(offDeg, 10f, $"at {reach:P1} extension the elbow sits {offDeg:F1} deg off its commanded pole. " + "The solver is fading the hint out on the elbow's own collapsing lever arm, which leaves " +"the elbow wherever the ANIMATION put it -- on the wrong side. This is the bug.");
            }
        }
        [Test]
        public void FullHintAuthority_DoesNotMoveTheHandOffItsTarget()
        {
            var rng = new System.Random(4242);

            for (int t = 0; t < 300; t++)
            {
                // Above the arm core's max-flexion floor: below it the hand is anatomically
                // unreachable and the solver correctly refuses, which is not what this test is about.
                float reach = Mathf.Lerp(0.35f, 0.999f, (float)rng.NextDouble());
                var dir = new Vector3((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1));
                if (dir.sqrMagnitude < 1e-4f) continue;
                dir.Normalize();

                Vector3 target = shoulder + dir * (reach * k_ArmLen);
                float swivel = (float)(rng.NextDouble() * 360.0 - 180.0);
                Vector3 hintPos = HintOnCircle(target, swivel);

                Vector3 animElbow = ElbowOnCircle(target, swivel + 137f);   // a real elbow, somewhere unhelpful
                BasisArmSolveCore.Solve(Input(animElbow, target, target, hintPos), out BasisArmSolveResult r);

                Assert.AreEqual(0f, Vector3.Distance(r.HandSolved, target), 2e-3f, $"the hand must stay on target at {reach:P1} extension with a {swivel:F0} deg pole (iter {t})");
                Assert.IsTrue(float.IsFinite(r.ElbowSolved.x) && float.IsFinite(r.ElbowSolved.y) && float.IsFinite(r.ElbowSolved.z),"the elbow must stay finite");
            }
        }
        [Test]
        public void ARealTracker_GetsFullAuthority_AtEveryExtension()
        {
            const float commanded = 35f;

            foreach (float reach in new[] { 0.80f, 0.94f, 0.97f, 0.985f, 0.995f, 0.999f })
            {
                Vector3 dir = new Vector3(0.92f, 0f, 0.39f).normalized, target = shoulder + dir * (reach * k_ArmLen);
                Vector3 hintPos = HintOnCircle(target, commanded);
                Vector3 animElbow = ElbowOnCircle(target, commanded + 180f);   // wrong side, as an idle anim leaves it

                BasisArmSolveInput i = Input(animElbow, target, target, hintPos);
                i.HintIsTracker = true;                       // A REAL TRACKER
                BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);

                Assert.AreEqual(1f, r.HintFade, 1e-4f, $"a real elbow tracker must be obeyed at FULL weight at {reach:P1} extension -- it got " + $"{r.HintFade:F2}. Partial weight lands the elbow neither on the tracker NOR on the " +"animation, but BETWEEN them -- and that is the inversion.");

                Assert.AreEqual(0f, Vector3.Distance(r.HandSolved, target), 2e-3f, $"and the hand must stay on target at {reach:P1}");

                Vector3 axis = (r.HandSolved - shoulder).normalized;
                Vector3 got = r.ElbowSolved - shoulder; got -= axis * Vector3.Dot(got, axis);
                Vector3 want = hintPos - shoulder; want -= axis * Vector3.Dot(want, axis);
                Assert.Less(Vector3.Angle(got.normalized, want.normalized), 10f, $"the tracked elbow must actually land where the tracker says, at {reach:P1} extension");
            }
        }
        [Test]
        public void TheStabilizer_DoesNotDragATrackedElbowTowardWorldDown()
        {
            Vector3 dir = new Vector3(0.92f, 0f, 0.39f).normalized, target = shoulder + dir * (0.99f * k_ArmLen);

            Vector3 hintPos = HintOnCircle(target, 88f);      // elbow out to the side: 88 deg off body-down
            Vector3 animElbow = ElbowOnCircle(target, 0f);
            BasisArmSolveInput i = Input(animElbow, target, target, hintPos);
            i.HintIsTracker = true;
            BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);

            Vector3 axis = (r.HandSolved - shoulder).normalized;
            Vector3 got = r.ElbowSolved - shoulder; got -= axis * Vector3.Dot(got, axis);
            Vector3 want = hintPos - shoulder; want -= axis * Vector3.Dot(want, axis);

            Assert.Less(Vector3.Angle(got.normalized, want.normalized), 12f, "the solver must not pull a TRACKED elbow toward world-down: the tracker is a measurement of " + "the user's actual arm, and overruling a measurement with a guess is exactly the 'not very " +"human movement' report");
        }
        [Test]
        public void ARealisticOffsetTracker_GivesTheSameElbow_WhereverTheAnimationLeftIt()
        {
            var rng = new System.Random(6060);

            foreach (float reach in new[] { 0.85f, 0.94f, 0.975f, 0.99f })
            {
                Vector3 dir = new Vector3(0.85f, -0.20f, 0.49f).normalized;
                Vector3 target = shoulder + dir * (reach * k_ArmLen);

                // The true elbow, and a tracker sitting ~2.5 cm off it (the residual a real calibration leaves)
                // plus 3 mm of jitter: a realistic strapped-on puck.
                Vector3 trueElbow = ElbowOnCircle(target, 30f), off = new Vector3(0.018f, 0.014f, -0.008f);
                Vector3 jitter = new Vector3((float)(rng.NextDouble() - 0.5) * 0.003f, (float)(rng.NextDouble() - 0.5) * 0.003f, (float)(rng.NextDouble() - 0.5) * 0.003f);
                Vector3 tracker = trueElbow + off + jitter;

                // THE SAME TRACKER READING, from four different animated starting poses.
                Vector3 first = Vector3.zero;
                for (int k = 0; k < 4; k++)
                {
                    Vector3 animElbow = ElbowOnCircle(target, 30f + k * 90f);   // including the wrong side
                    BasisArmSolveInput i = Input(animElbow, target, target, tracker);
                    i.HintIsTracker = true;
                    BasisArmSolveCore.Solve(i, out BasisArmSolveResult r);

                    if (k == 0) { first = r.ElbowSolved; continue; }

                    Assert.AreEqual(0f, Vector3.Distance(first, r.ElbowSolved), 2e-3f, $"the same tracker reading must give the SAME elbow regardless of where the animation " + $"left it (reach {reach:P1}, start {30f + k * 90f:F0} deg). If it does not, the elbow " +"flickers between the animation and the tracker, which is the reported inversion.");
                }
            }
        }
    }
}
