using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

[System.Serializable]
public class BasisHandHeldCameraUI
{
    public Button TakePhotoButton;
    public Button ResetButton;
    public Button CloseButton;
    public Button Timer;
    public Button Nameplates;
    public Button OverrideDesktopOutput;
    [Space(10)]
    public GameObject focusCursor;
    public Button DepthModeAutoButton;
    public Button DepthModeManualButton;
    public enum DepthMode { Auto, Manual }
    private DepthMode currentDepthMode = DepthMode.Auto;
    [Space(10)]
    [Space(10)]
    public Toggle Resolution;
    public GameObject[] ResolutionSprites; // 4 resolution sprites
    private int currentResolutionIndex = 0;
    public Toggle Format;
    public bool useEXR => Format != null && Format.isOn;
    private const int FORMAT_PNG = 0;
    private const int FORMAT_EXR = 1;
    public GameObject PngSprite; // Sprite 1
    public GameObject ExrSprite; // Sprite 2
    [Space(10)]
    public Slider ExposureSlider;
    private float[] exposureStops = new float[] { -3f, -2.5f, -2f, -1.5f, -1f, -0.5f, 0f, 0.5f, 1f, 1.5f, 2f, 2.5f, 3f };
    [Space(10)]
    public TextMeshProUGUI DOFFocusOutput;
    public TextMeshProUGUI DepthApertureOutput;
    public TextMeshProUGUI BloomIntensityOutput;
    public TextMeshProUGUI BloomThreshholdOutput;
    public TextMeshProUGUI ContrastOutput;
    public TextMeshProUGUI SaturationOutput;
    public TextMeshProUGUI FOVOutput;
    [Space(10)]
    public Slider FOVSlider;
    public Slider DepthFocusDistanceSlider;
    public Slider DepthApertureSlider;
    public Slider BloomIntensitySlider;
    public Slider BloomThresholdSlider;
    public Slider ContrastSlider;
    public Slider SaturationSlider;
    [Space(10)]
    public Toggle depthIsActiveButton;
    [Space(10)]
    public GameObject[] CameraSettingsSlidersPanel;
    public GameObject[] CameraSettingsButtonPanel;
    [Space(10)]
    public BasisHandHeldCamera HHC;
    public async Task Initalize(BasisHandHeldCamera hhc)
    {
        HHC = hhc;

        await LoadSettings();

        DepthApertureSlider.onValueChanged.AddListener(ChangeAperture);
        TakePhotoButton.onClick.AddListener(HHC.CapturePhoto);
        ResetButton.onClick.AddListener(ResetSettings);
        Timer.onClick.AddListener(HHC.Timer);
        Nameplates.onClick.AddListener(HHC.Nameplates);
        OverrideDesktopOutput.onClick.AddListener(HHC.OnOverrideDesktopOutputButtonPress);

        FOVSlider.onValueChanged.AddListener(ChangeFOV);
        CloseButton.onClick.AddListener(CloseUI);
        //Resolution
        Resolution.onValueChanged.AddListener(OnResolutionToggleChanged);
        //Formatting
        Format.onValueChanged.AddListener(OnFormatToggleChanged);
        OnFormatToggleChanged(Format.isOn);

        //Depth Modes
        DepthModeAutoButton.onClick.AddListener(() => SetDepthMode(DepthMode.Auto));
        DepthModeManualButton.onClick.AddListener(() => SetDepthMode(DepthMode.Manual));

        ExposureSlider.onValueChanged.AddListener(ChangeExposureCompensation);

        //Depth Is Active
        depthIsActiveButton.onValueChanged.AddListener(ChangeDepthActiveState);

        if (HHC.MetaData.Profile.TryGet(out HHC.MetaData.depthOfField))
        {
            DepthFocusDistanceSlider.onValueChanged.AddListener(DepthChangeFocusDistance);
        }

        if (HHC.MetaData.Profile.TryGet(out HHC.MetaData.bloom))
        {
            BloomIntensitySlider.onValueChanged.AddListener(ChangeBloomIntensity);
            BloomThresholdSlider.onValueChanged.AddListener(ChangeBloomThreshold);
        }

        if (HHC.MetaData.Profile.TryGet(out HHC.MetaData.colorAdjustments))
        {
            ContrastSlider.onValueChanged.AddListener(ChangeContrast);
            SaturationSlider.onValueChanged.AddListener(ChangeSaturation);
            //HueShiftSlider.onValueChanged.AddListener(ChangeHueShift);
        }
        if (HHC.MetaData.Profile.TryGet(out HHC.MetaData.colorAdjustments))
        {
            HHC.MetaData.colorAdjustments.active = true;
        }
        DepthApertureSlider.minValue = 0;
        DepthApertureSlider.maxValue = 32;
        FOVSlider.minValue = 20;
        FOVSlider.maxValue = 120;
        //FocusDistanceSlider.minValue = 0.1f;
        //FocusDistanceSlider.maxValue = 100f;
        DepthFocusDistanceSlider.minValue = 0.1f;
        DepthFocusDistanceSlider.maxValue = 100f;
        BloomIntensitySlider.minValue = 0;
        BloomIntensitySlider.maxValue = 5;
        BloomThresholdSlider.minValue = 0.1f;
        BloomThresholdSlider.maxValue = 2;
        ContrastSlider.minValue = -100;
        ContrastSlider.maxValue = 100;
        SaturationSlider.minValue = -100;
        SaturationSlider.maxValue = 100;
        //HueShiftSlider.minValue = -180;
        //HueShiftSlider.maxValue = 180;

        FOVSlider.value = HHC.captureCamera.fieldOfView;
        //FocusDistanceSlider.value = HHC.captureCamera.focalLength;
    }

    private void ChangeDepthActiveState(bool state)
    {
        if (HHC.MetaData.depthOfField != null)
        {
            HHC.MetaData.depthOfField.active = state;
        }

        DepthModeAutoButton.gameObject.SetActive(state);
        DepthModeManualButton.gameObject.SetActive(state);

        if (!state)
        {
            focusCursor?.gameObject.SetActive(false);
            DepthApertureSlider.gameObject.SetActive(false);
            DepthFocusDistanceSlider.gameObject.SetActive(false);
        }
        else
        {
            SetDepthMode(currentDepthMode); // Restore correct mode
        }
    }
    private void SetDepthMode(DepthMode mode)
    {
        currentDepthMode = mode;

        bool isActive = depthIsActiveButton.isOn;
        if (!isActive) return;

        bool useAuto = (mode == DepthMode.Auto);

        focusCursor?.gameObject.SetActive(true);
        DepthApertureSlider.gameObject.SetActive(true);
        DepthFocusDistanceSlider.gameObject.SetActive(!useAuto);

        BasisDebug.Log($"[DepthMode] Switched to {(useAuto ? "Auto" : "Manual")}");
    }

    public void ChangeExposureCompensation(float index)
    {
        if (HHC.MetaData.colorAdjustments != null)
        {
            int i = Mathf.Clamp((int)index, 0, exposureStops.Length - 1);
            float exposureValue = exposureStops[i];

            HHC.MetaData.colorAdjustments.postExposure.value = exposureValue;
        }
    }

    private void OnFormatToggleChanged(bool state)
    {
        BasisDebug.Log($"[Format] Changed to {(state ? "EXR" : "PNG")}");
        HHC.captureFormat = state ? "EXR" : "PNG";
        if (PngSprite != null) PngSprite.SetActive(!state);
        if (ExrSprite != null) ExrSprite.SetActive(state);
    }
    private void OnResolutionToggleChanged(bool state)
    {
        currentResolutionIndex = (currentResolutionIndex + 1) % 4;
        HHC.ChangeResolution(currentResolutionIndex);
        UpdateResolutionSprites();
        BasisDebug.Log($"[Resolution] Changed to index {currentResolutionIndex}");
    }
    private void UpdateResolutionSprites()
    {
        for (int i = 0; i < ResolutionSprites.Length; i++)
        {
            if (ResolutionSprites[i] != null)
                ResolutionSprites[i].SetActive(i == currentResolutionIndex);
        }
    }
    public int GetFormatIndex()
    {
        return Format != null && Format.isOn ? FORMAT_EXR : FORMAT_PNG;
    }
    public void CloseUI()
    {
        GameObject.Destroy(HHC.gameObject);
    }
    public const string CameraSettingsJson = "CameraSettings.json";
    public async Task SaveSettings()
    {
        try
        {
            CameraSettings settings = new CameraSettings()
            {
                resolutionIndex = currentResolutionIndex,
                formatIndex = GetFormatIndex(),
                fov = FOVSlider.value,
                bloomIntensity = BloomIntensitySlider.value,
                bloomThreshold = BloomThresholdSlider.value,
                contrast = ContrastSlider.value,
                saturation = SaturationSlider.value,
                depthAperture = DepthApertureSlider.value,
                depthFocusDistance = DepthFocusDistanceSlider.value,
                exposureIndex = Mathf.Clamp((int)ExposureSlider.value, 0, exposureStops.Length - 1),
                depthIsActive = depthIsActiveButton.isOn
            };

            string json = JsonUtility.ToJson(settings, true);
            string settingsFilePath = Path.Combine(Application.persistentDataPath, CameraSettingsJson);
            await File.WriteAllTextAsync(settingsFilePath, json);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Error saving settings: {ex.Message}");

            // Attempt to resave settings
            try
            {
                CameraSettings defaultSettings = new CameraSettings()
                {
                    resolutionIndex = 0,
                    formatIndex = 0,
                    apertureIndex = 0,
                    shutterSpeedIndex = 0,
                    isoIndex = 0,
                    fov = 60f,
                    focusDistance = 10f,
                    sensorSizeX = 36f,
                    sensorSizeY = 24f,
                    bloomIntensity = 0.5f,
                    bloomThreshold = 0.5f,
                    contrast = 1f,
                    saturation = 1f,
                    depthAperture = 1f,
                    depthIsActive = true,
                    depthFocusDistance = 10f
                };

                string defaultJson = JsonUtility.ToJson(defaultSettings, true);
                string settingsFilePath = Path.Combine(Application.persistentDataPath, CameraSettingsJson);
                await File.WriteAllTextAsync(settingsFilePath, defaultJson);
                BasisDebug.Log("Settings have been reset to default values.");
            }
            catch (Exception resaveEx)
            {
                BasisDebug.LogError($"Error resaving settings: {resaveEx.Message}");
            }
        }
    }
    public void ResetSettings()
    {
        try
        {
            ApplySettings(new CameraSettings());
            BasisDebug.Log("Settings have been reset to default values.");
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Error resetting settings: {ex.Message}");
        }
    }

    public async Task LoadSettings()
    {
        try
        {
            string settingsFilePath = Path.Combine(Application.persistentDataPath, CameraSettingsJson);
            if (File.Exists(settingsFilePath))
            {
                string json = await File.ReadAllTextAsync(settingsFilePath);
                CameraSettings settings = JsonUtility.FromJson<CameraSettings>(json);
                ApplySettings(settings);
            }
            else
            {
                BasisDebug.Log("Settings file not found, applying default settings.");
                ApplySettings(new CameraSettings()); // Apply default settings if file doesn't exist
            }
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Error loading settings: {ex.Message}");
            // Optionally apply default settings if loading fails
            ApplySettings(new CameraSettings());
        }
    }

    private void ApplySettings(CameraSettings settings)
    {
        try
        {
            // Set UI values without triggering event listeners
            FOVSlider.SetValueWithoutNotify(settings.fov);
            BloomIntensitySlider.SetValueWithoutNotify(settings.bloomIntensity);
            BloomThresholdSlider.SetValueWithoutNotify(settings.bloomThreshold);
            ContrastSlider.SetValueWithoutNotify(settings.contrast);
            SaturationSlider.SetValueWithoutNotify(settings.saturation);
            DepthApertureSlider.SetValueWithoutNotify(settings.depthAperture);
            DepthFocusDistanceSlider.SetValueWithoutNotify(settings.depthFocusDistance);

            HHC.captureFormat = HHC.MetaData.formats[settings.resolutionIndex];

            // Apply values to HHC after setting UI
            HHC.captureCamera.fieldOfView = settings.fov;
            HHC.captureCamera.focalLength = settings.focusDistance;
            HHC.captureCamera.sensorSize = new Vector2(settings.sensorSizeX, settings.sensorSizeY);
            HHC.captureCamera.aperture = float.Parse(HHC.MetaData.apertures[settings.apertureIndex].TrimStart('f', '/'));
            HHC.captureCamera.shutterSpeed = 1 / float.Parse(HHC.MetaData.shutterSpeeds[settings.shutterSpeedIndex].Split('/')[1]);
            HHC.captureCamera.iso = int.Parse(HHC.MetaData.isoValues[settings.isoIndex]);

            if (Format != null)
            {
                Format.isOn = settings.formatIndex == FORMAT_EXR;
            }

            if (HHC.MetaData.colorAdjustments != null)
            {
                int idx = Mathf.Clamp(settings.exposureIndex, 0, exposureStops.Length - 1);
                ExposureSlider.SetValueWithoutNotify(idx);
                if (HHC.MetaData.colorAdjustments != null)
                {
                    HHC.MetaData.colorAdjustments.postExposure.value = exposureStops[idx];
                }
            }

            if (HHC.MetaData.depthOfField != null)
            {
                HHC.MetaData.depthOfField.aperture.value = settings.depthAperture;
                HHC.MetaData.depthOfField.active = settings.depthIsActive;
                HHC.MetaData.depthOfField.focusDistance.value = settings.depthFocusDistance;
            }
            if (HHC.MetaData.bloom != null)
            {
                HHC.MetaData.bloom.intensity.value = settings.bloomIntensity;
                HHC.MetaData.bloom.threshold.value = settings.bloomThreshold;
            }
            if (HHC.MetaData.colorAdjustments != null)
            {
                HHC.MetaData.colorAdjustments.contrast.value = settings.contrast;
                HHC.MetaData.colorAdjustments.saturation.value = settings.saturation;
            }

            BasisDebug.Log("Settings applied successfully.");
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Error applying settings: {ex.Message}");
        }
    }
    public void DepthChangeFocusDistance(float value)
    {
        if (HHC.MetaData.depthOfField != null)
        {
            HHC.MetaData.depthOfField.focusDistance.value = value;
            DOFFocusOutput.text = value.ToString();
        }
    }

    public void ChangeAperture(float value)
    {
        if (HHC.MetaData.depthOfField != null)
        {
            HHC.MetaData.depthOfField.aperture.value = value;
            DepthApertureOutput.text = value.ToString();
        }
    }

    public void ChangeBloomIntensity(float value)
    {
        if (HHC.MetaData.bloom != null)
        {
            HHC.MetaData.bloom.intensity.value = value;
            BloomIntensityOutput.text = value.ToString();
        }
    }

    public void ChangeBloomThreshold(float value)
    {
        if (HHC.MetaData.bloom != null)
        {
            HHC.MetaData.bloom.threshold.value = value;
            BloomThreshholdOutput.text = value.ToString();
        }
    }

    public void ChangeContrast(float value)
    {
        if (HHC.MetaData.colorAdjustments != null)
        {
            HHC.MetaData.colorAdjustments.contrast.value = value;
            ContrastOutput.text = value.ToString();
        }
    }

    public void ChangeSaturation(float value)
    {
        if (HHC.MetaData.colorAdjustments != null)
        {
            HHC.MetaData.colorAdjustments.saturation.value = value;
            SaturationOutput.text = value.ToString();
        }
    }

    public void ChangeHueShift(float value)
    {
        if (HHC.MetaData.colorAdjustments != null)
        {
            HHC.MetaData.colorAdjustments.hueShift.value = value;
        }
    }
    //public void ChangeSensorSizeX(float value) { HHC.captureCamera.sensorSize = new Vector2(value, HHC.captureCamera.sensorSize.y); }
    //public void ChangeSensorSizeY(float value) { HHC.captureCamera.sensorSize = new Vector2(HHC.captureCamera.sensorSize.x, value); }
    public void ChangeFOV(float value)
    {
        HHC.captureCamera.fieldOfView = value;
        FOVOutput.text = value.ToString();
    }
    public void ChangeFocusDistance(float value)
    {
        HHC.captureCamera.focalLength = value;
    }
    public void ChangeAperture(int index)
    {
        HHC.captureCamera.aperture = float.Parse(HHC.MetaData.apertures[index].TrimStart('f', '/'));
    }

    public void ChangeShutterSpeed(int index)
    {
        HHC.captureCamera.shutterSpeed = 1 / float.Parse(HHC.MetaData.shutterSpeeds[index].Split('/')[1]);
    }

    public void ChangeISO(int index)
    {
        HHC.captureCamera.iso = int.Parse(HHC.MetaData.isoValues[index]);
    }
    private void PopulateDropdown(TMP_Dropdown dropdown, string[] options)
    {
        dropdown.ClearOptions();
        dropdown.AddOptions(options.ToList());
    }
    [System.Serializable]
    public class CameraSettings
    {
        public CameraSettings()
        {
            resolutionIndex = 0;
            formatIndex = 0;
            apertureIndex = 0;
            shutterSpeedIndex = 0;
            isoIndex = 0;
            fov = 60f;
            focusDistance = 10f;
            sensorSizeX = 36f;
            sensorSizeY = 24f;
            bloomIntensity = 0.5f;
            bloomThreshold = 0.5f;
            contrast = 1f;
            saturation = 1f;
            depthAperture = 1f;
            depthFocusDistance = 10;
            depthIsActive = true;
        }
        public int resolutionIndex = 2;
        public int formatIndex = 0;
        public int apertureIndex;
        public int shutterSpeedIndex;
        public int isoIndex;
        public int exposureIndex = 6;
        public float fov;
        public float focusDistance;
        public float sensorSizeX;
        public float sensorSizeY;
        public float bloomIntensity;
        public float bloomThreshold;
        public float contrast;
        public float saturation;
        public float hueShift;
        public float depthAperture;
        public float depthFocusDistance;
        public bool depthIsActive;
        public bool useManualFocus = true; // true = Manual, false = Auto
    }
}
