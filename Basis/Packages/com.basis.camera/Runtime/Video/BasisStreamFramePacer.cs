using UnityEngine;
namespace Basis
{
    public struct BasisStreamFramePacer
    {
        public const float IntervalSlack = 0.9f, MaxBankedIntervals = 2f;
        private float elapsed;
        public void Reset() => elapsed = 0f;
        public bool AllowThisFrame(float deltaTime, float frameRate, bool frameIsFresh, bool sinkIsReady)
        {
            float interval = frameRate > 0f ? 1f / frameRate : 0f;
            elapsed += deltaTime;
            if (interval > 0f && elapsed > interval * MaxBankedIntervals) elapsed = interval * MaxBankedIntervals;

            if (!frameIsFresh || !sinkIsReady) return false;
            if (interval > 0f && elapsed < interval * IntervalSlack) return false;

            elapsed = interval > 0f ? Mathf.Max(0f, elapsed - interval) : 0f;
            return true;
        }
    }
}
