using Basis.Scripts.Drivers;
using NUnit.Framework;
using Unity.Mathematics;

namespace Basis.Framework.IK.Tests
{
    /// <summary>
    /// Pure-math coverage of the locomotion stepper: blend weights, time advance, transition and
    /// trigger semantics. No Unity objects involved — the same code the pose system runs per frame.
    /// </summary>
    public class BasisLocomotionStepperTests
    {
        static float[] Lengths()
        {
            var lengths = new float[BasisLocomotionGraph.ClipCount];
            for (int i = 0; i < lengths.Length; i++)
            {
                lengths[i] = 1f;
            }
            return lengths;
        }

        static bool[] Looping(bool value = true)
        {
            var looping = new bool[BasisLocomotionGraph.ClipCount];
            for (int i = 0; i < looping.Length; i++)
            {
                looping[i] = value;
            }
            return looping;
        }

        static BasisLocoContribution[] Buffer() => new BasisLocoContribution[BasisLocomotionGraph.MaxContributions];

        [Test]
        public void WeightsAreOneAtEachChildPosition()
        {
            var weights = new float[BasisLocomotionGraph.WalkingChildCount];
            for (int i = 0; i < BasisLocomotionGraph.WalkingChildCount; i++)
            {
                BasisLocomotionGraph.FreeformCartesianWeights(
                    BasisLocomotionGraph.WalkingChildPositions[i],
                    BasisLocomotionGraph.WalkingChildPositions, weights);
                for (int j = 0; j < weights.Length; j++)
                {
                    Assert.AreEqual(i == j ? 1f : 0f, weights[j], 1e-4f, $"point {i}, weight {j}");
                }
            }
        }

        [Test]
        public void WeightsAreConvexEverywhere()
        {
            var weights = new float[BasisLocomotionGraph.WalkingChildCount];
            var rng = new Unity.Mathematics.Random(1234);
            for (int trial = 0; trial < 500; trial++)
            {
                float2 point = rng.NextFloat2(new float2(-5f, -5f), new float2(5f, 5f));
                BasisLocomotionGraph.FreeformCartesianWeights(point, BasisLocomotionGraph.WalkingChildPositions, weights);
                float sum = 0f;
                for (int i = 0; i < weights.Length; i++)
                {
                    Assert.GreaterOrEqual(weights[i], 0f);
                    Assert.LessOrEqual(weights[i], 1f + 1e-4f);
                    sum += weights[i];
                }
                Assert.AreEqual(1f, sum, 1e-3f, $"weights must normalize at {point}");
            }
        }

        [Test]
        public void IdleParamsBlendToIdleClip()
        {
            BasisLocoSimState sim = BasisLocomotionGraph.DefaultSimState;
            var p = new BasisLocoParams { CurrentSpeed = 1f };
            var contributions = Buffer();
            int count = BasisLocomotionGraph.Step(ref sim, ref p, 1f / 90f, Lengths(), Looping(), contributions);
            Assert.Greater(count, 0);
            float idleWeight = 0f;
            float total = 0f;
            for (int i = 0; i < count; i++)
            {
                total += contributions[i].Weight;
                if (contributions[i].Clip == BasisLocomotionGraph.WalkingClipStart + 9)
                {
                    idleWeight += contributions[i].Weight;
                }
            }
            Assert.AreEqual(1f, total, 1e-3f);
            Assert.Greater(idleWeight, 0.98f, "standing still should be nearly pure Idle");
        }

        [Test]
        public void ZeroCurrentSpeedFreezesWalkingPhase()
        {
            BasisLocoSimState sim = BasisLocomotionGraph.DefaultSimState;
            var p = new BasisLocoParams { CurrentSpeed = 0f, VelocityZ = 1.2f };
            var contributions = Buffer();
            BasisLocomotionGraph.Step(ref sim, ref p, 0.1f, Lengths(), Looping(), contributions);
            Assert.AreEqual(0f, sim.CurrentNorm, 1e-6f);
        }

        [Test]
        public void JumpTransitionStartsAndCompletes()
        {
            BasisLocoSimState sim = BasisLocomotionGraph.DefaultSimState;
            var p = new BasisLocoParams { CurrentSpeed = 1f, IsJumping = true };
            var contributions = Buffer();
            float[] lengths = Lengths();
            bool[] looping = Looping(false);

            BasisLocomotionGraph.Step(ref sim, ref p, 0.01f, lengths, looping, contributions);
            Assert.IsTrue(sim.InTransition);
            Assert.AreEqual(BasisLocoState.Jump, sim.To);

            int steps = (int)math.ceil(0.25f / 0.01f) + 1;
            for (int i = 0; i < steps; i++)
            {
                BasisLocomotionGraph.Step(ref sim, ref p, 0.01f, lengths, looping, contributions);
            }
            Assert.IsFalse(sim.InTransition);
            Assert.AreEqual(BasisLocoState.Jump, sim.Current);
        }

        [Test]
        public void CrossfadeWeightsSumToOne()
        {
            BasisLocoSimState sim = BasisLocomotionGraph.DefaultSimState;
            var p = new BasisLocoParams { CurrentSpeed = 1f, CrouchedState = true };
            var contributions = Buffer();
            float[] lengths = Lengths();
            bool[] looping = Looping();

            BasisLocomotionGraph.Step(ref sim, ref p, 0.01f, lengths, looping, contributions);
            Assert.IsTrue(sim.InTransition);
            Assert.AreEqual(BasisLocoState.Cawling, sim.To);

            int count = BasisLocomotionGraph.Step(ref sim, ref p, 0.05f, lengths, looping, contributions);
            float total = 0f;
            for (int i = 0; i < count; i++)
            {
                total += contributions[i].Weight;
            }
            Assert.AreEqual(1f, total, 1e-3f, "mid-crossfade contributions must stay convex");
        }

        [Test]
        public void LandingTriggerIsConsumedOnUse()
        {
            BasisLocoSimState sim = BasisLocomotionGraph.DefaultSimState;
            sim.Current = BasisLocoState.Falling;
            var p = new BasisLocoParams { CurrentSpeed = 1f, IsFalling = true, LandingTrigger = true };
            var contributions = Buffer();
            BasisLocomotionGraph.Step(ref sim, ref p, 0.01f, Lengths(), Looping(), contributions);
            Assert.IsTrue(sim.InTransition);
            Assert.AreEqual(BasisLocoState.Landing, sim.To);
            Assert.IsFalse(p.LandingTrigger, "the trigger must be consumed by the transition that used it");
        }

        [Test]
        public void FallingExitsToWalkingOnlyAfterExitTime()
        {
            BasisLocoSimState sim = BasisLocomotionGraph.DefaultSimState;
            sim.Current = BasisLocoState.Falling;
            sim.CurrentNorm = 0.5f;
            var p = new BasisLocoParams { CurrentSpeed = 1f, IsFalling = false };
            var contributions = Buffer();
            float[] lengths = Lengths();
            bool[] looping = Looping();

            BasisLocomotionGraph.Step(ref sim, ref p, 0.1f, lengths, looping, contributions);
            Assert.IsFalse(sim.InTransition, "0.6 has not crossed the 0.75 exit yet");

            BasisLocomotionGraph.Step(ref sim, ref p, 0.2f, lengths, looping, contributions);
            Assert.IsTrue(sim.InTransition, "crossing 0.75 with IsFalling false must start the exit");
            Assert.AreEqual(BasisLocoState.Walking, sim.To);
        }

        [Test]
        public void UninterruptibleTransitionIgnoresNewConditions()
        {
            BasisLocoSimState sim = BasisLocomotionGraph.DefaultSimState;
            var p = new BasisLocoParams { CurrentSpeed = 1f, CrouchedState = true };
            var contributions = Buffer();
            float[] lengths = Lengths();
            bool[] looping = Looping();

            BasisLocomotionGraph.Step(ref sim, ref p, 0.01f, lengths, looping, contributions);
            Assert.IsTrue(sim.InTransition);

            p.IsJumping = true;
            BasisLocomotionGraph.Step(ref sim, ref p, 0.01f, lengths, looping, contributions);
            Assert.AreEqual(BasisLocoState.Cawling, sim.To, "an active transition must not be interrupted");
        }
    }
}
