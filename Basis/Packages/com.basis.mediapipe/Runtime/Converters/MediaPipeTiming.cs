using UnityEngine;

namespace Basis.MediaPipe
{
    /// <summary>
    /// Webcam tracking arrives far slower than the game renders — three models share one worker thread, so the
    /// real rate is often well under the camera's 30 fps — and the same result is re-applied every frame until
    /// the next one lands. Filters therefore have to know the difference between the two clocks.
    ///
    /// Running a filter at render rate over a held sample is what makes the hands stutter: the signal is a
    /// staircase, so a one-euro filter reads each new sample as a burst of speed, opens its cutoff and snaps to
    /// it, then sits still until the next one. Smoothing must run on the SAMPLE clock, and a separate pass on
    /// the RENDER clock carries the pose between samples.
    /// </summary>
    public readonly struct MediaPipeTiming
    {
        /// <summary>Seconds since the last rendered frame.</summary>
        public readonly float RenderDelta;

        /// <summary>Seconds between the last two camera samples, smoothed. Never the render delta.</summary>
        public readonly float SampleDelta;

        /// <summary>True only on the frame a fresh camera result landed.</summary>
        public readonly bool IsNewSample;

        public MediaPipeTiming(float renderDelta, float sampleDelta, bool isNewSample)
        {
            RenderDelta = Mathf.Max(renderDelta, 1e-4f);
            SampleDelta = Mathf.Clamp(sampleDelta, 1e-3f, 0.5f);
            IsNewSample = isNewSample;
        }

        /// <summary>
        /// Cutoff for the render-rate pass, in Hz. Its time constant tracks the camera's actual period, so the
        /// pose is still travelling toward the last sample when the next one arrives — which is what turns a
        /// staircase back into continuous motion — without lagging further than it has to.
        /// </summary>
        public float CarryCutoff => 1f / (2f * Mathf.PI * SampleDelta * CarryPeriods);

        private const float CarryPeriods = 0.7f;

        public float TrackingHz => 1f / SampleDelta;
    }
}
