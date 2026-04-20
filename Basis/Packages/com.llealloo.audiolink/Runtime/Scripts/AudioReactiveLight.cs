using UnityEngine;

#if UDONSHARP
using UdonSharp;
#endif

namespace AudioLink
{
    [RequireComponent(typeof(Light))]
    public class AudioReactiveLight : AudioReactive

    {
        public AudioLink audioLink;
        [Header("AudioLink Settings")]
        public AudioLinkBand band;
        public AudioReactiveLightColorMode colorMode = AudioReactiveLightColorMode.STATIC;
        public bool smooth;
        [Range(0, 127)]
        public int delay;
        
        [Header("Reactivity Settings")]
        public bool affectIntensity = true;
        public float intensityMultiplier = 1f;
        public float hueShift;

        private Light _light;
        private Color _initialColor;

        void Start()
        {
            _light = GetComponent<Light>();
            _initialColor = _light.color;
        }

        void Update()
        {
            if (audioLink.AudioDataIsAvailable())
            {
                float amplitude = AudioLink.ToGrayscale(audioLink.GetBandAsSmooth(band, delay, smooth));
                if (affectIntensity) _light.intensity = amplitude * intensityMultiplier;
                if (colorMode == AudioReactiveLightColorMode.STATIC)
                    _light.color = HueShift(_initialColor, amplitude * hueShift);
                else
                {
                    Vector2 themePixel = Vector2.zero;
                    switch (colorMode)
                    {
                        case AudioReactiveLightColorMode.THEME0: themePixel = AudioLink.GetALPassThemeColor0(); break;
                        case AudioReactiveLightColorMode.THEME1: themePixel = AudioLink.GetALPassThemeColor1(); break;
                        case AudioReactiveLightColorMode.THEME2: themePixel = AudioLink.GetALPassThemeColor2(); break;
                        case AudioReactiveLightColorMode.THEME3: themePixel = AudioLink.GetALPassThemeColor3(); break;
                    }
                    _light.color = HueShift(audioLink.GetDataAtPixel(themePixel), amplitude * hueShift);
                }
            }
        }

        private Color HueShift(Color color, float hueShiftAmount)
        {
            float h, s, v;
            Color.RGBToHSV(color, out h, out s, out v);
            h += hueShiftAmount;
            return Color.HSVToRGB(h, s, v);
        }
    }
}
