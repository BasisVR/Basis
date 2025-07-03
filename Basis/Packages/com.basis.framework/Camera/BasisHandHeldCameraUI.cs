using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Basis.Scripts.UI.UI_Panels;

public enum CameraOrientation { Landscape, Portrait }

[System.Serializable]

public class BasisHandHeldCameraUI
{
    public Button TakePhotoButton;
    public Button ResetButton;
    public Button CloseButton;
    public Button Timer;
    public Button Nameplates;
    public Button OverrideDesktopOutput;
    public Button Selfie;
    [Space(10)]
    public GameObject focusCursor;
    public Button DepthModeAutoButton;
    public Button DepthModeManualButton;
    public enum DepthMode { Auto, Manual }
    public DepthMode currentDepthMode = DepthMode.Auto;
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
    public GameObject DoFAutoSprite;
    public GameObject DoFManualSprite;
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
    public RectTransform uiOrientationElement;
    public RectTransform uiOrientationElement2;
    public RectTransform uiOrientationElement3;
    public RectTransform uiOrientationElement4;
    public RectTransform uiOrientationElement5;
    [Space(10)]
    public GameObject cameraReference;
    private bool selfie = false;
    public BasisHandHeldCamera HHC;
    public async Task Initialize(BasisHandHeldCamera hhc)
    {
        HHC = hhc;
        CachePostProcessingReferences();
        await LoadSettings();
        BindUIEvents();
        SetupSliderRanges();
        InitializeFormatUI();
        SetInitialSliderValues();
    }

    private void CachePostProcessingReferences()
    {
        HHC.MetaData.Profile.TryGet(out HHC.MetaData.depthOfField);
        HHC.MetaData.Profile.TryGet(out HHC.MetaData.bloom);
        HHC.MetaData.Profile.TryGet(out HHC.MetaData.colorAdjustments);
        if (HHC.MetaData.colorAdjustments != null)
            HHC.MetaData.colorAdjustments.active = true;
    }

    private void BindUIEvents()
    {
        TakePhotoButton?.onClick.AddListener(HHC.CapturePhoto);
        ResetButton?.onClick.AddListener(ResetSettings);
        Timer?.onClick.AddListener(HHC.Timer);
        Nameplates?.onClick.AddListener(HHC.Nameplates);
        OverrideDesktopOutput?.onClick.AddListener(HHC.OnOverrideDesktopOutputButtonPress);
        CloseButton?.onClick.AddListener(CloseUI);
        Selfie?.onClick.AddListener(SelfieToggle);
        Resolution?.onValueChanged.AddListener(OnResolutionToggleChanged);
        Format?.onValueChanged.AddListener(OnFormatToggleChanged);

        FOVSlider?.onValueChanged.AddListener(ChangeFOV);
        ExposureSlider?.onValueChanged.AddListener(ChangeExposureCompensation);
        DepthApertureSlider?.onValueChanged.AddListener(ChangeAperture);
        DepthFocusDistanceSlider?.onValueChanged.AddListener(DepthChangeFocusDistance);

        BloomIntensitySlider?.onValueChanged.AddListener(ChangeBloomIntensity);
        BloomThresholdSlider?.onValueChanged.AddListener(ChangeBloomThreshold);
        ContrastSlider?.onValueChanged.AddListener(ChangeContrast);
        SaturationSlider?.onValueChanged.AddListener(ChangeSaturation);

        DepthModeAutoButton?.onClick.AddListener(() => SetDepthMode(DepthMode.Auto));
        DepthModeManualButton?.onClick.AddListener(() => SetDepthMode(DepthMode.Manual));
    }

    private void SetupSliderRanges()
    {
        DepthApertureSlider.minValue = 0;
        DepthApertureSlider.maxValue = 32;
        FOVSlider.minValue = 20;
        FOVSlider.maxValue = 120;
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
        FOVSlider.value = HHC.captureCamera.fieldOfView;
    }

    private void InitializeFormatUI()
    {
        OnFormatToggleChanged(Format.isOn);
    }

    private void SetInitialSliderValues()
    {
        FOVSlider.value = HHC.captureCamera.fieldOfView;
    }
    public void SetUIOrientation(CameraOrientation orientation)
    {
        if (uiOrientationElement == null)
        {
            BasisDebug.LogError("[Camera UI] uiOrientationElement is NULL! Did you forget to assign it in the Inspector?");
            return;
        }

        bool isPortrait = orientation == CameraOrientation.Portrait;

        uiOrientationElement.localRotation = isPortrait
            ? Quaternion.Euler(0f, 0f, -90f)
            : Quaternion.identity;
        uiOrientationElement.localPosition = isPortrait
            ? new Vector3(-525f, 0f, 0f)
            : Vector3.zero;

        uiOrientationElement2.localRotation = isPortrait
            ? Quaternion.Euler(0f, 0f, -90f)
            : Quaternion.identity;
        uiOrientationElement2.localPosition = isPortrait
            ? new Vector3(-500f, 0f, 0f)
            : Vector3.zero;

        uiOrientationElement3.localRotation = isPortrait
            ? Quaternion.Euler(0f, 0f, -90f)
            : Quaternion.identity;
        uiOrientationElement3.localPosition = isPortrait
            ? new Vector3(1050f, 0f, 0f)
            : new Vector3(0f, 600f, 0f);

        uiOrientationElement4.localRotation = isPortrait
            ? Quaternion.Euler(0f, 0f, 0f)
            : Quaternion.Euler(0f, 0f, 90f);
        uiOrientationElement4.localPosition = isPortrait
            ? new Vector3(0f, -725f, 0f)
            : new Vector3(1250f, 0f, 0f);

        uiOrientationElement5.localRotation = isPortrait
            ? Quaternion.Euler(0f, 0f, -90f)
            : Quaternion.Euler(0f, 0f, 0f);
        uiOrientationElement5.localPosition = isPortrait
            ? new Vector3(0f, -525f, 0f)
            : new Vector3(0f, 0f, 0f);
    }
    private void SelfieToggle()
    {
        cameraReference.transform.rotation *= Quaternion.Euler(0, 180, 0);
        selfie = !selfie;
    }
    public void SetDepthMode(DepthMode mode)
    {
        currentDepthMode = mode;

        bool useAuto = (mode == DepthMode.Auto);
        bool dofIsActive = HHC.MetaData.depthOfField.active;

        focusCursor?.SetActive(dofIsActive);

        DepthApertureSlider.gameObject.SetActive(dofIsActive); // Only show if DoF is active
        DepthFocusDistanceSlider.gameObject.SetActive(dofIsActive && !useAuto); // Only in Manual mode

        if (DoFAutoSprite != null) DoFAutoSprite.SetActive(dofIsActive && useAuto);
        if (DoFManualSprite != null) DoFManualSprite.SetActive(dofIsActive && !useAuto);

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
        int resolutionCount = ResolutionSprites.Length;

        // Ensure index is in bounds before the loop
        if (currentResolutionIndex < 0 || currentResolutionIndex >= resolutionCount)
        {
            BasisDebug.LogWarning($"[UpdateResolutionSprites] Invalid currentResolutionIndex: {currentResolutionIndex}, ResolutionSprites.Length: {resolutionCount}");
            return;
        }

        for (int i = 0; i < resolutionCount; i++)
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
        if (BasisHamburgerMenu.activeCameraInstance == this.HHC.gameObject)
        {
            BasisHamburgerMenu.activeCameraInstance = null;
        }
        var cameraInteractable = HHC.GetComponent<BasisHandHeldCameraInteractable>();
        if (cameraInteractable != null)
            cameraInteractable.ReleasePlayerLocks();

        GameObject.Destroy(HHC.gameObject);
        Cursor.visible = false;
    }

    public const string CameraSettingsJson = "CameraSettings.json";
    public async Task SaveSettings()
    {
        CameraSettings settingsToSave = CreateCurrentCameraSettings();

        try
        {
            string json = JsonUtility.ToJson(settingsToSave, true);
            string settingsFilePath = Path.Combine(Application.persistentDataPath, CameraSettingsJson);
            await File.WriteAllTextAsync(settingsFilePath, json);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"[SaveSettings] Failed: {ex.Message}");
            await SaveDefaultSettings();
        }
    }

    private CameraSettings CreateCurrentCameraSettings()
    {
        return new CameraSettings
        {
            resolutionIndex = currentResolutionIndex,
            formatIndex = GetFormatIndex(),
            fov = FOVSlider?.value ?? 60f,
            bloomIntensity = BloomIntensitySlider?.value ?? 0.5f,
            bloomThreshold = BloomThresholdSlider?.value ?? 0.5f,
            contrast = ContrastSlider?.value ?? 1f,
            saturation = SaturationSlider?.value ?? 1f,
            depthAperture = DepthApertureSlider?.value ?? 1f,
            depthFocusDistance = DepthFocusDistanceSlider?.value ?? 10f,
            exposureIndex = Mathf.Clamp((int)(ExposureSlider?.value ?? 6), 0, exposureStops.Length - 1),
        };
    }

    private async Task SaveDefaultSettings()
    {
        try
        {
            CameraSettings defaultSettings = new CameraSettings();
            string json = JsonUtility.ToJson(defaultSettings, true);
            string settingsFilePath = Path.Combine(Application.persistentDataPath, CameraSettingsJson);
            await File.WriteAllTextAsync(settingsFilePath, json);
            BasisDebug.Log("Default camera settings saved.");
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"[SaveDefaultSettings] Failed: {ex.Message}");
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
        string settingsFilePath = Path.Combine(Application.persistentDataPath, CameraSettingsJson);

        if (!File.Exists(settingsFilePath))
        {
            BasisDebug.Log("[LoadSettings] Settings file not found. Applying default values.");
            ApplySettings(new CameraSettings());
            return;
        }

        try
        {
            string json = await File.ReadAllTextAsync(settingsFilePath);
            CameraSettings loadedSettings = JsonUtility.FromJson<CameraSettings>(json);
            ApplySettings(loadedSettings);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"[LoadSettings] Failed to load settings: {ex.Message}");
            ApplySettings(new CameraSettings());
        }
    }

    private void ApplySettings(CameraSettings settings)
    {
        HHC.BasisDOFInteractionHandler?.SetDoFState(settings.depthIsActive);
        try
        {
            // Update resolution UI
            currentResolutionIndex = settings.resolutionIndex;
            HHC.ChangeResolution(currentResolutionIndex);
            UpdateResolutionSprites();

            // Optionally also force the Resolution toggle ON
            if (Resolution != null)
                Resolution.isOn = true;

            // Sync UI sliders (null safe)
            SetSliderValue(FOVSlider, settings.fov);
            SetSliderValue(BloomIntensitySlider, settings.bloomIntensity);
            SetSliderValue(BloomThresholdSlider, settings.bloomThreshold);
            SetSliderValue(ContrastSlider, settings.contrast);
            SetSliderValue(SaturationSlider, settings.saturation);
            SetSliderValue(DepthApertureSlider, settings.depthAperture);
            SetSliderValue(DepthFocusDistanceSlider, settings.depthFocusDistance);
            SetSliderValue(ExposureSlider, settings.exposureIndex);

            if (Format != null)
                Format.isOn = settings.formatIndex == FORMAT_EXR;

            // Camera setup — safe index access
            if (settings.resolutionIndex >= 0 && settings.resolutionIndex < HHC.MetaData.formats.Length)
            {
                HHC.captureFormat = HHC.MetaData.formats[settings.resolutionIndex];
            }
            else
            {
                BasisDebug.LogWarning($"[ApplySettings] Invalid resolutionIndex: {settings.resolutionIndex}, formats count: {HHC.MetaData.formats.Length}");
                HHC.captureFormat = HHC.MetaData.formats[0];
            }

            HHC.captureCamera.fieldOfView = settings.fov;
            HHC.captureCamera.focalLength = settings.focusDistance;
            HHC.captureCamera.sensorSize = new Vector2(settings.sensorSizeX, settings.sensorSizeY);

            if (settings.apertureIndex >= 0 && settings.apertureIndex < HHC.MetaData.apertures.Length)
            {
                HHC.captureCamera.aperture = float.Parse(HHC.MetaData.apertures[settings.apertureIndex].TrimStart('f', '/'));
            }
            else
            {
                BasisDebug.LogWarning($"[ApplySettings] Invalid apertureIndex: {settings.apertureIndex}, count: {HHC.MetaData.apertures.Length}");
            }

            if (settings.shutterSpeedIndex >= 0 && settings.shutterSpeedIndex < HHC.MetaData.shutterSpeeds.Length)
            {
                string[] parts = HHC.MetaData.shutterSpeeds[settings.shutterSpeedIndex].Split('/');
                if (parts.Length == 2 && float.TryParse(parts[1], out float denominator))
                {
                    HHC.captureCamera.shutterSpeed = 1f / denominator;
                }
                else
                {
                    BasisDebug.LogWarning($"[ApplySettings] Invalid shutter speed format: {HHC.MetaData.shutterSpeeds[settings.shutterSpeedIndex]}");
                }
            }
            else
            {
                BasisDebug.LogWarning($"[ApplySettings] Invalid shutterSpeedIndex: {settings.shutterSpeedIndex}, count: {HHC.MetaData.shutterSpeeds.Length}");
            }

            if (settings.isoIndex >= 0 && settings.isoIndex < HHC.MetaData.isoValues.Length)
            {
                HHC.captureCamera.iso = int.Parse(HHC.MetaData.isoValues[settings.isoIndex]);
            }
            else
            {
                BasisDebug.LogWarning($"[ApplySettings] Invalid isoIndex: {settings.isoIndex}, count: {HHC.MetaData.isoValues.Length}");
            }

            ApplyPostProcessingSettings(settings);
            SetDepthMode(settings.useManualFocus ? DepthMode.Manual : DepthMode.Auto);
            focusCursor?.SetActive(settings.depthIsActive);
            BasisDebug.Log("[ApplySettings] Camera settings applied successfully.");
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"[ApplySettings] Failed: {ex.Message}");
        }
    }

    private void SetSliderValue(Slider slider, float value)
    {
        if (slider != null)
            slider.SetValueWithoutNotify(value);
    }

    private void ApplyPostProcessingSettings(CameraSettings settings)
    {
        int clampedExposure = Mathf.Clamp(settings.exposureIndex, 0, exposureStops.Length - 1);

        if (HHC.MetaData.colorAdjustments != null)
        {
            HHC.MetaData.colorAdjustments.postExposure.value = exposureStops[clampedExposure];
            HHC.MetaData.colorAdjustments.contrast.value = settings.contrast;
            HHC.MetaData.colorAdjustments.saturation.value = settings.saturation;
        }

        if (HHC.MetaData.depthOfField != null)
        {
            HHC.MetaData.depthOfField.aperture.value = settings.depthAperture;
            HHC.MetaData.depthOfField.focusDistance.value = settings.depthFocusDistance;
            HHC.MetaData.depthOfField.active = settings.depthIsActive;
        }

        if (HHC.MetaData.bloom != null)
        {
            HHC.MetaData.bloom.intensity.value = settings.bloomIntensity;
            HHC.MetaData.bloom.threshold.value = settings.bloomThreshold;
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
            depthIsActive = false;
        }
        public int resolutionIndex = 0;
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
