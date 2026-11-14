using UnityEngine;
namespace Basis.MediaPipe
{
    /// <summary>
    /// Shape cues read straight from the face mesh, for channels the blendshape model under-reports. All values are
    /// ratios measured along the face's own up axis, so they hold under roll and camera distance; yaw and pitch still
    /// shift them, which the face converter handles by only trusting them near the user's neutral head pose.
    /// Nose lift is scaled by the inner eye corner gap rather than face height, since an open jaw stretches the face.
    /// </summary>
    public static class MediaPipeFaceGeometry
    {
        public const int Forehead = 10, Chin = 152, NoseTip = 1, RightEdge = 234, LeftEdge = 454;
        public const int RightJaw = 58, LeftJaw = 288, RightLowerCheek = 132, LeftLowerCheek = 361;
        public const int RightAlar = 98, LeftAlar = 327, RightInnerEye = 133, LeftInnerEye = 362;
        public const int LandmarkCount = 468;

        /// <summary>Cheek contour width at mouth level over the width at the cheekbones; puffed cheeks widen it.</summary>
        public static float CheekWidth(Vector2 rightEdge, Vector2 leftEdge, Vector2 rightJaw, Vector2 leftJaw, Vector2 rightLowerCheek, Vector2 leftLowerCheek, Vector2 across)
        {
            float upper = Mathf.Abs(Vector2.Dot(leftEdge - rightEdge, across));
            if (!(upper > 1e-5f)) return float.NaN;
            return (Mathf.Abs(Vector2.Dot(leftJaw - rightJaw, across)) + Mathf.Abs(Vector2.Dot(leftLowerCheek - rightLowerCheek, across))) / (2f * upper);
        }

        /// <summary>Rises as the nostril wing lifts toward the inner eye corner, as in a sneer.</summary>
        public static float NoseLift(Vector2 alar, Vector2 innerEye, Vector2 up, float eyeGap) => eyeGap > 1e-5f ? -Vector2.Dot(innerEye - alar, up) / eyeGap : float.NaN;

        /// <summary>Positive when the subject's left side of the face is the wider one in the image, so it faces the camera.</summary>
        public static float SideBalance(Vector2 noseTip, Vector2 rightEdge, Vector2 leftEdge, Vector2 across)
        {
            float right = Mathf.Abs(Vector2.Dot(rightEdge - noseTip, across)), left = Mathf.Abs(Vector2.Dot(leftEdge - noseTip, across));
            float sum = right + left;
            return sum > 1e-5f ? (left - right) / sum : 0f;
        }

        public static bool Axes(Vector2 forehead, Vector2 chin, out Vector2 up, out Vector2 across, out float faceHeight)
        {
            Vector2 axis = forehead - chin;
            faceHeight = axis.magnitude;
            if (!(faceHeight > 1e-4f))
            {
                up = across = Vector2.zero;
                return false;
            }
            up = axis / faceHeight;
            across = new Vector2(up.y, -up.x);
            return true;
        }
    }
}
