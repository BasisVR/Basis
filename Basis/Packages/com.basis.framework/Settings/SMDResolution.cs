using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace BattlePhaze.SettingsManager.Intergrations
{
    public class SMDResolution : MonoBehaviour
    {
        [Header("UI Dropdowns")]
        [SerializeField] private TMP_Dropdown monitorDropdown;     // Dropdown for monitors
        [SerializeField] private TMP_Dropdown screenModeDropdown;  // Dropdown for screen modes
        [SerializeField] private TMP_Dropdown resolutionDropdown;  // Dropdown for resolutions

        private List<Vector2Int> uniqueResolutions;

        private void Start()
        {
            SetupMonitors();
            SetupScreenModes();
            SetupResolutions();
        }

        #region Setup Dropdowns
        private void SetupMonitors()
        {
            monitorDropdown.ClearOptions();
            List<string> monitorOptions = new List<string>();

            for (int Index = 0; Index < Display.displays.Length; Index++)
            {
                monitorOptions.Add("Monitor " + (Index + 1));
            }

            monitorDropdown.AddOptions(monitorOptions);
            monitorDropdown.onValueChanged.AddListener(OnMonitorChanged);
        }

        private void SetupScreenModes()
        {
            screenModeDropdown.ClearOptions();
            List<string> screenModeOptions = new List<string>
            {
                "Fullscreen",
                "Borderless Window",
                "Windowed"
            };

            screenModeDropdown.AddOptions(screenModeOptions);
            screenModeDropdown.onValueChanged.AddListener(OnScreenModeChanged);
        }

        private void SetupResolutions()
        {
            resolutionDropdown.ClearOptions();
            uniqueResolutions = new List<Vector2Int>();
            List<string> resolutionOptions = new List<string>();

            foreach (Resolution res in Screen.resolutions)
            {
                Vector2Int size = new Vector2Int(res.width, res.height);

                // Only add if not already in the list (removes duplicates with different refresh rates)
                if (!uniqueResolutions.Contains(size))
                {
                    uniqueResolutions.Add(size);
                    resolutionOptions.Add(size.x + " x " + size.y);
                }
            }

            resolutionDropdown.AddOptions(resolutionOptions);

            // Default to the highest resolution available
            resolutionDropdown.value = resolutionOptions.Count - 1;
            resolutionDropdown.onValueChanged.AddListener(OnResolutionChanged);
        }
        #endregion

        #region Dropdown Listeners
        private void OnMonitorChanged(int monitorIndex)
        {
            // Unity does not support monitor switching directly via Screen API
            Debug.Log("Selected Monitor: " + (monitorIndex + 1));
        }

        private void OnScreenModeChanged(int screenModeIndex)
        {
            FullScreenMode mode = GetScreenModeFromIndex(screenModeIndex);
            Vector2Int currentResolution = uniqueResolutions[resolutionDropdown.value];

            Screen.SetResolution(currentResolution.x, currentResolution.y, mode);
            Debug.Log("Changed Screen Mode: " + mode);
        }

        private void OnResolutionChanged(int resolutionIndex)
        {
            Vector2Int selectedResolution = uniqueResolutions[resolutionIndex];
            FullScreenMode mode = GetScreenModeFromIndex(screenModeDropdown.value);

            Screen.SetResolution(selectedResolution.x, selectedResolution.y, mode);
            Debug.Log("Changed Resolution: " + selectedResolution.x + "x" + selectedResolution.y);
        }
        #endregion

        #region Helpers
        private FullScreenMode GetScreenModeFromIndex(int index)
        {
            switch (index)
            {
                case 0: return FullScreenMode.ExclusiveFullScreen;
                case 1: return FullScreenMode.FullScreenWindow;
                case 2: return FullScreenMode.Windowed;
                default: return FullScreenMode.FullScreenWindow;
            }
        }
        #endregion
    }
}
