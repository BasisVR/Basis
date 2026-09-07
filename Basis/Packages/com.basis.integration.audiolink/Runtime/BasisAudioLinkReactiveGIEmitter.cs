#if BASIS_GLOBALILLUMINATION_EXISTS
using UnityEngine;

namespace Basis.Integration.AudioLink
{
    /// <summary>
    /// Drives a BasisGlobalIlluminationEmitter's colour and intensity from a CPU-readback AudioLink band.
    ///
    /// The Emitter registry is the hand-placed analytic light both Basis GI modes already fall back to for a
    /// source their own trace cannot resolve on its own - too small for the screen space march or a ray
    /// probe to hit, or drawn with a transparent/additive shader that never reaches the opaque colour buffer
    /// Screen Space GI samples. That is exactly the shape of a typical AudioLink light prop, so without this
    /// bridge the only way to get one into the GI bounce is a static Emitter that cannot pulse with the music.
    /// </summary>
    [RequireComponent(typeof(BasisGlobalIlluminationEmitter))]
    [AddComponentMenu("Basis/AudioLink/Reactive GI Emitter")]
    public class BasisAudioLinkReactiveGIEmitter : BasisAudioLinkReactiveBase
    {
        [Header("Intensity")]
        public bool DriveIntensity = true;
        public float MinIntensity = 0f;
        public float MaxIntensity = 2f;
        [Tooltip("Scales band amplitude before it is mapped into the intensity range.")]
        public float IntensityMultiplier = 1f;

        [Header("Color")]
        public bool DriveColor = true;

        private BasisGlobalIlluminationEmitter _emitter;
        private Color _baseColor = Color.white;

        private void Awake()
        {
            _emitter = GetComponent<BasisGlobalIlluminationEmitter>();
            if (_emitter != null)
            {
                _baseColor = _emitter.Color;
            }
        }

        private void Update()
        {
            if (_emitter == null || !TryResolveAudioLink())
            {
                return;
            }

            float amplitude = ReadAmplitude();

            if (DriveIntensity)
            {
                _emitter.Intensity = MinIntensity + (MaxIntensity - MinIntensity) * Mathf.Max(0f, amplitude * IntensityMultiplier);
            }
            if (DriveColor)
            {
                _emitter.Color = EvaluateColor(amplitude, _baseColor);
            }
        }
    }
}
#endif
