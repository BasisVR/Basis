using Basis.Scripts.Drivers;
using UnityEngine;
namespace Basis.MediaPipe
{
    /// <summary>
    /// Learns one person's rest level and peak for a face channel and rescales samples between them, so a weak
    /// expression still reaches full strength. Rest follows the low envelope; peak follows sustained highs and
    /// slowly falls back, never closer to rest than MinSpan so a silent channel is not amplified into noise.
    /// </summary>
    public struct MediaPipeExpressionRange
    {
        public const float RestFallHz = 0.5f, RestRiseHz = 0.005f, PeakRiseHz = 0.5f, PeakFallHz = 0.005f, RestBand = 0.25f, DeadZone = 0.05f;
        public float Rest, Peak, MinSpan;
        public bool Started;
        public void Start(float rest, float full, float minSpan)
        {
            Rest = rest;
            MinSpan = Mathf.Max(minSpan, 1e-4f);
            Peak = Mathf.Max(full, rest + MinSpan);
            Started = true;
        }
        public void Learn(float x, float dt)
        {
            if (!Started || !float.IsFinite(x) || !(dt > 0f)) return;
            float span = Peak - Rest;
            if (x < Rest) Rest += (x - Rest) * BasisFilterMath.Alpha(RestFallHz, dt);
            else if (x < Rest + span * RestBand) Rest += (x - Rest) * BasisFilterMath.Alpha(RestRiseHz, dt);
            if (x > Peak) Peak += (x - Peak) * BasisFilterMath.Alpha(PeakRiseHz, dt);
            else Peak += (Rest + MinSpan - Peak) * BasisFilterMath.Alpha(PeakFallHz, dt);
            if (Peak < Rest + MinSpan) Peak = Rest + MinSpan;
        }
        public float Normalize(float x)
        {
            if (!Started || !float.IsFinite(x)) return 0f;
            float v = (x - Rest) / (Peak - Rest);
            return Mathf.Clamp01((v - DeadZone) / (1f - DeadZone));
        }
    }
}
