using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.MediaPipe
{
    /// <summary>
    /// MediaPipe 21-point hand landmarks → finger curl/splay (BasisLocalHandDriver) plus the wrist
    /// rotation for the hand trackers. Both run off the metric WORLD landmarks, so curl no longer
    /// collapses when the hand points at the camera and the palm frame is real 3D rather than a
    /// projection.
    ///
    /// Rotation is a retarget, not a calibration: the palm frame is measured relative to the user's torso
    /// and re-expressed relative to the avatar's, then corrected by the constant that maps a palm frame
    /// onto the avatar's hand bone. Holding your hand however you like reproduces it on the avatar with
    /// nothing to calibrate.
    /// </summary>
    public sealed class MediaPipeHandConverter
    {
        public float CurlGain = 1f;
        public float ThumbMaxAngle = 100f;
        public float FingerMaxAngle = 160f;
        public float MaxSplayDegrees = 20f;
        public float SplayGain = 1f;
        public float FingerSmoothing = 0.5f;
        public float PoseSmoothing = 0.5f;
        public bool UseRotation = true;

        private const float CutoffResponsive = 10f;
        private const float CutoffSmooth = 1.5f;
        private const float Beta = 3.25f;
        private const float DerivativeCutoff = 1f;

        private RotationFilter _leftRot, _rightRot;

        private float RotationCutoff => Mathf.Lerp(CutoffResponsive, CutoffSmooth, Mathf.Clamp01(PoseSmoothing));

        /// <summary>
        /// Same two-clock split the arm positions use: one-euro on the camera's delta when a fresh sample lands,
        /// then a carry slerp every rendered frame. Running the filter at render rate over a held sample makes
        /// each new sample look like a burst of speed, which is what snaps the wrist between poses.
        /// </summary>
        private struct RotationFilter
        {
            public BasisEuroQuatState Euro;
            public Quaternion Sampled;
            public Quaternion Carried;
            public bool HasSample;

            public Quaternion Apply(Quaternion target, in MediaPipeTiming timing, float cutoff)
            {
                if (timing.IsNewSample || !HasSample)
                {
                    Sampled = BasisFilterMath.EuroQuat(ref Euro, target, timing.SampleDelta,
                        cutoff, Beta, DerivativeCutoff);

                    if (!HasSample)
                    {
                        Carried = Sampled;
                        HasSample = true;
                        return Carried;
                    }
                }

                Carried = Quaternion.Slerp(Carried, Sampled,
                    BasisFilterMath.Alpha(timing.CarryCutoff, timing.RenderDelta));
                return Carried;
            }

            public void Reset()
            {
                Euro = default;
                HasSample = false;
            }
        }

        /// <summary>
        /// Avatar hand geometry in player-root-local space. Correction maps a MediaPipe palm frame onto the
        /// hand bone's rotation; it is built from the knuckle positions, which do not move relative to the
        /// hand when the fingers curl, so it holds for any pose and needs no reference pose to capture.
        ///
        /// IkOffsetInverse undoes a SECOND palm->bone correction that the IK applies downstream. We hand our
        /// rotation to a tracker, and BasisArmSolveCore does `tRotation = TargetRotation * TargetOffset` with
        /// TargetOffset = data.m_CalibratedRotationLeft/RightHand, which BasisAnimationRiggingHelper builds as
        /// `Inverse(landmarkBind) * boneBind` -- i.e. it is ALSO a palm-frame -> hand-bone constant. That offset
        /// is correct for a producer that reports a PALM/LANDMARK frame (a real tracker does). But `Correction`
        /// above already finishes the job: we hand over a BONE rotation, so the IK's offset lands on top of it
        /// and the hand ends up wrong by exactly that offset -- and DIFFERENTLY ON EVERY AVATAR, because it is
        /// calibrated per rig. (The feet had this identical bug.)
        ///
        /// Pre-multiplying by its inverse makes the IK's own `* TargetOffset` collapse back to what we meant:
        ///     (boneRot * offset^-1) * offset == boneRot
        ///
        /// Note the two corrections are NOT the same quaternion, so this does not reduce to "drop Correction":
        /// MediaPipeSpace's palm frame uses the middle-MCP knuckle with a left-hand normal flip, while
        /// HandRotationFromLandmarks uses the index/pinky midpoint. `Correction * IkOffsetInverse` is exactly the
        /// residual between those two conventions. Deleting Correction instead would leave that residual behind
        /// as a silent frame error -- close enough to look plausible, which is worse than obviously wrong.
        /// </summary>
        public struct AvatarHandRig
        {
            public Quaternion Body;
            public Quaternion LeftCorrection;
            public Quaternion RightCorrection;
            public Quaternion LeftIkOffsetInverse;
            public Quaternion RightIkOffsetInverse;
            public bool Valid;
        }

        public void Reset()
        {
            _leftRot.Reset();
            _rightRot.Reset();
        }

        public bool TryGetHandRotation(in BasisMediaPipeResult result, in AvatarHandRig rig, bool left,
            in MediaPipeTiming timing, out Quaternion rotation)
        {
            rotation = Quaternion.identity;
            if (!UseRotation || !rig.Valid) return false;

            // The body frame is what makes this a retarget rather than a copy of the camera's idea of the hand,
            // so without it there is no meaningful rotation to hand back at all.
            if (!MediaPipeSpace.TryBodyFrame(result.PoseWorldLandmarks, out _, out Quaternion bodyFrame)) return false;

            Vector3[] hand = left ? result.LeftHandWorldLandmarks : result.RightHandWorldLandmarks;
            bool detected = left ? result.HasLeftHand : result.HasRightHand;
            if (!detected || !MediaPipeSpace.TryHandFrame(hand, left, out Quaternion handFrame))
            {
                if (!MediaPipeSpace.TryPoseHandFrame(result.PoseWorldLandmarks, left, out handFrame)) return false;
            }

            // Filter the BODY-RELATIVE rotation, not the finished one. rig.Body is the avatar's own orientation and
            // moves at render rate, so smoothing it on the camera clock would drag the wrist behind every turn.
            Quaternion handInBody = Quaternion.Inverse(bodyFrame) * handFrame;
            Quaternion correction = left ? rig.LeftCorrection : rig.RightCorrection;

            // AvatarHandRig is a STRUCT, so an initializer that omits this field leaves it at (0,0,0,0) -- the ZERO
            // quaternion, not identity. Multiplying by that does not "do nothing", it ANNIHILATES the rotation.
            // Treating a degenerate offset as identity means a caller that never heard of this field simply gets
            // the old, uncancelled behaviour instead of a dead hand. Cheap, and it makes the struct impossible to
            // hold wrong.
            Quaternion ikOffsetInverse = left ? rig.LeftIkOffsetInverse : rig.RightIkOffsetInverse;
            float ikSqrNorm = ikOffsetInverse.x * ikOffsetInverse.x + ikOffsetInverse.y * ikOffsetInverse.y
                + ikOffsetInverse.z * ikOffsetInverse.z + ikOffsetInverse.w * ikOffsetInverse.w;
            if (ikSqrNorm < 0.5f) ikOffsetInverse = Quaternion.identity;

            float cutoff = RotationCutoff;
            Quaternion smoothed = left
                ? _leftRot.Apply(handInBody, in timing, cutoff)
                : _rightRot.Apply(handInBody, in timing, cutoff);

            // `rig.Body * smoothed * correction` is the finished HAND BONE rotation. The IK will multiply its own
            // palm->bone offset onto whatever we report, so pre-cancel it here (see AvatarHandRig).
            rotation = rig.Body * smoothed * correction * ikOffsetInverse;
            return true;
        }

        public void Apply(in BasisMediaPipeResult result, in MediaPipeTiming timing)
        {
            BasisLocalHandDriver driver = BasisLocalPlayer.Instance.LocalHandDriver;
            // Fingers only ever need the carry pass: the curl target is a plain function of the held landmarks,
            // so approaching it every rendered frame already turns the camera's steps into continuous motion.
            float alpha = BasisFilterMath.Alpha(
                Mathf.Min(Mathf.Lerp(CutoffResponsive, CutoffSmooth, Mathf.Clamp01(FingerSmoothing)), timing.CarryCutoff),
                timing.RenderDelta);

            Vector3[] left = Fingers(result.LeftHandWorldLandmarks, result.LeftHandLandmarks);
            if (result.HasLeftHand && left != null)
            {
                ApplyHand(left, driver.LeftHand, true, alpha);
            }

            Vector3[] right = Fingers(result.RightHandWorldLandmarks, result.RightHandLandmarks);
            if (result.HasRightHand && right != null)
            {
                ApplyHand(right, driver.RightHand, false, alpha);
            }
        }

        private static Vector3[] Fingers(Vector3[] world, Vector3[] image)
        {
            if (world != null && world.Length >= MediaPipeSpace.HandCount) return world;
            return image != null && image.Length >= MediaPipeSpace.HandCount ? image : null;
        }

        private void ApplyHand(Vector3[] lm, BasisFingerPose pose, bool isLeft, float t)
        {
            pose.ThumbPercentage = Vector2.Lerp(pose.ThumbPercentage, new Vector2(Curl(lm, 1, 2, 3, 4, ThumbMaxAngle), Splay(lm, 2, 3, 5, 6, isLeft)), t);
            pose.IndexPercentage = Vector2.Lerp(pose.IndexPercentage, new Vector2(Curl(lm, 5, 6, 7, 8, FingerMaxAngle), Splay(lm, 5, 6, 9, 10, isLeft)), t);
            pose.MiddlePercentage = Vector2.Lerp(pose.MiddlePercentage, new Vector2(Curl(lm, 9, 10, 11, 12, FingerMaxAngle), 0f), t);
            pose.RingPercentage = Vector2.Lerp(pose.RingPercentage, new Vector2(Curl(lm, 13, 14, 15, 16, FingerMaxAngle), Splay(lm, 13, 14, 9, 10, isLeft)), t);
            pose.LittlePercentage = Vector2.Lerp(pose.LittlePercentage, new Vector2(Curl(lm, 17, 18, 19, 20, FingerMaxAngle), Splay(lm, 17, 18, 13, 14, isLeft)), t);
        }

        private float Curl(Vector3[] lm, int a, int b, int c, int d, float maxAngle)
        {
            Vector3 s1 = lm[b] - lm[a];
            Vector3 s2 = lm[c] - lm[b];
            Vector3 s3 = lm[d] - lm[c];
            float angle = Vector3.Angle(s1, s2) + Vector3.Angle(s2, s3);
            float curl01 = Mathf.Clamp01(angle / maxAngle * CurlGain);
            return 1f - curl01 * 2f;
        }

        private float Splay(Vector3[] lm, int baseMcp, int basePip, int refMcp, int refPip, bool isLeft)
        {
            Vector3 dir = lm[basePip] - lm[baseMcp];
            Vector3 refDir = lm[refPip] - lm[refMcp];
            Vector3 palmNormal = Vector3.Cross(
                lm[MediaPipeSpace.HandIndexMcp] - lm[MediaPipeSpace.HandWrist],
                lm[MediaPipeSpace.HandPinkyMcp] - lm[MediaPipeSpace.HandWrist]);
            float signed = Vector3.SignedAngle(refDir, dir, palmNormal);
            float splay = Mathf.Clamp(signed / MaxSplayDegrees * SplayGain, -1f, 1f);
            return isLeft ? splay : -splay;
        }
    }
}
