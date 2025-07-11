using UnityEngine;

namespace AudioLink
{
#if UDONSHARP
    using UdonSharp;

    public class AudioReactiveLight : UdonSharpBehaviour
#else
    public class AudioReactiveLight : MonoBehaviour
#endif
    {
        public AudioLink audioLink;
        public int band;
        [Range(0, 127)]
        public int delay;
        public bool affectIntensity = true;
        public float intensityMultiplier = 1f;
        public float hueShift;

        private Light _light;
        private int _dataIndex;
        private Color _initialColor;

        void Start()
        {
            _light = transform.GetComponent<Light>();
            _initialColor = _light.color;
            _dataIndex = (band * 128) + delay;

        }

        void Update()
        {
            if (audioLink.AudioDataIsAvailable())
            {
                // Convert to grayscale
                float amplitude = Vector3.Dot(audioLink.GetDataAtPixel(delay, band), new Vector3(0.299f, 0.587f, 0.114f));
                if (affectIntensity) _light.intensity = amplitude * intensityMultiplier;
                _light.color = HueShift(_initialColor, amplitude * hueShift);
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
