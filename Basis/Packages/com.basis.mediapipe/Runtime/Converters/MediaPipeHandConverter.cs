using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using UnityEngine;
namespace Basis.MediaPipe
{
    public sealed class MediaPipeHandConverter
    {
        public float CurlGain = 1f, ThumbMaxAngle = 100f, FingerMaxAngle = 220f, MaxSplayDegrees = 20f, SplayGain = 1f, ThumbSpreadMinDegrees = 10f, ThumbSpreadMaxDegrees = 70f, ThumbAcrossStart = 0.1f, ThumbAcrossEnd = -0.8f;
        public bool UseRotation = true, RejectGlitches = true;
        private const float HoldSeconds = 0.5f, RelaxHz = 3f, RelaxedCurl = 0.6f;
        private const int FingerChannels = 10;
        private MediaPipeRotationFilter leftRot, rightRot;
        private readonly MediaPipeScalarFilter[] leftFingers = new MediaPipeScalarFilter[FingerChannels], rightFingers = new MediaPipeScalarFilter[FingerChannels];
        private float leftLost, rightLost;
        private static readonly int[] fingerBase = { 1, 5, 9, 13, 17 }, fingerTip = { 4, 8, 12, 16, 20 };
        private static readonly Vector3[] solveBases = new Vector3[5], solveTips = new Vector3[5];
        private readonly Vector2[] leftSolved = BasisFingerTipRetarget.NewState(), rightSolved = BasisFingerTipRetarget.NewState();
        public int RejectedSamples => leftRot.Rejected + rightRot.Rejected;
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
            for (int i = 0; i < 5; i++) leftSolved[i] = rightSolved[i] = new Vector2(0.75f, 0f);
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

            float maxTurn = RejectGlitches ? MediaPipeFilterMath.MaxTurnDegPerSec : 0f;
            Quaternion smoothed = left ? leftRot.Apply(handInBody, in timing, maxTurn) : rightRot.Apply(handInBody, in timing, maxTurn);

            // `rig.Body * smoothed * correction` is the finished HAND BONE rotation. The IK will multiply its own
            // palm->bone offset onto whatever we report, so pre-cancel it here (see AvatarHandRig).
            rotation = rig.Body * smoothed * correction * ikOffsetInverse;
            return true;
        }
        public void Apply(in BasisMediaPipeResult result, in MediaPipeTiming timing)
        {
            BasisLocalHandDriver driver = BasisLocalPlayer.Instance.LocalHandDriver;
            Vector3[] left = Fingers(result.LeftHandWorldLandmarks, result.LeftHandLandmarks);
            if (result.HasLeftHand && left != null)
            {
                leftLost = 0f;
                ApplyHand(left, driver.LeftHand, driver.TipRetarget, 0, leftSolved, leftFingers, in timing);
            }
            else leftLost = RelaxHand(driver.LeftHand, leftFingers, leftLost, in timing);

            Vector3[] right = Fingers(result.RightHandWorldLandmarks, result.RightHandLandmarks);
            if (result.HasRightHand && right != null)
            {
                rightLost = 0f;
                ApplyHand(right, driver.RightHand, driver.TipRetarget, 5, rightSolved, rightFingers, in timing);
            }
            else rightLost = RelaxHand(driver.RightHand, rightFingers, rightLost, in timing);
        }
        private static Vector3[] Fingers(Vector3[] world, Vector3[] image)
        {
            if (world != null && world.Length >= MediaPipeSpace.HandCount) return world;
            return image != null && image.Length >= MediaPipeSpace.HandCount ? image : null;
        }
        public bool TryMeasure(Vector3[] lm, out Vector2 thumb, out Vector2 index, out Vector2 middle, out Vector2 ring, out Vector2 little)
        {
            thumb = index = middle = ring = little = default;
            Vector3 radial = lm[MediaPipeSpace.HandIndexMcp] - lm[MediaPipeSpace.HandPinkyMcp];
            float palmWidth = radial.magnitude;
            if (palmWidth < 1e-5f) return false;
            radial /= palmWidth;
            Vector3 normal = Vector3.Cross(lm[MediaPipeSpace.HandMiddleMcp] - lm[MediaPipeSpace.HandWrist], radial);
            float normalLength = normal.magnitude;
            if (normalLength < 1e-6f) return false;
            normal /= normalLength;
            float thumbCurl = ThumbCurl(lm, radial, palmWidth, out float across);
            thumb = new Vector2(thumbCurl, ThumbSpread(lm, across));
            index = new Vector2(Curl(lm, 5, 6, 7, 8, radial), Splay(lm, 5, 6, 9, 10, -radial, normal));
            middle = new Vector2(Curl(lm, 9, 10, 11, 12, radial), 0f);
            ring = new Vector2(Curl(lm, 13, 14, 15, 16, radial), Splay(lm, 13, 14, 9, 10, -radial, normal));
            little = new Vector2(Curl(lm, 17, 18, 19, 20, radial), Splay(lm, 17, 18, 13, 14, -radial, normal));
            return true;
        }
        public static bool TrySolve(Vector3[] lm, BasisFingerTipRetarget retarget, int fingerOffset, Vector2[] solved)
        {
            if (retarget == null || !retarget.IsCreated) return false;
            for (int f = 0; f < 5; f++)
            {
                solveBases[f] = lm[fingerBase[f]];
                solveTips[f] = lm[fingerTip[f]];
            }
            return retarget.SolveHand(fingerOffset, lm[MediaPipeSpace.HandWrist], lm[MediaPipeSpace.HandIndexMcp], lm[MediaPipeSpace.HandMiddleMcp], lm[MediaPipeSpace.HandPinkyMcp], solveBases, solveTips, solved);
        }
        private void ApplyHand(Vector3[] lm, BasisFingerPose pose, BasisFingerTipRetarget retarget, int fingerOffset, Vector2[] solved, MediaPipeScalarFilter[] filters, in MediaPipeTiming timing)
        {
            if (pose == null) return;
            Vector2 thumb, index, middle, ring, little;
            if (TrySolve(lm, retarget, fingerOffset, solved))
            {
                thumb = solved[0];
                index = solved[1];
                middle = solved[2];
                ring = solved[3];
                little = solved[4];
            }
            else if (!TryMeasure(lm, out thumb, out index, out middle, out ring, out little)) return;
            pose.ThumbPercentage = Finger(filters, 0, thumb.x, thumb.y, in timing);
            pose.IndexPercentage = Finger(filters, 2, index.x, index.y, in timing);
            pose.MiddlePercentage = Finger(filters, 4, middle.x, middle.y, in timing);
            pose.RingPercentage = Finger(filters, 6, ring.x, ring.y, in timing);
            pose.LittlePercentage = Finger(filters, 8, little.x, little.y, in timing);
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
        private static Vector2 Finger(MediaPipeScalarFilter[] filters, int slot, float curl, float splay, in MediaPipeTiming timing) => new Vector2(filters[slot].Apply(curl, in timing), filters[slot + 1].Apply(splay, in timing));
        private float Curl(Vector3[] lm, int mcp, int pip, int dip, int tip, Vector3 radial)
        {
            Vector3 s0 = lm[mcp] - lm[MediaPipeSpace.HandWrist], s1 = lm[pip] - lm[mcp], s2 = lm[dip] - lm[pip], s3 = lm[tip] - lm[dip];
            float angle = Vector3.Angle(Vector3.ProjectOnPlane(s0, radial), Vector3.ProjectOnPlane(s1, radial)) + Vector3.Angle(s1, s2) + Vector3.Angle(s2, s3);
            return 1f - Mathf.Clamp01(angle / FingerMaxAngle * CurlGain) * 2f;
        }
        private float ThumbCurl(Vector3[] lm, Vector3 radial, float palmWidth, out float across)
        {
            float lateral = Vector3.Dot(lm[4] - lm[MediaPipeSpace.HandIndexMcp], radial) / palmWidth;
            across = Mathf.Clamp01((lateral - ThumbAcrossStart) / (ThumbAcrossEnd - ThumbAcrossStart));
            float bend = Mathf.Clamp01((Vector3.Angle(lm[2] - lm[1], lm[3] - lm[2]) + Vector3.Angle(lm[3] - lm[2], lm[4] - lm[3])) / ThumbMaxAngle * CurlGain);
            return (1f - bend) * (1f - across) * 2f - 1f;
        }
        private float ThumbSpread(Vector3[] lm, float across)
        {
            float angle = Vector3.Angle(lm[3] - lm[2], lm[MediaPipeSpace.HandIndexMcp] - lm[MediaPipeSpace.HandWrist]);
            float spread = Mathf.Clamp(((angle - ThumbSpreadMinDegrees) / (ThumbSpreadMaxDegrees - ThumbSpreadMinDegrees) * 2f - 1f) * SplayGain, -1f, 1f);
            return Mathf.Lerp(spread, -1f, across);
        }
        private float Splay(Vector3[] lm, int baseMcp, int basePip, int refMcp, int refPip, Vector3 outward, Vector3 normal)
        {
            Vector3 raw = lm[basePip] - lm[baseMcp], dir = Vector3.ProjectOnPlane(raw, normal), refDir = Vector3.ProjectOnPlane(lm[refPip] - lm[refMcp], normal);
            float rawLength = raw.magnitude;
            if (rawLength < 1e-6f) return 0f;
            float signed = Vector3.SignedAngle(refDir, dir, normal) * Mathf.Sign(Vector3.Dot(Vector3.Cross(refDir, outward), normal));
            return Mathf.Clamp(signed / MaxSplayDegrees * SplayGain, -1f, 1f) * (dir.magnitude / rawLength);
        }
    }
}
