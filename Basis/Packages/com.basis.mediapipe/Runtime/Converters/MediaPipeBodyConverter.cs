using UnityEngine;
namespace Basis.MediaPipe
{
    public sealed class MediaPipeBodyConverter
    {
        public float Strength = 0.6f, MaxAngle = 35f, Smoothing = 0.7f, MaxShift = 0.35f, AssumedHorizontalFov = 65f;
        public bool InvertTwist = false, InvertLean = false, InvertRoll = false, RejectGlitches = true;
        private const float CutoffResponsive = 6f, CutoffSmooth = 0.6f, Beta = 1.5f, HoldSeconds = 0.5f, FadeInHz = 6f, FadeOutHz = 3f;
        private Quaternion neutralInverse = Quaternion.identity;
        private bool calibrated, shiftCalibrated;
        private MediaPipeRotationFilter filter;
        private MediaPipePositionFilter translation;
        private MediaPipePresenceFade presence;
        private Vector2 neutralCenter;
        private float neutralWidth, neutralDistance;
        public int RejectedSamples => filter.Rejected + translation.Rejected;
        private float Cutoff => Mathf.Lerp(CutoffResponsive, CutoffSmooth, Mathf.Clamp01(Smoothing));
        public void Calibrate(in BasisMediaPipeResult result)
        {
            if (TryTorsoRotation(result, out Quaternion rot))
            {
                neutralInverse = Quaternion.Inverse(rot);
                calibrated = true;
                filter.Reset();
            }
            shiftCalibrated = false;
            translation.Reset();
        }
        public void Reset()
        {
            calibrated = false;
            shiftCalibrated = false;
            filter.Reset();
            translation.Reset();
            presence.Reset();
        }
        public bool TryGetTorsoOffset(in BasisMediaPipeResult result, in MediaPipeTiming timing, out Quaternion offset, out Vector3 shift)
        {
            offset = Quaternion.identity;
            shift = Vector3.zero;
            bool present = TryTorsoRotation(result, out Quaternion rot);
            float weight = presence.Step(present, timing.RenderDelta, HoldSeconds, FadeInHz, FadeOutHz);
            if (weight <= 0f)
            {
                filter.Reset();
                translation.Reset();
                return false;
            }
            Quaternion relative;
            Vector3 moved;
            if (present)
            {
                if (!calibrated)
                {
                    neutralInverse = Quaternion.Inverse(rot);
                    calibrated = true;
                }
                float cutoff = timing.Scaled(Cutoff);
                relative = filter.Apply(neutralInverse * rot, in timing, cutoff, Beta, RejectGlitches ? MediaPipeFilterMath.MaxTurnDegPerSec : 0f);
                moved = TryMeasureShift(in result, out Vector3 measured) ? translation.Apply(measured, in timing, cutoff, Beta, 1f, RejectGlitches ? MediaPipeFilterMath.MaxTorsoSpeed : 0f) : translation.Carry(in timing);
            }
            else
            {
                relative = filter.Carry(in timing);
                moved = translation.Carry(in timing);
            }
            Vector3 euler = relative.eulerAngles;
            Quaternion target = Quaternion.Euler(Axis(euler.x, InvertLean), Axis(euler.y, InvertTwist), Axis(euler.z, InvertRoll));
            offset = Quaternion.Slerp(Quaternion.identity, target, weight);
            shift = Vector3.ClampMagnitude(moved, MaxShift) * (Strength * weight);
            return true;
        }
        // Shoulder centre and shoulder width in the image, against the neutral captured at calibration. Lateral and
        // vertical movement come from the centre, scaled by the metric shoulder width; depth comes from how much
        // the shoulders shrink, against a distance estimated from an assumed webcam field of view. A shrug lifts
        // the shoulder centre and so lifts the chest, which is exactly what a shrug looks like.
        private bool TryMeasureShift(in BasisMediaPipeResult result, out Vector3 shift)
        {
            shift = Vector3.zero;
            Vector3[] image = result.PoseLandmarks, world = result.PoseWorldLandmarks;
            if (image == null || world == null || image.Length < MediaPipeSpace.PoseCount || world.Length < MediaPipeSpace.PoseCount) return false;
            float aspect = float.IsFinite(result.ImageAspect) && result.ImageAspect > 0f ? result.ImageAspect : 1f;
            Vector3 li = image[MediaPipeSpace.LeftShoulder], ri = image[MediaPipeSpace.RightShoulder];
            if (!MediaPipeSpace.IsFinite(li) || !MediaPipeSpace.IsFinite(ri) || !MediaPipeSpace.IsFinite(world[MediaPipeSpace.LeftShoulder]) || !MediaPipeSpace.IsFinite(world[MediaPipeSpace.RightShoulder])) return false;
            Vector2 l = new Vector2(li.x * aspect, li.y), r = new Vector2(ri.x * aspect, ri.y), center = (l + r) * 0.5f;
            float width = Vector2.Distance(l, r), metric = Vector3.Distance(world[MediaPipeSpace.LeftShoulder], world[MediaPipeSpace.RightShoulder]);
            if (!(width > 0.02f) || !(metric > 0.05f)) return false;
            if (!shiftCalibrated)
            {
                neutralCenter = center;
                neutralWidth = width;
                neutralDistance = metric / (2f * Mathf.Tan(AssumedHorizontalFov * 0.5f * Mathf.Deg2Rad) * (width / aspect));
                shiftCalibrated = true;
                return true;
            }
            Vector2 d = (center - neutralCenter) * (metric / width);
            float depth = neutralDistance * (neutralWidth / width - 1f);
            shift = new Vector3(-d.x, d.y, -depth);
            return MediaPipeSpace.IsFinite(shift);
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
