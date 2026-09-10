using UnityEngine;
namespace Basis.MediaPipe
{
    public static class MediaPipeGaze
    {
        public const int LandmarkCount = 478;
        public const int RightIris = 468, RightImageLeftCorner = 33, RightImageRightCorner = 133, RightUpperLid = 159, RightLowerLid = 145;
        public const int LeftIris = 473, LeftImageLeftCorner = 362, LeftImageRightCorner = 263, LeftUpperLid = 386, LeftLowerLid = 374;
        public const float ClosedLidRatio = 0.12f;
        public static bool TryMeasure(Vector2 imageLeftCorner, Vector2 imageRightCorner, Vector2 upperLid, Vector2 lowerLid, Vector2 iris, float aspect, out Vector2 gaze)
        {
            gaze = Vector2.zero;
            Vector2 a = Px(imageLeftCorner, aspect), b = Px(imageRightCorner, aspect), axis = b - a;
            float width = axis.magnitude;
            if (!(width > 1e-5f)) return false;
            axis /= width;
            Vector2 up = new Vector2(axis.y, -axis.x), offset = Px(iris, aspect) - (a + b) * 0.5f;
            float half = width * 0.5f;
            Vector2 measured = new Vector2(Vector2.Dot(offset, axis) / half, Vector2.Dot(offset, up) / half);
            if (!float.IsFinite(measured.x) || !float.IsFinite(measured.y)) return false;
            gaze = measured;
            float gap = Mathf.Abs(Vector2.Dot(Px(lowerLid, aspect) - Px(upperLid, aspect), up));
            return gap / width >= ClosedLidRatio;
        }
        private static Vector2 Px(Vector2 normalized, float aspect) => new Vector2(normalized.x * aspect, normalized.y);
    }
}
