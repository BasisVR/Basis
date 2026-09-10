using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using UnityEngine;
namespace Basis.MediaPipe
{
    public sealed class MediaPipeHandConverter
    {
        public float CurlGain = 1f, ThumbMaxAngle = 100f, FingerMaxAngle = 160f, MaxSplayDegrees = 20f, SplayGain = 1f;
        public float FingerSmoothing = 0.5f, PoseSmoothing = 0.5f;
        public bool UseRotation = true, RejectGlitches = true;
        private const float CutoffResponsive = 10f, CutoffSmooth = 1.5f, Beta = 3.25f, FingerBeta = 0.5f, HoldSeconds = 0.5f, RelaxHz = 3f, RelaxedCurl = 0.6f;
        private const int FingerChannels = 10;
        private MediaPipeRotationFilter leftRot, rightRot;
        private readonly MediaPipeScalarFilter[] leftFingers = new MediaPipeScalarFilter[FingerChannels], rightFingers = new MediaPipeScalarFilter[FingerChannels];
        private float leftLost, rightLost;
        public int RejectedSamples => leftRot.Rejected + rightRot.Rejected;
        private float RotationCutoff => Mathf.Lerp(CutoffResponsive, CutoffSmooth, Mathf.Clamp01(PoseSmoothing));
        private float FingerCutoff => Mathf.Lerp(CutoffResponsive, CutoffSmooth, Mathf.Clamp01(FingerSmoothing));
        public struct AvatarHandRig
        {
            public Quaternion Body, LeftCorrection, RightCorrection, LeftIkOffsetInverse, RightIkOffsetInverse;
            public bool Valid;
        }
        public void Reset()
        {
            leftRot.Reset();
            rightRot.Reset();
            for (int i = 0; i < FingerChannels; i++)
            {
                leftFingers[i].Reset();
                rightFingers[i].Reset();
            }
            leftLost = rightLost = 0f;
        }
        public bool TryGetHandRotation(in BasisMediaPipeResult result, in AvatarHandRig rig, bool left, in MediaPipeTiming timing, out Quaternion rotation)
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
            float ikSqrNorm = ikOffsetInverse.x * ikOffsetInverse.x + ikOffsetInverse.y * ikOffsetInverse.y + ikOffsetInverse.z * ikOffsetInverse.z + ikOffsetInverse.w * ikOffsetInverse.w;
            if (ikSqrNorm < 0.5f) ikOffsetInverse = Quaternion.identity;

            float cutoff = timing.Scaled(RotationCutoff), maxTurn = RejectGlitches ? MediaPipeFilterMath.MaxTurnDegPerSec : 0f;
            Quaternion smoothed = left ? leftRot.Apply(handInBody, in timing, cutoff, Beta, maxTurn) : rightRot.Apply(handInBody, in timing, cutoff, Beta, maxTurn);

            // `rig.Body * smoothed * correction` is the finished HAND BONE rotation. The IK will multiply its own
            // palm->bone offset onto whatever we report, so pre-cancel it here (see AvatarHandRig).
            rotation = rig.Body * smoothed * correction * ikOffsetInverse;
            return true;
        }
        public void Apply(in BasisMediaPipeResult result, in MediaPipeTiming timing)
        {
            BasisLocalHandDriver driver = BasisLocalPlayer.Instance.LocalHandDriver;
            float cutoff = timing.Scaled(FingerCutoff);
            Vector3[] left = Fingers(result.LeftHandWorldLandmarks, result.LeftHandLandmarks);
            if (result.HasLeftHand && left != null)
            {
                leftLost = 0f;
                ApplyHand(left, driver.LeftHand, true, leftFingers, cutoff, in timing);
            }
            else leftLost = RelaxHand(driver.LeftHand, leftFingers, leftLost, in timing);

            Vector3[] right = Fingers(result.RightHandWorldLandmarks, result.RightHandLandmarks);
            if (result.HasRightHand && right != null)
            {
                rightLost = 0f;
                ApplyHand(right, driver.RightHand, false, rightFingers, cutoff, in timing);
            }
            else rightLost = RelaxHand(driver.RightHand, rightFingers, rightLost, in timing);
        }
        private static Vector3[] Fingers(Vector3[] world, Vector3[] image)
        {
            if (world != null && world.Length >= MediaPipeSpace.HandCount) return world;
            return image != null && image.Length >= MediaPipeSpace.HandCount ? image : null;
        }
        private void ApplyHand(Vector3[] lm, BasisFingerPose pose, bool isLeft, MediaPipeScalarFilter[] filters, float cutoff, in MediaPipeTiming timing)
        {
            pose.ThumbPercentage = Finger(filters, 0, Curl(lm, 1, 2, 3, 4, ThumbMaxAngle), Splay(lm, 2, 3, 5, 6, isLeft), cutoff, in timing);
            pose.IndexPercentage = Finger(filters, 2, Curl(lm, 5, 6, 7, 8, FingerMaxAngle), Splay(lm, 5, 6, 9, 10, isLeft), cutoff, in timing);
            pose.MiddlePercentage = Finger(filters, 4, Curl(lm, 9, 10, 11, 12, FingerMaxAngle), 0f, cutoff, in timing);
            pose.RingPercentage = Finger(filters, 6, Curl(lm, 13, 14, 15, 16, FingerMaxAngle), Splay(lm, 13, 14, 9, 10, isLeft), cutoff, in timing);
            pose.LittlePercentage = Finger(filters, 8, Curl(lm, 17, 18, 19, 20, FingerMaxAngle), Splay(lm, 17, 18, 13, 14, isLeft), cutoff, in timing);
        }
        // A hand that has left the frame holds briefly, then eases to a relaxed pose instead of freezing mid-gesture.
        private static float RelaxHand(BasisFingerPose pose, MediaPipeScalarFilter[] filters, float lost, in MediaPipeTiming timing)
        {
            lost += timing.RenderDelta;
            if (pose == null || !filters[0].HasSample) return lost;
            if (lost <= HoldSeconds)
            {
                pose.ThumbPercentage = new Vector2(filters[0].Carry(in timing), filters[1].Carry(in timing));
                pose.IndexPercentage = new Vector2(filters[2].Carry(in timing), filters[3].Carry(in timing));
                pose.MiddlePercentage = new Vector2(filters[4].Carry(in timing), filters[5].Carry(in timing));
                pose.RingPercentage = new Vector2(filters[6].Carry(in timing), filters[7].Carry(in timing));
                pose.LittlePercentage = new Vector2(filters[8].Carry(in timing), filters[9].Carry(in timing));
                return lost;
            }
            float alpha = BasisFilterMath.Alpha(RelaxHz, timing.RenderDelta);
            pose.ThumbPercentage = new Vector2(filters[0].Relax(RelaxedCurl, alpha), filters[1].Relax(0f, alpha));
            pose.IndexPercentage = new Vector2(filters[2].Relax(RelaxedCurl, alpha), filters[3].Relax(0f, alpha));
            pose.MiddlePercentage = new Vector2(filters[4].Relax(RelaxedCurl, alpha), filters[5].Relax(0f, alpha));
            pose.RingPercentage = new Vector2(filters[6].Relax(RelaxedCurl, alpha), filters[7].Relax(0f, alpha));
            pose.LittlePercentage = new Vector2(filters[8].Relax(RelaxedCurl, alpha), filters[9].Relax(0f, alpha));
            return lost;
        }
        private static Vector2 Finger(MediaPipeScalarFilter[] filters, int slot, float curl, float splay, float cutoff, in MediaPipeTiming timing) => new Vector2(filters[slot].Apply(curl, in timing, cutoff, FingerBeta), filters[slot + 1].Apply(splay, in timing, cutoff, FingerBeta));
        private float Curl(Vector3[] lm, int a, int b, int c, int d, float maxAngle)
        {
            Vector3 s1 = lm[b] - lm[a], s2 = lm[c] - lm[b], s3 = lm[d] - lm[c];
            float angle = Vector3.Angle(s1, s2) + Vector3.Angle(s2, s3);
            float curl01 = Mathf.Clamp01(angle / maxAngle * CurlGain);
            return 1f - curl01 * 2f;
        }
        private float Splay(Vector3[] lm, int baseMcp, int basePip, int refMcp, int refPip, bool isLeft)
        {
            Vector3 dir = lm[basePip] - lm[baseMcp], refDir = lm[refPip] - lm[refMcp];
            Vector3 palmNormal = Vector3.Cross(lm[MediaPipeSpace.HandIndexMcp] - lm[MediaPipeSpace.HandWrist], lm[MediaPipeSpace.HandPinkyMcp] - lm[MediaPipeSpace.HandWrist]);
            float signed = Vector3.SignedAngle(refDir, dir, palmNormal);
            float splay = Mathf.Clamp(signed / MaxSplayDegrees * SplayGain, -1f, 1f);
            return isLeft ? splay : -splay;
        }
    }
}
