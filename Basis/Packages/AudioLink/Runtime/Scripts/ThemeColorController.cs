using UnityEngine;
using UnityEngine.UI;
using System;

namespace AudioLink
{
#if UDONSHARP
    using UdonSharp;

    using VRC.SDKBase;

    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class ThemeColorController : UdonSharpBehaviour
#else
    public class ThemeColorController : MonoBehaviour
#endif
    {
        [UdonSynced] private int _themeColorMode;

        [Obsolete("This array will return a copy of the data, causing it to not write any data when trying to write directly to a index. Use " + nameof(GetCustomThemeColors) + " and " + nameof(SetCustomThemeColors) + " instead.", false)]
        public Color[] customThemeColors
        {
            get
            {
                return GetCustomThemeColors();
            }
            set
            {
                SetCustomThemeColors(value);
            }
        }

        [UdonSynced]
        public Color themeColor1 = Color.yellow;
        [UdonSynced]
        public Color themeColor2 = Color.blue;
        [UdonSynced]
        public Color themeColor3 = Color.red;
        [UdonSynced]
        public Color themeColor4 = Color.green;

        public AudioLink audioLink; // Initialized by AudioLinkController.

        public Material audioLinkUI; // Initialized by AudioLinkController.

        private Color[] _initCustomThemeColors;
        private int _initThemeColorMode;

        private bool _processGUIEvents = true;

#if UDONSHARP
        private VRCPlayerApi localPlayer;
#endif

        // A view-controller for customThemeColors
        public Slider sliderHue;
        public Slider sliderSaturation;
        public Slider sliderValue;
        public Toggle themeColorToggle;
        public int customColorIndex = 0;

        [HideInInspector] public bool networkSynced = true;

        private void Start()
        {
#if UDONSHARP
            localPlayer = Networking.LocalPlayer;
#endif

            _initCustomThemeColors = GetCustomThemeColors();
        }

#if UDONSHARP
        public override void OnDeserialization()
        {
            if (!networkSynced) return;
            UpdateGUI();
            UpdateAudioLinkThemeColors();
        }
#endif

        public void SelectCustomColor0() { SelectCustomColorN(0); }
        public void SelectCustomColor1() { SelectCustomColorN(1); }
        public void SelectCustomColor2() { SelectCustomColorN(2); }
        public void SelectCustomColor3() { SelectCustomColorN(3); }
        public void SelectCustomColorN(int n)
        {
            customColorIndex = n;
            UpdateGUI();
        }

        /// <summary>
        /// Get a copy of the custom theme colors.
        /// </summary>
        /// <returns> An array of 4 colors. </returns>
        public Color[] GetCustomThemeColors()
        {
            return new[] {
                        themeColor1,
                        themeColor2,
                        themeColor3,
                        themeColor4
                    };
        }

        /// <summary>
        /// Set the custom theme colors. Passing an array with less than 4 colors will only set the provided slots, the rest will remain unchanged. Sending an array with more than 4 colors will only set the first 4 colors.
        /// </summary>
        /// <param name="colors"> An array of colors. </param>
        public void SetCustomThemeColors(params Color[] colors)
        {
            Color[] safeColorArray = GetCustomThemeColors();

            for (int i = 0; i < colors.Length; ++i)
            {
                if (i < safeColorArray.Length)
                {
                    safeColorArray[i] = colors[i];
                }
            }

            themeColor1 = safeColorArray[0];
            themeColor2 = safeColorArray[1];
            themeColor3 = safeColorArray[2];
            themeColor4 = safeColorArray[3];
        }

        public void ToggleThemeColorMode()
        {
#if UDONSHARP
            if (!Networking.IsOwner(gameObject))
                Networking.SetOwner(localPlayer, gameObject);
#endif
            _themeColorMode = themeColorToggle.isOn ? 0 : 1;
            UpdateGUI();
            UpdateAudioLinkThemeColors();
#if UDONSHARP
            if (networkSynced)
                RequestSerialization();
#endif
        }

        public void ForceThemeColorMode()
        {
            if (!_processGUIEvents)
            {
                return;
            }

            themeColorToggle.isOn = false;
        }

        public void OnGUIchange()
        {
            if (!_processGUIEvents)
            {
                return;
            }
#if UDONSHARP
            if (!Networking.IsOwner(gameObject))
                Networking.SetOwner(localPlayer, gameObject);
#endif
            Color[] themeColors = GetCustomThemeColors();
            themeColors[customColorIndex] = Color.HSVToRGB(
                sliderHue.value,
                sliderSaturation.value,
                sliderValue.value
            );
            SetCustomThemeColors(themeColors);

            UpdateGUI();
            UpdateAudioLinkThemeColors();
#if UDONSHARP
            if (networkSynced)
                RequestSerialization();
#endif
        }

        public void ResetThemeColors()
        {
            _themeColorMode = _initThemeColorMode;

            SetCustomThemeColors(_initCustomThemeColors);

            UpdateGUI();
            UpdateAudioLinkThemeColors();
#if UDONSHARP
            if (networkSynced)
                RequestSerialization();
#endif
        }

        public void UpdateGUI()
        {
            _processGUIEvents = false;

            bool isCustom = _themeColorMode == 1;

            // update HSV sliders
            float h, s, v;
            Color[] customThemeColors = GetCustomThemeColors();
            Color.RGBToHSV(customThemeColors[customColorIndex], out h, out s, out v);
            sliderHue.value = h;
            sliderSaturation.value = s;
            sliderValue.value = v;

            // update toggle
            themeColorToggle.isOn = _themeColorMode == 0;

            if (audioLinkUI != null)
            {
                audioLinkUI.SetInt("_ThemeColorMode", _themeColorMode);
                audioLinkUI.SetInt("_SelectedColor", customColorIndex);
                audioLinkUI.SetFloat("_Hue", h);
                audioLinkUI.SetFloat("_Saturation", s);
                audioLinkUI.SetFloat("_Value", v);

                audioLinkUI.SetColor("_CustomColor0", customThemeColors[0]);
                audioLinkUI.SetColor("_CustomColor1", customThemeColors[1]);
                audioLinkUI.SetColor("_CustomColor2", customThemeColors[2]);
                audioLinkUI.SetColor("_CustomColor3", customThemeColors[3]);
            }

            _processGUIEvents = true;
        }

        public void InitializeAudioLinkThemeColors()
        {
            if (audioLink == null) return;

            Color[] customThemeColors = GetCustomThemeColors();

            customThemeColors[0] = audioLink.customThemeColor0;
            customThemeColors[1] = audioLink.customThemeColor1;
            customThemeColors[2] = audioLink.customThemeColor2;
            customThemeColors[3] = audioLink.customThemeColor3;

            //shallow copy of the array
            _initCustomThemeColors = GetCustomThemeColors();

            _initThemeColorMode = audioLink.themeColorMode;
            _themeColorMode = _initThemeColorMode;

            UpdateGUI();
            UpdateAudioLinkThemeColors();
#if UDONSHARP
            if (Networking.IsOwner(gameObject) && networkSynced)
                RequestSerialization();
#endif
        }

        public void UpdateAudioLinkThemeColors()
        {
            if (audioLink == null) return;
            audioLink.themeColorMode = _themeColorMode;

            Color[] customThemeColors = GetCustomThemeColors();

            audioLink.customThemeColor0 = customThemeColors[0];
            audioLink.customThemeColor1 = customThemeColors[1];
            audioLink.customThemeColor2 = customThemeColors[2];
            audioLink.customThemeColor3 = customThemeColors[3];
            audioLink.UpdateThemeColors();
        }
    }
}
