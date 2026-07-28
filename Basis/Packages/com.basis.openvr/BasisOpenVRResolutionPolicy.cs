namespace Basis.Scripts.Device_Management.Devices.OpenVR
{
    public static class BasisOpenVRResolutionPolicy
    {
        public const float DefaultDeadband = 0.02f;

        public static float LargestSpan(float leftMin, float leftMax, float rightMin, float rightMax)
        {
            float left = leftMax - leftMin;
            float right = rightMax - rightMin;
            return left > right ? left : right;
        }

        public static float GrowForLensOverlap(float recommended, float boundsSpan)
        {
            if (recommended <= 0f)
            {
                return 0f;
            }
            if (boundsSpan <= 0f)
            {
                return recommended;
            }
            return recommended / boundsSpan;
        }

        public static bool TryComputeEyeTextureScale(float targetMax, float currentMax, float currentScale, float deadband, out float newScale)
        {
            newScale = currentScale;

            if (targetMax <= 0f || currentMax <= 0f)
            {
                return false;
            }

            if (currentScale <= 0f)
            {
                currentScale = 1f;
                newScale = 1f;
            }

            float difference = targetMax - currentMax;
            if (difference < 0f)
            {
                difference = -difference;
            }

            if (deadband < 0f)
            {
                deadband = 0f;
            }

            if (difference <= targetMax * deadband)
            {
                return false;
            }

            float baseMax = currentMax / currentScale;
            if (baseMax <= 0f)
            {
                return false;
            }

            newScale = targetMax / baseMax;
            return true;
        }
    }
}
