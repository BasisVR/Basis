using UnityEngine;
namespace Basis.MediaPipe
{
    public sealed class MediaPipeHeadConverter
    {
        public float YawGain = 1f, PitchGain = 1f, RollGain = 1f, PositionGain = 1f, HeightOffset = 0f, Smoothing = 0.5f;
        public bool InvertYaw = false, InvertPitch = true, InvertRoll = false, RejectGlitches = true;
        private const float CutoffResponsive = 8f, CutoffSmooth = 1f, Beta = 1.5f, DepthCutoffScale = 0.4f, HoldSeconds = 0.5f, FadeInHz = 6f, FadeOutHz = 4f;
        private Quaternion neutralInverse = Quaternion.identity;
        private Vector3 neutralPosition;
        private bool calibrated;
        private MediaPipeRotationFilter rotation;
        private MediaPipePositionFilter position;
        private MediaPipePresenceFade presence;
        public int RejectedSamples => rotation.Rejected + position.Rejected;
        private float Cutoff => Mathf.Lerp(CutoffResponsive, CutoffSmooth, Mathf.Clamp01(Smoothing));
        public void Calibrate(in BasisMediaPipeResult result)
        {
            if (!result.HasFace || !MediaPipeSpace.IsUsable(result.FaceTransform)) return;
            neutralInverse = Quaternion.Inverse(result.FaceTransform.rotation);
            neutralPosition = result.FaceTransform.GetColumn(3);
            calibrated = true;
            rotation.Reset();
            position.Reset();
        }
        public void Reset()
        {
            rotation.Reset();
            position.Reset();
            presence.Reset();
        }
        public bool TryGetHeadOffset(in BasisMediaPipeResult result, in MediaPipeTiming timing, out Quaternion rotationOffset, out Vector3 positionOffset)
        {
            rotationOffset = Quaternion.identity;
            positionOffset = Vector3.zero;
            bool present = result.HasFace && MediaPipeSpace.IsUsable(result.FaceTransform);
            float weight = presence.Step(present, timing.RenderDelta, HoldSeconds, FadeInHz, FadeOutHz);
            if (weight <= 0f)
            {
                rotation.Reset();
                position.Reset();
                return false;
            }
            Quaternion relative;
            Vector3 shift;
            if (present)
            {
                Quaternion headRot = result.FaceTransform.rotation;
                Vector3 translation = result.FaceTransform.GetColumn(3);
                Vector3 delta = calibrated ? translation - neutralPosition : Vector3.zero;
                float cutoff = timing.Scaled(Cutoff);
                relative = rotation.Apply(calibrated ? neutralInverse * headRot : headRot, in timing, cutoff, Beta, RejectGlitches ? MediaPipeFilterMath.MaxTurnDegPerSec : 0f);
                shift = position.Apply(new Vector3(-delta.x, 0f, -delta.z) * 0.01f, in timing, cutoff, Beta, DepthCutoffScale, RejectGlitches ? MediaPipeFilterMath.MaxHeadSpeed : 0f);
            }
            else
            {
                relative = rotation.Carry(in timing);
                shift = position.Carry(in timing);
            }
            Vector3 euler = relative.eulerAngles;
            float pitch = NormalizeAngle(euler.x) * (InvertPitch ? -1f : 1f) * PitchGain, yaw = NormalizeAngle(euler.y) * (InvertYaw ? -1f : 1f) * YawGain, roll = NormalizeAngle(euler.z) * (InvertRoll ? -1f : 1f) * RollGain;
            rotationOffset = Quaternion.Slerp(Quaternion.identity, Quaternion.Euler(pitch, yaw, roll), weight);
            positionOffset = new Vector3(shift.x * PositionGain, HeightOffset, shift.z * PositionGain) * weight;
            return true;
        }
        private static float NormalizeAngle(float angle) => angle > 180f ? angle - 360f : angle;
    }
}
