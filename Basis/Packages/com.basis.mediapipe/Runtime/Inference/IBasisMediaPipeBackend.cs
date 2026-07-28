using UnityEngine;

namespace Basis.MediaPipe
{
    /// <summary>Abstraction over the landmark inference engine (homuler MediaPipe, or a no-op).</summary>
    public interface IBasisMediaPipeBackend
    {
        bool IsAvailable { get; }
        string BackendName { get; }

        void Initialize(BasisMediaPipeConfig config);
        void SubmitFrame(WebCamTexture frame, double timestampMs);
        bool TryGetLatestResult(out BasisMediaPipeResult result);
        void Shutdown();

        /// <summary>
        /// Where the last frame's milliseconds went, stage by stage, for the diagnostics readout. Empty when the
        /// backend doesn't measure. The tracking rate is the one number that decides whether smoothing work is
        /// even worth doing, so it needs to be attributable, not just observed.
        /// </summary>
        string TimingBreakdown();
    }
}
