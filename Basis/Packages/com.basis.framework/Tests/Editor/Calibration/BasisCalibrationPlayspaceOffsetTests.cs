using System.Collections.Generic;
using Basis.Scripts.Avatar;
using Basis.Scripts.TransformBinders.BoneControl;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Calibration
{
    /// <summary>
    /// FBT calibration under a play-space offset. The play-space mover's vertical drag
    /// (BasisLocalPlayspaceMover.VerticalOffset) is added to EVERY device pose in
    /// BasisInput.ComputeUnscaledDeviceCoord — head and trackers alike — and the calibration pipeline is
    /// built so that uniform shift cancels end-to-end:
    ///
    ///   * PlayerEyeHeight subtracts the offset at capture (BasisLocalHeightCalculator:99), so the body
    ///     frame's floor (floorY = hmdY - eyeHeight, BasisAvatarIKStageCalibration:571) lands under the
    ///     player's REAL feet wherever the space is dragged;
    ///   * the constellation classifier scores pure eye-height ratios in that frame;
    ///   * the tracker position offsets are captured against the avatar's T-pose bone anchored AT THE
    ///     HEAD (BasisCalibrationMath.ComputeTposeAnchor), so tracker and reference carry the same shift.
    ///
    /// These tests pin each of those cancellations with the REAL classifier and the REAL calibration
    /// math: calibrating while space-dragged must classify the same roles and capture the same offsets
    /// as calibrating in place. (The one part of the body that did NOT ride the shift was the virtual
    /// spine's synthesized hips/chest — see BasisVirtualSpinePlayspaceLiftTests, which is the "legs and
    /// hips never lock in after a play-space drag" bug itself.)
    /// </summary>
    public sealed class BasisCalibrationPlayspaceOffsetTests
    {
        const float TrueEyeHeight = 1.70f;

        /// <summary>A typical 6-point FBT set + hands, in offset-free playspace metres.</summary>
        struct Body
        {
            public Vector3 Hmd;
            public Vector3[] Trackers;
            public Vector3 LeftHand;
            public Vector3 RightHand;

            public static Body Standard()
            {
                return new Body
                {
                    Hmd = new Vector3(0f, TrueEyeHeight, 0f),
                    Trackers = new[]
                    {
                        new Vector3(0.00f, 0.95f, -0.03f),   // hip puck
                        new Vector3(0.00f, 1.32f, -0.05f),   // chest puck
                        new Vector3(-0.12f, 0.45f, 0.02f),   // left knee
                        new Vector3(0.12f, 0.45f, 0.02f),    // right knee
                        new Vector3(-0.15f, 0.08f, 0.05f),   // left foot
                        new Vector3(0.15f, 0.08f, 0.05f),    // right foot
                    },
                    LeftHand = new Vector3(-0.65f, 1.45f, 0f),
                    RightHand = new Vector3(0.65f, 1.45f, 0f),
                };
            }
        }

        /// <summary>
        /// Runs the REAL BasisConstellationClassifier over the body with the play space dragged by
        /// <paramref name="verticalOffset"/>, converting device poses to samples with the same math as
        /// ClassifyAndAssignTrackersFromTPose: eye height is the offset-compensated capture, the floor is
        /// derived from the (offset-carrying) HMD pose, ratios are body-local over eye height.
        /// </summary>
        static BasisConstellationResult Classify(Body body, float verticalOffset)
        {
            Vector3 lift = new Vector3(0f, verticalOffset, 0f);
            Vector3 hmd = body.Hmd + lift;

            float eyeHeight = Mathf.Max(hmd.y - verticalOffset, 0.5f);
            float floorY = hmd.y - eyeHeight;
            Vector3 bodyOrigin = new Vector3(hmd.x, floorY, hmd.z);
            Quaternion bodyRotInv = Quaternion.Inverse(Quaternion.LookRotation(Vector3.forward, Vector3.up));

            var samples = new BasisConstellationSample[body.Trackers.Length];
            for (int i = 0; i < body.Trackers.Length; i++)
            {
                Vector3 local = bodyRotInv * ((body.Trackers[i] + lift) - bodyOrigin);
                samples[i] = new BasisConstellationSample
                {
                    HeightRatio = local.y / eyeHeight,
                    LateralRatio = local.x / eyeHeight,
                    DepthLocal = local.z,
                    NearOrigin = false,
                    ForcedRole = -1,
                };
            }

            BasisConstellationHand Hand(Vector3 pos)
            {
                Vector3 local = bodyRotInv * ((pos + lift) - bodyOrigin);
                return new BasisConstellationHand
                {
                    Present = true,
                    HeightRatio = local.y / eyeHeight,
                    LateralRatio = local.x / eyeHeight,
                };
            }

            return BasisConstellationClassifier.Classify(
                samples, samples.Length, Hand(body.LeftHand), Hand(body.RightHand),
                tolerance: 1f, roleEnabled: _ => true);
        }

        /// <summary>
        /// Calibrating while space-dragged must assign the SAME roles as calibrating in place — legs and
        /// hips included. The drag shifts the HMD-derived floor and every tracker by the same amount, and
        /// the compensated eye height keeps the ratios identical, so nothing may change.
        /// </summary>
        [Test]
        public void RolesClassifyIdentically_AtAnyVerticalPlayspaceOffset()
        {
            Body body = Body.Standard();
            BasisConstellationResult reference = Classify(body, 0f);

            var expected = new[]
            {
                BasisBoneTrackedRole.Hips,
                BasisBoneTrackedRole.Chest,
                BasisBoneTrackedRole.LeftLowerLeg,
                BasisBoneTrackedRole.RightLowerLeg,
                BasisBoneTrackedRole.LeftFoot,
                BasisBoneTrackedRole.RightFoot,
            };
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.IsTrue(reference.TryGetRole(i, out BasisBoneTrackedRole role) && role == expected[i],
                    $"sanity: the offset-free constellation must classify tracker {i} as {expected[i]} "
                    + $"(got {(reference.TryGetRole(i, out var r) ? r.ToString() : "unassigned")}) -- "
                    + "otherwise the invariance sweep below compares garbage to garbage.");
            }

            foreach (float offset in new[] { -1.5f, -0.5f, 0.5f, 1.5f })
            {
                BasisConstellationResult dragged = Classify(body, offset);
                for (int i = 0; i < expected.Length; i++)
                {
                    bool assigned = dragged.TryGetRole(i, out BasisBoneTrackedRole role);
                    Assert.IsTrue(assigned && role == expected[i],
                        $"with the play space dragged {offset:+0.00;-0.00} m, tracker {i} classified as "
                        + $"{(assigned ? role.ToString() : "unassigned")} instead of {expected[i]}. The "
                        + "HMD-derived floor and the compensated eye height are supposed to cancel the drag "
                        + "exactly (BasisAvatarIKStageCalibration:571, BasisLocalHeightCalculator:99).");
                }
            }
        }

        /// <summary>
        /// If the eye-height capture ever stops subtracting the play-space offset, the floor lands at the
        /// dragged height while the priors stay eye-relative, and legs/hips stop classifying. This is the
        /// paired negative that keeps the invariance gate above honest.
        /// </summary>
        [Test]
        public void WithoutEyeHeightCompensation_ADrag_LosesTheLegsAndHips()
        {
            Body body = Body.Standard();
            const float offset = 1.0f;

            Vector3 lift = new Vector3(0f, offset, 0f);
            Vector3 hmd = body.Hmd + lift;
            float eyeHeight = hmd.y;               // the defect: raw device Y, offset left in
            float floorY = hmd.y - eyeHeight;      // floor collapses to playspace zero
            Vector3 bodyOrigin = new Vector3(hmd.x, floorY, hmd.z);

            var samples = new BasisConstellationSample[body.Trackers.Length];
            for (int i = 0; i < body.Trackers.Length; i++)
            {
                Vector3 local = (body.Trackers[i] + lift) - bodyOrigin;
                samples[i] = new BasisConstellationSample
                {
                    HeightRatio = local.y / eyeHeight,
                    LateralRatio = local.x / eyeHeight,
                    DepthLocal = local.z,
                    ForcedRole = -1,
                };
            }

            BasisConstellationResult result = BasisConstellationClassifier.Classify(
                samples, samples.Length, BasisConstellationHand.None, BasisConstellationHand.None,
                tolerance: 1f, roleEnabled: _ => true);

            var misses = new List<int>();
            for (int i = 0; i < samples.Length; i++)
            {
                var expected = new[]
                {
                    BasisBoneTrackedRole.Hips, BasisBoneTrackedRole.Chest,
                    BasisBoneTrackedRole.LeftLowerLeg, BasisBoneTrackedRole.RightLowerLeg,
                    BasisBoneTrackedRole.LeftFoot, BasisBoneTrackedRole.RightFoot,
                }[i];
                if (!result.TryGetRole(i, out BasisBoneTrackedRole role) || role != expected) misses.Add(i);
            }

            Assert.IsTrue(misses.Count >= 2,
                "an UNcompensated eye height under a 1 m drag should visibly break classification (it is "
                + "what the compensation at BasisLocalHeightCalculator:99 exists for). It no longer does, "
                + "so the invariance gate above is not exercising the compensation.");
        }

        /// <summary>
        /// "Calibrate anywhere": with body trackers present, the floor comes from the trackers
        /// themselves (BasisCalibrationMath.TryEstimateFloorFromTrackers), so the eye height — and with
        /// it the whole classification frame — cancels ANY vertical shift, including ones Basis has no
        /// bookkeeping for (an OVRAS/SteamVR space drag, a stale grounding lift). This sweeps shifts
        /// with NO compensation term at all and requires identical eye height and identical roles.
        /// </summary>
        [Test]
        public void TrackedFloor_CancelsAnyVerticalShift_EvenOnesBasisCannotSee()
        {
            Body body = Body.Standard();

            float EyeFromTrackedFloor(float shift)
            {
                var heights = new List<float>();
                foreach (Vector3 t in body.Trackers) heights.Add(t.y + shift);
                Assert.IsTrue(BasisCalibrationMath.TryEstimateFloorFromTrackers(heights, body.Hmd.y + shift, out float floorY),
                    $"the 6-point constellation must always yield a tracked floor (shift {shift:+0.00;-0.00;0.00})");
                return (body.Hmd.y + shift) - floorY;
            }

            float referenceEye = EyeFromTrackedFloor(0f);
            Assert.AreEqual(TrueEyeHeight, referenceEye, 0.05f,
                "sanity: the tracked-floor eye height should land near the player's true eye height");

            foreach (float shift in new[] { -2.0f, -0.5f, 0.5f, 2.0f })
            {
                Assert.AreEqual(referenceEye, EyeFromTrackedFloor(shift), 1e-4f,
                    $"a {shift:+0.00;-0.00} m shift leaked into the tracked-floor eye height — the whole "
                    + "point of anchoring to the trackers is that hmd and trackers carry the same shift.");

                // And the classification frame built from it assigns the same roles, with no
                // compensation subtraction anywhere (the external-drag case the old design lost).
                float eyeHeight = EyeFromTrackedFloor(shift);
                float floorY = (body.Hmd.y + shift) - eyeHeight;
                var samples = new BasisConstellationSample[body.Trackers.Length];
                for (int i = 0; i < body.Trackers.Length; i++)
                {
                    Vector3 local = (body.Trackers[i] + new Vector3(0f, shift, 0f)) - new Vector3(body.Hmd.x, floorY, body.Hmd.z);
                    samples[i] = new BasisConstellationSample
                    {
                        HeightRatio = local.y / eyeHeight,
                        LateralRatio = local.x / eyeHeight,
                        DepthLocal = local.z,
                        ForcedRole = -1,
                    };
                }
                BasisConstellationResult result = BasisConstellationClassifier.Classify(
                    samples, samples.Length, BasisConstellationHand.None, BasisConstellationHand.None,
                    tolerance: 1f, roleEnabled: _ => true);

                var expected = new[]
                {
                    BasisBoneTrackedRole.Hips, BasisBoneTrackedRole.Chest,
                    BasisBoneTrackedRole.LeftLowerLeg, BasisBoneTrackedRole.RightLowerLeg,
                    BasisBoneTrackedRole.LeftFoot, BasisBoneTrackedRole.RightFoot,
                };
                for (int i = 0; i < expected.Length; i++)
                {
                    Assert.IsTrue(result.TryGetRole(i, out BasisBoneTrackedRole role) && role == expected[i],
                        $"tracked-floor frame at shift {shift:+0.00;-0.00} classified tracker {i} as "
                        + $"{(result.TryGetRole(i, out var r) ? r.ToString() : "unassigned")} instead of {expected[i]}");
                }
            }
        }

        /// <summary>
        /// The tracked floor must refuse to guess from torso trackers: a lone hip puck (or hip+chest)
        /// sits half a body above the floor, and treating it as feet would halve the measured eye
        /// height. Feet come in pairs — fewer than two trackers in the foot band means no estimate.
        /// </summary>
        [Test]
        public void TrackedFloor_RefusesToGuessFromTorsoTrackers()
        {
            Assert.IsFalse(BasisCalibrationMath.TryEstimateFloorFromTrackers(
                new List<float> { 0.95f }, TrueEyeHeight, out _),
                "a lone hip tracker must not define the floor");
            Assert.IsFalse(BasisCalibrationMath.TryEstimateFloorFromTrackers(
                new List<float> { 0.95f, 1.32f }, TrueEyeHeight, out _),
                "hip+chest sit far apart vertically — neither is floor evidence");
            Assert.IsFalse(BasisCalibrationMath.TryEstimateFloorFromTrackers(
                new List<float>(), TrueEyeHeight, out _),
                "no trackers, no estimate");
            Assert.IsFalse(BasisCalibrationMath.TryEstimateFloorFromTrackers(
                new List<float> { -1.5f, -1.45f }, TrueEyeHeight, out _),
                "a floor implying a 3+ metre body is a bad read (chargers on the floor of a lower room), not a measurement");
        }

        /// <summary>
        /// The lift-poison guard, pinned on the exact pair recovered from a poisoned install: a saved
        /// eye of 1.992 m against a span of 1.507 m is anatomically impossible (that span implies a
        /// ~1.5 m body) — it is a calibration taken while vertically shifted and must never persist.
        /// Honest short-armed players stay inside the band.
        /// </summary>
        [Test]
        public void LiftPoisonGuard_CatchesTheImpossibleEye_AndSparesRealBodies()
        {
            Assert.IsTrue(BasisCalibrationMath.EyeHeightLooksLiftPoisoned(1.992f, 1.507f),
                "the field-recovered poisoned pair (eye 1.992 / span 1.507) must be flagged");
            Assert.IsFalse(BasisCalibrationMath.EyeHeightLooksLiftPoisoned(1.60f, 1.50f),
                "a short-armed but honest body must not be flagged");
            Assert.IsFalse(BasisCalibrationMath.EyeHeightLooksLiftPoisoned(1.50f, 1.55f),
                "a normal body must not be flagged");
            Assert.IsFalse(BasisCalibrationMath.EyeHeightLooksLiftPoisoned(1.60f, 0f),
                "no span measurement, no verdict");
        }

        /// <summary>
        /// The position offset a tracker captures at calibration must be play-space invariant: the
        /// reference bone is the avatar's T-pose bind anchored at the HEAD (ComputeTposeAnchor), and both
        /// the head and the tracker carry the same drag, so the captured inverse offset — tracker-local
        /// geometry — must not change by a millimetre. This is what keeps a calibration taken while
        /// dragged interchangeable with one taken in place, and existing calibrations valid across drags.
        /// Exercises the REAL BasisCalibrationMath capture path end-to-end.
        /// </summary>
        [Test]
        public void CapturedTrackerOffsets_ArePlayspaceInvariant()
        {
            const float deviceScale = 0.92f;
            Vector3 headTposeLocal = new Vector3(0f, 1.58f, 0.02f) * deviceScale;
            Vector3 footBoneTposeLocal = new Vector3(-0.11f, 0.09f, 0.01f) * deviceScale;

            Vector3 footTrackerUnscaled = new Vector3(-0.15f, 0.08f, 0.05f);
            Quaternion footTrackerRotUnscaled = Quaternion.Euler(25f, 10f, -80f);
            Vector3 hmdUnscaled = new Vector3(0.02f, TrueEyeHeight, -0.01f);
            Quaternion hmdRotUnscaled = Quaternion.Euler(5f, 30f, 0f);

            Vector3 CaptureOffset(float verticalOffset)
            {
                Vector3 lift = new Vector3(0f, verticalOffset, 0f);

                BasisCalibrationMath.ScaleDeviceCoord(footTrackerUnscaled + lift, footTrackerRotUnscaled,
                    deviceScale, Vector3.zero, Quaternion.identity, out Vector3 trackerPos, out Quaternion trackerRot);
                BasisCalibrationMath.ScaleDeviceCoord(hmdUnscaled + lift, hmdRotUnscaled,
                    deviceScale, Vector3.zero, Quaternion.identity, out Vector3 headPos, out Quaternion headRot);

                BasisCalibrationMath.ComputeTposeAnchor(headPos, headRot, headTposeLocal,
                    out Vector3 anchorPos, out Quaternion anchorRot);
                Vector3 reference = anchorPos + anchorRot * footBoneTposeLocal;

                BasisCalibrationMath.ComputeInverseOffset(trackerPos, trackerRot, reference, Quaternion.identity,
                    out Vector3 inverseOffsetPosition, out _);
                return inverseOffsetPosition;
            }

            Vector3 baseline = CaptureOffset(0f);
            foreach (float offset in new[] { -1.5f, -0.5f, 0.5f, 1.5f })
            {
                Vector3 dragged = CaptureOffset(offset);
                Assert.Less((dragged - baseline).magnitude, 1e-4f,
                    $"the captured foot offset moved {(dragged - baseline).magnitude * 1000f:F2} mm under a "
                    + $"{offset:+0.00;-0.00} m play-space drag. The head-anchored reference is supposed to "
                    + "ride the drag with the tracker so the capture is drag-invariant.");
            }
        }

        /// <summary>
        /// Reprojection must agree with capture under a drag: snapshots taken while dragged (offset baked
        /// into BOTH the tracker and its paired head anchor, as BasisInput.CalculateOffset stores them)
        /// must rebuild the same inverse offset as an in-place calibration — including at a different
        /// DeviceScale, which is reprojection's whole job (avatar swap / scale slider after calibrating).
        /// </summary>
        [Test]
        public void ReprojectedOffsets_MatchInPlaceCalibration_AcrossDragsAndScales()
        {
            Vector3 headTposeUnit = new Vector3(0f, 1.58f, 0.02f);
            Vector3 footBoneTposeUnit = new Vector3(-0.11f, 0.09f, 0.01f);

            Vector3 footTrackerUnscaled = new Vector3(-0.15f, 0.08f, 0.05f);
            Quaternion footTrackerRot = Quaternion.Euler(25f, 10f, -80f);
            Vector3 hmdUnscaled = new Vector3(0.02f, TrueEyeHeight, -0.01f);
            Quaternion hmdRot = Quaternion.Euler(5f, 30f, 0f);

            foreach (float newScale in new[] { 0.92f, 1.35f })
            {
                BasisCalibrationMath.ReprojectInverseOffsetPosition(
                    footTrackerUnscaled, footTrackerRot,
                    hmdUnscaled, hmdRot,
                    newScale, Vector3.zero, Quaternion.identity,
                    headTposeUnit * newScale, footBoneTposeUnit * newScale,
                    out Vector3 inPlace);

                foreach (float offset in new[] { -1.5f, 1.5f })
                {
                    Vector3 lift = new Vector3(0f, offset, 0f);
                    BasisCalibrationMath.ReprojectInverseOffsetPosition(
                        footTrackerUnscaled + lift, footTrackerRot,
                        hmdUnscaled + lift, hmdRot,
                        newScale, Vector3.zero, Quaternion.identity,
                        headTposeUnit * newScale, footBoneTposeUnit * newScale,
                        out Vector3 dragged);

                    Assert.Less((dragged - inPlace).magnitude, 1e-4f,
                        $"reprojecting a calibration snapshot taken under a {offset:+0.00;-0.00} m drag at "
                        + $"DeviceScale {newScale:F2} diverged {(dragged - inPlace).magnitude * 1000f:F2} mm "
                        + "from the in-place calibration -- dragged-and-stored calibrations would go stale "
                        + "the moment the avatar or scale changes.");
                }
            }
        }
    }
}
