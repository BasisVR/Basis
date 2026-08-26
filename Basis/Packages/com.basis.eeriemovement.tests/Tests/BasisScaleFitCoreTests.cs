using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisScaleFitCoreTests
    {
        const float Eps = 1e-4f, PlayerEye = 1.60f, PlayerSpan = 1.65f, AvatarShoulders = 0.35f, Deviation = 0.15f;
        static BasisScaleFitInput Baseline(float avatarEye, float avatarSpan)
        {
            var measurements = new BasisBodyFitMeasurements
            {
                AvatarArmSpan = avatarSpan,
                AvatarShoulderWidth = AvatarShoulders,
                AvatarLegSpan = 0.84f,
                AvatarSpineSpan = 0.55f,
            };

            return new BasisScaleFitInput
            {
                MaxEyeDeviation = BasisScaleFitCore.DefaultMaxEyeDeviation,
                Eye = new BasisScaleFitSample
                {
                    Player = PlayerEye,
                    Avatar = avatarEye,
                    Weight = BasisScaleFitCore.EyeWeight,
                },
                ArmSpan = new BasisScaleFitSample
                {
                    Player = PlayerSpan,
                    Avatar = avatarSpan,
                    Slack = BasisBodyFitCore.ArmSpanSlack(in measurements, Deviation),
                    Weight = BasisScaleFitCore.ArmSpanWeight,
                },
                HipHeight = BasisScaleFitSample.None,
            };
        }
        [Test]
        public void EyeAlone_ReproducesThePlainEyeRatio()
        {
            BasisScaleFitInput input = Baseline(1.40f, 1.45f);
            input.ArmSpan = BasisScaleFitSample.None;

            BasisScaleFitResult fit = BasisScaleFitCore.Solve(in input);

            Assert.AreEqual(BasisScaleFitStatus.EyeExact, fit.Status);
            Assert.AreEqual(1.40f / PlayerEye, fit.Scale, Eps);
            Assert.AreEqual(1, fit.UsedCount);
        }
        [Test]
        public void ArmInsideTheStretcherBudget_LeavesEyeHeightExact()
        {
            // Avatar arms a little longer than the player's, well inside what the stretcher can shorten.
            BasisScaleFitInput input = Baseline(1.60f, 1.70f);
            BasisScaleFitResult fit = BasisScaleFitCore.Solve(in input);

            Assert.AreEqual(BasisScaleFitStatus.EyeExact, fit.Status, "the stretcher can absorb this, so the scale must not move");
            Assert.AreEqual(1f, fit.Scale, Eps);
            Assert.AreEqual(1f, fit.EyeResidual, Eps, "an exact eye match is what costs no floor offset");
            Assert.AreEqual(1.70f / PlayerSpan, fit.ArmResidual, Eps, "the whole arm difference is left for the stretcher");
        }
        [Test]
        public void ArmBeyondTheStretcherBudget_MovesScaleExactlyToTheBudgetEdge()
        {
            const float AvatarSpan = 1.45f;
            BasisScaleFitInput input = Baseline(1.60f, AvatarSpan);
            BasisScaleFitResult fit = BasisScaleFitCore.Solve(in input);

            Assert.AreEqual(BasisScaleFitStatus.Adjusted, fit.Status);
            // The stretcher can stretch the arm-only part (span minus shoulders) by at most the deviation,
            // so the furthest the player's reach may sit from the avatar's is exactly that much.
            float slack = Deviation * (AvatarSpan - AvatarShoulders);
            Assert.AreEqual(AvatarSpan + slack, PlayerSpan * fit.Scale, Eps,"the scale should stop the instant the arm lands inside the stretcher's reach, not go further");
            Assert.Less(fit.Scale, 1f, "the avatar's arms are shorter, so the scale has to come down to meet them");
        }
        [Test]
        public void ScaleAndStretcherCompose_PlayerReachLandsOnTheAvatarHands()
        {
            // The point of the whole exercise: after the scale and then the fit, the player's real reach
            // maps exactly onto the avatar's fitted hands.
            BasisScaleFitInput input = Baseline(1.60f, 1.70f);
            BasisScaleFitResult fit = BasisScaleFitCore.Solve(in input);

            var m = new BasisBodyFitMeasurements
            {
                PlayerEyeHeight = PlayerEye,
                PlayerArmSpan = PlayerSpan,
                AvatarEyeHeight = 1.60f,
                AvatarArmSpan = 1.70f,
                AvatarShoulderWidth = AvatarShoulders,
                AvatarLegSpan = 0.84f,
                AvatarSpineSpan = 0.55f,
                UniformScale = fit.Scale,
            };
            BasisBodyFitResult body = BasisBodyFitCore.Solve(m, Deviation);

            Assert.IsTrue(body.HasArmFit);
            float fittedAvatarSpan = AvatarShoulders + (m.AvatarArmSpan - AvatarShoulders) * body.ArmScale;
            Assert.AreEqual(fittedAvatarSpan, PlayerSpan * fit.Scale, Eps,"reach must match after the fit, or the hands sit somewhere the player's do not");
        }
        [Test]
        public void UnmeasuredArmSpan_IsIgnoredRatherThanSteeringTheScale()
        {
            BasisScaleFitInput input = Baseline(1.60f, 1.45f);
            input.ArmSpan.Weight = 0f; // never measured on this player

            BasisScaleFitResult fit = BasisScaleFitCore.Solve(in input);

            Assert.AreEqual(BasisScaleFitStatus.EyeExact, fit.Status);
            Assert.AreEqual(1f, fit.Scale, Eps);
            Assert.AreEqual(1, fit.UsedCount);
        }
        [Test]
        public void ConflictingSegments_FallBackToTheWeightedMean()
        {
            // Arms demand a small scale, hips demand a large one, with no scale satisfying both.
            BasisScaleFitInput input = Baseline(1.60f, 1.15f);
            input.HipHeight = new BasisScaleFitSample
            {
                Player = 0.90f,
                Avatar = 1.30f,
                Slack = 0.02f,
                Weight = BasisScaleFitCore.HipWeight,
            };

            BasisScaleFitResult fit = BasisScaleFitCore.Solve(in input);

            Assert.AreEqual(BasisScaleFitStatus.Compromised, fit.Status);
            Assert.IsTrue(fit.IsValid, "an impossible avatar still has to produce a usable scale");
            Assert.AreEqual(3, fit.UsedCount);
        }
        [Test]
        public void ScaleNeverDriftsFurtherFromEyeHeightThanTheDeviationCap()
        {
            // Arms far too short for any reachable scale: the cap, not the arms, has to win.
            BasisScaleFitInput input = Baseline(1.60f, 0.95f);
            BasisScaleFitResult fit = BasisScaleFitCore.Solve(in input);
            float eyeRatio = 1.60f / PlayerEye, lowest = eyeRatio / (1f + BasisScaleFitCore.DefaultMaxEyeDeviation);
            Assert.GreaterOrEqual(fit.Scale, lowest - Eps,"past the cap the invented floor offset costs more than the proportion error saved");
            Assert.AreEqual(BasisScaleFitStatus.Compromised, fit.Status);
        }
        [Test]
        public void EveryResidualComposesBackToTheAvatarMeasurement()
        {
            BasisScaleFitInput input = Baseline(1.55f, 1.72f);
            input.HipHeight = new BasisScaleFitSample
            {
                Player = 0.90f,
                Avatar = 0.88f,
                Slack = 0.08f,
                Weight = BasisScaleFitCore.HipWeight,
            };

            BasisScaleFitResult fit = BasisScaleFitCore.Solve(in input);

            Assert.AreEqual(input.Eye.Avatar, input.Eye.Player * fit.Scale * fit.EyeResidual, Eps);
            Assert.AreEqual(input.ArmSpan.Avatar, input.ArmSpan.Player * fit.Scale * fit.ArmResidual, Eps);
            Assert.AreEqual(input.HipHeight.Avatar, input.HipHeight.Player * fit.Scale * fit.HipResidual, Eps);
        }
        [Test]
        public void NothingMeasurable_IsInvalidRatherThanInventingAScale()
        {
            var input = new BasisScaleFitInput { MaxEyeDeviation = BasisScaleFitCore.DefaultMaxEyeDeviation };

            BasisScaleFitResult fit = BasisScaleFitCore.Solve(in input);

            Assert.IsFalse(fit.IsValid);
            Assert.AreEqual(BasisScaleFitStatus.NoData, fit.Status);
            Assert.AreEqual(0, fit.UsedCount);
        }
        [Test]
        public void AbsurdMeasurementPair_IsDiscardedNotFitted()
        {
            BasisScaleFitInput input = Baseline(1.60f, 1.70f);
            input.ArmSpan.Player = 0.20f; // a glitched frame, not a reach

            BasisScaleFitResult fit = BasisScaleFitCore.Solve(in input);

            Assert.AreEqual(1, fit.UsedCount, "the broken pair should not count as a measurement");
            Assert.AreEqual(1f, fit.Scale, Eps);
        }
        [Test]
        public void StretcherDisabled_MakesEverySegmentAHardConstraint()
        {
            // With no slack the arm pins the scale to its own ratio outright.
            var measurements = new BasisBodyFitMeasurements
            {
                AvatarArmSpan = 1.45f,
                AvatarShoulderWidth = AvatarShoulders,
                AvatarLegSpan = 0.84f,
                AvatarSpineSpan = 0.55f,
            };
            BasisScaleFitInput input = Baseline(1.60f, 1.45f);
            input.ArmSpan.Slack = BasisBodyFitCore.ArmSpanSlack(in measurements, 0f);

            BasisScaleFitResult fit = BasisScaleFitCore.Solve(in input);

            Assert.AreEqual(1.45f / PlayerSpan, fit.Scale, Eps);
            Assert.AreEqual(1f, fit.ArmResidual, Eps, "nothing is left over when nothing can absorb it");
        }
    }
    public class BasisBodyFitUniformScaleTests
    {
        const float Eps = 1e-4f;
        static BasisBodyFitMeasurements Baseline() => new BasisBodyFitMeasurements
        {
            PlayerEyeHeight = 1.60f,
            PlayerArmSpan = 1.65f,
            PlayerHipHeight = 0.90f,
            AvatarEyeHeight = 1.40f,
            AvatarArmSpan = 1.50f,
            AvatarHipHeight = 0.80f,
            AvatarLegSpan = 0.74f,
            AvatarSpineSpan = 0.48f,
            AvatarShoulderWidth = 0.32f,
        };
        [Test]
        public void UnsetUniformScale_StillUsesTheEyeRatio()
        {
            BasisBodyFitMeasurements m = Baseline();
            m.UniformScale = 0f;

            BasisBodyFitResult withDefault = BasisBodyFitCore.Solve(m, 0.15f);

            m.UniformScale = m.AvatarEyeHeight / m.PlayerEyeHeight;
            BasisBodyFitResult withExplicit = BasisBodyFitCore.Solve(m, 0.15f);

            Assert.AreEqual(withExplicit.ArmScale, withDefault.ArmScale, Eps);
            Assert.AreEqual(withExplicit.LegScale, withDefault.LegScale, Eps);
            Assert.AreEqual(withExplicit.TorsoScale, withDefault.TorsoScale, Eps);
        }
        [Test]
        public void UniformScale_IsWhatTheResidualIsMeasuredAgainst()
        {
            BasisBodyFitMeasurements m = Baseline();
            m.UniformScale = 0.80f; // deliberately not the eye ratio (0.875)

            BasisBodyFitResult fit = BasisBodyFitCore.Solve(m, 0.5f);

            Assert.IsTrue(fit.HasArmFit);
            float avatarArm = (m.AvatarArmSpan - m.AvatarShoulderWidth) * 0.5f;
            float playerArm = (m.PlayerArmSpan * m.UniformScale - m.AvatarShoulderWidth) * 0.5f;
            Assert.AreEqual(playerArm / avatarArm, fit.ArmScale, Eps);
        }
        [Test]
        public void AScaleThatAlreadyMatchesTheArms_LeavesNothingToStretch()
        {
            BasisBodyFitMeasurements m = Baseline();
            m.UniformScale = m.AvatarArmSpan / m.PlayerArmSpan;

            BasisBodyFitResult fit = BasisBodyFitCore.Solve(m, 0.15f);

            Assert.IsTrue(fit.HasArmFit);
            Assert.AreEqual(1f, fit.ArmScale, Eps);
        }
    }
}
