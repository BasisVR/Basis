using UnityEngine;
namespace Basis.MediaPipe
{
    public sealed class MediaPipeBodyConverter
    {
        public float Strength = 0.6f, MaxAngle = 35f, Smoothing = 0.7f;
        public bool InvertTwist = false, InvertLean = false, InvertRoll = false, RejectGlitches = true;
        private const float CutoffResponsive = 6f, CutoffSmooth = 0.6f, Beta = 1.5f, HoldSeconds = 0.5f, FadeInHz = 6f, FadeOutHz = 3f;
        private Quaternion neutralInverse = Quaternion.identity;
        private bool calibrated;
        private MediaPipeRotationFilter filter;
        private MediaPipePresenceFade presence;
        public int RejectedSamples => filter.Rejected;
        private float Cutoff => Mathf.Lerp(CutoffResponsive, CutoffSmooth, Mathf.Clamp01(Smoothing));
        public void Calibrate(in BasisMediaPipeResult result)
        {
            if (TryTorsoRotation(result, out Quaternion rot))
            {
                neutralInverse = Quaternion.Inverse(rot);
                calibrated = true;
                filter.Reset();
            }
        }
        public void Reset()
        {
            calibrated = false;
            filter.Reset();
            presence.Reset();
        }
        public bool TryGetTorsoOffset(in BasisMediaPipeResult result, in MediaPipeTiming timing, out Quaternion offset)
        {
            offset = Quaternion.identity;
            bool present = TryTorsoRotation(result, out Quaternion rot);
            float weight = presence.Step(present, timing.RenderDelta, HoldSeconds, FadeInHz, FadeOutHz);
            if (weight <= 0f)
            {
                filter.Reset();
                return false;
            }
            Quaternion relative;
            if (present)
            {
                if (!calibrated)
                {
                    neutralInverse = Quaternion.Inverse(rot);
                    calibrated = true;
                }
                relative = filter.Apply(neutralInverse * rot, in timing, timing.Scaled(Cutoff), Beta, RejectGlitches ? MediaPipeFilterMath.MaxTurnDegPerSec : 0f);
            }
            else relative = filter.Carry(in timing);
            Vector3 euler = relative.eulerAngles;
            Quaternion target = Quaternion.Euler(Axis(euler.x, InvertLean), Axis(euler.y, InvertTwist), Axis(euler.z, InvertRoll));
            offset = Quaternion.Slerp(Quaternion.identity, target, weight);
            return true;
        }
        private float Axis(float raw, bool invert)
        {
            float angle = raw > 180f ? raw - 360f : raw;
            angle = Mathf.Clamp(angle, -MaxAngle, MaxAngle);
            return angle * (invert ? -1f : 1f) * Strength;
        }
        private static bool TryTorsoRotation(in BasisMediaPipeResult result, out Quaternion rot)
        {
            rot = Quaternion.identity;
            if (!result.HasPose) return false;
            return MediaPipeSpace.TryBodyFrame(result.PoseWorldLandmarks, out _, out rot);
        }
    }
}
