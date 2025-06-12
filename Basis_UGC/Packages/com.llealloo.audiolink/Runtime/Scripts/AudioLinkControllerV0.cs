using UnityEngine;
using UnityEngine.UI;

namespace AudioLink
{
#if UDONSHARP
    
    
    
    using static VRC.SDKBase.VRCShader;

    public class AudioLinkControllerV0 : MonoBehaviour
#else
    using static Shader;

    public class AudioLinkControllerV0 : MonoBehaviour
#endif
    {
        [Space(10)]
        public AudioLink audioLink;
        [Space(10)]
        [Header("Internal (Do not modify)")]
        public ThemeColorControllerV0 themeColorController;
        public Material audioSpectrumDisplay;
        public Text gainLabel;
        public Slider gainSlider;
        public Text trebleLabel;
        public Slider trebleSlider;
        public Text bassLabel;
        public Slider bassSlider;
        public Text fadeLengthLabel;
        public Slider fadeLengthSlider;
        public Text fadeExpFalloffLabel;
        public Slider fadeExpFalloffSlider;
        public Slider x0Slider;
        public Slider x1Slider;
        public Slider x2Slider;
        public Slider x3Slider;
        public Slider threshold0Slider;
        public Slider threshold1Slider;
        public Slider threshold2Slider;
        public Slider threshold3Slider;

        private float _initGain;
        private float _initTreble;
        private float _initBass;
        private float _initFadeLength;
        private float _initFadeExpFalloff;
        private float _initX0;
        private float _initX1;
        private float _initX2;
        private float _initX3;
        private float _initThreshold0;
        private float _initThreshold1;
        private float _initThreshold2;
        private float _initThreshold3;

        private RectTransform _threshold0Rect;
        private RectTransform _threshold1Rect;
        private RectTransform _threshold2Rect;
        private RectTransform _threshold3Rect;

        #region PropertyIDs

        // ReSharper disable InconsistentNaming
        private int _X0;
        private int _X1;
        private int _X2;
        private int _X3;
        private int _Threshold0;
        private int _Threshold1;
        private int _Threshold2;
        private int _Threshold3;
        // ReSharper restore InconsistentNaming

        private void InitIDs()
        {
            _X0 = PropertyToID("_X0");
            _X1 = PropertyToID("_X1");
            _X2 = PropertyToID("_X2");
            _X3 = PropertyToID("_X3");
            _Threshold0 = PropertyToID("_Threshold0");
            _Threshold1 = PropertyToID("_Threshold1");
            _Threshold2 = PropertyToID("_Threshold2");
            _Threshold3 = PropertyToID("_Threshold3");
        }

        #endregion

        ThemeColorControllerV0 FindThemeColorController()
        {
            Transform controllerTransform = transform.Find("ThemeColorController");
            if (controllerTransform == null) return null;
            return controllerTransform.GetComponent<ThemeColorControllerV0>();
        }

        void Start()
        {
            InitIDs();
            if (audioLink == null)
            {
                Debug.LogError("[AudioLink] Controller not connected to AudioLink");
                return;
            }

            if (themeColorController == null)
            {
                // This is here in case someone upgraded AudioLink, which updates
                // everything in the prefab, but not the outermost properties of the prefab.
                // TODO: Double check that this is how upgrading ends up working.
                Debug.Log("[AudioLink] AudioLinkController using fallback method for finding themeColorController");
                themeColorController = FindThemeColorController();
            }
            if (themeColorController == null)
            {
                // Something really weird has gone on. maybe using updated script
                // on un-updated prefab?
                Debug.LogError("[AudioLink] AudioLinkController could not find themeColorController");
            }
            else
            {
                themeColorController.audioLink = audioLink;
                themeColorController.InitializeAudioLinkThemeColors();
            }

            GetSettings();

            _initGain = gainSlider.value;
            _initTreble = trebleSlider.value;
            _initBass = bassSlider.value;
            _initFadeLength = fadeLengthSlider.value;
            _initFadeExpFalloff = fadeExpFalloffSlider.value;
            _initX0 = x0Slider.value;
            _initX1 = x1Slider.value;
            _initX2 = x2Slider.value;
            _initX3 = x3Slider.value;
            _initThreshold0 = threshold0Slider.value;
            _initThreshold1 = threshold1Slider.value;
            _initThreshold2 = threshold2Slider.value;
            _initThreshold3 = threshold3Slider.value;
            _threshold0Rect = threshold0Slider.GetComponent<RectTransform>();
            _threshold1Rect = threshold1Slider.GetComponent<RectTransform>();
            _threshold2Rect = threshold2Slider.GetComponent<RectTransform>();
            _threshold3Rect = threshold3Slider.GetComponent<RectTransform>();

            UpdateSettings();
        }

        private void GetSettings()
        {
            // General settings
            gainSlider.value = audioLink.gain;
            trebleSlider.value = audioLink.treble;
            bassSlider.value = audioLink.bass;
            fadeLengthSlider.value = audioLink.fadeLength;
            fadeExpFalloffSlider.value = audioLink.fadeExpFalloff;
            fadeExpFalloffSlider.value = audioLink.fadeExpFalloff;

            // Crossover Settings
            x0Slider.value = audioLink.x0;
            x1Slider.value = audioLink.x1;
            x2Slider.value = audioLink.x2;
            x3Slider.value = audioLink.x3;
            threshold0Slider.value = audioLink.threshold0;
            threshold1Slider.value = audioLink.threshold1;
            threshold2Slider.value = audioLink.threshold2;
            threshold3Slider.value = audioLink.threshold3;
        }

        public void UpdateSettings()
        {
            // Update labels
            gainLabel.text = "Gain: " + ((int)Remap(gainSlider.value, 0f, 2f, 0f, 200f)).ToString() + "%";
            trebleLabel.text = "Treble: " + ((int)Remap(trebleSlider.value, 0f, 2f, 0f, 200f)).ToString() + "%";
            bassLabel.text = "Bass: " + ((int)Remap(bassSlider.value, 0f, 2f, 0f, 200f)).ToString() + "%";

            // Update Sliders
            Vector2 anchor0 = new Vector2(x0Slider.value, 1f);
            Vector2 anchor1 = new Vector2(x1Slider.value, 1f);
            Vector2 anchor2 = new Vector2(x2Slider.value, 1f);
            Vector2 anchor3 = new Vector2(x3Slider.value, 1f);
            if (_threshold0Rect != null) _threshold0Rect.anchorMin = anchor0;
            if (_threshold0Rect != null) _threshold0Rect.anchorMax = anchor1;
            if (_threshold1Rect != null) _threshold1Rect.anchorMin = anchor1;
            if (_threshold1Rect != null) _threshold1Rect.anchorMax = anchor2;
            if (_threshold2Rect != null) _threshold2Rect.anchorMin = anchor2;
            if (_threshold2Rect != null) _threshold2Rect.anchorMax = anchor3;
            if (_threshold3Rect != null) _threshold3Rect.anchorMin = anchor3;
            // threshold3Rect.anchorMax is a constant value. Skip

            audioSpectrumDisplay.SetFloat(_X0, x0Slider.value);
            audioSpectrumDisplay.SetFloat(_X1, x1Slider.value);
            audioSpectrumDisplay.SetFloat(_X2, x2Slider.value);
            audioSpectrumDisplay.SetFloat(_X3, x3Slider.value);
            audioSpectrumDisplay.SetFloat(_Threshold0, threshold0Slider.value);
            audioSpectrumDisplay.SetFloat(_Threshold1, threshold1Slider.value);
            audioSpectrumDisplay.SetFloat(_Threshold2, threshold2Slider.value);
            audioSpectrumDisplay.SetFloat(_Threshold3, threshold3Slider.value);

            if (audioLink == null)
            {
                Debug.LogError("[AudioLink] Controller not connected to AudioLink");
                return;
            }
            // General settings
            audioLink.gain = gainSlider.value;
            audioLink.treble = trebleSlider.value;
            audioLink.bass = bassSlider.value;
            audioLink.fadeLength = fadeLengthSlider.value;
            audioLink.fadeExpFalloff = fadeExpFalloffSlider.value;
            audioLink.fadeExpFalloff = fadeExpFalloffSlider.value;

            // Crossover settings
            audioLink.x0 = x0Slider.value;
            audioLink.x1 = x1Slider.value;
            audioLink.x2 = x2Slider.value;
            audioLink.x3 = x3Slider.value;
            audioLink.threshold0 = threshold0Slider.value;
            audioLink.threshold1 = threshold1Slider.value;
            audioLink.threshold2 = threshold2Slider.value;
            audioLink.threshold3 = threshold3Slider.value;

            audioLink.UpdateSettings();
        }

        public void ResetSettings()
        {
            gainSlider.value = _initGain;
            trebleSlider.value = _initTreble;
            bassSlider.value = _initBass;
            fadeLengthSlider.value = _initFadeLength;
            fadeExpFalloffSlider.value = _initFadeExpFalloff;
            x0Slider.value = _initX0;
            x1Slider.value = _initX1;
            x2Slider.value = _initX2;
            x3Slider.value = _initX3;
            threshold0Slider.value = _initThreshold0;
            threshold1Slider.value = _initThreshold1;
            threshold2Slider.value = _initThreshold2;
            threshold3Slider.value = _initThreshold3;
            if (themeColorController != null)
            {
                themeColorController.ResetThemeColors();
            }
        }


        private float Remap(float t, float a, float b, float u, float v)
        {
            return ((t - a) / (b - a)) * (v - u) + u;
        }
    }
}
