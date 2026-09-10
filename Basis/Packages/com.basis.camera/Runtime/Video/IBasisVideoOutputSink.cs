using UnityEngine;
namespace Basis
{
    public interface IBasisVideoOutputSink
    {
        string FailureMessage { get; }
        bool SupportsAlpha { get; }
        bool Start(BasisVideoOutputSettings settings, GameObject host);
        void PushFrame(RenderTexture frame, bool keepAlpha);
        void Stop();
    }
}
