using Basis.IK;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using NUnit.Framework;
using UnityEngine;
namespace Basis.Tests.Fitting
{
    public class BasisFitResolverTests
    {
        static BasisScaleInputs Inputs(BasisSelectedHeightMode mode, float playerEye, float playerSpan, float avatarEye, float avatarSpan, float shoulders = 0.35f, float k = 1f, float deviation = BasisBodyFitCore.DefaultMaxDeviation)
        {
            return new BasisScaleInputs { Mode = mode, PlayerEye = playerEye, PlayerSpan = playerSpan, AvatarEye = avatarEye, AvatarSpan = avatarSpan, AvatarShoulderWidth = shoulders, AppliedUpScale = k, FitDeviation = deviation, SpanUsable = true };
        }
        static float StandingEyeInAvatar(in BasisScaleInputs i, in BasisScaleState s) => ((i.PlayerTrackedEye > 0f ? i.PlayerTrackedEye : i.PlayerEye) + i.PlayerEyeOffset + s.GroundingOffset) * s.DeviceScale;
        static BasisBodyFitMeasurements Body(float playerEye, float playerSpan, float avatarEye, float avatarSpan, float uniformScale, float shoulders = 0.35f)
        {
            return new BasisBodyFitMeasurements
            {
                PlayerEyeHeight = playerEye,
                PlayerArmSpan = playerSpan,
                AvatarEyeHeight = avatarEye,
                AvatarArmSpan = avatarSpan,
                AvatarShoulderWidth = shoulders,
                UniformScale = uniformScale,
            };
        }
        [Test]
        public void BestFitStaysNearTheEyeMatchForTinyAndGiantAvatars([Values(0.25f, 0.42f, 2.6f, 4f)] float eyeRatio, [Values(0.7f, 1.3f)] float avatarArmRatio)
        {
            BasisScaleInputs inputs = Inputs(BasisSelectedHeightMode.BestFit, 1.65f, 1.45f, 1.65f * eyeRatio, 1.45f * eyeRatio * avatarArmRatio, 0.35f * eyeRatio);
            BasisScaleState state = BasisScaleResolver.Resolve(inputs);
            Assert.That(state.BestFit.IsValid, Is.True);
            Assert.That(state.UniformScale / eyeRatio, Is.InRange(1f / 1.05f - 1e-4f, 1.05f + 1e-4f), "BestFit may only trade 5% of eye match for arms, whatever the avatar's size");
        }
        [Test]
        public void EveryModeLandsTheStandingEyeOnTheAvatarEye([Values(BasisSelectedHeightMode.EyeHeight, BasisSelectedHeightMode.ArmSpan, BasisSelectedHeightMode.BestFit, BasisSelectedHeightMode.Auto)] BasisSelectedHeightMode mode, [Values(0.6f, 1f, 1.4f)] float k, [Values(0.7f, 1f, 1.3f)] float avatarArmRatio, [Values(0f, 0.02f)] float eyeOffset)
        {
            var inputs = Inputs(mode, 1.62f, 1.45f, 1.55f, 1.45f * (1.55f / 1.62f) * avatarArmRatio, k: k);
            inputs.PlayerEyeOffset = eyeOffset;
            BasisScaleState state = BasisScaleResolver.Resolve(inputs);
            Assert.That(StandingEyeInAvatar(inputs, state), Is.EqualTo(1.55f * k).Within(1e-4f), $"{mode} k {k} arms {avatarArmRatio}: the camera must sit on the avatar's eye, never above its head");
        }
        [Test]
        public void EyeModeNeverMovesThePlaySpace()
        {
            var inputs = Inputs(BasisSelectedHeightMode.EyeHeight, 1.62f, 1.2f, 1.3f, 1.6f);
            BasisScaleState state = BasisScaleResolver.Resolve(inputs);
            Assert.That(state.GroundingOffset, Is.EqualTo(0f));
            Assert.That(state.UniformScale, Is.EqualTo(1.3f / 1.62f).Within(1e-6f));
        }
        [Test]
        public void BestFitStaysOnTheEyeWhileTheArmFitCanAbsorbTheMismatch()
        {
            float eyeRatio = 1.5f / 1.62f;
            var inputs = Inputs(BasisSelectedHeightMode.BestFit, 1.62f, 1.45f, 1.5f, 1.45f * eyeRatio * 1.08f);
            BasisScaleState state = BasisScaleResolver.Resolve(inputs);
            Assert.That(state.BestFit.Status, Is.EqualTo(BasisScaleFitStatus.EyeExact));
            Assert.That(state.UniformScale, Is.EqualTo(eyeRatio).Within(1e-5f));
            Assert.That(state.GroundingOffset, Is.EqualTo(0f));
        }
        [Test]
        public void BestFitMovesAtMostTheWindowForArmsTheFitCannotReach([Values(0.55f, 1.6f)] float avatarArmRatio)
        {
            float eyeRatio = 1.5f / 1.62f;
            var inputs = Inputs(BasisSelectedHeightMode.BestFit, 1.62f, 1.45f, 1.5f, 0.35f + (1.45f * eyeRatio - 0.35f) * avatarArmRatio);
            BasisScaleState state = BasisScaleResolver.Resolve(inputs);
            float move = state.UniformScale / eyeRatio;
            Assert.That(move, Is.InRange(1f / (1f + BasisScaleFitCore.DefaultMaxEyeDeviation) - 1e-4f, 1f + BasisScaleFitCore.DefaultMaxEyeDeviation + 1e-4f));
            Assert.That(Mathf.Abs(move - 1f), Is.GreaterThan(0.01f), "an unreachable arm mismatch should still use the window");
            Assert.That(Mathf.Sign(state.GroundingOffset), Is.EqualTo(avatarArmRatio < 1f ? 1f : -1f), "short avatar arms lift the play space, long ones lower it");
        }
        [Test]
        public void ArmSpanModeMatchesTheAuthoredSpanAndLeavesTheArmsUnstretched()
        {
            var inputs = Inputs(BasisSelectedHeightMode.ArmSpan, 1.62f, 1.45f, 1.5f, 1.2f);
            BasisScaleState state = BasisScaleResolver.Resolve(inputs);
            Assert.That(state.UniformScale, Is.EqualTo(1.2f / 1.45f).Within(1e-6f));
            BasisBodyFitResult fit = BasisBodyFitCore.Solve(Body(1.62f, 1.45f, 1.5f, 1.2f, state.UniformScale), BasisBodyFitCore.DefaultMaxDeviation);
            Assert.That(fit.HasArmFit, Is.True);
            Assert.That(fit.ArmScale, Is.EqualTo(1f).Within(1e-5f));
        }
        [Test]
        public void SeatedAndDesktopNeverOffsetThePlaySpace()
        {
            var seated = Inputs(BasisSelectedHeightMode.ArmSpan, 1.62f, 1.45f, 1.5f, 1.2f);
            seated.Seated = true;
            Assert.That(BasisScaleResolver.Resolve(seated).GroundingOffset, Is.EqualTo(0f));
            var seatedBestFit = Inputs(BasisSelectedHeightMode.BestFit, 1.62f, 1.45f, 1.5f, 0.8f);
            seatedBestFit.Seated = true;
            Assert.That(BasisScaleResolver.Resolve(seatedBestFit).UniformScale, Is.EqualTo(1.5f / 1.62f).Within(1e-6f));
            var desktop = Inputs(BasisSelectedHeightMode.ArmSpan, 1.62f, 1.45f, 1.5f, 1.2f);
            desktop.Desktop = true;
            BasisScaleState state = BasisScaleResolver.Resolve(desktop);
            Assert.That(state.Mode, Is.EqualTo(BasisSelectedHeightMode.EyeHeight));
            Assert.That(state.GroundingOffset, Is.EqualTo(0f));
        }
        [Test]
        public void TheReportedSetupNoLongerSquashesTheBodyByThirteenPercent()
        {
            var inputs = Inputs(BasisSelectedHeightMode.BestFit, 1.544f, 1.454f, 1.604f, 1.272f, 0.278f);
            BasisScaleState state = BasisScaleResolver.Resolve(inputs);
            float eyeRatio = 1.604f / 1.544f;
            Assert.That(state.UniformScale / eyeRatio, Is.GreaterThanOrEqualTo(1f / 1.05f - 1e-4f));
            Assert.That(StandingEyeInAvatar(inputs, state), Is.EqualTo(1.604f).Within(1e-4f));
            BasisBodyFitResult fit = BasisBodyFitCore.Solve(Body(1.544f, 1.454f, 1.604f, 1.272f, state.UniformScale), BasisBodyFitCore.DefaultMaxDeviation);
            Assert.That(fit.ArmScale, Is.EqualTo(1f + BasisBodyFitCore.MaxDeviationCeiling).Within(1e-5f), "arms 18% shorter than the player's reach are lengthened by the full 10% and no more");
            float playerArm = (1.454f * state.UniformScale - 0.35f) * 0.5f, avatarArm = (1.272f - 0.35f) * 0.5f * fit.ArmScale;
            Assert.That(playerArm - avatarArm, Is.LessThan(0.04f), "past the 10% the hand stops short at full stretch instead of the avatar being reshaped further");
        }
        [Test]
        public void TheArmsAreNeverResizedMoreThanTenPercent([Values(0.55f, 0.8f, 1.25f, 1.9f)] float playerToAvatarArm, [Values(0.15f, 0.5f)] float requestedDeviation)
        {
            float u = 1.55f / 1.62f, avatarSpan = 0.35f + (1.45f * u - 0.35f) / playerToAvatarArm;
            BasisBodyFitResult fit = BasisBodyFitCore.Solve(Body(1.62f, 1.45f, 1.55f, avatarSpan, u), requestedDeviation);
            Assert.That(fit.HasArmFit, Is.True);
            Assert.That(Mathf.Abs(fit.ArmScale - 1f), Is.LessThanOrEqualTo(0.1f + 1e-5f), $"player arm x{playerToAvatarArm} of the avatar's, asked for {requestedDeviation:P0}");
            Assert.That(Mathf.Abs(fit.ArmScale - 1f), Is.EqualTo(0.1f).Within(1e-5f), "every case here is past the cap");
        }
        [Test]
        public void AnImpossibleEyeHeightIsSizedFromTheReach()
        {
            float fromReach = 1.479f / BasisCalibrationMath.SpanToHeightRatio * BasisCalibrationMath.EyeToHeightRatio;
            Assert.That(BasisCalibrationMath.SizingEyeHeight(2.118f, 1.479f, true, 0f), Is.EqualTo(fromReach).Within(1e-4f), "a 2.12 m standing eye with a 1.48 m reach is a VR floor set too low");
            Assert.That(BasisCalibrationMath.SizingEyeHeight(2.118f, 1.479f, true, 1.62f), Is.EqualTo(1.62f), "a stated height outranks the reach");
            Assert.That(BasisCalibrationMath.SizingEyeHeight(2.118f, 1.479f, false, 0f), Is.EqualTo(2.118f), "without a measured reach there is nothing better to size from");
            Assert.That(BasisCalibrationMath.SizingEyeHeight(1.75f, 1.0f, true, 0f), Is.EqualTo(1.75f), "a believable eye height is never shrunk to match a short reach, which may just be bent arms");
            Assert.That(BasisCalibrationMath.SizingEyeHeight(1.95f, 1.85f, true, 0f), Is.EqualTo(1.95f), "a tall player whose reach agrees is left alone");
        }
        [Test]
        public void ALowVrFloorStillLandsTheEyeAndPutsTheFloorBack([Values(BasisSelectedHeightMode.EyeHeight, BasisSelectedHeightMode.BestFit, BasisSelectedHeightMode.ArmSpan)] BasisSelectedHeightMode mode, [Values(1f, 1.4f)] float k)
        {
            float trueEye = 1.6f, span = trueEye / BasisCalibrationMath.EyeToHeightRatio * BasisCalibrationMath.SpanToHeightRatio, floorError = 0.518f;
            var level = Inputs(mode, trueEye, span, 1.592f, 1.323f, 0.3f, k);
            var low = level;
            low.PlayerTrackedEye = trueEye + floorError;
            low.PlayerEye = BasisCalibrationMath.SizingEyeHeight(low.PlayerTrackedEye, span, true, 0f);
            BasisScaleState levelState = BasisScaleResolver.Resolve(level), lowState = BasisScaleResolver.Resolve(low);
            Assert.That(low.PlayerEye, Is.EqualTo(trueEye).Within(1e-4f));
            Assert.That(lowState.UniformScale, Is.EqualTo(levelState.UniformScale).Within(1e-4f), "a floor error must not change how big the player is");
            Assert.That(StandingEyeInAvatar(low, lowState), Is.EqualTo(1.592f * k).Within(1e-4f), "the camera still sits on the avatar's eye");
            float realFloor = (floorError + lowState.GroundingOffset) * lowState.DeviceScale, levelFloor = levelState.GroundingOffset * levelState.DeviceScale;
            Assert.That(realFloor, Is.EqualTo(levelFloor).Within(1e-4f), "the real floor lands where it would with the VR floor set right");
        }
        [Test]
        public void TheReportedVrSessionIsSizedFromTheReach()
        {
            float measuredEye = 2.118261f, span = 1.479412f, avatarEye = 1.592411f, avatarSpan = 1.322879f, shoulders = 0.3f;
            var inputs = Inputs(BasisSelectedHeightMode.BestFit, BasisCalibrationMath.SizingEyeHeight(measuredEye, span, true, 0f), span, avatarEye, avatarSpan, shoulders);
            inputs.PlayerTrackedEye = measuredEye;
            BasisScaleState state = BasisScaleResolver.Resolve(inputs);
            Assert.That(state.DeviceScale, Is.GreaterThan(0.9f), "was 0.75, which shrank every reach by a quarter");
            Assert.That(StandingEyeInAvatar(inputs, state), Is.EqualTo(avatarEye).Within(1e-4f));
            Assert.That(state.GroundingOffset, Is.LessThan(-0.4f), "the play space comes down by the floor error");
            BasisBodyFitResult fit = BasisBodyFitCore.Solve(Body(inputs.PlayerEye, span, avatarEye, avatarSpan, state.UniformScale, shoulders), BasisBodyFitCore.DefaultMaxDeviation);
            Assert.That(Mathf.Abs(fit.ArmScale - 1f), Is.LessThanOrEqualTo(BasisBodyFitCore.MaxDeviationCeiling + 1e-5f));
            float playerArm = (span * state.UniformScale - shoulders) * 0.5f, avatarArm = (avatarSpan - shoulders) * 0.5f * fit.ArmScale;
            Assert.That(Mathf.Abs(playerArm - avatarArm), Is.LessThan(0.005f), "in a T-pose the avatar's hands land in the controllers");
        }
        [Test]
        public void TorsoFitNeverTouchesTheNeckOrHead()
        {
            var fit = new BasisBodyFitResult { ArmScale = 1f, LegScale = 0.9f, TorsoScale = 1.12f, ArmStatus = BasisBodyFitStatus.Disabled, BodyStatus = BasisBodyFitStatus.Fitted };
            float[] scales = new float[BasisBodyFitApply.BoneCount];
            BasisBodyFitApply.CollectScales(in fit, scales);
            Assert.That(scales[12], Is.EqualTo(1.12f));
            Assert.That(scales[13], Is.EqualTo(1.12f));
            Assert.That(scales[14], Is.EqualTo(1.12f));
            Assert.That(scales[15], Is.EqualTo(1f), "neck");
            Assert.That(scales[16], Is.EqualTo(1f), "head");
        }
        [Test]
        public void TheFitOnlyEverResizesArms([Values(BasisSelectedHeightMode.EyeHeight, BasisSelectedHeightMode.ArmSpan, BasisSelectedHeightMode.BestFit)] BasisSelectedHeightMode mode, [Values(0.7f, 1f, 1.3f)] float avatarArmRatio)
        {
            var inputs = Inputs(mode, 1.62f, 1.45f, 1.55f, 1.45f * (1.55f / 1.62f) * avatarArmRatio);
            BasisScaleState state = BasisScaleResolver.Resolve(inputs);
            BasisBodyFitResult fit = BasisBodyFitCore.Solve(Body(1.62f, 1.45f, 1.55f, inputs.AvatarSpan, state.UniformScale), BasisBodyFitCore.DefaultMaxDeviation);
            Assert.That(fit.HasBodyFit, Is.False, "standing, the hips and head stay where the avatar has them");
            Assert.That(fit.LegScale, Is.EqualTo(1f));
            Assert.That(fit.TorsoScale, Is.EqualTo(1f));
        }
        [Test]
        public void ShouldersHangingFromTheChestKeepTheUpperChestOutOfTheTorsoFit()
        {
            var root = new GameObject("TorsoTopRoot");
            try
            {
                Transform hips = new GameObject("Hips").transform, spine = new GameObject("Spine").transform, chest = new GameObject("Chest").transform, upper = new GameObject("UpperChest").transform, shoulder = new GameObject("LeftShoulder").transform;
                hips.SetParent(root.transform); spine.SetParent(hips); chest.SetParent(spine); upper.SetParent(chest); shoulder.SetParent(chest);
                var mapping = new BasisTransformMapping { Hips = hips, spine = spine, chest = chest, Upperchest = upper, leftShoulder = shoulder };
                Assert.That(BasisBodyFitApply.TorsoTop(mapping), Is.EqualTo(chest));
                Transform[] bones = new Transform[BasisBodyFitApply.BoneCount];
                BasisBodyFitApply.CollectBones(mapping, bones);
                Assert.That(bones[12], Is.EqualTo(spine));
                Assert.That(bones[13], Is.EqualTo(chest));
                Assert.That(bones[14], Is.Null);
                shoulder.SetParent(upper);
                Assert.That(BasisBodyFitApply.TorsoTop(mapping), Is.EqualTo(upper));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
        [Test]
        public void WristSpanHeuristicsAcceptAnOrdinaryReach()
        {
            Assert.That(BasisCalibrationMath.ArmSpanLooksUnderMeasured(1.64f, 1.45f), Is.False);
            Assert.That(BasisCalibrationMath.ArmSpanLooksUnderMeasured(1.64f, 1.38f), Is.False, "an average wrist-to-wrist span is not a bent-arm reading");
            Assert.That(BasisCalibrationMath.ArmSpanLooksUnderMeasured(1.64f, 1.05f), Is.True);
            Assert.That(BasisCalibrationMath.AutoHeightModePicksArmSpan(1.64f, 1.45f), Is.False);
        }
        [Test]
        public void GroundingOffsetIsSigned()
        {
            Assert.That(BasisCalibrationMath.HeightModeGroundingOffset(1.6f, 1f, 1.6f / 1.5f, 1.6f), Is.LessThan(0f));
            Assert.That(BasisCalibrationMath.HeightModeGroundingOffset(1.6f, 1f, 1.6f / 1.7f, 1.6f), Is.GreaterThan(0f));
            Assert.That(BasisCalibrationMath.HeightModeGroundingOffset(1.6f, 1f, 1f, 1.6f), Is.EqualTo(0f));
        }
    }
}
