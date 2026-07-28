using UnityEngine;

namespace Basis.MediaPipe
{
    /// <summary>Fallback backend used when the MediaPipe Unity Plugin is not installed.</summary>
    public sealed class BasisMediaPipeNullBackend : IBasisMediaPipeBackend
    {
        public bool IsAvailable => false;
        public string BackendName => "None (MediaPipe plugin not installed)";
        public void Initialize(BasisMediaPipeConfig config) { }
        public void SubmitFrame(WebCamTexture frame, double timestampMs) { }
        public bool TryGetLatestResult(out BasisMediaPipeResult result) { result = default; return false; }
        public void Shutdown() { }
        public string TimingBreakdown() => string.Empty;
    }
}
