using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.IK
{
    /// <summary>
    /// Regression tests for FBT position-offset reprojection
    /// (<see cref="BasisCalibrationMath.ReprojectInverseOffsetPosition"/>): the mechanism that lets a
    /// T-pose calibration keep fitting after an avatar swap, a scale-slider change, or a device
    /// reconnect — WITHOUT redoing the T-pose. Rotation calibration has had this since 2026-06 (the
    /// s_ref mechanism); positions were left stale, so every swap/scale change degraded FBT fit until
    /// a manual recalibration.
    ///
    /// The reprojection rebuilds the calibration geometry from scale-free snapshots (tracker + head in
    /// unscaled device space) at the current DeviceScale/OffsetCoords, anchors the avatar with
    /// DriveTpose's own math, and re-captures the offset against the current avatar's T-pose bind.
    /// These tests pin the exactness properties that make that valid: same-state reprojection is a
    /// no-op, and after any scale / proportion / frame change the bone lands exactly on the rebuilt
    /// reference. All assertions are exact math (small float tolerances only).
    /// </summary>
    public class BasisCalibrationOffsetReprojectionTests
    {
        // A believable calibration scene: player head ~1.65 m, hip tracker on the body, mild yaw.
        static readonly Vector3 HeadUnscaled = new Vector3(0.03f, 1.65f, -0.02f);
        static readonly Vector3 TrackerUnscaled = new Vector3(0.05f, 0.94f, 0.02f);
        static readonly Quaternion TrackerRotUnscaled = Quaternion.Euler(10f, 40f, -5f);

        // Avatar T-pose binds (root-local, scaled space): head and hip of the calibration avatar,
        // plus a differently proportioned swap target (shorter, wider hips).
        static readonly Vector3 HeadTposeA = new Vector3(0f, 1.58f, 0.04f);
        static readonly Vector3 HipTposeA = new Vector3(0.02f, 0.92f, 0f);
        static readonly Vector3 HeadTposeB = new Vector3(0f, 1.10f, 0.02f);
        static readonly Vector3 HipTposeB = new Vector3(0.06f, 0.58f, -0.01f);

        // What BasisInput.CalculateOffset produces at a live calibration: the avatar is DriveTpose'd to
        // the (scaled) head and the offset captured against the bone's anchored T-pose position.
        static Vector3 CaptureLive(Vector3 headUnscaled, Quaternion headRotUnscaled,
            Vector3 trackerUnscaled, Quaternion trackerRotUnscaled,
            float scale, Vector3 offPos, Quaternion offRot,
            Vector3 headTpose, Vector3 boneTpose,
            out Vector3 trackerScaledPos, out Quaternion trackerScaledRot, out Vector3 reference)
        {
            BasisCalibrationMath.ScaleDeviceCoord(trackerUnscaled, trackerRotUnscaled, scale, offPos, offRot, out trackerScaledPos, out trackerScaledRot);
            BasisCalibrationMath.ScaleDeviceCoord(headUnscaled, headRotUnscaled, scale, offPos, offRot, out Vector3 headScaled, out Quaternion headScaledRot);

            Vector3 flatFwd = headScaledRot * Vector3.forward;
            flatFwd.y = 0f;
            Quaternion rootRot = flatFwd.sqrMagnitude < 1e-6f ? Quaternion.identity : Quaternion.LookRotation(flatFwd.normalized, Vector3.up);
            Vector3 rootPos = headScaled - rootRot * headTpose;
            reference = rootPos + rootRot * boneTpose;

            BasisCalibrationMath.ComputeInverseOffset(trackerScaledPos, trackerScaledRot, reference, Quaternion.identity, out Vector3 invOff, out _);
            return invOff;
        }

        static Vector3 RuntimeBone(Vector3 trackerScaledPos, Quaternion trackerScaledRot, Vector3 invOffPos)
            => trackerScaledPos + trackerScaledRot * invOffPos;

        [Test]
        public void UnscaleDeviceCoord_IsExactInverseOfScaleDeviceCoord()
        {
            // The snapshot is only scale-free if unscale exactly inverts scale, including under a
            // non-identity OffsetCoords (the world-seat frame).
            foreach (float scale in new[] { 0.35f, 1f, 1.72f })
            foreach (var off in new[] { (p: Vector3.zero, r: Quaternion.identity), (p: new Vector3(2f, 0.4f, -1f), r: Quaternion.Euler(0f, 137f, 0f)) })
            {
                Vector3 u = new Vector3(0.12f, 1.43f, -0.31f);
                Quaternion ur = Quaternion.Euler(23f, -71f, 8f);
                BasisCalibrationMath.ScaleDeviceCoord(u, ur, scale, off.p, off.r, out Vector3 s, out Quaternion sr);
                BasisCalibrationMath.UnscaleDeviceCoord(s, sr, scale, off.p, off.r, out Vector3 u2, out Quaternion ur2);
                Assert.That((u2 - u).magnitude, Is.LessThan(1e-5f), $"scale {scale}: unscale did not invert scale (pos).");
                Assert.That(Quaternion.Angle(ur2, ur), Is.LessThan(1e-3f), $"scale {scale}: unscale did not invert scale (rot).");
            }
        }

        [Test]
        public void Reproject_SameAvatarSameScale_ReproducesCapturedOffsetExactly()
        {
            // Reprojection must be a NO-OP when nothing changed: ApplyScaleAndHeight runs it on every
            // re-resolve, so any drift here would walk a good calibration away from what the T-pose
            // captured. Checked across yawed and pitched calibration headings.
            foreach (var headRot in new[] { Quaternion.identity, Quaternion.Euler(0f, 90f, 0f), Quaternion.Euler(15f, -130f, 0f) })
            foreach (float scale in new[] { 0.62f, 1f, 1.38f })
            {
                Vector3 captured = CaptureLive(HeadUnscaled, headRot, TrackerUnscaled, TrackerRotUnscaled,
                    scale, Vector3.zero, Quaternion.identity, HeadTposeA, HipTposeA, out _, out _, out _);

                BasisCalibrationMath.ReprojectInverseOffsetPosition(
                    TrackerUnscaled, TrackerRotUnscaled, HeadUnscaled, headRot,
                    scale, Vector3.zero, Quaternion.identity, HeadTposeA, HipTposeA,
                    out Vector3 reprojected);

                Assert.That((reprojected - captured).magnitude, Is.LessThan(1e-5f),
                    $"heading {headRot.eulerAngles} scale {scale}: same-state reprojection moved the offset by {(reprojected - captured).magnitude:0.000000} m.");
            }
        }

        [Test]
        public void Reproject_ScaleChange_LandsBoneExactlyOnRebuiltReference()
        {
            // The avatar-swap/scale-slider case: DeviceScale moves from s0 to s1 with the SAME body
            // wearing the trackers. The stale offset misses by |s1-s0| x pose; the reprojected offset
            // must land the bone exactly on the anchor rebuilt at s1.
            float s0 = 1.00f, s1 = 0.63f;
            Quaternion headRot = Quaternion.Euler(5f, 25f, 0f);

            Vector3 stale = CaptureLive(HeadUnscaled, headRot, TrackerUnscaled, TrackerRotUnscaled,
                s0, Vector3.zero, Quaternion.identity, HeadTposeA, HipTposeA, out _, out _, out _);

            // Ground truth at s1 = what a fresh T-pose capture would produce.
            Vector3 fresh = CaptureLive(HeadUnscaled, headRot, TrackerUnscaled, TrackerRotUnscaled,
                s1, Vector3.zero, Quaternion.identity, HeadTposeA, HipTposeA,
                out Vector3 trackerS1Pos, out Quaternion trackerS1Rot, out Vector3 referenceS1);

            BasisCalibrationMath.ReprojectInverseOffsetPosition(
                TrackerUnscaled, TrackerRotUnscaled, HeadUnscaled, headRot,
                s1, Vector3.zero, Quaternion.identity, HeadTposeA, HipTposeA,
                out Vector3 reprojected);

            Assert.That((reprojected - fresh).magnitude, Is.LessThan(1e-5f),
                "reprojected offset must equal a fresh T-pose capture at the new scale.");
            Assert.That((RuntimeBone(trackerS1Pos, trackerS1Rot, reprojected) - referenceS1).magnitude, Is.LessThan(1e-5f),
                "bone must land exactly on the rebuilt reference at the new scale.");
            Assert.That((RuntimeBone(trackerS1Pos, trackerS1Rot, stale) - referenceS1).magnitude, Is.GreaterThan(0.05f),
                "the stale offset was expected to visibly miss at the new scale; the scenario no longer exercises the bug.");
        }

        [Test]
        public void Reproject_AvatarProportionChange_LandsBoneOnNewAvatarsBone()
        {
            // The swap-to-different-proportions case: same player, same scale-space geometry, but the
            // new avatar's hip bind sits somewhere else. The reprojected offset must land the bone on
            // the NEW avatar's hip — equal to what a fresh T-pose on that avatar would capture.
            float scale = 0.85f;
            Quaternion headRot = Quaternion.Euler(0f, -60f, 0f);

            Vector3 fresh = CaptureLive(HeadUnscaled, headRot, TrackerUnscaled, TrackerRotUnscaled,
                scale, Vector3.zero, Quaternion.identity, HeadTposeB, HipTposeB,
                out Vector3 trackerPos, out Quaternion trackerRot, out Vector3 referenceB);

            BasisCalibrationMath.ReprojectInverseOffsetPosition(
                TrackerUnscaled, TrackerRotUnscaled, HeadUnscaled, headRot,
                scale, Vector3.zero, Quaternion.identity, HeadTposeB, HipTposeB,
                out Vector3 reprojected);

            Assert.That((reprojected - fresh).magnitude, Is.LessThan(1e-5f),
                "reprojection onto a differently-proportioned avatar must equal a fresh capture on it.");
            Assert.That((RuntimeBone(trackerPos, trackerRot, reprojected) - referenceB).magnitude, Is.LessThan(1e-5f),
                "bone must land on the new avatar's own T-pose hip.");
        }

        [Test]
        public void Reproject_SurvivesOffsetCoordsChange()
        {
            // Calibrated free-standing (identity OffsetCoords), then the player sits in a world seat
            // (rotated + translated rigid frame). The reprojected offset must land the bone on the
            // anchor rebuilt in the NEW frame — the whole constellation moves rigidly together.
            float scale = 1.10f;
            Quaternion headRot = Quaternion.Euler(0f, 35f, 0f);
            Vector3 seatPos = new Vector3(1.4f, 0.1f, -2.2f);
            Quaternion seatRot = Quaternion.Euler(0f, 155f, 0f);

            Vector3 fresh = CaptureLive(HeadUnscaled, headRot, TrackerUnscaled, TrackerRotUnscaled,
                scale, seatPos, seatRot, HeadTposeA, HipTposeA,
                out Vector3 trackerPos, out Quaternion trackerRot, out Vector3 reference);

            BasisCalibrationMath.ReprojectInverseOffsetPosition(
                TrackerUnscaled, TrackerRotUnscaled, HeadUnscaled, headRot,
                scale, seatPos, seatRot, HeadTposeA, HipTposeA,
                out Vector3 reprojected);

            Assert.That((reprojected - fresh).magnitude, Is.LessThan(1e-5f),
                "reprojection into a world-seat frame must equal a fresh capture in that frame.");
            Assert.That((RuntimeBone(trackerPos, trackerRot, reprojected) - reference).magnitude, Is.LessThan(1e-5f),
                "bone must land on the reference rebuilt in the seat frame.");
        }

        [Test]
        public void Reproject_IsIdempotent_RepeatedResolvesDoNotDrift()
        {
            // ApplyScaleAndHeight fires on every height re-resolve (sliders, sit/stand, swaps); the
            // reprojection is a pure function of (snapshot, avatar, scale), so repeated calls must give
            // bit-identical results — a good calibration can never drift from being re-resolved.
            float scale = 1.21f;
            Quaternion headRot = Quaternion.Euler(3f, 200f, 0f);

            BasisCalibrationMath.ReprojectInverseOffsetPosition(
                TrackerUnscaled, TrackerRotUnscaled, HeadUnscaled, headRot,
                scale, Vector3.zero, Quaternion.identity, HeadTposeA, HipTposeA, out Vector3 first);
            BasisCalibrationMath.ReprojectInverseOffsetPosition(
                TrackerUnscaled, TrackerRotUnscaled, HeadUnscaled, headRot,
                scale, Vector3.zero, Quaternion.identity, HeadTposeA, HipTposeA, out Vector3 second);

            Assert.That(second, Is.EqualTo(first), "reprojection must be deterministic.");
        }
    }
}
