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
    public TMP_Dropdown ResolutionDropdown;
    public TMP_Dropdown FormatDropdown;
    public TMP_Dropdown CameraApertureDropdown;
    public TMP_Dropdown ShutterSpeedDropdown;
    public TMP_Dropdown ISODropdown;

    public Slider FOVSlider;
    public Slider DepthFocusDistanceSlider;
    public Slider DepthApertureSlider;

    public Slider SensorSizeXSlider;
    public Slider SensorSizeYSlider;
    public Slider FocusDistanceSlider;
    public Slider BloomIntensitySlider;
    public Slider BloomThresholdSlider;
    public Slider ContrastSlider;
    public Slider SaturationSlider;
    //  public Slider HueShiftSlider;
    public BasisHandHeldCamera HHC;
    public async Task Initalize(BasisHandHeldCamera hhc)
    {
        HHC = hhc;

        await LoadSettings();

        PopulateDropdown(ResolutionDropdown, HHC.MetaData.resolutions.Select(r => $"{r.width}x{r.height}").ToArray());
        PopulateDropdown(FormatDropdown, HHC.MetaData.formats);
        PopulateDropdown(CameraApertureDropdown, HHC.MetaData.apertures);
        PopulateDropdown(ShutterSpeedDropdown, HHC.MetaData.shutterSpeeds);
        PopulateDropdown(ISODropdown, HHC.MetaData.isoValues);

        DepthApertureSlider.onValueChanged.AddListener(ChangeAperture);
        TakePhotoButton.onClick.AddListener(HHC.CapturePhoto);
        ResetButton.onClick.AddListener(ResetSettings);
        Timer.onClick.AddListener(HHC.Timer);
        Nameplates.onClick.AddListener(HHC.Nameplates);
        ResolutionDropdown.onValueChanged.AddListener(HHC.ChangeResolution);
        FormatDropdown.onValueChanged.AddListener(HHC.ChangeFormat);
        CameraApertureDropdown.onValueChanged.AddListener(ChangeAperture);
        ShutterSpeedDropdown.onValueChanged.AddListener(ChangeShutterSpeed);
        ISODropdown.onValueChanged.AddListener(ChangeISO);
        FOVSlider.onValueChanged.AddListener(ChangeFOV);
        FocusDistanceSlider.onValueChanged.AddListener(ChangeFocusDistance);
        SensorSizeXSlider.onValueChanged.AddListener(ChangeSensorSizeX);
        SensorSizeYSlider.onValueChanged.AddListener(ChangeSensorSizeY);
        CloseButton.onClick.AddListener(CloseUI);

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
        FocusDistanceSlider.minValue = 0.1f;
        FocusDistanceSlider.maxValue = 100f;
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
        FocusDistanceSlider.value = HHC.captureCamera.focalLength;
        SensorSizeXSlider.value = HHC.captureCamera.sensorSize.x;
        SensorSizeYSlider.value = HHC.captureCamera.sensorSize.y;
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
                resolutionIndex = ResolutionDropdown.value,
                formatIndex = FormatDropdown.value,
                apertureIndex = CameraApertureDropdown.value,
                shutterSpeedIndex = ShutterSpeedDropdown.value,
                isoIndex = ISODropdown.value,
                fov = FOVSlider.value,
                focusDistance = FocusDistanceSlider.value,
                sensorSizeX = SensorSizeXSlider.value,
                sensorSizeY = SensorSizeYSlider.value,
                bloomIntensity = BloomIntensitySlider.value,
                bloomThreshold = BloomThresholdSlider.value,
                contrast = ContrastSlider.value,
                saturation = SaturationSlider.value,
                depthAperture = DepthApertureSlider.value,
                depthFocusDistance = DepthFocusDistanceSlider.value
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
            ResolutionDropdown.SetValueWithoutNotify(settings.resolutionIndex);
            FormatDropdown.SetValueWithoutNotify(settings.formatIndex);
            CameraApertureDropdown.SetValueWithoutNotify(settings.apertureIndex);
            ShutterSpeedDropdown.SetValueWithoutNotify(settings.shutterSpeedIndex);
            ISODropdown.SetValueWithoutNotify(settings.isoIndex);

            FOVSlider.SetValueWithoutNotify(settings.fov);
            FocusDistanceSlider.SetValueWithoutNotify(settings.focusDistance);
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
        }
    }

    public void ChangeAperture(float value)
    {
        if (HHC.MetaData.depthOfField != null)
        {
            HHC.MetaData.depthOfField.aperture.value = value;
        }
    }

    public void ChangeBloomIntensity(float value)
    {
        if (HHC.MetaData.bloom != null)
        {
            HHC.MetaData.bloom.intensity.value = value;
        }
    }

    public void ChangeBloomThreshold(float value)
    {
        if (HHC.MetaData.bloom != null)
        {
            HHC.MetaData.bloom.threshold.value = value;
        }
    }

    public void ChangeContrast(float value)
    {
        if (HHC.MetaData.colorAdjustments != null)
        {
            HHC.MetaData.colorAdjustments.contrast.value = value;
        }
    }

    public void ChangeSaturation(float value)
    {
        if (HHC.MetaData.colorAdjustments != null)
        {
            HHC.MetaData.colorAdjustments.saturation.value = value;
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
    }
    public void ChangeSensorSizeY(float value)
    {
        HHC.captureCamera.sensorSize = new Vector2(HHC.captureCamera.sensorSize.x, value);
    }
    public void ChangeFOV(float value)
    {
        HHC.captureCamera.fieldOfView = value;
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
    }
}
