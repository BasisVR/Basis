using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Basis.BasisUI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Handles the handheld camera UI: wiring buttons, toggles, sliders; loading/saving
/// settings; and reflecting values into the capture camera and post-processing stack.
/// </summary>
[Serializable]
public partial class BasisHandHeldCameraUI
{
    public Button TakePhotoButton;
    public Button ResetButton;
    public Button CloseButton;
    public Button Timer;
    public Button Nameplates;
    public Button OverrideDesktopOutput;
    public Button Selfie;
    public Button AutoLevelButton;
    public Button VRStabilizationButton;

    [Space(10)]
    public GameObject focusCursor;

    public Button DepthModeAutoButton;
    public Button DepthModeManualButton;

    [Space(10)]
    // Optional dynamic button layout
    public Transform DynamicButtonRoot;
    public Button ButtonPrefab;
    public BasisCameraButtonDescriptor[] ScriptableButtons;

    public enum DepthMode { Auto, Manual }
    public DepthMode currentDepthMode = DepthMode.Auto;
    public bool IsSelfieMode => selfieBool;
    public Transform imagePreviewFlip;

    [Space(10)]
    /// <summary>
    /// IMPORTANT: This behaves like a "cycle button" in your original code, not a real toggle.
    /// We keep it as Toggle to avoid breaking prefab hookups, but we force it back off.
    /// </summary>
    public Toggle Resolution;
    public GameObject[] ResolutionSprites; // 4 resolution sprites
    private int currentResolutionIndex = 0;

    public Toggle Format;
    public bool useEXR => Format != null && Format.isOn;

    private const int FORMAT_PNG = 0;
    private const int FORMAT_EXR = 1;

    public GameObject PngSprite;
    public GameObject ExrSprite;

    public GameObject DoFAutoSprite;
    public GameObject DoFManualSprite;

    [Space(10)]
    public Slider ExposureSlider;

    [Space(10)]
    public Slider volumetricDensitySlider;

    private static readonly float[] ExposureStops =
    {
        -3f, -2.5f, -2f, -1.5f, -1f, -0.5f, 0f, 0.5f, 1f, 1.5f, 2f, 2.5f, 3f
    };

    [Space(10)]
    public TextMeshProUGUI DOFFocusOutput;
    public TextMeshProUGUI DepthApertureOutput;
    public TextMeshProUGUI BloomIntensityOutput;
    public TextMeshProUGUI BloomThreshholdOutput;
    public TextMeshProUGUI ContrastOutput;
    public TextMeshProUGUI SaturationOutput;
    public TextMeshProUGUI FOVOutput;
    public TextMeshProUGUI VolFogOutput;

    [Space(10)]
    public Slider FOVSlider;
    public Slider DepthFocusDistanceSlider;
    public Slider DepthApertureSlider;
    public Slider BloomIntensitySlider;
    public Slider BloomThresholdSlider;
    public Slider ContrastSlider;
    public Slider SaturationSlider;

    [Space(10)]
    // Keep your existing fields so you don't have to redo prefab references.
    public RectTransform uiOrientationElement;
    public RectTransform uiOrientationElement2;
    public RectTransform uiOrientationElement3;
    public RectTransform uiOrientationElement4;
    public RectTransform uiOrientationElement5;

    [Space(10)]
    public GameObject cameraReference;
    public GameObject uiOrientationReference;
    private bool selfieBool = false;

    public BasisHandHeldCamera HHC;
    public async Task Initialize(BasisHandHeldCamera hhc)
    {
        HHC = hhc;

        CachePostProcessingReferences();
        SetupSliderRanges();

        // Build default descriptors from existing button references if not set
        EnsureDefaultScriptableButtons();

        // Bind ONCE through descriptor system (prevents double listeners)
        BindScriptableButtons();

        // Bind non-descriptor UI (sliders/toggles)
        BindNonButtonUIEvents();

        // Load and apply settings after bindings are in place (but we use SetValueWithoutNotify to prevent spam)
        await LoadSettings();

        InitializeFormatUI();
        SeedInitialSliderValues();
        UpdateResolutionSprites();
    }

    private void CachePostProcessingReferences()
    {
        HHC.MetaData.Profile.TryGet(out HHC.MetaData.depthOfField);
        HHC.MetaData.Profile.TryGet(out HHC.MetaData.bloom);
        HHC.MetaData.Profile.TryGet(out HHC.MetaData.colorAdjustments);

        if (HHC.MetaData.colorAdjustments != null)
            HHC.MetaData.colorAdjustments.active = true;
    }

    // ---------- Binding (Buttons via descriptors) ----------

    private void EnsureDefaultScriptableButtons()
    {
        if (ScriptableButtons != null && ScriptableButtons.Length > 0)
            return;

        var list = new List<BasisCameraButtonDescriptor>();

        AddIf(list, "TakePhoto", TakePhotoButton, BasisCameraButtonAction.TakePhoto);
        AddIf(list, "Reset", ResetButton, BasisCameraButtonAction.ResetSettings);
        AddIf(list, "Close", CloseButton, BasisCameraButtonAction.CloseUI);
        AddIf(list, "Timer", Timer, BasisCameraButtonAction.Timer);

        // Optional buttons (may be removed in your project)
        AddIf(list, "Nameplates", Nameplates, BasisCameraButtonAction.ToggleNameplates);
        AddIf(list, "OverrideDesktopOutput", OverrideDesktopOutput, BasisCameraButtonAction.ToggleDesktopOutput);
        AddIf(list, "Selfie", Selfie, BasisCameraButtonAction.ToggleSelfie);
        AddIf(list, "AutoLevel", AutoLevelButton, BasisCameraButtonAction.ToggleAutoLevel);
        AddIf(list, "VRStabilization", VRStabilizationButton, BasisCameraButtonAction.ToggleVRHandheldSmoothing);

        AddIf(list, "DepthAuto", DepthModeAutoButton, BasisCameraButtonAction.DepthModeAuto);
        AddIf(list, "DepthManual", DepthModeManualButton, BasisCameraButtonAction.DepthModeManual);

        ScriptableButtons = list.ToArray();

        static void AddIf(List<BasisCameraButtonDescriptor> l, string id, Button b, BasisCameraButtonAction a)
        {
            if (b == null) return;
            l.Add(new BasisCameraButtonDescriptor { id = id, action = a, button = b });
        }
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

            // Create dynamically if allowed
            if (button == null && ButtonPrefab != null && DynamicButtonRoot != null)
            {
                button = UnityEngine.Object.Instantiate(ButtonPrefab, DynamicButtonRoot, false);
                descriptor.button = button;
            }

            if (button == null)
                continue;

            // Prevent stacking listeners on re-init / reuse
            button.onClick.RemoveAllListeners();

            // Apply icon if present
            if (descriptor.icon != null)
            {
                var image = button.GetComponent<Image>() ?? button.GetComponentInChildren<Image>();
                if (image != null)
                    image.sprite = descriptor.icon;
            }

            AttachButtonAction(button, descriptor.action);
        }
    }

    private void AttachButtonAction(Button button, BasisCameraButtonAction action)
    {
        if (button == null) return;

        switch (action)
        {
            case BasisCameraButtonAction.TakePhoto:
                button.onClick.AddListener(HHC.CapturePhoto);
                break;

            case BasisCameraButtonAction.ResetSettings:
                button.onClick.AddListener(ResetSettings);
                break;

            case BasisCameraButtonAction.CloseUI:
                button.onClick.AddListener(CloseUI);
                break;

            case BasisCameraButtonAction.Timer:
                button.onClick.AddListener(HHC.Timer);
                break;

            case BasisCameraButtonAction.ToggleNameplates:
                button.onClick.AddListener(HHC.Nameplates);
                break;

            case BasisCameraButtonAction.ToggleDesktopOutput:
                button.onClick.AddListener(HHC.OnOverrideDesktopOutputButtonPress);
                break;

            case BasisCameraButtonAction.ToggleSelfie:
                button.onClick.AddListener(SelfieToggle);
                break;

            case BasisCameraButtonAction.ToggleAutoLevel:
                button.onClick.AddListener(ToggleAutoLevel);
                break;

            case BasisCameraButtonAction.ToggleVRHandheldSmoothing:
                button.onClick.AddListener(ToggleVRHandheldSmoothing);
                break;

            case BasisCameraButtonAction.DepthModeAuto:
                button.onClick.AddListener(() => SetDepthMode(DepthMode.Auto));
                break;

            case BasisCameraButtonAction.DepthModeManual:
                button.onClick.AddListener(() => SetDepthMode(DepthMode.Manual));
                break;
        }
    }
    private void BindNonButtonUIEvents()
    {
        if (Resolution != null)
        {
            Resolution.onValueChanged.RemoveAllListeners();
            Resolution.onValueChanged.AddListener(_ => CycleResolutionPreset());
        }

        if (Format != null)
        {
            Format.onValueChanged.RemoveAllListeners();
            Format.onValueChanged.AddListener(OnFormatToggleChanged);
        }

        HookSlider(FOVSlider, ChangeFOV);
        HookSlider(ExposureSlider, ChangeExposureCompensation);
        HookSlider(DepthApertureSlider, ChangeAperture);
        HookSlider(DepthFocusDistanceSlider, DepthChangeFocusDistance);
        HookSlider(BloomIntensitySlider, ChangeBloomIntensity);
        HookSlider(BloomThresholdSlider, ChangeBloomThreshold);
        HookSlider(ContrastSlider, ChangeContrast);
        HookSlider(SaturationSlider, ChangeSaturation);
        HookSlider(volumetricDensitySlider, ChangeVolumetricDensity);
    }

    private static void HookSlider(Slider slider, Action<float> handler)
    {
        if (slider == null) return;
        slider.onValueChanged.RemoveAllListeners();
        slider.onValueChanged.AddListener(v => handler(v));
    }

    // ---------- Ranges / Initial ----------

    private void SetupSliderRanges()
    {
        if (DepthApertureSlider != null) { DepthApertureSlider.minValue = 0f; DepthApertureSlider.maxValue = 32f; }
        if (FOVSlider != null) { FOVSlider.minValue = 20f; FOVSlider.maxValue = 120f; }
        if (DepthFocusDistanceSlider != null) { DepthFocusDistanceSlider.minValue = 0.1f; DepthFocusDistanceSlider.maxValue = 100f; }
        if (BloomIntensitySlider != null) { BloomIntensitySlider.minValue = 0f; BloomIntensitySlider.maxValue = 5f; }
        if (BloomThresholdSlider != null) { BloomThresholdSlider.minValue = 0.1f; BloomThresholdSlider.maxValue = 2f; }
        if (ContrastSlider != null) { ContrastSlider.minValue = -100f; ContrastSlider.maxValue = 100f; }
        if (SaturationSlider != null) { SaturationSlider.minValue = -100f; SaturationSlider.maxValue = 100f; }

        if (HHC != null && HHC.captureCamera != null && FOVSlider != null)
            FOVSlider.SetValueWithoutNotify(HHC.captureCamera.fieldOfView);
    }

    private void InitializeFormatUI()
    {
        if (Format != null)
            OnFormatToggleChanged(Format.isOn);
    }

    private void SeedInitialSliderValues()
    {
        if (HHC != null && HHC.captureCamera != null && FOVSlider != null)
            FOVSlider.SetValueWithoutNotify(HHC.captureCamera.fieldOfView);
    }

    // ---------- Orientation ----------

    public void SetUIOrientation(BasisCameraOrientation orientation)
    {
        if (uiOrientationElement == null)
        {
            BasisDebug.LogError("[Camera UI] uiOrientationElement is NULL! Did you forget to assign it in the Inspector?");
            return;
        }

        switch (orientation)
        {
            case BasisCameraOrientation.Landscape:
                ApplyLandscapeLayout();
                break;

            case BasisCameraOrientation.LandscapeFlipped:
                ApplyLandscapeLayout();
                RotateAllUI180();
                break;

            case BasisCameraOrientation.PortraitCW:
                ApplyPortraitLayout(true);
                break;

            case BasisCameraOrientation.PortraitCCW:
                ApplyPortraitLayout(false);
                break;
        }
    }

    private void ApplyLandscapeLayout()
    {
        if (uiOrientationElement != null) { uiOrientationElement.localRotation = Quaternion.identity; uiOrientationElement.localPosition = Vector3.zero; }
        if (uiOrientationElement2 != null) { uiOrientationElement2.localRotation = Quaternion.identity; uiOrientationElement2.localPosition = Vector3.zero; }
        if (uiOrientationElement3 != null) { uiOrientationElement3.localRotation = Quaternion.identity; uiOrientationElement3.localPosition = new Vector3(0f, 600f, 0f); }
        if (uiOrientationElement4 != null) { uiOrientationElement4.localRotation = Quaternion.Euler(0f, 0f, 90f); uiOrientationElement4.localPosition = new Vector3(1250f, 0f, 0f); }
        if (uiOrientationElement5 != null) { uiOrientationElement5.localRotation = Quaternion.identity; uiOrientationElement5.localPosition = Vector3.zero; }
    }

    private void RotateAllUI180()
    {
        RotateElement180(uiOrientationElement);
        RotateElement180(uiOrientationElement2);
        RotateElement180(uiOrientationElement3);
        RotateElement180(uiOrientationElement4);
        RotateElement180(uiOrientationElement5);
    }

    private static void RotateElement180(RectTransform t)
    {
        if (t == null) return;
        t.localRotation *= Quaternion.Euler(0f, 0f, 180f);
        var p = t.localPosition;
        t.localPosition = new Vector3(-p.x, -p.y, p.z);
    }

    private void ApplyPortraitLayout(bool isClockwise)
    {
        const float mainSideOffset = 525f;
        const float secondSideOffset = 500f;
        const float thirdSideOffsetSum = 1050f;
        const float bottomMainOffset = 725f;
        const float bottomSecondaryOffset = 525f;

        float sideSign = isClockwise ? -1f : 1f;
        float rotZ = isClockwise ? -90f : 90f;

        if (uiOrientationElement != null)
        {
            uiOrientationElement.localRotation = Quaternion.Euler(0f, 0f, rotZ);
            uiOrientationElement.localPosition = new Vector3(sideSign * mainSideOffset, 0f, 0f);
        }

        if (uiOrientationElement2 != null)
        {
            uiOrientationElement2.localRotation = Quaternion.Euler(0f, 0f, rotZ);
            uiOrientationElement2.localPosition = new Vector3(sideSign * secondSideOffset, 0f, 0f);
        }

        if (uiOrientationElement3 != null)
        {
            uiOrientationElement3.localRotation = Quaternion.Euler(0f, 0f, rotZ);
            uiOrientationElement3.localPosition = new Vector3(-sideSign * thirdSideOffsetSum, 0f, 0f);
        }

        if (uiOrientationElement4 != null)
        {
            uiOrientationElement4.localRotation = Quaternion.identity;
            uiOrientationElement4.localPosition = new Vector3(0f, -bottomMainOffset, 0f);
        }

        if (uiOrientationElement5 != null)
        {
            uiOrientationElement5.localRotation = Quaternion.Euler(0f, 0f, rotZ);
            uiOrientationElement5.localPosition = new Vector3(0f, -bottomSecondaryOffset, 0f);
        }
    }

    // ---------- UI Actions ----------

    private void SelfieToggle()
    {
        selfieBool = !selfieBool;

        var interactable = HHC != null ? HHC.GetComponent<BasisHandHeldCameraInteractable>() : null;
        interactable?.SetSelfieRotationEnabled(selfieBool);

        if (imagePreviewFlip != null)
        {
            Vector3 scale = imagePreviewFlip.localScale;
            scale.x = selfieBool ? -Mathf.Abs(scale.x) : Mathf.Abs(scale.x);
            imagePreviewFlip.localScale = scale;
        }
    }
    
    private void ToggleAutoLevel()
    {
        if (HHC == null)
            return;

        HHC.useAutoLeveling = !HHC.useAutoLeveling;
        BasisDebug.Log($"[AutoLevel] Auto leveling is now {(HHC.useAutoLeveling ? "ON" : "OFF")}");
    }
    private void ToggleVRHandheldSmoothing()
    {
        if (HHC == null)
            return;

        HHC.useVRHandheldSmoothing = !HHC.useVRHandheldSmoothing;
        BasisDebug.Log($"[VRStabilization] VR handheld smoothing is now {(HHC.useVRHandheldSmoothing ? "ON" : "OFF")}");
    }

    public void SetDepthMode(DepthMode mode)
    {
        currentDepthMode = mode;

        bool useAuto = (mode == DepthMode.Auto);
        bool dofIsActive = HHC != null && HHC.MetaData.depthOfField != null && HHC.MetaData.depthOfField.active;

        focusCursor?.SetActive(dofIsActive);

        if (DepthApertureSlider != null)
            DepthApertureSlider.gameObject.SetActive(dofIsActive);

        if (DepthFocusDistanceSlider != null)
            DepthFocusDistanceSlider.gameObject.SetActive(dofIsActive && !useAuto);

        if (DoFAutoSprite != null) DoFAutoSprite.SetActive(dofIsActive && useAuto);
        if (DoFManualSprite != null) DoFManualSprite.SetActive(dofIsActive && !useAuto);

        BasisDebug.Log($"[DepthMode] Switched to {(useAuto ? "Auto" : "Manual")}");
    }

    public void ChangeExposureCompensation(float index)
    {
        if (HHC == null || HHC.MetaData.colorAdjustments == null) return;

        int i = Mathf.Clamp((int)index, 0, ExposureStops.Length - 1);
        HHC.MetaData.colorAdjustments.postExposure.value = ExposureStops[i];
    }

    private void OnFormatToggleChanged(bool state)
    {
        BasisDebug.Log($"[Format] Changed to {(state ? "EXR" : "PNG")}");

        if (HHC != null)
            HHC.captureFormat = state ? "EXR" : "PNG";

        if (PngSprite != null) PngSprite.SetActive(!state);
        if (ExrSprite != null) ExrSprite.SetActive(state);
    }

    private void CycleResolutionPreset()
    {
        currentResolutionIndex = (currentResolutionIndex + 1) % 4;

        if (HHC != null)
            HHC.ChangeResolution(currentResolutionIndex);

        UpdateResolutionSprites();

        // Make the Toggle behave like a momentary "cycle" control (prevents it staying checked)
        if (Resolution != null)
            Resolution.SetIsOnWithoutNotify(false);

        BasisDebug.Log($"[Resolution] Changed to index {currentResolutionIndex}");
    }

    private void UpdateResolutionSprites()
    {
        if (ResolutionSprites == null || ResolutionSprites.Length == 0)
            return;

        int count = ResolutionSprites.Length;

        if (currentResolutionIndex < 0 || currentResolutionIndex >= count)
        {
            BasisDebug.LogWarning($"[UpdateResolutionSprites] Invalid currentResolutionIndex: {currentResolutionIndex}, ResolutionSprites.Length: {count}");
            return;
        }

        for (int i = 0; i < count; i++)
        {
            if (ResolutionSprites[i] != null)
                ResolutionSprites[i].SetActive(i == currentResolutionIndex);
        }
    }

    public int GetFormatIndex()
    {
        return Format != null && Format.isOn ? FORMAT_EXR : FORMAT_PNG;
    }

    public void ReleaseUILock()
    {
        var cameraInteractable = HHC.GetComponent<BasisHandHeldCameraInteractable>();
        cameraInteractable?.ReleasePlayerLocks();

        // only hide the cursor if the basis main menu is not there
        if(BasisMainMenu.Instance == null)
            Cursor.visible = false;
    }

    public void CloseUI()
    {
        if (HHC == null) return;

        ReleaseUILock();
        GameObject.Destroy(HHC.gameObject);
    }

    // ---------- Persistence ----------

    public const string CameraSettingsJson = "CameraSettings.json";

    public async Task SaveSettings()
    {
        var settingsToSave = CreateCurrentCameraSettings();

        try
        {
            string json = JsonUtility.ToJson(settingsToSave, true);
            string path = Path.Combine(Application.persistentDataPath, CameraSettingsJson);
            await File.WriteAllTextAsync(path, json);
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
            fov = FOVSlider != null ? FOVSlider.value : 40f,
            bloomIntensity = BloomIntensitySlider != null ? BloomIntensitySlider.value : 0.5f,
            bloomThreshold = BloomThresholdSlider != null ? BloomThresholdSlider.value : 0.5f,
            contrast = ContrastSlider != null ? ContrastSlider.value : 1f,
            saturation = SaturationSlider != null ? SaturationSlider.value : 1f,
            depthAperture = DepthApertureSlider != null ? DepthApertureSlider.value : 1f,
            depthFocusDistance = DepthFocusDistanceSlider != null ? DepthFocusDistanceSlider.value : 10f,
            exposureIndex = Mathf.Clamp((int)(ExposureSlider != null ? ExposureSlider.value : 6), 0, ExposureStops.Length - 1),
            VolumetricFogVolumedensity = volumetricDensitySlider != null ? volumetricDensitySlider.value : 0.01f,
            VolumetricFogenableAPVContribution = true,
            VolumetricFogenableMainLightContribution = true,
            VolumetricenableAdditionalLightsContribution = true,
        };
    }

    private async Task SaveDefaultSettings()
    {
        try
        {
            var defaultSettings = new CameraSettings();
            string json = JsonUtility.ToJson(defaultSettings, true);
            string path = Path.Combine(Application.persistentDataPath, CameraSettingsJson);
            await File.WriteAllTextAsync(path, json);
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
        string path = Path.Combine(Application.persistentDataPath, CameraSettingsJson);

        if (!File.Exists(path))
        {
            BasisDebug.Log("[LoadSettings] Settings file not found. Applying default values.");
            ApplySettings(new CameraSettings());
            return;
        }

        try
        {
            string json = await File.ReadAllTextAsync(path);
            var loaded = JsonUtility.FromJson<CameraSettings>(json);
            ApplySettings(loaded);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"[LoadSettings] Failed to load settings: {ex.Message}");
            ApplySettings(new CameraSettings());
        }
    }

    private void ApplySettings(CameraSettings settings)
    {
        if (HHC == null) return;

        // DOF interaction handler first (if present)
        HHC.BasisDOFInteractionHandler?.SetDoFState(settings.depthIsActive);

        try
        {
            // Resolution & indicator sprites
            currentResolutionIndex = settings.resolutionIndex;
            HHC.ChangeResolution(currentResolutionIndex);
            UpdateResolutionSprites();

            // Resolution toggle momentary behavior
            if (Resolution != null)
                Resolution.SetIsOnWithoutNotify(false);

            // Sliders and toggles (no notify)
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
                Format.SetIsOnWithoutNotify(settings.formatIndex == FORMAT_EXR);

            // Apply camera intrinsics
            if (HHC.captureCamera != null)
            {
                HHC.captureCamera.fieldOfView = settings.fov;
                HHC.captureCamera.focalLength = settings.focusDistance;
                HHC.captureCamera.sensorSize = new Vector2(settings.sensorSizeX, settings.sensorSizeY);

                // Aperture
                if (settings.apertureIndex >= 0 && settings.apertureIndex < HHC.MetaData.apertures.Length)
                {
                    HHC.captureCamera.aperture = float.Parse(HHC.MetaData.apertures[settings.apertureIndex].TrimStart('f', '/'));
                }
                else
                {
                    BasisDebug.LogWarning($"[ApplySettings] Invalid apertureIndex: {settings.apertureIndex}, count: {HHC.MetaData.apertures.Length}");
                }

                // Shutter speed
                if (settings.shutterSpeedIndex >= 0 && settings.shutterSpeedIndex < HHC.MetaData.shutterSpeeds.Length)
                {
                    string[] parts = HHC.MetaData.shutterSpeeds[settings.shutterSpeedIndex].Split('/');
                    if (parts.Length == 2 && float.TryParse(parts[1], out float denominator) && denominator != 0f)
                        HHC.captureCamera.shutterSpeed = 1f / denominator;
                    else
                        BasisDebug.LogWarning($"[ApplySettings] Invalid shutter speed format: {HHC.MetaData.shutterSpeeds[settings.shutterSpeedIndex]}");
                }
                else
                {
                    BasisDebug.LogWarning($"[ApplySettings] Invalid shutterSpeedIndex: {settings.shutterSpeedIndex}, count: {HHC.MetaData.shutterSpeeds.Length}");
                }

                // ISO
                if (settings.isoIndex >= 0 && settings.isoIndex < HHC.MetaData.isoValues.Length)
                {
                    HHC.captureCamera.iso = int.Parse(HHC.MetaData.isoValues[settings.isoIndex]);
                }
                else
                {
                    BasisDebug.LogWarning($"[ApplySettings] Invalid isoIndex: {settings.isoIndex}, count: {HHC.MetaData.isoValues.Length}");
                }
            }

            // Post-processing
            ApplyPostProcessingSettings(settings);

            // Depth UI mode & cursor
            SetDepthMode(settings.useManualFocus ? DepthMode.Manual : DepthMode.Auto);
            focusCursor?.SetActive(settings.depthIsActive);

            // Update readouts
            RefreshAllReadouts();

            // Ensure format UI reflects toggle
            if (Format != null)
                OnFormatToggleChanged(Format.isOn);

            BasisDebug.Log("[ApplySettings] Camera settings applied successfully.");
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"[ApplySettings] Failed: {ex.Message}");
        }
    }

    private static void SetSliderValue(Slider slider, float value)
    {
        if (slider != null)
            slider.SetValueWithoutNotify(value);
    }

    private void ApplyPostProcessingSettings(CameraSettings settings)
    {
        int clampedExposure = Mathf.Clamp(settings.exposureIndex, 0, ExposureStops.Length - 1);

        if (HHC.MetaData.colorAdjustments != null)
        {
            HHC.MetaData.colorAdjustments.postExposure.value = ExposureStops[clampedExposure];
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

    private void RefreshAllReadouts()
    {
        if (FOVOutput != null && FOVSlider != null) FOVOutput.text = FOVSlider.value.ToString();
        if (BloomIntensityOutput != null && BloomIntensitySlider != null) BloomIntensityOutput.text = BloomIntensitySlider.value.ToString();
        if (BloomThreshholdOutput != null && BloomThresholdSlider != null) BloomThreshholdOutput.text = BloomThresholdSlider.value.ToString();
        if (ContrastOutput != null && ContrastSlider != null) ContrastOutput.text = ContrastSlider.value.ToString();
        if (SaturationOutput != null && SaturationSlider != null) SaturationOutput.text = SaturationSlider.value.ToString();
        if (DepthApertureOutput != null && DepthApertureSlider != null) DepthApertureOutput.text = DepthApertureSlider.value.ToString();
        if (DOFFocusOutput != null && DepthFocusDistanceSlider != null) DOFFocusOutput.text = DepthFocusDistanceSlider.value.ToString();
#if Basis_VOLUMETRIC_SUPPORTED
        if (VolFogOutput != null && volumetricDensitySlider != null) VolFogOutput.text = volumetricDensitySlider.value.ToString("F1");
#endif
    }
    public void DepthChangeFocusDistance(float value)
    {
        if (HHC.MetaData.depthOfField != null)
        {
            HHC.MetaData.depthOfField.focusDistance.value = value;
            if (DOFFocusOutput != null) DOFFocusOutput.text = value.ToString();
        }
    }

    public void ChangeAperture(float value)
    {
        if (HHC.MetaData.depthOfField != null)
        {
            HHC.MetaData.depthOfField.aperture.value = value;
            if (DepthApertureOutput != null) DepthApertureOutput.text = value.ToString();
        }
    }

    public void ChangeBloomIntensity(float value)
    {
        if (HHC.MetaData.bloom != null)
        {
            HHC.MetaData.bloom.intensity.value = value;
            if (BloomIntensityOutput != null) BloomIntensityOutput.text = value.ToString();
        }
    }

    public void ChangeBloomThreshold(float value)
    {
        if (HHC.MetaData.bloom != null)
        {
            HHC.MetaData.bloom.threshold.value = value;
            if (BloomThreshholdOutput != null) BloomThreshholdOutput.text = value.ToString();
        }
    }

    public void ChangeContrast(float value)
    {
        if (HHC.MetaData.colorAdjustments != null)
        {
            HHC.MetaData.colorAdjustments.contrast.value = value;
            if (ContrastOutput != null) ContrastOutput.text = value.ToString();
        }
    }

    public void ChangeSaturation(float value)
    {
        if (HHC.MetaData.colorAdjustments != null)
        {
            HHC.MetaData.colorAdjustments.saturation.value = value;
            if (SaturationOutput != null) SaturationOutput.text = value.ToString();
        }
    }

    public void ChangeHueShift(float value)
    {
        if (HHC.MetaData.colorAdjustments != null)
        {
            HHC.MetaData.colorAdjustments.hueShift.value = value;
        }
    }

    public void ChangeFOV(float value)
    {
        if (HHC.captureCamera != null)
            HHC.captureCamera.fieldOfView = value;

        if (FOVOutput != null)
            FOVOutput.text = value.ToString();
    }

    public void ChangeFocusDistance(float value)
    {
        if (HHC.captureCamera != null)
            HHC.captureCamera.focalLength = value;
    }

    public void ChangeAperture(int index)
    {
        if (HHC.captureCamera == null) return;
        HHC.captureCamera.aperture = float.Parse(HHC.MetaData.apertures[index].TrimStart('f', '/'));
    }

    public void ChangeShutterSpeed(int index)
    {
        if (HHC.captureCamera == null) return;
        HHC.captureCamera.shutterSpeed = 1 / float.Parse(HHC.MetaData.shutterSpeeds[index].Split('/')[1]);
    }

    public void ChangeISO(int index)
    {
        if (HHC.captureCamera == null) return;
        HHC.captureCamera.iso = int.Parse(HHC.MetaData.isoValues[index]);
    }

    public void ChangeVolumetricDensity(float value)
    {
#if Basis_VOLUMETRIC_SUPPORTED
        if (HHC.MetaData.VolumetricFogVolume != null)
        {
            HHC.MetaData.VolumetricFogVolume.density.value = value;
            if (VolFogOutput != null) VolFogOutput.text = value.ToString("F1");
        }
#endif
    }
}
