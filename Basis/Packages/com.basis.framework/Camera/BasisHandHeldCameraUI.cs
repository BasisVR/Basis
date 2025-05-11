using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
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
    public Button ResolutionButton;
    public Button FormatButton;
    public Button ApertureButton;
    public Button ShutterButton;
    public Button ISOButton;
    [Space(10)]
    public Button RES_OPTION_720p;
    public Button RES_OPTION_1080p;
    public Button RES_OPTION_4K;
    public Button RES_OPTION_8K;
    public int RES_OPTION = 0;
    [Space(10)]
    public Button FORMAT_OPTION_PNG;
    public Button FORMAT_OPTION_EXR;
    public int FORMAT_OPTION = 0;
    [Space(10)]
    public Button APERTURE_OPTION_14;
    public Button APERTURE_OPTION_28;
    public Button APERTURE_OPTION_40;
    public Button APERTURE_OPTION_56;
    public Button APERTURE_OPTION_80;
    public Button APERTURE_OPTION_11;
    public Button APERTURE_OPTION_16;
    public int APERTURE_OPTION = 0;
    [Space(10)]
    public Button SHUTTER_OPTION_1000;
    public Button SHUTTER_OPTION_0500;
    public Button SHUTTER_OPTION_0250;
    public Button SHUTTER_OPTION_0125;
    public Button SHUTTER_OPTION_0060;
    public Button SHUTTER_OPTION_0030;
    public Button SHUTTER_OPTION_0015;
    public int SHUTTER_OPTION = 0;
    [Space(10)]
    public Button ISO_OPTION_0100;
    public Button ISO_OPTION_0200;
    public Button ISO_OPTION_0400;
    public Button ISO_OPTION_0800;
    public Button ISO_OPTION_1600;
    public Button ISO_OPTION_3200;
    public Button ISO_OPTION_6400;
    public int ISO_OPTION = 0;
    [Space(10)]
    public TextMeshProUGUI DOFFocusOutput;
    public TextMeshProUGUI DepthApertureOutput;
    public TextMeshProUGUI BloomIntensityOutput;
    public TextMeshProUGUI BloomThreshholdOutput;
    public TextMeshProUGUI ContrastOutput;
    public TextMeshProUGUI SaturationOutput;
    public TextMeshProUGUI FOVOutput;
    public TextMeshProUGUI SSXOutput;
    public TextMeshProUGUI SSYOutput;
    [Space(10)]
    public Slider FOVSlider;
    public Slider DepthFocusDistanceSlider;
    public Slider DepthApertureSlider;
    public Slider SensorSizeXSlider;
    public Slider SensorSizeYSlider;
    public Slider BloomIntensitySlider;
    public Slider BloomThresholdSlider;
    public Slider ContrastSlider;
    public Slider SaturationSlider;
    [Space(10)]
    public Button CameraSettingsSlidersPanelButton;
    public Button CameraSettingsButtonPanelButton;
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

        ResolutionButton.onClick.AddListener(HHC.ResolutionButton);
        FormatButton.onClick.AddListener(HHC.FormatButton);
        ApertureButton.onClick.AddListener(HHC.ApertureButton);
        ShutterButton.onClick.AddListener(HHC.ShutterButton);
        ISOButton.onClick.AddListener(HHC.ISOButton);



        DepthApertureSlider.onValueChanged.AddListener(ChangeAperture);
        TakePhotoButton.onClick.AddListener(HHC.CapturePhoto);
        ResetButton.onClick.AddListener(ResetSettings);
        Timer.onClick.AddListener(HHC.Timer);
        Nameplates.onClick.AddListener(HHC.Nameplates);
        OverrideDesktopOutput.onClick.AddListener(HHC.OnOverrideDesktopOutputButtonPress);

        FOVSlider.onValueChanged.AddListener(ChangeFOV);
        SensorSizeXSlider.onValueChanged.AddListener(ChangeSensorSizeX);
        SensorSizeYSlider.onValueChanged.AddListener(ChangeSensorSizeY);
        CloseButton.onClick.AddListener(CloseUI);

        //Depth Is Active
        depthIsActiveButton.onValueChanged.AddListener(ChangeDepthActiveState);

        //Camera Setting Panels
        CameraSettingsSlidersPanelButton.onClick.AddListener(() => CameraSettingsButtons(0));
        CameraSettingsButtonPanelButton.onClick.AddListener(() => CameraSettingsButtons(1));

        // Resolution Options
        RES_OPTION_720p.onClick.AddListener(() => SetResolutionOption(0));
        RES_OPTION_1080p.onClick.AddListener(() => SetResolutionOption(1));
        RES_OPTION_4K.onClick.AddListener(() => SetResolutionOption(2));
        RES_OPTION_8K.onClick.AddListener(() => SetResolutionOption(3));

        // Format Options
        FORMAT_OPTION_PNG.onClick.AddListener(() => SetFormatOption(0));
        FORMAT_OPTION_EXR.onClick.AddListener(() => SetFormatOption(1));

        // Aperture Options
        APERTURE_OPTION_14.onClick.AddListener(() => SetApertureOption(0));
        APERTURE_OPTION_28.onClick.AddListener(() => SetApertureOption(1));
        APERTURE_OPTION_40.onClick.AddListener(() => SetApertureOption(2));
        APERTURE_OPTION_56.onClick.AddListener(() => SetApertureOption(3));
        APERTURE_OPTION_80.onClick.AddListener(() => SetApertureOption(4));
        APERTURE_OPTION_11.onClick.AddListener(() => SetApertureOption(5));
        APERTURE_OPTION_16.onClick.AddListener(() => SetApertureOption(6));

        // Shutter Options
        SHUTTER_OPTION_1000.onClick.AddListener(() => SetShutterOption(0));
        SHUTTER_OPTION_0500.onClick.AddListener(() => SetShutterOption(1));
        SHUTTER_OPTION_0250.onClick.AddListener(() => SetShutterOption(2));
        SHUTTER_OPTION_0125.onClick.AddListener(() => SetShutterOption(3));
        SHUTTER_OPTION_0060.onClick.AddListener(() => SetShutterOption(4));
        SHUTTER_OPTION_0030.onClick.AddListener(() => SetShutterOption(5));
        SHUTTER_OPTION_0015.onClick.AddListener(() => SetShutterOption(6));

        // ISO Options
        ISO_OPTION_0100.onClick.AddListener(() => SetISOOption(0));
        ISO_OPTION_0200.onClick.AddListener(() => SetISOOption(1));
        ISO_OPTION_0400.onClick.AddListener(() => SetISOOption(2));
        ISO_OPTION_0800.onClick.AddListener(() => SetISOOption(3));
        ISO_OPTION_1600.onClick.AddListener(() => SetISOOption(4));
        ISO_OPTION_3200.onClick.AddListener(() => SetISOOption(5));
        ISO_OPTION_6400.onClick.AddListener(() => SetISOOption(6));

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
            // HueShiftSlider.onValueChanged.AddListener(ChangeHueShift);
        }
        DepthApertureSlider.minValue = 0;
        DepthApertureSlider.maxValue = 32;
        FOVSlider.minValue = 20;
        FOVSlider.maxValue = 120;
        //FocusDistanceSlider.minValue = 0.1f;
        //FocusDistanceSlider.maxValue = 100f;
        SensorSizeXSlider.minValue = 10;
        SensorSizeXSlider.maxValue = 50;
        SensorSizeYSlider.minValue = 10;
        SensorSizeYSlider.maxValue = 50;
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
        // HueShiftSlider.minValue = -180;
        //  HueShiftSlider.maxValue = 180;

        FOVSlider.value = HHC.captureCamera.fieldOfView;
        //FocusDistanceSlider.value = HHC.captureCamera.focalLength;
        SensorSizeXSlider.value = HHC.captureCamera.sensorSize.x;
        SensorSizeYSlider.value = HHC.captureCamera.sensorSize.y;
    }
    private void ChangeDepthActiveState(bool state)
    {
        if (HHC.MetaData.depthOfField != null)
        {
            HHC.MetaData.depthOfField.active = state;
        }
    }
    private void CameraSettingsButtons(int mode)
    {
        bool showSliders = (mode == 0);

        for (int i = 0; i < CameraSettingsSlidersPanel.Length; i++)
        {
            if (CameraSettingsSlidersPanel[i] != null)
                CameraSettingsSlidersPanel[i].SetActive(showSliders);
        }

        for (int i = 0; i < CameraSettingsButtonPanel.Length; i++)
        {
            if (CameraSettingsButtonPanel[i] != null)
                CameraSettingsButtonPanel[i].SetActive(!showSliders);
        }

        // Hide all button subpanels when switching modes
        if (HHC != null)
        {
            if (HHC.ResolutionOptions != null) HHC.ResolutionOptions.SetActive(false);
            if (HHC.FormatOptions != null) HHC.FormatOptions.SetActive(false);
            if (HHC.ApertureOptions != null) HHC.ApertureOptions.SetActive(false);
            if (HHC.ShutterOptions != null) HHC.ShutterOptions.SetActive(false);
            if (HHC.ISOOptions != null) HHC.ISOOptions.SetActive(false);

            if (!showSliders)
            {
                DepthFocusDistanceSlider.gameObject.SetActive(false);
                DepthApertureSlider.gameObject.SetActive(false);
                SensorSizeXSlider.gameObject.SetActive(false);
                SensorSizeYSlider.gameObject.SetActive(false);
                BloomIntensitySlider.gameObject.SetActive(false);
                BloomThresholdSlider.gameObject.SetActive(false);
                ContrastSlider.gameObject.SetActive(false);
                SaturationSlider.gameObject.SetActive(false);
            }
            else
            {
                DepthFocusDistanceSlider.gameObject.SetActive(true);
                DepthApertureSlider.gameObject.SetActive(true);
                SensorSizeXSlider.gameObject.SetActive(true);
                SensorSizeYSlider.gameObject.SetActive(true);
                BloomIntensitySlider.gameObject.SetActive(true);
                BloomThresholdSlider.gameObject.SetActive(true);
                ContrastSlider.gameObject.SetActive(true);
                SaturationSlider.gameObject.SetActive(true);
            }
        }

        BasisDebug.Log($"Camera Settings toggled to {(showSliders ? "Sliders" : "Buttons")}");
    }

    private void SetResolutionOption(int index)
    {
        RES_OPTION = index;
        HHC.ChangeResolution(index);
        BasisDebug.Log($"Resolution changed to index {index} ({HHC.MetaData.resolutions[index].width}x{HHC.MetaData.resolutions[index].height})");
    }

    private void SetFormatOption(int index)
    {
        FORMAT_OPTION = index;
        HHC.ChangeFormat(index);
        BasisDebug.Log($"Format changed to index {index} ({HHC.MetaData.formats[index]})");
    }

    private void SetApertureOption(int index)
    {
        APERTURE_OPTION = index;
        ChangeAperture(index);
        BasisDebug.Log($"Aperture changed to index {index} ({HHC.MetaData.apertures[index]})");
    }

    private void SetShutterOption(int index)
    {
        SHUTTER_OPTION = index;
        ChangeShutterSpeed(index);
        BasisDebug.Log($"Shutter speed changed to index {index} ({HHC.MetaData.shutterSpeeds[index]})");
    }

    private void SetISOOption(int index)
    {
        ISO_OPTION = index;
        ChangeISO(index);
        BasisDebug.Log($"ISO changed to index {index} ({HHC.MetaData.isoValues[index]})");
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
                resolutionIndex = RES_OPTION,
                formatIndex = FORMAT_OPTION,
                apertureIndex = APERTURE_OPTION,
                shutterSpeedIndex = SHUTTER_OPTION,
                isoIndex = ISO_OPTION,
                fov = FOVSlider.value,
                sensorSizeX = SensorSizeXSlider.value,
                sensorSizeY = SensorSizeYSlider.value,
                bloomIntensity = BloomIntensitySlider.value,
                bloomThreshold = BloomThresholdSlider.value,
                contrast = ContrastSlider.value,
                saturation = SaturationSlider.value,
                depthAperture = DepthApertureSlider.value,
                depthFocusDistance = DepthFocusDistanceSlider.value,
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
            SensorSizeXSlider.SetValueWithoutNotify(settings.sensorSizeX);
            SensorSizeYSlider.SetValueWithoutNotify(settings.sensorSizeY);
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
    public void ChangeSensorSizeX(float value)
    {
        HHC.captureCamera.sensorSize = new Vector2(value, HHC.captureCamera.sensorSize.y);
        SSXOutput.text = value.ToString();
    }
    public void ChangeSensorSizeY(float value)
    {
        HHC.captureCamera.sensorSize = new Vector2(HHC.captureCamera.sensorSize.x, value);
        SSYOutput.text = value.ToString();
    }
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
        public int formatIndex = 1;
        public int apertureIndex;
        public int shutterSpeedIndex;
        public int isoIndex;
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
    }
}
