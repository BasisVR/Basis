using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.IK
{
    /// <summary>
    /// Regression tests for the calibration "feel" math (<see cref="BasisCalibrationMath.ComputeDeviceScale"/>),
    /// the formula <see cref="BasisHeightDriver.ChooseHeightToUse"/> runs to turn a measured eye height into the
    /// DeviceScale the camera is scaled by. The avatar feels right iff the DeviceScale denominator equals the
    /// player's TRUE standing eye height; these pin what happens when it doesn't -- the cause of the report that
    /// people must nudge once or twice to stop feeling too tall.
    ///
    /// Backends measure the denominator differently: OpenXR binds the head to centerEyePosition so the tracked
    /// point IS the eye (eyeReference 0), while OpenVR tracks the HMD pose origin sitting a gap g below the eyes
    /// and bridges it with CenterEyeVerticalOffset. If that bridge under-reports the gap (or is 0 because the
    /// SteamVR eye transform wasn't ready at capture), the denominator is short and the avatar renders too tall.
    /// </summary>
    public class BasisCalibrationHeightTests
    {
        // Real-world standing eye heights, avatar authored eye heights, and custom avatar scales to sweep.
        static readonly float[] StandingEye = { 1.40f, 1.55f, 1.61f, 1.75f, 1.90f };
        static readonly float[] AvatarEye = { 1.30f, 1.50f, 1.61f, 1.72f };
        static readonly float[] UpScale = { 0.80f, 1.00f, 1.50f };

        /// <summary>The viewpoint the camera renders at: real standing eye height scaled by DeviceScale
        /// (BasisLocalCameraDriver sets camera.localScale = DeviceScale).</summary>
        static float RenderedViewpoint(float trueStandingEye, float deviceScale) => trueStandingEye * deviceScale;

        [Test]
        public void CorrectDenominator_ViewpointLandsExactlyAtAvatarEye()
        {
            foreach (float E in StandingEye)
                foreach (float A in AvatarEye)
                    foreach (float u in UpScale)
                    {
                        // Denominator measured perfectly (== true standing eye): player height E, no reference
                        // gap, no nudge. This is the well-formed calibration the formula assumes.
                        float deviceScale = BasisCalibrationMath.ComputeDeviceScale(A, u, E, 0f, 0f);
                        float avatarRenderedEye = A * u;
                        Assert.That(RenderedViewpoint(E, deviceScale), Is.EqualTo(avatarRenderedEye).Within(1e-4f),
                            $"E={E} A={A} u={u}: a correctly measured eye height must land the viewpoint on the avatar's eye.");
                    }
        }

        [Test]
        public void OpenXrTrackedPointIsEye_NoNudgeNeeded()
        {
            // OpenXR: tracked point is centerEyePosition, so the measured height already IS the standing eye
            // and eyeReference is 0. No systematic bias, no nudge.
            foreach (float E in StandingEye)
                foreach (float A in AvatarEye)
                {
                    float deviceScale = BasisCalibrationMath.ComputeDeviceScale(A, 1f, E, 0f, 0f);
                    Assert.That(RenderedViewpoint(E, deviceScale), Is.EqualTo(A).Within(1e-4f),
                        $"E={E} A={A}: the eye-tracked backend must be unbiased without a nudge.");
                }
        }

        [Test]
        public void UnderBridgedEyeReference_RendersTooTall_ByTheShortfallRatio()
        {
            // OpenVR: the HMD pose origin sits a gap g below the eyes; the captured CenterEyeVerticalOffset o
            // should equal g but under-reports it (o < g, or 0 when SteamVR's eye transform isn't ready). The
            // denominator is then short by (g - o), so DeviceScale is too high and the viewpoint too tall.
            foreach (float E in StandingEye)
                foreach (float g in new[] { 0.05f, 0.10f, 0.15f })
                    foreach (float o in new[] { 0f, g * 0.5f })
                    {
                        const float A = 1.61f, u = 1f;
                        float deviceOriginY = E - g;        // what OpenVR reports as PlayerEyeHeight
                        float shortfall = g - o;            // the un-bridged remainder
                        float deviceScale = BasisCalibrationMath.ComputeDeviceScale(A, u, deviceOriginY, o, 0f);

                        float viewpoint = RenderedViewpoint(E, deviceScale);
                        float avatarRenderedEye = A * u;
                        Assert.That(viewpoint, Is.GreaterThan(avatarRenderedEye),
                            $"E={E} g={g} o={o}: an under-bridged eye reference must render too tall.");
                        // The tallness is exactly E/(E - shortfall): the bias is the un-bridged measurement gap.
                        Assert.That(viewpoint / avatarRenderedEye, Is.EqualTo(E / (E - shortfall)).Within(1e-4f),
                            $"E={E} g={g} o={o}: too-tall factor must equal E/(E-shortfall).");
                    }
        }

        [Test]
        public void Nudge_EqualToTheShortfall_RestoresCorrectFeel()
        {
            // The third denominator term is an additive standing-height correction. Adding exactly the
            // measurement shortfall makes the denominator true again -- so the correction that restores
            // correct feel is a direct readout of how far the eye reference under-measured.
            foreach (float E in StandingEye)
                foreach (float g in new[] { 0.05f, 0.10f, 0.15f })
                {
                    const float A = 1.61f, u = 1f;
                    float deviceOriginY = E - g;
                    float shortfall = g; // worst case: eye reference reported 0

                    float biased = BasisCalibrationMath.ComputeDeviceScale(A, u, deviceOriginY, 0f, 0f);
                    Assert.That(RenderedViewpoint(E, biased), Is.Not.EqualTo(A * u).Within(1e-3f),
                        "sanity: the un-nudged calibration is off.");

                    float nudged = BasisCalibrationMath.ComputeDeviceScale(A, u, deviceOriginY, 0f, shortfall);
                    Assert.That(RenderedViewpoint(E, nudged), Is.EqualTo(A * u).Within(1e-4f),
                        $"E={E} g={g}: a nudge equal to the shortfall must restore the avatar eye exactly.");
                }
        }

        [Test]
        public void DegenerateDenominator_ReturnsOne_NoNaN()
        {
            // A zero/negative measured height (bad poll) must never produce NaN/Inf scale that poisons bones.
            float deviceScale = BasisCalibrationMath.ComputeDeviceScale(1.61f, 1f, 0f, 0f, 0f);
            Assert.That(deviceScale, Is.EqualTo(1f).Within(1e-6f));
            Assert.That(float.IsNaN(deviceScale) || float.IsInfinity(deviceScale), Is.False);
        }

        [Test]
        public void ArmSpanGrounding_SunkAvatar_LiftsScaledFeetToFloor()
        {
            // Arm-span DeviceScale matches reach, so a long-ape-index player (span > eye) scales the avatar
            // down and the scaled head lands below its standing eye -- feet under the floor. The grounding
            // lift must raise the tracking space so the scaled feet land exactly on the floor (y == 0).
            const float avatarEye = 1.5f, avatarSpan = 1.5f, u = 1f;
            foreach (float playerEye in StandingEye)
                foreach (float apeIndex in new[] { 1.00f, 1.06f, 1.12f })
                {
                    float playerSpan = playerEye * apeIndex;
                    float deviceScale = BasisCalibrationMath.ComputeDeviceScale(avatarSpan, u, playerSpan, 0f, 0f);
                    float lift = BasisCalibrationMath.ArmSpanFloorGroundingLift(avatarEye, u, deviceScale, playerEye);

                    float avatarStandingEye = avatarEye * u;
                    float unliftedFootY = playerEye * deviceScale - avatarStandingEye;
                    float groundedFootY = (playerEye + lift) * deviceScale - avatarStandingEye;

                    if (unliftedFootY < -1e-4f)
                        Assert.That(groundedFootY, Is.EqualTo(0f).Within(1e-4f),
                            $"eye={playerEye} ape={apeIndex}: lift must land the scaled feet on the floor.");
                    else
                        Assert.That(lift, Is.EqualTo(0f).Within(1e-6f),
                            $"eye={playerEye} ape={apeIndex}: no lift when the feet aren't below the floor.");
                }
        }

        [Test]
        public void ArmToHeightBlend_EndpointsReproduceThePureModes_MidpointBetween()
        {
            // The blend interpolates the metric pair (and the eye offset by its eye share, as
            // ChooseHeightToUse assembles it), so 0% must be exactly eye-height mode, 100% exactly
            // arm-distance mode, and anything between must land between the two DeviceScales.
            const float avatarEye = 1.45f, avatarSpan = 1.60f, u = 1f, eyeOffset = 0.08f;
            foreach (float playerEye in StandingEye)
                foreach (float apeIndex in new[] { 0.94f, 1.00f, 1.10f })
                {
                    float playerSpan = playerEye * apeIndex;
                    float eyeScale = BasisCalibrationMath.ComputeDeviceScale(avatarEye, u, playerEye, eyeOffset, 0f);
                    float spanScale = BasisCalibrationMath.ComputeDeviceScale(avatarSpan, u, playerSpan, 0f, 0f);

                    float BlendScale(float t) => BasisCalibrationMath.ComputeDeviceScale(
                        BasisCalibrationMath.BlendEyeSpanMetric(avatarEye, avatarSpan, t), u,
                        BasisCalibrationMath.BlendEyeSpanMetric(playerEye, playerSpan, t), eyeOffset * Mathf.Clamp01(1f - t), 0f);

                    Assert.That(BlendScale(0f), Is.EqualTo(eyeScale).Within(1e-5f),
                        $"eye={playerEye} ape={apeIndex}: 0% must reproduce eye-height mode.");
                    Assert.That(BlendScale(1f), Is.EqualTo(spanScale).Within(1e-5f),
                        $"eye={playerEye} ape={apeIndex}: 100% must reproduce arm-distance mode.");
                    float mid = BlendScale(0.5f);
                    Assert.That(mid, Is.InRange(Mathf.Min(eyeScale, spanScale) - 1e-5f, Mathf.Max(eyeScale, spanScale) + 1e-5f),
                        $"eye={playerEye} ape={apeIndex}: 50% must land between the two modes.");

                    // The blend no longer extrapolates: the uniform scale only ever sits between the two
                    // measurements, and the residual mismatch is taken up per-segment by the body fit.
                    // Anything outside 0..1 clamps onto the nearer endpoint.
                    Assert.That(BlendScale(-0.5f), Is.EqualTo(eyeScale).Within(1e-5f),
                        $"eye={playerEye} ape={apeIndex}: below 0% must clamp to eye-height mode.");
                    Assert.That(BlendScale(1.5f), Is.EqualTo(spanScale).Within(1e-5f),
                        $"eye={playerEye} ape={apeIndex}: above 100% must clamp to arm-distance mode.");

                    float lo = Mathf.Min(eyeScale, spanScale) - 1e-5f;
                    float hi = Mathf.Max(eyeScale, spanScale) + 1e-5f;
                    for (float t = 0f; t <= 1f; t += 0.1f)
                    {
                        Assert.That(BlendScale(t), Is.InRange(lo, hi),
                            $"eye={playerEye} ape={apeIndex} t={t:F1}: the blend must never leave the span of the two modes.");
                    }
                }
        }

        [Test]
        public void ArmToHeightBlend_AtZero_GroundingLiftIsZero()
        {
            // The blended grounding lift adds the blended eye offset to the player eye term; at 0% the
            // eye-mode denominator already grounds the feet, so the lift must vanish (no double-count).
            const float avatarEye = 1.5f, u = 1f, eyeOffset = 0.08f;
            foreach (float playerEye in StandingEye)
            {
                float eyeScale = BasisCalibrationMath.ComputeDeviceScale(avatarEye, u, playerEye, eyeOffset, 0f);
                float lift = BasisCalibrationMath.ArmSpanFloorGroundingLift(avatarEye, u, eyeScale, playerEye + eyeOffset);
                Assert.That(lift, Is.EqualTo(0f).Within(1e-5f),
                    $"eye={playerEye}: at 0% blend the grounding lift must be exactly zero.");
            }
        }

        [Test]
        public void ArmSpanGrounding_FloatingAvatar_PushUpOnly_NoLift()
        {
            // Short arms relative to height scale the avatar UP, floating the feet above the floor. The lift
            // is push-up only: it returns 0 rather than sinking the view to chase a floating avatar.
            const float avatarEye = 1.5f, avatarSpan = 1.5f, u = 1f;
            const float playerEye = 1.75f, playerSpan = 1.5f;
            float deviceScale = BasisCalibrationMath.ComputeDeviceScale(avatarSpan, u, playerSpan, 0f, 0f);
            float unliftedFootY = playerEye * deviceScale - avatarEye * u;
            Assert.That(unliftedFootY, Is.GreaterThan(1e-3f), "sanity: this config floats the avatar.");
            Assert.That(BasisCalibrationMath.ArmSpanFloorGroundingLift(avatarEye, u, deviceScale, playerEye),
                Is.EqualTo(0f).Within(1e-6f));
        }
    }
}
