using UnityEngine;
using AL = global::AudioLink;

namespace Basis.Integration.AudioLink
{
    public enum BasisAudioLinkReactiveColorMode
    {
        [InspectorName("Amplitude Gradient")] Gradient,
        [InspectorName("Per-Band RGB")] PerBandRgb,
        [InspectorName("AudioLink Theme")] ThemeColor,
        [InspectorName("Static Tint")] StaticTint
    }

    /// <summary>
    /// Shared base for Basis components that read CPU-side AudioLink band amplitudes and drive a visual target.
    /// Auto-resolves the scene's AudioLink instance and stays inert until its CPU readback data is available.
    /// </summary>
    public abstract class BasisAudioLinkReactiveBase : MonoBehaviour
    {
        [Header("AudioLink Source")]
        [Tooltip("Leave empty to auto-resolve the AudioLink instance in the scene.")]
        public AL.AudioLink AudioLinkSource;

        [Header("Band")]
        public AL.AudioLinkBand Band = AL.AudioLinkBand.BASS;
        [Range(0, 127)]
        [Tooltip("History delay (raw) or smoothing strength (when Smooth is on).")]
        public int Delay = 0;
        [Tooltip("Use AudioLink's smoothed band buffer instead of the raw history buffer.")]
        public bool Smooth = true;
        [Min(0f)]
        [Tooltip("Seconds to ease toward the target amplitude. 0 = instant.")]
        public float SmoothingTime = 0.05f;

        [Header("Color")]
        public BasisAudioLinkReactiveColorMode ColorMode = BasisAudioLinkReactiveColorMode.Gradient;
        public Gradient ColorGradient;
        [Range(0, 3)]
        public int ThemeColorIndex = 0;
        [Tooltip("Hue rotation per unit amplitude (Static Tint and AudioLink Theme modes).")]
        public float HueShift = 0f;

        private float _smoothedAmplitude;
        private int _nextSearchFrame;

        protected bool TryResolveAudioLink()
        {
            if (AudioLinkSource == null)
            {
                if (Time.frameCount < _nextSearchFrame)
                {
                    return false;
                }
                _nextSearchFrame = Time.frameCount + 30;
                AudioLinkSource = FindAnyObjectByType<AL.AudioLink>();
                if (AudioLinkSource == null)
                {
                    return false;
                }
            }
            return AudioLinkSource.AudioDataIsAvailable();
        }

        protected float ReadAmplitude()
        {
            float target = SampleBand(Band);
            if (SmoothingTime <= 0f)
            {
                _smoothedAmplitude = target;
            }
            else
            {
                _smoothedAmplitude = Mathf.Lerp(_smoothedAmplitude, target, 1f - Mathf.Exp(-Time.deltaTime / SmoothingTime));
            }
            return _smoothedAmplitude;
        }

        protected float SampleBand(AL.AudioLinkBand band)
        {
            int delay = Smooth ? Mathf.Clamp(Delay, 0, 15) : Delay;
            return AL.AudioLink.ToGrayscale(AudioLinkSource.GetBandAsSmooth(band, delay, Smooth));
        }

        protected Color EvaluateColor(float amplitude, Color baseColor)
        {
            switch (ColorMode)
            {
                case BasisAudioLinkReactiveColorMode.Gradient:
                    return ColorGradient != null ? ColorGradient.Evaluate(Mathf.Clamp01(amplitude)) : baseColor;
                case BasisAudioLinkReactiveColorMode.PerBandRgb:
                    return new Color(
                        SampleBand(AL.AudioLinkBand.BASS),
                        0.5f * (SampleBand(AL.AudioLinkBand.LOWMID) + SampleBand(AL.AudioLinkBand.HIGHMID)),
                        SampleBand(AL.AudioLinkBand.TREBLE),
                        1f);
                case BasisAudioLinkReactiveColorMode.ThemeColor:
                    return ShiftHue(ReadThemeColor(), amplitude * HueShift);
                default:
                    return ShiftHue(baseColor, amplitude * HueShift);
            }
        }

        private Color ReadThemeColor()
        {
            Vector2 pixel;
            switch (ThemeColorIndex)
            {
                case 1: pixel = AL.AudioLink.GetALPassThemeColor1(); break;
                case 2: pixel = AL.AudioLink.GetALPassThemeColor2(); break;
                case 3: pixel = AL.AudioLink.GetALPassThemeColor3(); break;
                default: pixel = AL.AudioLink.GetALPassThemeColor0(); break;
            }
            Vector4 c = AudioLinkSource.GetDataAtPixel(pixel);
            return new Color(c.x, c.y, c.z, 1f);
        }

        protected static Color ShiftHue(Color color, float amount)
        {
            if (Mathf.Approximately(amount, 0f))
            {
                return color;
            }
            Color.RGBToHSV(color, out float h, out float s, out float v);
            return Color.HSVToRGB(Mathf.Repeat(h + amount, 1f), s, v);
        }

        protected virtual void Reset()
        {
            ColorGradient = new Gradient();
            ColorGradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(0f, 0.1f, 0.6f), 0f),
                    new GradientColorKey(new Color(0f, 0.8f, 1f), 0.5f),
                    new GradientColorKey(Color.white, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, 1f)
                });
        }
    }
}
