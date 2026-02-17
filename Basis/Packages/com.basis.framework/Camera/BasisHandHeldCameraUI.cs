using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Basis.Scripts.UI.UI_Panels;
using System.Collections.Generic;

public enum CameraOrientation
{
    Landscape,
    LandscapeFlipped,
    PortraitCW,
    PortraitCCW
}
public enum CameraButtonAction
{
    None,
    TakePhoto,
    ResetSettings,
    CloseUI,
    Timer,
    ToggleNameplates,
    ToggleDesktopOutput,
    ToggleSelfie,
    DepthModeAuto,
    DepthModeManual
}

[Serializable]
public class CameraButtonDescriptor
{
    public string id;
    public CameraButtonAction action;
    public Button button;
    public Sprite icon;
}

/// <summary>
/// Handles the handheld camera UI: wiring buttons, toggles, sliders; loading/saving
/// settings; and reflecting values into the capture camera and post-processing stack.
/// </summary>
[Serializable]
public class BasisHandHeldCameraUI
{
    // ---------- Buttons / Toggles / Widgets ----------

    /// <summary>Capture a photo from the handheld camera.</summary>
    public Button TakePhotoButton;

    /// <summary>Reset all UI-controlled settings to defaults.</summary>
    public Button ResetButton;

    /// <summary>Close the camera UI and destroy the handheld camera instance.</summary>
    public Button CloseButton;

    /// <summary>Trigger a photo timer (countdown) capture.</summary>
    public Button Timer;

    /// <summary>Toggle nameplate overlay visibility.</summary>
    public Button Nameplates;

    /// <summary>Toggle desktop output override behavior.</summary>
    public Button OverrideDesktopOutput;

    /// <summary>Toggle selfie (flip) orientation of the camera reference.</summary>
    public Button Selfie;

    [Space(10)]
    /// <summary>Focus cursor GameObject displayed on the preview.</summary>
    public GameObject focusCursor;

    /// <summary>Button to switch DoF to Auto mode.</summary>
    public Button DepthModeAutoButton;

    /// <summary>Button to switch DoF to Manual mode.</summary>
    public Button DepthModeManualButton;

    [Space(10)]
    // Optional dynamic button layout
    public Transform DynamicButtonRoot;
    public Button ButtonPrefab;
    public CameraButtonDescriptor[] ScriptableButtons;

    /// <summary>Depth of Field mode selector.</summary>
    public enum DepthMode { Auto, Manual }

    /// <summary>Current DoF mode.</summary>
    public DepthMode currentDepthMode = DepthMode.Auto;

    [Space(10)]
    /// <summary>Toggle between resolution presets (cycles indexed presets).</summary>
    public Toggle Resolution;

    /// <summary>Resolution indicator sprites; one active at a time.</summary>
    public GameObject[] ResolutionSprites; // 4 resolution sprites

    private int currentResolutionIndex = 0;

    /// <summary>Toggle capture format (PNG / EXR).</summary>
    public Toggle Format;

    /// <summary>True if EXR is selected; otherwise PNG.</summary>
    public bool useEXR => Format != null && Format.isOn;

    private const int FORMAT_PNG = 0;
    private const int FORMAT_EXR = 1;

    /// <summary>UI sprite shown when PNG is active.</summary>
    public GameObject PngSprite;

    /// <summary>UI sprite shown when EXR is active.</summary>
    public GameObject ExrSprite;

    /// <summary>UI sprite indicating DoF Auto mode.</summary>
    public GameObject DoFAutoSprite;

    /// <summary>UI sprite indicating DoF Manual mode.</summary>
    public GameObject DoFManualSprite;

    [Space(10)]
    /// <summary>Discrete exposure control; mapped to <see cref="exposureStops"/>.</summary>
    public Slider ExposureSlider;

    [Space(10)]
    /// <summary>Discrete volumetric Density control; mapped to <see cref="exposureStops"/>.</summary>
    public Slider volumetricDensitySlider;

    private float[] exposureStops = new float[] { -3f, -2.5f, -2f, -1.5f, -1f, -0.5f, 0f, 0.5f, 1f, 1.5f, 2f, 2.5f, 3f };

    [Space(10)]
    /// <summary>UI readout for DoF focus distance.</summary>
    public TextMeshProUGUI DOFFocusOutput;

    /// <summary>UI readout for DoF aperture.</summary>
    public TextMeshProUGUI DepthApertureOutput;

    /// <summary>UI readout for bloom intensity.</summary>
    public TextMeshProUGUI BloomIntensityOutput;

    /// <summary>UI readout for bloom threshold.</summary>
    public TextMeshProUGUI BloomThreshholdOutput;

    /// <summary>UI readout for contrast.</summary>
    public TextMeshProUGUI ContrastOutput;

    /// <summary>UI readout for saturation.</summary>
    public TextMeshProUGUI SaturationOutput;

    /// <summary>UI readout for field-of-view.</summary>
    public TextMeshProUGUI FOVOutput;

    /// <summary>UI readout for Volumetric Fog.</summary>
    public TextMeshProUGUI VolFogOutput;

    [Space(10)]
    /// <summary>Field-of-view slider.</summary>
    public Slider FOVSlider;

    /// <summary>Manual DoF focus distance slider.</summary>
    public Slider DepthFocusDistanceSlider;

    /// <summary>DoF aperture slider.</summary>
    public Slider DepthApertureSlider;

    /// <summary>Bloom intensity slider.</summary>
    public Slider BloomIntensitySlider;

    /// <summary>Bloom threshold slider.</summary>
    public Slider BloomThresholdSlider;

    /// <summary>Color adjustments: contrast slider.</summary>
    public Slider ContrastSlider;

    /// <summary>Color adjustments: saturation slider.</summary>
    public Slider SaturationSlider;

    [Space(10)]
    /// <summary>UI element reoriented between portrait/landscape.</summary>
    public RectTransform uiOrientationElement;

    /// <summary>Secondary UI element reoriented between portrait/landscape.</summary>
    public RectTransform uiOrientationElement2;

    /// <summary>Tertiary UI element reoriented between portrait/landscape.</summary>
    public RectTransform uiOrientationElement3;

    /// <summary>Quaternary UI element reoriented between portrait/landscape.</summary>
    public RectTransform uiOrientationElement4;

    /// <summary>Quinary UI element reoriented between portrait/landscape.</summary>
    public RectTransform uiOrientationElement5;

    [Space(10)]
    /// <summary>Reference object used for selfie flipping (rotates 180° yaw).</summary>
    public GameObject cameraReference;

    private bool selfieBool = false;

    /// <summary>The owning handheld camera component.</summary>
    public BasisHandHeldCamera HHC;

    /// <summary>
    /// Initializes the UI against a handheld camera: caches PP references, loads settings,
    /// binds events, configures ranges and format UI, and seeds slider values.
    /// </summary>
    public async Task Initialize(BasisHandHeldCamera hhc)
    {
        HHC = hhc;
        CachePostProcessingReferences();
        await LoadSettings();

        EnsureDefaultScriptableButtons();
        BindScriptableButtons();
        BindUIEvents();
        SetupSliderRanges();
        InitializeFormatUI();
        SetInitialSliderValues();
    }

    /// <summary>
    /// Reads post-processing volumes/components used by the UI and ensures color adjustments are active.
    /// </summary>
    private void CachePostProcessingReferences()
    {
        HHC.MetaData.Profile.TryGet(out HHC.MetaData.depthOfField);
        HHC.MetaData.Profile.TryGet(out HHC.MetaData.bloom);
        HHC.MetaData.Profile.TryGet(out HHC.MetaData.colorAdjustments);
        if (HHC.MetaData.colorAdjustments != null)
            HHC.MetaData.colorAdjustments.active = true;
    }

    /// <summary>
    /// Wires all UI controls to the appropriate camera handlers.
    /// </summary>
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
        volumetricDensitySlider?.onValueChanged.AddListener(ChangeVolumetricDensity);
        DepthModeAutoButton?.onClick.AddListener(() => SetDepthMode(DepthMode.Auto));
        DepthModeManualButton?.onClick.AddListener(() => SetDepthMode(DepthMode.Manual));
    }
    private void EnsureDefaultScriptableButtons()
    {
        if (ScriptableButtons != null && ScriptableButtons.Length > 0)
            return;

        var list = new List<CameraButtonDescriptor>();

        if (TakePhotoButton != null)
            list.Add(new CameraButtonDescriptor
            {
                id = "TakePhoto",
                action = CameraButtonAction.TakePhoto,
                button = TakePhotoButton
            });

        if (ResetButton != null)
            list.Add(new CameraButtonDescriptor
            {
                id = "Reset",
                action = CameraButtonAction.ResetSettings,
                button = ResetButton
            });

        if (CloseButton != null)
            list.Add(new CameraButtonDescriptor
            {
                id = "Close",
                action = CameraButtonAction.CloseUI,
                button = CloseButton
            });

        if (Timer != null)
            list.Add(new CameraButtonDescriptor
            {
                id = "Timer",
                action = CameraButtonAction.Timer,
                button = Timer
            });
        //Removed Nameplate code
        if (OverrideDesktopOutput != null)
            list.Add(new CameraButtonDescriptor
            {
                id = "OverrideDesktopOutput",
                action = CameraButtonAction.ToggleDesktopOutput,
                button = OverrideDesktopOutput
            });
        //Remove Selfie code
        if (DepthModeAutoButton != null)
            list.Add(new CameraButtonDescriptor
            {
                id = "DepthAuto",
                action = CameraButtonAction.DepthModeAuto,
                button = DepthModeAutoButton
            });

        if (DepthModeManualButton != null)
            list.Add(new CameraButtonDescriptor
            {
                id = "DepthManual",
                action = CameraButtonAction.DepthModeManual,
                button = DepthModeManualButton
            });

        ScriptableButtons = list.ToArray();
    }
    private void BindScriptableButtons()
    {
        if (ScriptableButtons == null || ScriptableButtons.Length == 0)
            return;

        foreach (var descriptor in ScriptableButtons)
        {
            if (descriptor == null)
                continue;

            var button = descriptor.button;

            // Dynamic creation if no button was assigned
            if (button == null && ButtonPrefab != null && DynamicButtonRoot != null)
            {
                button = UnityEngine.Object.Instantiate(ButtonPrefab, DynamicButtonRoot, false);
                descriptor.button = button;
            }

            if (button == null)
                continue;

            if (descriptor.icon != null)
            {
                var image = button.GetComponent<Image>();
                if (image == null)
                    image = button.GetComponentInChildren<Image>();

                if (image != null)
                    image.sprite = descriptor.icon;
            }

            AttachButtonAction(button, descriptor.action);
        }
    }
    private void AttachButtonAction(Button button, CameraButtonAction action)
    {
        if (button == null)
            return;

        switch (action)
        {
            case CameraButtonAction.TakePhoto:
                button.onClick.AddListener(HHC.CapturePhoto);
                break;

            case CameraButtonAction.ResetSettings:
                button.onClick.AddListener(ResetSettings);
                break;

            case CameraButtonAction.CloseUI:
                button.onClick.AddListener(CloseUI);
                break;

            case CameraButtonAction.Timer:
                button.onClick.AddListener(HHC.Timer);
                break;

            case CameraButtonAction.ToggleNameplates:
                button.onClick.AddListener(HHC.Nameplates);
                break;

            case CameraButtonAction.ToggleDesktopOutput:
                button.onClick.AddListener(HHC.OnOverrideDesktopOutputButtonPress);
                break;

            case CameraButtonAction.ToggleSelfie:
                button.onClick.AddListener(SelfieToggle);
                break;

            case CameraButtonAction.DepthModeAuto:
                button.onClick.AddListener(() => SetDepthMode(DepthMode.Auto));
                break;

            case CameraButtonAction.DepthModeManual:
                button.onClick.AddListener(() => SetDepthMode(DepthMode.Manual));
                break;
        }
    }

    /// <summary>
    /// Defines min/max ranges for sliders and seeds FOV from the capture camera.
    /// </summary>
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

    /// <summary>Initializes PNG/EXR toggle sprites based on current state.</summary>
    private void InitializeFormatUI()
    {
        OnFormatToggleChanged(Format.isOn);
    }

    /// <summary>Seeds initial slider values (currently seeds FOV from camera).</summary>
    private void SetInitialSliderValues()
    {
        FOVSlider.value = HHC.captureCamera.fieldOfView;
    }

    /// <summary>
    /// Rotates/positions UI groups depending on device orientation.
    /// </summary>
    public void SetUIOrientation(CameraOrientation orientation)
    {
        if (uiOrientationElement == null)
        {
            BasisDebug.LogError("[Camera UI] uiOrientationElement is NULL! Did you forget to assign it in the Inspector?");
            return;
        }

        switch (orientation)
        {
            case CameraOrientation.Landscape:
                ApplyLandscapeLayout();
                break;

            case CameraOrientation.LandscapeFlipped:
                ApplyLandscapeLayout();
                RotateAllUI180();
                break;

            case CameraOrientation.PortraitCW:
                ApplyPortraitLayout(true);   // “right-hand” portrait
                break;

            case CameraOrientation.PortraitCCW:
                ApplyPortraitLayout(false);  // mirrored portrait
                break;
        }
    }
    private void ApplyLandscapeLayout()
    {
        uiOrientationElement.localRotation = Quaternion.identity;
        uiOrientationElement.localPosition = Vector3.zero;

        uiOrientationElement2.localRotation = Quaternion.identity;
        uiOrientationElement2.localPosition = Vector3.zero;

        uiOrientationElement3.localRotation = Quaternion.identity;
        uiOrientationElement3.localPosition = new Vector3(0f, 600f, 0f);

        uiOrientationElement4.localRotation = Quaternion.Euler(0f, 0f, 90f);
        uiOrientationElement4.localPosition = new Vector3(1250f, 0f, 0f);

        uiOrientationElement5.localRotation = Quaternion.identity;
        uiOrientationElement5.localPosition = Vector3.zero;
    }

    // Rotate everything 180° for upside-down landscape
    private void RotateAllUI180()
    {
        RotateElement180(uiOrientationElement);
        RotateElement180(uiOrientationElement2);
        RotateElement180(uiOrientationElement3);
        RotateElement180(uiOrientationElement4);
        RotateElement180(uiOrientationElement5);
    }

    private void RotateElement180(RectTransform t)
    {
        if (t == null) return;
        t.localRotation *= Quaternion.Euler(0f, 0f, 180f);
        var p = t.localPosition;
        t.localPosition = new Vector3(-p.x, -p.y, p.z);
    }

    private void ApplyPortraitLayout(bool isClockwise)
    {
        const float mainSideOffset = 525f;  // was -525
        const float secondSideOffset = 500f;  // was -500
        const float thirdSideOffsetSum = 1050f; // ~525+525
        const float bottomMainOffset = 725f;  // was -725
        const float bottomSecondaryOffset = 525f;// was -525

        float sideSign = isClockwise ? -1f : 1f;
        float rotZ = isClockwise ? -90f : 90f;

        // Top / left/right bars
        uiOrientationElement.localRotation = Quaternion.Euler(0f, 0f, rotZ);
        uiOrientationElement.localPosition = new Vector3(sideSign * mainSideOffset, 0f, 0f);

        uiOrientationElement2.localRotation = Quaternion.Euler(0f, 0f, rotZ);
        uiOrientationElement2.localPosition = new Vector3(sideSign * secondSideOffset, 0f, 0f);

        uiOrientationElement3.localRotation = Quaternion.Euler(0f, 0f, rotZ);
        uiOrientationElement3.localPosition = new Vector3(-sideSign * thirdSideOffsetSum, 0f, 0f);

        uiOrientationElement4.localRotation = Quaternion.identity;
        uiOrientationElement4.localPosition = new Vector3(0f, -bottomMainOffset, 0f);

        uiOrientationElement5.localRotation = Quaternion.Euler(0f, 0f, rotZ);
        uiOrientationElement5.localPosition = new Vector3(0f, -bottomSecondaryOffset, 0f);
    }
    /// <summary>Flips the camera reference by 180° yaw for a quick selfie toggle.</summary>
    private void SelfieToggle()
    {
        cameraReference.transform.rotation *= Quaternion.Euler(0, 180, 0);
        selfieBool = !selfieBool;
    }

    /// <summary>
    /// Switches Depth of Field mode between Auto and Manual, showing/hiding relevant controls.
    /// </summary>
    public void SetDepthMode(DepthMode mode)
    {
        currentDepthMode = mode;

        bool useAuto = (mode == DepthMode.Auto);
        bool dofIsActive = HHC.MetaData.depthOfField.active;

        focusCursor?.SetActive(dofIsActive);

        // Show if DoF is active; focus slider only in Manual
        DepthApertureSlider.gameObject.SetActive(dofIsActive);
        DepthFocusDistanceSlider.gameObject.SetActive(dofIsActive && !useAuto);

        if (DoFAutoSprite != null) DoFAutoSprite.SetActive(dofIsActive && useAuto);
        if (DoFManualSprite != null) DoFManualSprite.SetActive(dofIsActive && !useAuto);

        BasisDebug.Log($"[DepthMode] Switched to {(useAuto ? "Auto" : "Manual")}");
    }

    /// <summary>
    /// Maps a discrete index to exposure stops and applies post-exposure.
    /// </summary>
    public void ChangeExposureCompensation(float index)
    {
        if (HHC.MetaData.colorAdjustments != null)
        {
            int i = Mathf.Clamp((int)index, 0, exposureStops.Length - 1);
            float exposureValue = exposureStops[i];
            HHC.MetaData.colorAdjustments.postExposure.value = exposureValue;
        }
    }

    /// <summary>
    /// Handles PNG/EXR toggle: sets capture format and updates UI sprites.
    /// </summary>
    private void OnFormatToggleChanged(bool state)
    {
        BasisDebug.Log($"[Format] Changed to {(state ? "EXR" : "PNG")}");
        HHC.captureFormat = state ? "EXR" : "PNG";
        if (PngSprite != null) PngSprite.SetActive(!state);
        if (ExrSprite != null) ExrSprite.SetActive(state);
    }

    /// <summary>
    /// Cycles through resolution presets, updates camera and UI indicators.
    /// </summary>
    private void OnResolutionToggleChanged(bool state)
    {
        currentResolutionIndex = (currentResolutionIndex + 1) % 4;
        HHC.ChangeResolution(currentResolutionIndex);
        UpdateResolutionSprites();
        BasisDebug.Log($"[Resolution] Changed to index {currentResolutionIndex}");
    }

    /// <summary>
    /// Activates only the sprite matching the current resolution index.
    /// </summary>
    private void UpdateResolutionSprites()
    {
        int resolutionCount = ResolutionSprites.Length;

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

    /// <summary>Returns the capture format index (PNG or EXR) for persistence.</summary>
    public int GetFormatIndex()
    {
        return Format != null && Format.isOn ? FORMAT_EXR : FORMAT_PNG;
    }

    /// <summary>
    /// Closes the handheld camera UI, releases player locks, and destroys the camera.
    /// </summary>
    public void CloseUI()
    {
        var cameraInteractable = HHC.GetComponent<BasisHandHeldCameraInteractable>();
        if (cameraInteractable != null)
            cameraInteractable.ReleasePlayerLocks();

        GameObject.Destroy(HHC.gameObject);
        Cursor.visible = false;
    }

    // ---------- Persistence ----------

    /// <summary>Settings file name stored under <see cref="Application.persistentDataPath"/>.</summary>
    public const string CameraSettingsJson = "CameraSettings.json";

    /// <summary>
    /// Serializes and writes current camera settings to disk. Falls back to defaults on failure.
    /// </summary>
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

    /// <summary>Builds a <see cref="CameraSettings"/> snapshot from current UI values.</summary>
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
            VolumetricFogVolumedensity = volumetricDensitySlider?.value ?? 0.01f,
            VolumetricFogenableAPVContribution = true,
            VolumetricFogenableMainLightContribution = true,
            VolumetricenableAdditionalLightsContribution = true,
        };
    }

    /// <summary>Writes a default settings file when saving fails.</summary>
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

    /// <summary>Resets UI and camera to default values (non-persistent).</summary>
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

    /// <summary>
    /// Loads settings from disk; applies defaults if the file is missing or invalid.
    /// </summary>
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

    /// <summary>
    /// Applies a settings snapshot to the camera, post-processing, and UI controls.
    /// </summary>
    private void ApplySettings(CameraSettings settings)
    {
        HHC.BasisDOFInteractionHandler?.SetDoFState(settings.depthIsActive);
        try
        {
            // Resolution & indicator sprites
            currentResolutionIndex = settings.resolutionIndex;
            HHC.ChangeResolution(currentResolutionIndex);
            UpdateResolutionSprites();

            // Optionally force Resolution toggle ON
            if (Resolution != null)
                Resolution.isOn = true;

            // Sliders and toggles
            SetSliderValue(FOVSlider, settings.fov);
            SetSliderValue(BloomIntensitySlider, settings.bloomIntensity);
            SetSliderValue(BloomThresholdSlider, settings.bloomThreshold);
            SetSliderValue(ContrastSlider, settings.contrast);
            SetSliderValue(SaturationSlider, settings.saturation);
            SetSliderValue(DepthApertureSlider, settings.depthAperture);
            SetSliderValue(DepthFocusDistanceSlider, settings.depthFocusDistance);
            SetSliderValue(ExposureSlider, settings.exposureIndex);
            SetSliderValue(volumetricDensitySlider, settings.VolumetricFogVolumedensity);

            if (Format != null)
                Format.isOn = settings.formatIndex == FORMAT_EXR;

            // Capture format guard
            if (settings.resolutionIndex >= 0 && settings.resolutionIndex < HHC.MetaData.formats.Length)
            {
                HHC.captureFormat = HHC.MetaData.formats[settings.resolutionIndex];
            }
            else
            {
                BasisDebug.LogWarning($"[ApplySettings] Invalid resolutionIndex: {settings.resolutionIndex}, formats count: {HHC.MetaData.formats.Length}");
                HHC.captureFormat = HHC.MetaData.formats[0];
            }

            // Camera intrinsics/exposure
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

    /// <summary>Sets a slider’s value without invoking change events (null-safe).</summary>
    private void SetSliderValue(Slider slider, float value)
    {
        if (slider != null)
            slider.SetValueWithoutNotify(value);
    }

    /// <summary>
    /// Applies post-processing values (exposure, color adjustments, DoF, bloom).
    /// </summary>
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
#if Basis_VOLUMETRIC_SUPPORTED
        if (HHC.MetaData.VolumetricFogVolume != null)
        {
            HHC.MetaData.VolumetricFogVolume.density.value = settings.VolumetricFogVolumedensity;
            HHC.MetaData.VolumetricFogVolume.enableAPVContribution.value = settings.VolumetricFogenableAPVContribution;
            HHC.MetaData.VolumetricFogVolume.enableMainLightContribution.value = settings.VolumetricFogenableMainLightContribution;
            HHC.MetaData.VolumetricFogVolume.enableAdditionalLightsContribution.value = settings.VolumetricenableAdditionalLightsContribution;
        }
#endif
    }

    /// <summary>Updates DoF focus distance and readout.</summary>
    public void DepthChangeFocusDistance(float value)
    {
        if (HHC.MetaData.depthOfField != null)
        {
            HHC.MetaData.depthOfField.focusDistance.value = value;
            DOFFocusOutput.text = value.ToString();
        }
    }

    /// <summary>Updates DoF aperture and readout.</summary>
    public void ChangeAperture(float value)
    {
        if (HHC.MetaData.depthOfField != null)
        {
            HHC.MetaData.depthOfField.aperture.value = value;
            DepthApertureOutput.text = value.ToString();
        }
    }

    /// <summary>Updates bloom intensity and readout.</summary>
    public void ChangeBloomIntensity(float value)
    {
        if (HHC.MetaData.bloom != null)
        {
            HHC.MetaData.bloom.intensity.value = value;
            BloomIntensityOutput.text = value.ToString();
        }
    }

    /// <summary>Updates bloom threshold and readout.</summary>
    public void ChangeBloomThreshold(float value)
    {
        if (HHC.MetaData.bloom != null)
        {
            HHC.MetaData.bloom.threshold.value = value;
            BloomThreshholdOutput.text = value.ToString();
        }
    }

    /// <summary>Updates contrast and readout.</summary>
    public void ChangeContrast(float value)
    {
        if (HHC.MetaData.colorAdjustments != null)
        {
            HHC.MetaData.colorAdjustments.contrast.value = value;
            ContrastOutput.text = value.ToString();
        }
    }

    /// <summary>Updates saturation and readout.</summary>
    public void ChangeSaturation(float value)
    {
        if (HHC.MetaData.colorAdjustments != null)
        {
            HHC.MetaData.colorAdjustments.saturation.value = value;
            SaturationOutput.text = value.ToString();
        }
    }

    /// <summary>Updates hue shift (no readout here).</summary>
    public void ChangeHueShift(float value)
    {
        if (HHC.MetaData.colorAdjustments != null)
        {
            HHC.MetaData.colorAdjustments.hueShift.value = value;
        }
    }

    /// <summary>Updates camera field-of-view and readout.</summary>
    public void ChangeFOV(float value)
    {
        HHC.captureCamera.fieldOfView = value;
        FOVOutput.text = value.ToString();
    }

    /// <summary>Updates camera focal length (focus distance for physical camera model).</summary>
    public void ChangeFocusDistance(float value)
    {
        HHC.captureCamera.focalLength = value;
    }

    /// <summary>Parses and applies aperture from metadata by index.</summary>
    public void ChangeAperture(int index)
    {
        HHC.captureCamera.aperture = float.Parse(HHC.MetaData.apertures[index].TrimStart('f', '/'));
    }

    /// <summary>Parses and applies shutter speed from metadata by index (e.g. "1/125").</summary>
    public void ChangeShutterSpeed(int index)
    {
        HHC.captureCamera.shutterSpeed = 1 / float.Parse(HHC.MetaData.shutterSpeeds[index].Split('/')[1]);
    }

    /// <summary>Parses and applies ISO from metadata by index.</summary>
    public void ChangeISO(int index)
    {
        HHC.captureCamera.iso = int.Parse(HHC.MetaData.isoValues[index]);
    }

    public void ChangeVolumetricDensity(float value)
    {
#if Basis_VOLUMETRIC_SUPPORTED
        if (HHC.MetaData.VolumetricFogVolume != null)
        {
            HHC.MetaData.VolumetricFogVolume.density.value = value;
            VolFogOutput.text = value.ToString("F1");
        }
#endif
    }

    /// <summary>
    /// Serializable snapshot of camera + UI state for persistence.
    /// </summary>
    [Serializable]
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
            useManualFocus = true;
            VolumetricFogVolumedensity = 0.01f;
            VolumetricFogenableAPVContribution = true;
            VolumetricFogenableMainLightContribution = true;
            VolumetricenableAdditionalLightsContribution = true;
        }

        /// <summary>Index into resolution presets.</summary>
        public int resolutionIndex = 0;

        /// <summary>0 = PNG, 1 = EXR.</summary>
        public int formatIndex = 0;

        /// <summary>Index into aperture presets (metadata).</summary>
        public int apertureIndex;

        /// <summary>Index into shutter speed presets (metadata).</summary>
        public int shutterSpeedIndex;

        /// <summary>Index into ISO presets (metadata).</summary>
        public int isoIndex;

        /// <summary>Index into <see cref="BasisHandHeldCameraUI.exposureStops"/>.</summary>
        public int exposureIndex = 6;

        /// <summary>Field of view in degrees.</summary>
        public float fov;

        /// <summary>Focal length (used as focus distance in your camera model).</summary>
        public float focusDistance;

        /// <summary>Camera sensor width (mm).</summary>
        public float sensorSizeX;

        /// <summary>Camera sensor height (mm).</summary>
        public float sensorSizeY;

        /// <summary>Bloom intensity value.</summary>
        public float bloomIntensity;

        /// <summary>Bloom threshold value.</summary>
        public float bloomThreshold;

        /// <summary>Color adjustments: contrast.</summary>
        public float contrast;

        /// <summary>Color adjustments: saturation.</summary>
        public float saturation;

        /// <summary>Color adjustments: hue shift (degrees).</summary>
        public float hueShift;

        /// <summary>Depth of Field aperture (f-number).</summary>
        public float depthAperture;

        /// <summary>Depth of Field focus distance.</summary>
        public float depthFocusDistance;

        /// <summary>Whether Depth of Field is active.</summary>
        public bool depthIsActive;

        /// <summary>True = Manual focus mode; False = Auto focus mode.</summary>
        public bool useManualFocus = true;

        /// <summary>
        /// Density of Fog in Camera
        /// </summary>
        public float VolumetricFogVolumedensity;
        /// <summary>
        /// APV enabled Fog
        /// </summary>
        public bool VolumetricFogenableAPVContribution;
        /// <summary>
        /// Main Light Enabled Fog
        /// </summary>
        public bool VolumetricFogenableMainLightContribution;
        /// <summary>
        /// Addittional Light Contribution
        /// </summary>
        public bool VolumetricenableAdditionalLightsContribution;
    }
}
