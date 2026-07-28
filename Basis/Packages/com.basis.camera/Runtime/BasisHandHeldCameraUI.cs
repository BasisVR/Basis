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
    public Button CloseButton;
    public Button Timer;
    public Button Selfie;
    public Button OpenCameraPanelButton;

    [Space(10)]
    public GameObject focusCursor;

    public Button DepthModeAutoButton;
    public Button DepthModeManualButton;

    [Space(10)]
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

    public bool useEXR => FormatIndex == FORMAT_EXR;

    public const int FORMAT_PNG = 0;
    public const int FORMAT_EXR = 1;

    public int FormatIndex { get; private set; } = FORMAT_PNG;

    public void SetFormat(int index)
    {
        FormatIndex = index == FORMAT_EXR ? FORMAT_EXR : FORMAT_PNG;
        bool isEXR = FormatIndex == FORMAT_EXR;

        if (HHC != null)
            HHC.captureFormat = isEXR ? "EXR" : "PNG";
    }

    public GameObject DoFAutoSprite;
    public GameObject DoFManualSprite;

    [Space(10)]
    public Slider ExposureSlider;

    private static readonly float[] ExposureStops =
    {
        -3f, -2.5f, -2f, -1.5f, -1f, -0.5f, 0f, 0.5f, 1f, 1.5f, 2f, 2.5f, 3f
    };

    public static int ExposureStopCount => ExposureStops.Length;

    public int ExposureIndex { get; private set; } = 6;

    /// <summary>
    /// Whether the exposure slider is shown on the camera's own interface. Off by default —
    /// exposure is always reachable from the camera panel, this only adds it to the prop.
    /// </summary>
    public bool ShowExposureOnCamera { get; private set; }

    /// <summary>Shows or hides the exposure slider on the camera's interface. Persists with the rest of the camera settings.</summary>
    public void SetExposureOnCameraVisible(bool visible)
    {
        ShowExposureOnCamera = visible;
        // The slider's own GameObject is the whole exposure block on the prop
        // (bar, fill and handle are its children).
        if (ExposureSlider != null) ExposureSlider.gameObject.SetActive(visible);
    }

    [Space(10)]
    public TextMeshProUGUI DOFFocusOutput;
    public TextMeshProUGUI DepthApertureOutput;
    public TextMeshProUGUI FOVOutput;

    [Space(10)]
    public Slider FOVSlider;
    public Slider DepthFocusDistanceSlider;
    public Slider DepthApertureSlider;

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
        SetCapture360State(HHC != null && HHC.capture360Enabled);
        RefreshAllToggleIndicators();
    }

    private void CachePostProcessingReferences()
    {
        HHC.MetaData.Profile.TryGet(out HHC.MetaData.depthOfField);
        HHC.MetaData.Profile.TryGet(out HHC.MetaData.bloom);
        HHC.MetaData.Profile.TryGet(out HHC.MetaData.colorAdjustments);

        if (HHC.MetaData.colorAdjustments != null)
            HHC.MetaData.colorAdjustments.active = true;

        // The camera owns its profile asset outright (nothing else references it), so adding the
        // extra overrides at runtime is safe and keeps the authored asset untouched. Each starts
        // inactive so an unconfigured effect never silently alters the shot.
        HHC.MetaData.vignette = GetOrAddOverride<UnityEngine.Rendering.Universal.Vignette>();
        HHC.MetaData.chromaticAberration = GetOrAddOverride<UnityEngine.Rendering.Universal.ChromaticAberration>();
        HHC.MetaData.filmGrain = GetOrAddOverride<UnityEngine.Rendering.Universal.FilmGrain>();
        HHC.MetaData.whiteBalance = GetOrAddOverride<UnityEngine.Rendering.Universal.WhiteBalance>();
        HHC.MetaData.lensDistortion = GetOrAddOverride<UnityEngine.Rendering.Universal.LensDistortion>();
    }

    private T GetOrAddOverride<T>() where T : UnityEngine.Rendering.VolumeComponent
    {
        if (HHC == null || HHC.MetaData.Profile == null) return null;
        if (HHC.MetaData.Profile.TryGet(out T existing)) return existing;

        T added = HHC.MetaData.Profile.Add<T>(true);
        if (added != null) added.active = false;
        return added;
    }

    // ---------- Binding (Buttons via descriptors) ----------

    private void EnsureDefaultScriptableButtons()
    {
        if (ScriptableButtons != null && ScriptableButtons.Length > 0)
            return;

        var list = new List<BasisCameraButtonDescriptor>();

        AddIf(list, "TakePhoto", TakePhotoButton, BasisCameraButtonAction.TakePhoto);
        AddIf(list, "Close", CloseButton, BasisCameraButtonAction.CloseUI);
        AddIf(list, "Timer", Timer, BasisCameraButtonAction.Timer);
        AddIf(list, "Selfie", Selfie, BasisCameraButtonAction.ToggleSelfie);

        AddIf(list, "DepthAuto", DepthModeAutoButton, BasisCameraButtonAction.DepthModeAuto);
        AddIf(list, "DepthManual", DepthModeManualButton, BasisCameraButtonAction.DepthModeManual);
        AddIf(list, "OpenCameraPanel", OpenCameraPanelButton, BasisCameraButtonAction.OpenCameraPanel);

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
            SetupToggleIndicator(descriptor);
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

            case BasisCameraButtonAction.ToggleSelfie:
                button.onClick.AddListener(SelfieToggle);
                break;

            case BasisCameraButtonAction.DepthModeAuto:
                button.onClick.AddListener(() => SetDepthMode(DepthMode.Auto));
                break;

            case BasisCameraButtonAction.DepthModeManual:
                button.onClick.AddListener(() => SetDepthMode(DepthMode.Manual));
                break;

            case BasisCameraButtonAction.OpenCameraPanel:
                button.onClick.AddListener(OpenCameraPanel);
                break;
        }
    }

    private void OpenCameraPanel()
    {
        BasisMainMenu.OpenWithProvider(Basis.BasisUI.HandHeldCamera.BasisHandHeldCameraPanelProvider.StaticTitle);
    }
    private void BindNonButtonUIEvents()
    {
        if (Resolution != null)
        {
            Resolution.onValueChanged.RemoveAllListeners();
            Resolution.onValueChanged.AddListener(_ => CycleResolutionPreset());
        }

        HookSlider(FOVSlider, ChangeFOV);
        HookSlider(ExposureSlider, ChangeExposureCompensation);
        HookSlider(DepthApertureSlider, ChangeAperture);
        HookSlider(DepthFocusDistanceSlider, DepthChangeFocusDistance);
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
        if (DepthApertureSlider != null) { DepthApertureSlider.minValue = MinAperture; DepthApertureSlider.maxValue = MaxAperture; }
        if (FOVSlider != null) { FOVSlider.minValue = 20f; FOVSlider.maxValue = 120f; }
        if (DepthFocusDistanceSlider != null) { DepthFocusDistanceSlider.minValue = 0.1f; DepthFocusDistanceSlider.maxValue = 100f; }

        if (HHC != null && HHC.captureCamera != null && FOVSlider != null)
            FOVSlider.SetValueWithoutNotify(HHC.captureCamera.fieldOfView);
    }

    private void InitializeFormatUI()
    {
        SetFormat(FormatIndex);
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

    /// <summary>Flips the camera between selfie (facing the user, mirrored) and normal.</summary>
    public void ToggleSelfie() => SelfieToggle();

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
    
    public void SetCapture360State(bool enabled)
    {
        if (HHC != null)
            HHC.capture360Enabled = enabled;

        BasisDebug.Log($"[360] Capture mode is now {(enabled ? "ON" : "OFF")}");
    }

    /// <summary>
    /// True when Auto is selected and auto-focus will actually drive the focus distance. Auto with
    /// nothing to track leaves the slider as the only way to focus, so it has to stay reachable.
    /// </summary>
    public bool AutoFocusIsDriving =>
        currentDepthMode == DepthMode.Auto && HHC != null && HHC.autoFocusFollowSubject && HHC.CanAutoFocusOnFollowSubject;

    public void SetDepthMode(DepthMode mode)
    {
        currentDepthMode = mode;

        bool useAuto = (mode == DepthMode.Auto);
        bool dofIsActive = HHC != null && HHC.MetaData.depthOfField != null && HHC.MetaData.depthOfField.active;

        focusCursor?.SetActive(dofIsActive);

        if (DepthApertureSlider != null)
            DepthApertureSlider.gameObject.SetActive(dofIsActive && DoFMode == 2);

        if (DepthFocusDistanceSlider != null)
            DepthFocusDistanceSlider.gameObject.SetActive(dofIsActive && !AutoFocusIsDriving);

        if (DoFAutoSprite != null) DoFAutoSprite.SetActive(dofIsActive && useAuto);
        if (DoFManualSprite != null) DoFManualSprite.SetActive(dofIsActive && !useAuto);

        BasisDebug.Log($"[DepthMode] Switched to {(useAuto ? "Auto" : "Manual")}");
    }

    public void ChangeExposureCompensation(float index)
    {
        if (HHC == null || HHC.MetaData.colorAdjustments == null) return;

        int i = Mathf.Clamp((int)index, 0, ExposureStops.Length - 1);
        ExposureIndex = i;
        HHC.MetaData.colorAdjustments.postExposure.value = ExposureStops[i];
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
        return FormatIndex;
    }

    public void ReleaseUILock()
    {
        var cameraInteractable = HHC.GetComponent<BasisHandHeldCameraInteractable>();
        cameraInteractable?.ReleasePlayerLocks();

        // only hide the cursor if the basis main menu is not there
        if(BasisMainMenu.Instance == null)
            Cursor.visible = false;
    }

    /// <summary>
    /// Whether closing the camera hides it instead of destroying it. Left on, the camera keeps
    /// capturing and streaming while invisible, and bringing it out again restores that session
    /// rather than starting a new one.
    /// </summary>
    public bool CloseHidesCamera;

    public void CloseUI()
    {
        if (HHC == null) return;

        ReleaseUILock();

        if (CloseHidesCamera)
        {
            // Dismiss to hidden (not just hide the visuals) so the panel offers Bring Back.
            HHC.CloseToHidden();
            return;
        }

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
        var bloom = HHC != null ? HHC.MetaData.bloom : null;
        var colorAdjustments = HHC != null ? HHC.MetaData.colorAdjustments : null;

        var settings = new CameraSettings
        {
            resolutionIndex = currentResolutionIndex,
            formatIndex = GetFormatIndex(),
            msaaSamples = HHC != null ? HHC.msaaSamples : 2,
            fov = FOVSlider != null ? FOVSlider.value : 40f,
            bloomIntensity = bloom != null ? bloom.intensity.value : 0.5f,
            bloomThreshold = bloom != null ? bloom.threshold.value : 0.5f,
            contrast = colorAdjustments != null ? colorAdjustments.contrast.value : 1f,
            saturation = colorAdjustments != null ? colorAdjustments.saturation.value : 1f,
            depthAperture = DepthApertureSlider != null ? DepthApertureSlider.value : 1f,
            depthFocusDistance = DepthFocusDistanceSlider != null ? DepthFocusDistanceSlider.value : 10f,
            // depthIsActive owns whether DoF is on, so it has to be captured from the live effect.
            // It was never saved before, which only went unnoticed because loading re-derived the
            // on/off from dofMode instead.
            depthIsActive = HHC != null && HHC.MetaData.depthOfField != null && HHC.MetaData.depthOfField.active,
            // Applied on load but never captured, so the auto/manual focus choice reset every session.
            useManualFocus = currentDepthMode == DepthMode.Manual,
            dofMode = HHC != null && HHC.MetaData.depthOfField != null ? (int)HHC.MetaData.depthOfField.mode.value : 2,
            dofFocalLength = HHC != null && HHC.MetaData.depthOfField != null ? HHC.MetaData.depthOfField.focalLength.value : 125f,
            dofBladeCount = HHC != null && HHC.MetaData.depthOfField != null ? HHC.MetaData.depthOfField.bladeCount.value : 5,
            exposureIndex = Mathf.Clamp((int)(ExposureSlider != null ? ExposureSlider.value : 6), 0, ExposureStops.Length - 1),
            showExposureOnCamera = ShowExposureOnCamera,
            VolumetricFogVolumedensity = 0.01f,
            VolumetricFogenableAPVContribution = true,
            VolumetricFogenableMainLightContribution = true,
            hueShift = HHC != null && HHC.MetaData.colorAdjustments != null ? HHC.MetaData.colorAdjustments.hueShift.value : 0f,
            vignette = HHC != null && HHC.MetaData.vignette != null ? HHC.MetaData.vignette.intensity.value : 0f,
            chromaticAberration = HHC != null && HHC.MetaData.chromaticAberration != null ? HHC.MetaData.chromaticAberration.intensity.value : 0f,
            filmGrain = HHC != null && HHC.MetaData.filmGrain != null ? HHC.MetaData.filmGrain.intensity.value : 0f,
            whiteBalanceTemperature = HHC != null && HHC.MetaData.whiteBalance != null ? HHC.MetaData.whiteBalance.temperature.value : 0f,
            whiteBalanceTint = HHC != null && HHC.MetaData.whiteBalance != null ? HHC.MetaData.whiteBalance.tint.value : 0f,
            lensDistortion = HHC != null && HHC.MetaData.lensDistortion != null ? HHC.MetaData.lensDistortion.intensity.value : 0f,
            autoFocusFollowSubject = HHC != null && HHC.autoFocusFollowSubject,
            autoFollowPositionOffset = HHC != null ? HHC.autoFollowPositionOffset : new Vector3(0.5f, 0f, 1.4f),
            autoFollowRotationOffset = HHC != null ? HHC.autoFollowRotationOffset : Vector3.zero,
            autoFollowPlayspace = HHC == null || HHC.autoFollowPlayspace,
            autoFollowLookAtPlayer = HHC == null || HHC.autoFollowLookAtPlayer,
            autoFollowLookAtHeightOffset = HHC != null ? HHC.autoFollowLookAtHeightOffset : 0f,
            capture360 = HHC != null && HHC.capture360Enabled,
            useAutoLeveling = HHC != null && HHC.useAutoLeveling,
            useVRHandheldSmoothing = HHC != null && HHC.useVRHandheldSmoothing,
        };

#if Basis_VOLUMETRIC_SUPPORTED
        if (HHC != null && HHC.MetaData.VolumetricFogVolume != null)
            settings.VolumetricFogVolumedensity = HHC.MetaData.VolumetricFogVolume.density.value;
#endif

        return settings;
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
            MigrateSettings(loaded);
            ApplySettings(loaded);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"[LoadSettings] Failed to load settings: {ex.Message}");
            ApplySettings(new CameraSettings());
        }
    }

    /// <summary>
    /// Fills fields that a pre-v2 file lacks with their real defaults, since JsonUtility zero-fills
    /// absent fields — without this, upgrading a save would silently turn follow features off and
    /// park the camera at the player's origin (position offset 0,0,0).
    /// </summary>
    private static void MigrateSettings(CameraSettings settings)
    {
        if (settings.settingsVersion < 2)
        {
            var defaults = new CameraSettings();
            settings.autoFollowPositionOffset = defaults.autoFollowPositionOffset;
            settings.autoFollowRotationOffset = defaults.autoFollowRotationOffset;
            settings.autoFollowPlayspace = defaults.autoFollowPlayspace;
            settings.autoFollowLookAtPlayer = defaults.autoFollowLookAtPlayer;
            settings.autoFollowLookAtHeightOffset = defaults.autoFollowLookAtHeightOffset;
            settings.msaaSamples = defaults.msaaSamples;
        }

        if (settings.settingsVersion < 3)
        {
            // dofMode zero-fills to 0 (Off), which would silently disable DoF on upgrade.
            var defaults = new CameraSettings();
            settings.dofMode = defaults.dofMode;
            settings.dofFocalLength = defaults.dofFocalLength;
            settings.dofBladeCount = defaults.dofBladeCount;
        }

        if (settings.settingsVersion < 4)
        {
            // depthIsActive was never written before v4, so every old file has it false while the
            // real on/off lived in dofMode (Off meant disabled). Recover it from there, or upgrading
            // would silently switch depth of field off for anyone who had it on.
            settings.depthIsActive = settings.dofMode != 0;
        }

        if (settings.settingsVersion < 5)
        {
            // f/1 on a 125mm lens is a depth of field about 3cm deep at portrait range, so no
            // subject was ever usefully in focus. Take everyone to the portrait-sane defaults.
            var defaults = new CameraSettings();
            settings.depthAperture = defaults.depthAperture;
            settings.dofFocalLength = defaults.dofFocalLength;
        }

        settings.settingsVersion = CameraSettings.CurrentVersion;
    }

#if UNITY_INCLUDE_TESTS
    /// <summary>Test-only access to the private migration.</summary>
    public static void MigrateSettingsForTest(CameraSettings settings) => MigrateSettings(settings);
#endif

    private void ApplySettings(CameraSettings settings)
    {
        if (HHC == null) return;

        // DOF interaction handler first (if present)
        HHC.BasisDOFInteractionHandler?.SetDoFState(settings.depthIsActive);

        try
        {
            // MSAA before resolution: ChangeResolution rebuilds the RT, so the sample count must
            // be in place first or the preview keeps the old one until the next rebuild.
            HHC.msaaSamples = settings.msaaSamples;

            // Resolution & indicator sprites
            currentResolutionIndex = settings.resolutionIndex;
            HHC.ChangeResolution(currentResolutionIndex);
            UpdateResolutionSprites();

            // Resolution toggle momentary behavior
            if (Resolution != null)
                Resolution.SetIsOnWithoutNotify(false);

            // Sliders and toggles (no notify)
            SetSliderValue(FOVSlider, settings.fov);
            SetSliderValue(DepthApertureSlider, settings.depthAperture);
            SetSliderValue(DepthFocusDistanceSlider, settings.depthFocusDistance);
            SetSliderValue(ExposureSlider, settings.exposureIndex);
            SetExposureOnCameraVisible(settings.showExposureOnCamera);

            SetFormat(settings.formatIndex);

            // Apply camera intrinsics. On a physical camera (usePhysicalProperties = true) the
            // sensor size, focal length and field of view are one value seen three ways, so order
            // matters: sensor and gate fit first, then FOV LAST so it is authoritative. focusDistance
            // is a depth-of-field concept in metres and must never be written to focalLength (mm of
            // lens) — doing so drove the FOV to ~100 deg regardless of settings.fov.
            if (HHC.captureCamera != null)
            {
                HHC.captureCamera.sensorSize = new Vector2(settings.sensorSizeX, settings.sensorSizeY);
                HHC.captureCamera.gateFit = Camera.GateFitMode.Vertical;
                HHC.captureCamera.fieldOfView = settings.fov;

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
        ExposureIndex = clampedExposure;

        if (HHC.MetaData.colorAdjustments != null)
        {
            HHC.MetaData.colorAdjustments.postExposure.value = ExposureStops[clampedExposure];
            HHC.MetaData.colorAdjustments.contrast.value = settings.contrast;
            HHC.MetaData.colorAdjustments.saturation.value = settings.saturation;
        }

        if (HHC.MetaData.depthOfField != null)
        {
            HHC.MetaData.depthOfField.aperture.value = Mathf.Clamp(settings.depthAperture, MinAperture, MaxAperture);
            // Focal length before focus: the minimum focus distance is derived from it.
            ChangeDoFFocalLength(settings.dofFocalLength);
            // Style first, then the on/off — ApplyDoFModeOnly deliberately does not touch active,
            // so depthIsActive stays the single owner of whether DoF is enabled.
            ApplyDoFModeOnly(settings.dofMode);
            HHC.MetaData.depthOfField.active = settings.depthIsActive;
            HHC.ApplyFocusDistance(settings.depthFocusDistance);
            ChangeDoFBladeCount(settings.dofBladeCount);
        }

        if (HHC.MetaData.bloom != null)
        {
            HHC.MetaData.bloom.intensity.value = settings.bloomIntensity;
            HHC.MetaData.bloom.threshold.value = settings.bloomThreshold;
        }

        if (HHC.MetaData.colorAdjustments != null)
            HHC.MetaData.colorAdjustments.hueShift.value = settings.hueShift;

        ChangeVignette(settings.vignette);
        ChangeChromaticAberration(settings.chromaticAberration);
        ChangeFilmGrain(settings.filmGrain);
        ChangeWhiteBalanceTemperature(settings.whiteBalanceTemperature);
        ChangeWhiteBalanceTint(settings.whiteBalanceTint);
        ChangeLensDistortion(settings.lensDistortion);

        HHC.autoFocusFollowSubject = settings.autoFocusFollowSubject;

        HHC.autoFollowPositionOffset = settings.autoFollowPositionOffset;
        HHC.autoFollowRotationOffset = settings.autoFollowRotationOffset;
        HHC.autoFollowPlayspace = settings.autoFollowPlayspace;
        HHC.autoFollowLookAtPlayer = settings.autoFollowLookAtPlayer;
        HHC.autoFollowLookAtHeightOffset = settings.autoFollowLookAtHeightOffset;
        HHC.capture360Enabled = settings.capture360;
        HHC.useAutoLeveling = settings.useAutoLeveling;
        HHC.useVRHandheldSmoothing = settings.useVRHandheldSmoothing;

#if Basis_VOLUMETRIC_SUPPORTED
        if (HHC.MetaData.VolumetricFogVolume != null)
        {
            HHC.MetaData.VolumetricFogVolume.density.value = settings.VolumetricFogVolumedensity;
            HHC.MetaData.VolumetricFogVolume.enableAPVContribution.value = settings.VolumetricFogenableAPVContribution;
            HHC.MetaData.VolumetricFogVolume.enableMainLightContribution.value = settings.VolumetricFogenableMainLightContribution;
        }
#endif
    }

    /// <summary>
    /// Re-seeds the prop's own sliders and indicators from the authoritative camera state, so a
    /// change made from the main-menu panel (or the click-to-focus handler, or auto-focus) shows
    /// up on the prop HUD too. Pure SetValueWithoutNotify, so it never re-drives the camera and is
    /// safe to call every frame — the panel does exactly that while it is open.
    /// </summary>
    public void SyncPropControlsFromState()
    {
        if (HHC == null) return;

        if (FOVSlider != null && HHC.captureCamera != null)
            FOVSlider.SetValueWithoutNotify(HHC.captureCamera.fieldOfView);

        if (ExposureSlider != null)
            ExposureSlider.SetValueWithoutNotify(ExposureIndex);

        if (HHC.MetaData.depthOfField != null)
        {
            if (DepthApertureSlider != null)
                DepthApertureSlider.SetValueWithoutNotify(HHC.MetaData.depthOfField.aperture.value);
            if (DepthFocusDistanceSlider != null)
                DepthFocusDistanceSlider.SetValueWithoutNotify(HHC.MetaData.depthOfField.focusDistance.value);
        }

        RefreshAllReadouts();
        RefreshAllToggleIndicators();
    }

    private void RefreshAllReadouts()
    {
        if (FOVOutput != null && FOVSlider != null) FOVOutput.text = FOVSlider.value.ToString();
        if (DepthApertureOutput != null && DepthApertureSlider != null) DepthApertureOutput.text = DepthApertureSlider.value.ToString();
        if (DOFFocusOutput != null && DepthFocusDistanceSlider != null) DOFFocusOutput.text = DepthFocusDistanceSlider.value.ToString();
    }
    /// <summary>Range URP clamps <c>DepthOfField.aperture</c> to; anything outside it is dead slider travel.</summary>
    public const float MinAperture = 1f;
    public const float MaxAperture = 32f;

    public void DepthChangeFocusDistance(float value)
    {
        if (HHC == null || HHC.MetaData.depthOfField == null) return;

        HHC.ApplyFocusDistance(value);
        if (DOFFocusOutput != null) DOFFocusOutput.text = HHC.MetaData.depthOfField.focusDistance.value.ToString();
    }

    public void ChangeAperture(float value)
    {
        if (HHC.MetaData.depthOfField != null)
        {
            HHC.MetaData.depthOfField.aperture.value = Mathf.Clamp(value, MinAperture, MaxAperture);
            if (DepthApertureOutput != null) DepthApertureOutput.text = HHC.MetaData.depthOfField.aperture.value.ToString();
        }
    }

    /// <summary>Depth of field mode: 0 = Off, 1 = Gaussian, 2 = Bokeh.</summary>
    public int DoFMode => HHC != null && HHC.MetaData.depthOfField != null
        ? (int)HHC.MetaData.depthOfField.mode.value
        : 0;

    /// <summary>
    /// Sets the depth of field mode from the panel dropdown, which offers Off as its first entry
    /// and so also owns the on/off state.
    /// </summary>
    public void SetDoFMode(int mode)
    {
        if (HHC.MetaData.depthOfField == null) return;
        int clamped = ApplyDoFModeOnly(mode);
        HHC.MetaData.depthOfField.active = clamped != 0;
        SetDepthMode(currentDepthMode);
    }

    /// <summary>
    /// Sets only the mode (the blur style), leaving the on/off state alone, and returns the
    /// clamped mode. Loading settings uses this: dofMode stores the style and depthIsActive
    /// stores on/off, so applying the style must not decide whether DoF is enabled — doing that
    /// let a saved style of Bokeh switch DoF back on over a saved off state.
    /// </summary>
    private int ApplyDoFModeOnly(int mode)
    {
        int clamped = Mathf.Clamp(mode, 0, 2);
        HHC.MetaData.depthOfField.mode.overrideState = true;
        HHC.MetaData.depthOfField.mode.value = (UnityEngine.Rendering.Universal.DepthOfFieldMode)clamped;
        HHC.ApplyFocusDistance(HHC.MetaData.depthOfField.focusDistance.value);
        return clamped;
    }

    public void ChangeDoFFocalLength(float value)
    {
        if (HHC.MetaData.depthOfField != null)
        {
            HHC.MetaData.depthOfField.focalLength.overrideState = true;
            HHC.MetaData.depthOfField.focalLength.value = value;
            HHC.ApplyFocusDistance(HHC.MetaData.depthOfField.focusDistance.value);
        }
    }

    public void ChangeDoFBladeCount(float value)
    {
        if (HHC.MetaData.depthOfField != null)
        {
            HHC.MetaData.depthOfField.bladeCount.overrideState = true;
            HHC.MetaData.depthOfField.bladeCount.value = Mathf.RoundToInt(value);
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
            // hueShift is authored with overrideState off in the profile (unlike contrast /
            // saturation / exposure), so the volume ignores the value until it is overridden.
            HHC.MetaData.colorAdjustments.hueShift.overrideState = true;
            HHC.MetaData.colorAdjustments.hueShift.value = value;
        }
    }

    public void ChangeVignette(float value)
    {
        if (HHC.MetaData.vignette != null)
        {
            // A VolumeParameter is blended into the final image only when overrideState is true;
            // without it the volume keeps its default and the slider looks dead.
            HHC.MetaData.vignette.intensity.overrideState = true;
            HHC.MetaData.vignette.intensity.value = value;
            HHC.MetaData.vignette.active = value > 0f;
        }
    }

    public void ChangeChromaticAberration(float value)
    {
        if (HHC.MetaData.chromaticAberration != null)
        {
            HHC.MetaData.chromaticAberration.intensity.overrideState = true;
            HHC.MetaData.chromaticAberration.intensity.value = value;
            HHC.MetaData.chromaticAberration.active = value > 0f;
        }
    }

    public void ChangeFilmGrain(float value)
    {
        if (HHC.MetaData.filmGrain != null)
        {
            // Film grain needs its lookup type overridden too, or no grain texture is chosen and
            // nothing renders however high the intensity is.
            HHC.MetaData.filmGrain.type.overrideState = true;
            if (HHC.MetaData.filmGrain.type.value == UnityEngine.Rendering.Universal.FilmGrainLookup.Custom)
            {
                HHC.MetaData.filmGrain.type.value = UnityEngine.Rendering.Universal.FilmGrainLookup.Thin1;
            }
            HHC.MetaData.filmGrain.intensity.overrideState = true;
            HHC.MetaData.filmGrain.intensity.value = value;
            HHC.MetaData.filmGrain.response.overrideState = true;
            HHC.MetaData.filmGrain.active = value > 0f;
        }
    }

    public void ChangeWhiteBalanceTemperature(float value)
    {
        if (HHC.MetaData.whiteBalance != null)
        {
            HHC.MetaData.whiteBalance.temperature.overrideState = true;
            HHC.MetaData.whiteBalance.temperature.value = value;
            HHC.MetaData.whiteBalance.active = value != 0f || HHC.MetaData.whiteBalance.tint.value != 0f;
        }
    }

    public void ChangeWhiteBalanceTint(float value)
    {
        if (HHC.MetaData.whiteBalance != null)
        {
            HHC.MetaData.whiteBalance.tint.overrideState = true;
            HHC.MetaData.whiteBalance.tint.value = value;
            HHC.MetaData.whiteBalance.active = value != 0f || HHC.MetaData.whiteBalance.temperature.value != 0f;
        }
    }

    public void ChangeLensDistortion(float value)
    {
        if (HHC.MetaData.lensDistortion != null)
        {
            HHC.MetaData.lensDistortion.intensity.overrideState = true;
            HHC.MetaData.lensDistortion.intensity.value = value;
            HHC.MetaData.lensDistortion.active = value != 0f;
        }
    }

    public void ChangeFOV(float value)
    {
        // Applies the field of view directly. (Follow distance is a plain dolly, not a lens zoom.)
        HHC?.SetFieldOfView(value);

        if (FOVOutput != null)
            FOVOutput.text = value.ToString();
    }

    public void ChangeFocusDistance(float value)
    {
        // focusDistance is a depth-of-field concept, not a lens focal length — forward it there
        // rather than writing captureCamera.focalLength, which would corrupt the FOV.
        DepthChangeFocusDistance(value);
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
        }
#endif
    }
}
