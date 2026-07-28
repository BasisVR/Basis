using NUnit.Framework;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    public class BasisBodyFitCoreTests
    {
        const float Eps = 1e-4f;

        static BasisBodyFitMeasurements Baseline()
        {
            return new BasisBodyFitMeasurements
            {
                PlayerEyeHeight = 1.60f,
                PlayerArmSpan = 1.65f,
                PlayerHipHeight = 0.90f,
                AvatarEyeHeight = 1.60f,
                AvatarArmSpan = 1.65f,
                AvatarHipHeight = 0.90f,
                AvatarLegSpan = 0.84f,
                AvatarSpineSpan = 0.55f,
                AvatarShoulderWidth = 0.35f,
            };
        }

        [Test]
        public void MatchingProportions_IsExactIdentity()
        {
            BasisBodyFitResult fit = BasisBodyFitCore.Solve(Baseline(), 0.15f);

            Assert.AreEqual(1f, fit.ArmScale, Eps);
            Assert.AreEqual(1f, fit.LegScale, Eps);
            Assert.AreEqual(1f, fit.TorsoScale, Eps);
            Assert.IsTrue(fit.IsIdentity);
        }

        [Test]
        public void ZeroDeviation_IsExactIdentity()
        {
            BasisBodyFitMeasurements m = Baseline();
            m.PlayerArmSpan = 1.80f;
            m.PlayerHipHeight = 0.98f;

            BasisBodyFitResult fit = BasisBodyFitCore.Solve(m, 0f);

            Assert.IsTrue(fit.IsIdentity);
            Assert.IsFalse(fit.HasArmFit);
            Assert.IsFalse(fit.HasBodyFit);
        }

        [Test]
        public void LongApeIndex_StretchesArms()
        {
            BasisBodyFitMeasurements m = Baseline();
            m.PlayerArmSpan = 1.75f;

            BasisBodyFitResult fit = BasisBodyFitCore.Solve(m, 0.15f);

            Assert.IsTrue(fit.HasArmFit);
            Assert.Greater(fit.ArmScale, 1f);

            float avatarArm = (m.AvatarArmSpan - m.AvatarShoulderWidth) * 0.5f;
            float playerArm = (m.PlayerArmSpan - m.AvatarShoulderWidth) * 0.5f;
            Assert.AreEqual(playerArm / avatarArm, fit.ArmScale, Eps);
        }

        [Test]
        public void ShortArms_CollapseArms()
        {
            BasisBodyFitMeasurements m = Baseline();
            m.PlayerArmSpan = 1.55f;

            BasisBodyFitResult fit = BasisBodyFitCore.Solve(m, 0.15f);

            Assert.IsTrue(fit.HasArmFit);
            Assert.Less(fit.ArmScale, 1f);
        }

        [Test]
        public void ArmScale_NeverExceedsDeviation()
        {
            BasisBodyFitMeasurements m = Baseline();
            m.PlayerArmSpan = 2.20f;

            BasisBodyFitResult fit = BasisBodyFitCore.Solve(m, 0.15f);

            Assert.AreEqual(1.15f, fit.ArmScale, Eps);
        }

        [Test]
        public void ArmFit_UsesPlayerScaledIntoAvatarSpace()
        {
            BasisBodyFitMeasurements m = Baseline();
            m.PlayerEyeHeight = 1.80f;
            m.PlayerArmSpan = 1.65f * (1.80f / 1.60f);

            BasisBodyFitResult fit = BasisBodyFitCore.Solve(m, 0.15f);

            Assert.AreEqual(1f, fit.ArmScale, Eps);
        }

        [Test]
        public void BodyFit_LongLegsRaiseHipsAndShortenTorso()
        {
            BasisBodyFitMeasurements m = Baseline();
            m.PlayerHipHeight = 0.96f;

            BasisBodyFitResult fit = BasisBodyFitCore.Solve(m, 0.15f);

            Assert.IsTrue(fit.HasBodyFit);
            Assert.Greater(fit.LegScale, 1f);
            Assert.Less(fit.TorsoScale, 1f);
        }

        [Test]
        public void BodyFit_ShortLegsLowerHipsAndLengthenTorso()
        {
            BasisBodyFitMeasurements m = Baseline();
            m.PlayerHipHeight = 0.84f;

            BasisBodyFitResult fit = BasisBodyFitCore.Solve(m, 0.15f);

            Assert.IsTrue(fit.HasBodyFit);
            Assert.Less(fit.LegScale, 1f);
            Assert.Greater(fit.TorsoScale, 1f);
        }

        [Test]
        public void BodyFit_IsHeightNeutral()
        {
            for (float hip = 0.70f; hip <= 1.10f; hip += 0.02f)
            {
                BasisBodyFitMeasurements m = Baseline();
                m.PlayerHipHeight = hip;

                BasisBodyFitResult fit = BasisBodyFitCore.Solve(m, 0.15f);

                float legDelta = (fit.LegScale - 1f) * m.AvatarLegSpan;
                float spineDelta = (fit.TorsoScale - 1f) * m.AvatarSpineSpan;

                Assert.AreEqual(0f, legDelta + spineDelta, Eps,
                    $"hip {hip:F2} shifted total standing height by {legDelta + spineDelta:F6} m");
            }
        }

        [Test]
        public void BodyFit_HipsLandOnTargetInsideTheBudget()
        {
            BasisBodyFitMeasurements m = Baseline();
            m.PlayerHipHeight = 0.94f;

            BasisBodyFitResult fit = BasisBodyFitCore.Solve(m, 0.15f);

            float fittedHip = m.AvatarHipHeight + (fit.LegScale - 1f) * m.AvatarLegSpan;
            Assert.AreEqual(m.PlayerHipHeight, fittedHip, Eps);
        }

        [Test]
        public void BodyFit_NeitherSegmentExceedsDeviation()
        {
            for (float hip = 0.50f; hip <= 1.40f; hip += 0.01f)
            {
                BasisBodyFitMeasurements m = Baseline();
                m.PlayerHipHeight = hip;

                BasisBodyFitResult fit = BasisBodyFitCore.Solve(m, 0.15f);

                Assert.LessOrEqual(Mathf.Abs(fit.LegScale - 1f), 0.15f + Eps, $"leg at hip {hip:F2}");
                Assert.LessOrEqual(Mathf.Abs(fit.TorsoScale - 1f), 0.15f + Eps, $"torso at hip {hip:F2}");
            }
        }

        [Test]
        public void NoHipsTracker_LeavesBodyUnfittedButStillFitsArms()
        {
            BasisBodyFitMeasurements m = Baseline();
            m.PlayerHipHeight = 0f;
            m.PlayerArmSpan = 1.75f;

            BasisBodyFitResult fit = BasisBodyFitCore.Solve(m, 0.15f);

            Assert.IsTrue(fit.HasArmFit);
            Assert.IsFalse(fit.HasBodyFit);
            Assert.AreEqual(1f, fit.LegScale, Eps);
            Assert.AreEqual(1f, fit.TorsoScale, Eps);
        }

        [Test]
        public void NonsenseMeasurements_StayInert()
        {
            BasisBodyFitMeasurements m = Baseline();
            m.PlayerEyeHeight = 0f;
            Assert.IsTrue(BasisBodyFitCore.Solve(m, 0.15f).IsIdentity);

            m = Baseline();
            m.PlayerArmSpan = float.NaN;
            Assert.AreEqual(1f, BasisBodyFitCore.Solve(m, 0.15f).ArmScale, Eps);

            m = Baseline();
            m.PlayerHipHeight = 0.20f;
            Assert.IsFalse(BasisBodyFitCore.Solve(m, 0.15f).HasBodyFit);

            m = Baseline();
            m.AvatarShoulderWidth = 2f;
            Assert.IsFalse(BasisBodyFitCore.Solve(m, 0.15f).HasArmFit);
        }

        [Test]
        public void EveryRefusalReportsItsOwnReason()
        {
            BasisBodyFitMeasurements m = Baseline();
            Assert.AreEqual(BasisBodyFitStatus.Disabled, BasisBodyFitCore.Solve(m, 0f).ArmStatus);

            m = Baseline();
            m.PlayerEyeHeight = 0f;
            Assert.AreEqual(BasisBodyFitStatus.PlayerEyeHeightMissing, BasisBodyFitCore.Solve(m, 0.15f).ArmStatus);

            m = Baseline();
            m.AvatarEyeHeight = 0f;
            Assert.AreEqual(BasisBodyFitStatus.AvatarEyeHeightMissing, BasisBodyFitCore.Solve(m, 0.15f).BodyStatus);

            m = Baseline();
            m.PlayerArmSpan = 0f;
            Assert.AreEqual(BasisBodyFitStatus.PlayerArmSpanMissing, BasisBodyFitCore.Solve(m, 0.15f).ArmStatus);

            m = Baseline();
            m.AvatarArmSpan = 0f;
            Assert.AreEqual(BasisBodyFitStatus.AvatarArmSpanMissing, BasisBodyFitCore.Solve(m, 0.15f).ArmStatus);

            m = Baseline();
            m.AvatarShoulderWidth = 2f;
            Assert.AreEqual(BasisBodyFitStatus.ArmLengthDegenerate, BasisBodyFitCore.Solve(m, 0.15f).ArmStatus);

            m = Baseline();
            m.PlayerArmSpan = 3.5f;
            Assert.AreEqual(BasisBodyFitStatus.ArmRatioOutOfBand, BasisBodyFitCore.Solve(m, 0.15f).ArmStatus);

            m = Baseline();
            m.PlayerHipHeight = 0f;
            Assert.AreEqual(BasisBodyFitStatus.HipsTrackerMissing, BasisBodyFitCore.Solve(m, 0.15f).BodyStatus);

            m = Baseline();
            m.AvatarHipHeight = 0f;
            Assert.AreEqual(BasisBodyFitStatus.AvatarHipHeightMissing, BasisBodyFitCore.Solve(m, 0.15f).BodyStatus);

            m = Baseline();
            m.AvatarLegSpan = 0f;
            Assert.AreEqual(BasisBodyFitStatus.AvatarLegSpanDegenerate, BasisBodyFitCore.Solve(m, 0.15f).BodyStatus);

            m = Baseline();
            m.AvatarSpineSpan = 0f;
            Assert.AreEqual(BasisBodyFitStatus.AvatarSpineSpanDegenerate, BasisBodyFitCore.Solve(m, 0.15f).BodyStatus);

            m = Baseline();
            m.PlayerHipHeight = 1.55f;
            Assert.AreEqual(BasisBodyFitStatus.HipHeightImplausible, BasisBodyFitCore.Solve(m, 0.15f).BodyStatus);

            m = Baseline();
            m.PlayerHipHeight = 0.30f;
            Assert.AreEqual(BasisBodyFitStatus.HipHeightImplausible, BasisBodyFitCore.Solve(m, 0.15f).BodyStatus);

            m = Baseline();
            m.PlayerHipHeight = 0.95f;
            m.AvatarHipHeight = 0.40f;
            Assert.AreEqual(BasisBodyFitStatus.HipRatioOutOfBand, BasisBodyFitCore.Solve(m, 0.15f).BodyStatus);
        }

        [Test]
        public void RepeatedCalibrationsDoNotDrift()
        {
            BasisBodyFitMeasurements m = Baseline();
            m.PlayerArmSpan = 1.74f;
            m.PlayerHipHeight = 0.955f;

            BasisBodyFitResult first = BasisBodyFitCore.Solve(m, 0.15f);

            for (int pass = 0; pass < 12; pass++)
            {
                BasisBodyFitResult again = BasisBodyFitCore.Solve(m, 0.15f);
                Assert.AreEqual(first.ArmScale, again.ArmScale, 0f, $"arm scale moved on pass {pass}");
                Assert.AreEqual(first.LegScale, again.LegScale, 0f, $"leg scale moved on pass {pass}");
                Assert.AreEqual(first.TorsoScale, again.TorsoScale, 0f, $"torso scale moved on pass {pass}");
            }
        }

        [Test]
        public void FeedingBackFittedAvatarMeasurementsWouldDrift_SoTheyMustStayAuthored()
        {
            // Guards the rule the applier relies on: the avatar terms must always be the authored
            // proportions. If a later calibration ever re-measured the already-fitted avatar, the fit
            // collapses to a no-op and the body snaps back -- the "multiple calibrations make it worse"
            // failure. Pinned here so a future change that starts measuring live bones fails loudly.
            BasisBodyFitMeasurements authored = Baseline();
            authored.PlayerArmSpan = 1.74f;

            BasisBodyFitResult correct = BasisBodyFitCore.Solve(authored, 0.15f);
            Assert.Greater(correct.ArmScale, 1f);

            BasisBodyFitMeasurements remeasured = authored;
            remeasured.AvatarArmSpan =
                authored.AvatarShoulderWidth +
                (authored.AvatarArmSpan - authored.AvatarShoulderWidth) * correct.ArmScale;

            BasisBodyFitResult drifted = BasisBodyFitCore.Solve(remeasured, 0.15f);
            Assert.AreEqual(1f, drifted.ArmScale, 1e-3f,
                "re-measuring a fitted avatar yields a no-op scale, which is why avatar terms must come from the load-time snapshot");
        }

        [Test]
        public void EveryStatusHasAHumanReadableDescription()
        {
            foreach (BasisBodyFitStatus status in System.Enum.GetValues(typeof(BasisBodyFitStatus)))
            {
                string text = BasisBodyFitCore.Describe(status);
                Assert.IsNotEmpty(text, $"{status} has no description");
                Assert.AreNotEqual("unknown", text, $"{status} fell through to the unknown case");
            }
        }
    }
}
