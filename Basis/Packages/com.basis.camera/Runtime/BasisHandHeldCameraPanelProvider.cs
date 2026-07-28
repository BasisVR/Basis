using System.Collections.Generic;
using Basis.BasisUI.Styling;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI.HandHeldCamera
{
    public class BasisHandHeldCameraPanelProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        public const string StaticTitle = "Camera Settings";

        private static readonly int[] VideoResolutionWidths = { 1280, 1920, 2560, 3840 };
        private static readonly int[] VideoResolutionHeights = { 720, 1080, 1440, 2160 };
        private static readonly int[] MsaaSampleCounts = { 1, 2, 4, 8 };

        // Index 0 follows the subject's depth automatically; index 1 uses the Focus Distance slider.
        private static readonly string[] FocusModeLabels = { "Follow Subject", "Manual" };

        // Ordered to match BasisCameraDetachedMarker (Off / Puck / Wireframe).
        private static readonly string[] DetachedMarkerLabels = { "Off", "Puck", "Wireframe" };

        /// <summary>Preview height used until the row has a laid-out width to derive one from.</summary>
        private const float PreviewFallbackHeight = 320f;

        private static BasisHandHeldCameraPanelProvider _instance;

        public override string Title => StaticTitle;
        public override string IconAddress => AddressableAssets.Sprites.CameraSettings;
        public override int Order => 9;
        public override bool Hidden => BasisHandHeldCameraRegistry.Count == 0;

        private BasisMenuPanel _panel;
        private RectTransform _scrollContent;
        private PanelDropdown _selector;
        private PanelElementDescriptor _emptyState;
        private PanelElementDescriptor _hiddenState;
        private PanelButton _bringBackButton;
        private bool? _lastHiddenState;
        private PanelElementDescriptor _previewGroup;
        private PanelElementDescriptor _lensGroup;
        private PanelElementDescriptor _dofGroup;
        private PanelElementDescriptor _colorGroup;
        private PanelElementDescriptor _effectsGroup;
        private PanelElementDescriptor _outputGroup;
        private PanelElementDescriptor _actionGroup;
        private PanelSectionToggle _previewSection;
        private PanelSectionToggle _lensSection;
        private PanelSectionToggle _dofSection;
        private PanelSectionToggle _colorSection;
        private PanelSectionToggle _effectsSection;
        private PanelSectionToggle _outputSection;
        private PanelSectionToggle _followSection;
        private PanelSectionToggle _actionSection;
        private PanelElementDescriptor _layersGroup;
        private PanelSectionToggle _layersSection;
        private readonly Dictionary<int, PanelToggle> _layerToggles = new Dictionary<int, PanelToggle>();
        private PanelElementDescriptor _gizmoGroup;
        private PanelSectionToggle _gizmoSection;
        private readonly Dictionary<BasisCameraGizmoLayers, PanelToggle> _gizmoToggles =
            new Dictionary<BasisCameraGizmoLayers, PanelToggle>();
        private PanelElementDescriptor _performanceGroup;
        private PanelSectionToggle _performanceSection;
        private PanelToggle _limitRenderRateToggle;
        private PanelSlider _renderRateSlider;
        private PanelButton _resetPageButton;
        private PanelButton _timerButton;
        private int _lastCountdownShown = -1;
        private const string TimerIdleLabel = "Timer";
        private RectTransform _topActions;
        private RawImage _previewImage;

        private PanelSlider _fovSlider;
        private PanelSlider _exposureSlider;
        private PanelToggle _exposureOnCameraToggle;
        private PanelSlider _bloomIntensitySlider;
        private PanelSlider _bloomThresholdSlider;
        private PanelSlider _contrastSlider;
        private PanelSlider _saturationSlider;
        private PanelDropdown _dofModeDropdown;
        private PanelSlider _apertureSlider;
        private PanelSlider _focusSlider;
        private PanelSlider _dofFocalLengthSlider;
        private PanelSlider _dofBladeCountSlider;
        private PanelSlider _hueSlider;
        private PanelSlider _vignetteSlider;
        private PanelSlider _chromaticSlider;
        private PanelSlider _filmGrainSlider;
        private PanelSlider _whiteBalanceTempSlider;
        private PanelSlider _whiteBalanceTintSlider;
        private PanelSlider _lensDistortionSlider;
        private PanelDropdown _msaaDropdown;
#if Basis_VOLUMETRIC_SUPPORTED
        private PanelSlider _fogSlider;
#endif

        private PanelElementDescriptor _followGroup;
        private PanelToggle _autoFollowToggle;
        private PanelDropdown _followMarkerDropdown;
        private PanelDropdown _followTargetDropdown;
        private readonly List<ushort> _followTargetIds = new List<ushort>();
        private PanelDropdown _focusModeDropdown;
        private PanelToggle _followPlayspaceToggle;
        // private PanelToggle _followLookAtToggle;  // "Look At Me" removed — follow always aims at the player.
        private PanelSlider _followLookAtHeightSlider;
        private PanelSlider _followSideSlider;
        private PanelSlider _followLateralSlider;
        private PanelSlider _followHeightSlider;
        private PanelSlider _followDistanceSlider;
        private PanelSlider _followYawSlider;
        private PanelSlider _followPitchSlider;

        private PanelDropdown _resolutionDropdown;
        private PanelDropdown _formatDropdown;
        private PanelToggle _recordToggle;
        private PanelToggle _autoLevelToggle;
        private PanelToggle _vrStabToggle;
        private PanelToggle _capture360Toggle;
        private PanelToggle _previewScreenToggle;
        private PanelToggle _audioListenerToggle;
        private PanelToggle _selfieToggle;
        private PanelToggle _hideCameraToggle;
        private PanelToggle _closeHidesToggle;
        private PanelToggle _videoOutputToggle;
        private PanelDropdown _transportDropdown;
        private List<BasisVideoTransport> _transports = new List<BasisVideoTransport>();
        private PanelDropdown _videoResolutionDropdown;
        private PanelSlider _videoFrameRateSlider;
        private PanelSlider _webQualitySlider;
        private PanelTextField _webPortField;
        private PanelButton _openStreamButton;
        private PanelTextField _videoSenderNameField;

        private BasisHandHeldCamera _activeCamera;
        private readonly List<BasisHandHeldCamera> _entries = new List<BasisHandHeldCamera>();
        private bool _panelTickSubscribed;
        private bool? _lastVideoOutputActive;
        private bool? _lastWebStreamActive;
        private string _lastWebStreamDescription;
        private bool? _lastRecordingView;
        private bool? _lastPreviewScreenVisible;
        private bool? _lastCameraHidden;
        private bool? _lastAudioListener;
        private float _lastFov = float.NaN;
        private float _lastExposure = float.NaN;
        private float _lastAperture = float.NaN;
        private float _lastFocus = float.NaN;
        private bool? _lastSelfie;
        private bool? _lastAutoLevel;
        private bool? _lastVrStab;
        private bool? _lastCloseHides;
        private bool? _lastAutoFollow;
        private bool? _lastExposureOnCamera;

        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            _instance = new BasisHandHeldCameraPanelProvider();
            BasisMenuBase<BasisMainMenu>.AddProvider(_instance);
            // Detach first: statics survive a domain reload, so with reload disabled in the editor
            // this runs again each play session and would stack up duplicate handlers.
            BasisHandHeldCameraRegistry.OnChanged -= RefreshMainMenu;
            BasisHandHeldCameraRegistry.OnChanged += RefreshMainMenu;
        }

        private static void RefreshMainMenu()
        {
            if (BasisMenuBase<BasisMainMenu>.Instance != null)
            {
                BasisMenuBase<BasisMainMenu>.Instance.BindProvidersToButtons();
            }
            if (BasisMainMenu.ActiveMenuTitle != StaticTitle || _instance == null) return;

            // The last camera just closed. Its menu button is already hidden, so a panel left
            // open would strand the user on a page with nothing to drive and no way back to it.
            if (BasisHandHeldCameraRegistry.Count == 0)
            {
                BasisMainMenu.CloseActivePanel();
                return;
            }

            _instance.RebuildSelector();
        }

        public override void RunAction()
        {
            if (BasisMainMenu.ActiveMenuTitle == Title)
            {
                BasisMainMenu.CloseActivePanel();
                return;
            }

            BasisMenuPanel panel = BasisMainMenu.CreateActiveMenu(
                BasisMenuPanel.PanelData.Standard(Title),
                BasisMenuPanel.PanelStyles.Page);
            BoundButton?.BindActiveStateToAddressablesInstance(panel);
            _panel = panel;

            panel.OnInstanceReleased += OnPanelClosed;

            RectTransform container = panel.Descriptor.ContentParent;
            PanelElementDescriptor scroll = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.ScrollViewVertical, container);
            _scrollContent = scroll.ContentParent;

            // The shared scroll-view prefab ships a bare, zero-anchored viewport
            // with no mask, so content taller than the panel draws straight past
            // its bounds. Bound the viewport to the scroll rect and mask it.
            if (scroll.TryGetComponent(out ScrollRect scrollRect) && scrollRect.viewport != null)
            {
                RectTransform viewport = scrollRect.viewport;
                viewport.anchorMin = Vector2.zero;
                viewport.anchorMax = Vector2.one;
                viewport.offsetMin = Vector2.zero;
                viewport.offsetMax = new Vector2(-25f, 0f);
                if (!viewport.TryGetComponent(out RectMask2D _))
                {
                    viewport.gameObject.AddComponent<RectMask2D>();
                }
            }

            BuildTopActions(_scrollContent);

            _selector = PanelDropdown.CreateNewEntry(_scrollContent);
            _selector.Descriptor.SetTitle("Camera");
            _selector.OnValueChanged = _ => OnSelectionChanged();

            _emptyState = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, _scrollContent);
            _emptyState.SetTitle("No Cameras Open");
            _emptyState.SetDescription("Spawn a handheld camera to adjust it here.");

            BuildHiddenState(_scrollContent);

            BuildPreviewGroup(_scrollContent);
            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_previewSection, _previewGroup, true, OnSectionExpanded);

            BuildLensGroup(_scrollContent);
            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_lensSection, _lensGroup, false, OnSectionExpanded);

            BuildDofGroup(_scrollContent);
            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_dofSection, _dofGroup, false, OnSectionExpanded);

            BuildColorGroup(_scrollContent);
            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_colorSection, _colorGroup, false, OnSectionExpanded);

            BuildEffectsGroup(_scrollContent);
            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_effectsSection, _effectsGroup, false, OnSectionExpanded);

            BuildOutputGroup(_scrollContent);
            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_outputSection, _outputGroup, false, OnSectionExpanded);

            BuildFollowGroup(_scrollContent);
            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_followSection, _followGroup, false, OnSectionExpanded);

            BuildActionsGroup(_scrollContent);
            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_actionSection, _actionGroup, true, OnSectionExpanded);

            BuildLayersGroup(_scrollContent);
            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_layersSection, _layersGroup, false, OnSectionExpanded);

            BuildPerformanceGroup(_scrollContent);
            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_performanceSection, _performanceGroup, false, OnSectionExpanded);

            BuildGizmoGroup(_scrollContent);
            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_gizmoSection, _gizmoGroup, false, OnSectionExpanded);

            BuildResetButton(_scrollContent);

            MakeSlidersLive(_scrollContent);
            AssignSliderResetDefaults();

            RebuildSelector();

            SetPanelTickSubscription(true);
        }

        /// <summary>
        /// Makes this page's sliders apply while being dragged instead of on release.
        /// <para>
        /// PanelSlider writes through only on confirm — deliberately, since most settings are
        /// expensive to apply — but this page shows its result live in the preview, so waiting
        /// for release makes framing and grading feel disconnected from the handle.
        /// </para>
        /// <para>
        /// Scoped to this panel by hooking the underlying Slider directly, so PanelSlider keeps
        /// its confirm-only behaviour everywhere else. Sweeping the built page rather than
        /// naming each slider means sliders added here later are live without extra wiring.
        /// Pushing values in stays safe: SetValueWithoutNotify routes through the Slider's own
        /// SetValueWithoutNotify, so it cannot feed back into this listener.
        /// </para>
        /// </summary>
        private static void MakeSlidersLive(RectTransform page)
        {
            if (page == null) return;

            PanelSlider[] sliders = page.GetComponentsInChildren<PanelSlider>(true);
            for (int Index = 0; Index < sliders.Length; Index++)
            {
                PanelSlider slider = sliders[Index];
                if (slider == null || slider.SliderComponent == null) continue;
                // Read the callback at invoke time, not now — the Build methods assign
                // OnValueChanged after the slider is created.
                slider.SliderComponent.onValueChanged.AddListener(value => slider.OnValueChanged?.Invoke(value));
            }
        }

        /// <summary>
        /// Gives every camera slider the value a reset returns it to, so right-click (desktop) and
        /// press-and-hold (VR) can reset them. These sliders are callback-driven, not bound to the
        /// settings system, so PanelSlider can't derive the default itself. The grading sliders
        /// (contrast, saturation, hue, white balance, and the added effects) show the live
        /// post-process value whose neutral is 0; the rest use the CameraSettings / follow defaults.
        /// </summary>
        private void AssignSliderResetDefaults()
        {
            BasisHandHeldCameraUI.CameraSettings defaults = new BasisHandHeldCameraUI.CameraSettings();

            _fovSlider?.SetResetDefault(defaults.fov);
            _exposureSlider?.SetResetDefault(defaults.exposureIndex);
            _bloomIntensitySlider?.SetResetDefault(defaults.bloomIntensity);
            _bloomThresholdSlider?.SetResetDefault(defaults.bloomThreshold);
            _apertureSlider?.SetResetDefault(defaults.depthAperture);
            _focusSlider?.SetResetDefault(defaults.depthFocusDistance);
            _dofFocalLengthSlider?.SetResetDefault(defaults.dofFocalLength);
            _dofBladeCountSlider?.SetResetDefault(defaults.dofBladeCount);

            // Grading effects — neutral is 0 (no grade), matching a fresh camera.
            _contrastSlider?.SetResetDefault(0f);
            _saturationSlider?.SetResetDefault(0f);
            _hueSlider?.SetResetDefault(0f);
            _whiteBalanceTempSlider?.SetResetDefault(0f);
            _whiteBalanceTintSlider?.SetResetDefault(0f);
            _vignetteSlider?.SetResetDefault(0f);
            _chromaticSlider?.SetResetDefault(0f);
            _filmGrainSlider?.SetResetDefault(0f);
            _lensDistortionSlider?.SetResetDefault(0f);
#if Basis_VOLUMETRIC_SUPPORTED
            _fogSlider?.SetResetDefault(defaults.VolumetricFogVolumedensity);
#endif

            _videoFrameRateSlider?.SetResetDefault(30f);
            _webQualitySlider?.SetResetDefault(70f);

            // Follow — from the interactable's field initializers.
            _followSideSlider?.SetResetDefault(defaults.autoFollowPositionOffset.x);
            _followHeightSlider?.SetResetDefault(defaults.autoFollowPositionOffset.y);
            _followDistanceSlider?.SetResetDefault(defaults.autoFollowPositionOffset.z);
            _followYawSlider?.SetResetDefault(defaults.autoFollowRotationOffset.y);
            _followPitchSlider?.SetResetDefault(defaults.autoFollowRotationOffset.x);
            _followLookAtHeightSlider?.SetResetDefault(defaults.autoFollowLookAtHeightOffset);
            _followLateralSlider?.SetResetDefault(0.5f);
        }

        /// <summary>
        /// Page-level reset at the very bottom, mirroring the settings pages: confirm first,
        /// then reset and reopen the panel so every control rebuilds from the new values.
        /// Reuses the settings pages' localization keys so the wording stays in step.
        /// </summary>
        private void BuildResetButton(RectTransform parent)
        {
            _resetPageButton = PanelButton.CreateNew(parent);
            _resetPageButton.Descriptor.SetTitle(BasisLocalization.Get("ui.resetPage.title", StaticTitle));
            _resetPageButton.OnClicked += () =>
            {
                // Hold the camera across the dialogue: closing the panel clears _activeCamera.
                BasisHandHeldCamera camera = _activeCamera;
                if (camera == null) return;

                BasisMainMenu.Instance.OpenDialogue(
                    BasisLocalization.Get("ui.resetPage.title", StaticTitle),
                    BasisLocalization.Get("ui.resetPage.confirm", StaticTitle),
                    BasisLocalization.Get("ui.reset"),
                    BasisLocalization.Get("ui.cancel"),
                    confirmed =>
                    {
                        if (!confirmed || camera == null) return;

                        camera.HandHeld.ResetSettings();
                        BasisSettingsDefaults.LimitHandHeldCameraRate.ResetToDefault();
                        BasisSettingsDefaults.HandHeldCameraRenderHz.ResetToDefault();
                        BasisMainMenu.Close();
                        BasisMainMenu.OpenWithProvider(StaticTitle);
                    });
            };
        }

        /// <summary>
        /// On desktop this panel and the prop's own HUD compete for the same flat screen, so
        /// the prop HUD steps aside while the panel drives the camera. In VR they sit in
        /// different places and the HUD stays put.
        /// </summary>
        private static void ApplyOnPropUIVisibility(bool panelOpen)
        {
            // Restore unconditionally: the user can leave desktop for VR while the panel is
            // open, and a HUD hidden under the old mode must still come back.
            bool hide = panelOpen && BasisDeviceManagement.IsUserInDesktop();
            IReadOnlyList<BasisHandHeldCamera> cameras = BasisHandHeldCameraRegistry.Cameras;
            for (int Index = 0; Index < cameras.Count; Index++)
            {
                cameras[Index]?.SetOnPropUIHidden(hide);
            }
        }

        private void OnPanelClosed()
        {
            ApplyOnPropUIVisibility(false);
            SetPanelTickSubscription(false);
            _panel = null;
            _scrollContent = null;
            _selector = null;
            _emptyState = null;
            _hiddenState = null;
            _bringBackButton = null;
            _lastHiddenState = null;
            _previewGroup = null;
            _lensGroup = null;
            _dofGroup = null;
            _colorGroup = null;
            _effectsGroup = null;
            _outputGroup = null;
            _actionGroup = null;
            _previewImage = null;
            _fovSlider = null;
            _exposureSlider = null;
            _exposureOnCameraToggle = null;
            _bloomIntensitySlider = null;
            _bloomThresholdSlider = null;
            _contrastSlider = null;
            _saturationSlider = null;
            _apertureSlider = null;
            _focusSlider = null;
            _dofModeDropdown = null;
            _dofFocalLengthSlider = null;
            _dofBladeCountSlider = null;
            _hueSlider = null;
            _vignetteSlider = null;
            _chromaticSlider = null;
            _filmGrainSlider = null;
            _whiteBalanceTempSlider = null;
            _whiteBalanceTintSlider = null;
            _lensDistortionSlider = null;
            _focusModeDropdown = null;
            _followTargetDropdown = null;
            _followTargetIds.Clear();
            _msaaDropdown = null;
#if Basis_VOLUMETRIC_SUPPORTED
            _fogSlider = null;
#endif
            _followGroup = null;
            _resetPageButton = null;
            _topActions = null;
            _timerButton = null;
            _lastCountdownShown = -1;
            _previewSection = null;
            _lensSection = null;
            _dofSection = null;
            _colorSection = null;
            _effectsSection = null;
            _outputSection = null;
            _followSection = null;
            _actionSection = null;
            _layersGroup = null;
            _layersSection = null;
            _layerToggles.Clear();
            _gizmoGroup = null;
            _gizmoSection = null;
            _gizmoToggles.Clear();
            _performanceGroup = null;
            _performanceSection = null;
            _limitRenderRateToggle = null;
            _renderRateSlider = null;
            _autoFollowToggle = null;
            _followMarkerDropdown = null;
            _followPlayspaceToggle = null;
            _followLookAtHeightSlider = null;
            _followSideSlider = null;
            _followLateralSlider = null;
            _followHeightSlider = null;
            _followDistanceSlider = null;
            _followYawSlider = null;
            _followPitchSlider = null;
            _lastAutoFollow = null;
            _resolutionDropdown = null;
            _formatDropdown = null;
            _recordToggle = null;
            _autoLevelToggle = null;
            _vrStabToggle = null;
            _lastAutoLevel = null;
            _lastVrStab = null;
            _lastFov = float.NaN;
            _lastExposure = float.NaN;
            _lastAperture = float.NaN;
            _lastFocus = float.NaN;
            _capture360Toggle = null;
            _previewScreenToggle = null;
            _audioListenerToggle = null;
            _selfieToggle = null;
            _hideCameraToggle = null;
            _closeHidesToggle = null;
            _videoOutputToggle = null;
            _transportDropdown = null;
            _videoResolutionDropdown = null;
            _videoFrameRateSlider = null;
            _webQualitySlider = null;
            _webPortField = null;
            _openStreamButton = null;
            _videoSenderNameField = null;
            _activeCamera = null;
            _lastVideoOutputActive = null;
            _lastWebStreamActive = null;
            _lastWebStreamDescription = null;
            _lastRecordingView = null;
            _lastPreviewScreenVisible = null;
            _lastCameraHidden = null;
            _lastAudioListener = null;
            _lastSelfie = null;
            _lastCloseHides = null;
            _lastExposureOnCamera = null;
            _entries.Clear();
        }

        public override void OnReleaseEvent() => OnPanelClosed();

        private void BuildPreviewGroup(RectTransform parent)
        {
            _previewSection = PanelSectionToggle.CreateNewEntry(parent);
            _previewGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup( _previewSection, parent, "Preview", false);

            // A live RenderTexture can only be drawn by a RawImage, and the element's base
            // image is a UnityEngine.UI.Image, which draws sprites only. Graphic is
            // DisallowMultipleComponent, so the Image has to come off before the RawImage can
            // take its place on the card — and UiStyleImage goes first, since it requires the
            // Image and Unity refuses the removal while it's attached. DestroyImmediate
            // rather than Destroy: a deferred destroy would still hold the slot when
            // AddComponent runs later in this frame.
            RectTransform card = (RectTransform)_previewGroup.transform;
            if (card.TryGetComponent(out UiStyleImage styleImage)) Object.DestroyImmediate(styleImage);

            // Take the card's material with us: it's the overlay-variant UI material the rest
            // of the menu draws with, so the preview keeps sorting on top the way the card
            // did. Read it before the destroy, and don't take the colour — the card is tinted
            // and the feed wants a plain white pass-through.
            Material cardMaterial = null;
            if (card.TryGetComponent(out Graphic baseGraphic))
            {
                cardMaterial = baseGraphic.material;
                Object.DestroyImmediate(baseGraphic);
            }

            _previewImage = card.gameObject.AddComponent<RawImage>();
            _previewImage.raycastTarget = false;
            if (cardMaterial != null) _previewImage.material = cardMaterial;

            // The card shrink-wraps its rows; the header is hidden and Content is empty for
            // this group, so its fitter would collapse the preview flat. Own the height and
            // let ApplyPreviewAspect drive it from the feed.
            if (card.TryGetComponent(out ContentSizeFitter fitter)) fitter.enabled = false;
            card.sizeDelta = new Vector2(card.sizeDelta.x, PreviewFallbackHeight);
        }

        private void BuildLensGroup(RectTransform parent)
        {
            _lensSection = PanelSectionToggle.CreateNewEntry(parent);
            _lensGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _lensSection, parent, "Lens", false);
            RectTransform content = _lensGroup.ContentParent;

            _fovSlider = PanelSlider.CreateNew(content);
            _fovSlider.SetSliderSettings(PanelSlider.SliderSettings.Degrees("Field Of View", 20f, 120f, false, 1));
            _fovSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeFOV(v);

            _msaaDropdown = PanelDropdown.CreateNewEntry(content);
            _msaaDropdown.Descriptor.SetTitle("MSAA");
            _msaaDropdown.Descriptor.SetDescription("Multisample anti-aliasing on the captured image. Higher is smoother but costs more.");
            _msaaDropdown.AssignEntries(BuildMsaaLabels());
            _msaaDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _msaaDropdown == null) return;
                int index = _msaaDropdown.Index;
                if (index >= 0 && index < MsaaSampleCounts.Length)
                {
                    _activeCamera.SetMsaaSamples(MsaaSampleCounts[index]);
                }
            };
        }

        private void BuildDofGroup(RectTransform parent)
        {
            _dofSection = PanelSectionToggle.CreateNewEntry(parent);
            _dofGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _dofSection, parent, "Depth Of Field", false);
            RectTransform content = _dofGroup.ContentParent;

            _dofModeDropdown = PanelDropdown.CreateNewEntry(content);
            _dofModeDropdown.Descriptor.SetTitle("Mode");
            _dofModeDropdown.Descriptor.SetDescription("Off, Gaussian (cheap far blur) or Bokeh (physical, shaped highlights).");
            _dofModeDropdown.AssignEntries(new List<string> { "Off", "Gaussian", "Bokeh" });
            _dofModeDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _dofModeDropdown == null) return;
                _activeCamera.HandHeld.SetDoFMode(_dofModeDropdown.Index);
                RefreshDoFModeVisibility();
            };

            _focusModeDropdown = PanelDropdown.CreateNewEntry(content);
            _focusModeDropdown.Descriptor.SetTitle("Focus");
            _focusModeDropdown.Descriptor.SetDescription("Follow Subject keeps depth of field on whoever the camera follows (turns it on), and needs Auto Follow on or a Follow Target picked; Manual sets the focus distance by hand.");
            _focusModeDropdown.AssignEntries(new List<string>(FocusModeLabels));
            _focusModeDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _focusModeDropdown == null) return;
                SetFocusFollowsSubject(_focusModeDropdown.Index == 0);
            };

            _focusSlider = PanelSlider.CreateNew(content);
            _focusSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                "Focus Distance", 0.1f, 100f, false, 1, ValueDisplayMode.Meters));
            _focusSlider.OnValueChanged = v => _activeCamera?.HandHeld.DepthChangeFocusDistance(v);

            _apertureSlider = PanelSlider.CreateNew(content);
            _apertureSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                "Aperture", BasisHandHeldCameraUI.MinAperture, BasisHandHeldCameraUI.MaxAperture, false, 2, ValueDisplayMode.Raw));
            _apertureSlider.Descriptor.SetDescription("Lower is a shallower focus with more blur (Bokeh).");
            _apertureSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeAperture(v);

            _dofFocalLengthSlider = PanelSlider.CreateNew(content);
            _dofFocalLengthSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                "Focal Length", 1f, 300f, false, 0, ValueDisplayMode.Raw));
            _dofFocalLengthSlider.Descriptor.SetDescription("Longer compresses the scene and deepens the blur (Bokeh).");
            _dofFocalLengthSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeDoFFocalLength(v);

            _dofBladeCountSlider = PanelSlider.CreateNew(content);
            _dofBladeCountSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                "Bokeh Blades", 3f, 9f, true, 0, ValueDisplayMode.Raw));
            _dofBladeCountSlider.Descriptor.SetDescription("Number of aperture blades — the shape of out-of-focus highlights (Bokeh).");
            _dofBladeCountSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeDoFBladeCount(v);
        }

        private void BuildColorGroup(RectTransform parent)
        {
            _colorSection = PanelSectionToggle.CreateNewEntry(parent);
            _colorGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _colorSection, parent, "Exposure & Colour", false);
            RectTransform content = _colorGroup.ContentParent;

            _exposureSlider = PanelSlider.CreateNew(content);
            _exposureSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                "Exposure", 0f, BasisHandHeldCameraUI.ExposureStopCount - 1, true, 0, ValueDisplayMode.Raw));
            _exposureSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeExposureCompensation(v);

            _exposureOnCameraToggle = PanelToggle.CreateNewEntry(content);
            _exposureOnCameraToggle.Descriptor.SetTitle("Exposure On Camera");
            _exposureOnCameraToggle.Descriptor.SetDescription("Also show the exposure slider on the camera's own interface.");
            _exposureOnCameraToggle.OnValueChanged = v =>
            {
                _activeCamera?.HandHeld.SetExposureOnCameraVisible(v);
                // Keep the cache tracking what the widget shows, or switching to a camera
                // that already holds this value would skip the push and strand the knob.
                _lastExposureOnCamera = v;
            };

            _contrastSlider = PanelSlider.CreateNew(content);
            _contrastSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                "Contrast", -100f, 100f, false, 1, ValueDisplayMode.Raw));
            _contrastSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeContrast(v);

            _saturationSlider = PanelSlider.CreateNew(content);
            _saturationSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                "Saturation", -100f, 100f, false, 1, ValueDisplayMode.Raw));
            _saturationSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeSaturation(v);

            _hueSlider = PanelSlider.CreateNew(content);
            _hueSlider.SetSliderSettings(PanelSlider.SliderSettings.Degrees("Hue Shift", -180f, 180f, false, 0));
            _hueSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeHueShift(v);

            _whiteBalanceTempSlider = PanelSlider.CreateNew(content);
            _whiteBalanceTempSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                "White Balance Temp", -100f, 100f, false, 0, ValueDisplayMode.Raw));
            _whiteBalanceTempSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeWhiteBalanceTemperature(v);

            _whiteBalanceTintSlider = PanelSlider.CreateNew(content);
            _whiteBalanceTintSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                "White Balance Tint", -100f, 100f, false, 0, ValueDisplayMode.Raw));
            _whiteBalanceTintSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeWhiteBalanceTint(v);
        }

        private void BuildEffectsGroup(RectTransform parent)
        {
            _effectsSection = PanelSectionToggle.CreateNewEntry(parent);
            _effectsGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _effectsSection, parent, "Effects", false);
            RectTransform content = _effectsGroup.ContentParent;

            _bloomIntensitySlider = PanelSlider.CreateNew(content);
            _bloomIntensitySlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                "Bloom Intensity", 0f, 5f, false, 2, ValueDisplayMode.Raw));
            _bloomIntensitySlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeBloomIntensity(v);

            _bloomThresholdSlider = PanelSlider.CreateNew(content);
            _bloomThresholdSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                "Bloom Threshold", 0.1f, 2f, false, 2, ValueDisplayMode.Raw));
            _bloomThresholdSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeBloomThreshold(v);

            _vignetteSlider = PanelSlider.CreateNew(content);
            _vignetteSlider.SetSliderSettings(PanelSlider.SliderSettings.Percentage("Vignette"));
            _vignetteSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeVignette(v / 100f);

            _chromaticSlider = PanelSlider.CreateNew(content);
            _chromaticSlider.SetSliderSettings(PanelSlider.SliderSettings.Percentage("Chromatic Aberration"));
            _chromaticSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeChromaticAberration(v / 100f);

            _filmGrainSlider = PanelSlider.CreateNew(content);
            _filmGrainSlider.SetSliderSettings(PanelSlider.SliderSettings.Percentage("Film Grain"));
            _filmGrainSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeFilmGrain(v / 100f);

            _lensDistortionSlider = PanelSlider.CreateNew(content);
            _lensDistortionSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                "Lens Distortion", -100f, 100f, false, 0, ValueDisplayMode.Raw));
            _lensDistortionSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeLensDistortion(v / 100f);

#if Basis_VOLUMETRIC_SUPPORTED
            _fogSlider = PanelSlider.CreateNew(content);
            _fogSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                "Volumetric Fog", 0f, 1f, false, 2, ValueDisplayMode.Raw));
            _fogSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeVolumetricDensity(v);
#endif
        }

        private static List<string> BuildMsaaLabels()
        {
            var labels = new List<string>(MsaaSampleCounts.Length);
            for (int Index = 0; Index < MsaaSampleCounts.Length; Index++)
            {
                labels.Add(MsaaSampleCounts[Index] <= 1 ? "Off" : $"{MsaaSampleCounts[Index]}x");
            }
            return labels;
        }

        private void BuildOutputGroup(RectTransform parent)
        {
            _outputSection = PanelSectionToggle.CreateNewEntry(parent);
            _outputGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _outputSection, parent, "Output", false);
            RectTransform content = _outputGroup.ContentParent;

            _recordToggle = PanelToggle.CreateNewEntry(content);
            _recordToggle.Descriptor.SetTitle("Direct To Screen");
            _recordToggle.Descriptor.SetDescription("Render this camera to the main screen in place of your own view. Nothing is written to disk.");
            _recordToggle.OnValueChanged = v =>
            {
                if (_activeCamera == null) return;
                if (_activeCamera.enableRecordingView != v) _activeCamera.OnOverrideDesktopOutputButtonPress();
            };

            _hideCameraToggle = PanelToggle.CreateNewEntry(content);
            _hideCameraToggle.Descriptor.SetTitle("Hide Camera");
            _hideCameraToggle.Descriptor.SetDescription("Hide the camera itself while it keeps running — capture, streaming and follow all continue.");
            _hideCameraToggle.OnValueChanged = v =>
            {
                _activeCamera?.SetCameraHidden(v);
                _lastCameraHidden = v;
            };

            _closeHidesToggle = PanelToggle.CreateNewEntry(content);
            _closeHidesToggle.Descriptor.SetTitle("Close Hides Instead");
            _closeHidesToggle.Descriptor.SetDescription("Closing the camera hides it rather than destroying it, so bringing it back resumes the same session.");
            _closeHidesToggle.OnValueChanged = v =>
            {
                if (_activeCamera != null) _activeCamera.HandHeld.CloseHidesCamera = v;
                _lastCloseHides = v;
            };

            _previewScreenToggle = PanelToggle.CreateNewEntry(content);
            _previewScreenToggle.Descriptor.SetTitle("Preview Screen");
            _previewScreenToggle.Descriptor.SetDescription("Spawn a grabbable, resizable screen showing this camera's feed.");
            _previewScreenToggle.OnValueChanged = v => _activeCamera?.SetPreviewScreenVisible(v);

            _audioListenerToggle = PanelToggle.CreateNewEntry(content);
            _audioListenerToggle.Descriptor.SetTitle("Hear From Camera");
            _audioListenerToggle.Descriptor.SetDescription("Move the audio listener to this camera, so a recording or stream is heard from the camera's viewpoint instead of yours. Only one camera at a time.");
            _audioListenerToggle.OnValueChanged = v =>
            {
                _activeCamera?.SetAudioListener(v);
                // One listener exists, so enabling here disables it on every other camera —
                // drop the caches so the panel re-reads them for the current camera next tick.
                _lastAudioListener = null;
            };

            // No platform gate here: the web stream is pure sockets, so there is always at
            // least one transport to choose from, even where no shared-texture backend exists.
            _transports = BasisHandHeldCamera.AvailableVideoTransports();
            List<string> transportLabels = new List<string>();
            for (int Index = 0; Index < _transports.Count; Index++)
            {
                transportLabels.Add(BasisHandHeldCamera.GetVideoTransportName(_transports[Index]));
            }

            _transportDropdown = PanelDropdown.CreateNewEntry(content);
            _transportDropdown.Descriptor.SetTitle("Transport");
            _transportDropdown.AssignEntries(transportLabels);
            _transportDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _transportDropdown == null) return;
                int index = _transportDropdown.Index;
                if (index < 0 || index >= _transports.Count) return;
                // Carries the running state across, so switching transport mid-stream just
                // moves it rather than silently stopping the output.
                _activeCamera.SetVideoTransport(_transports[index]);
                _lastVideoOutputActive = null;
                _lastWebStreamActive = null;
                RefreshVideoOutputState();
            };

            _videoOutputToggle = PanelToggle.CreateNewEntry(content);
            _videoOutputToggle.Descriptor.SetTitle("Live Output");
            _videoOutputToggle.OnValueChanged = v =>
            {
                if (_activeCamera == null) return;
                if (v) _activeCamera.StartLiveOutput();
                else _activeCamera.StopLiveOutput();
                // The click already moved the widget, so the cached value no longer describes
                // it — clear it or a failed start would leave the toggle stuck on.
                _lastVideoOutputActive = null;
                _lastWebStreamActive = null;
                RefreshVideoOutputState();
            };

            // Resolution and frame rate apply to every transport, so they sit above the
            // platform gate — otherwise a machine with no shared-texture backend would get the
            // web stream with no controls at all.
            _videoResolutionDropdown = PanelDropdown.CreateNewEntry(content);
            _videoResolutionDropdown.Descriptor.SetTitle("Stream Resolution");
            List<string> resolutionLabels = new List<string>();
            for (int Index = 0; Index < VideoResolutionWidths.Length; Index++)
            {
                resolutionLabels.Add($"{VideoResolutionWidths[Index]} x {VideoResolutionHeights[Index]}");
            }
            _videoResolutionDropdown.AssignEntries(resolutionLabels);
            _videoResolutionDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _videoResolutionDropdown == null) return;
                int index = _videoResolutionDropdown.Index;
                if (index < 0 || index >= VideoResolutionWidths.Length) return;
                _activeCamera.SetVideoOutputResolution(VideoResolutionWidths[index], VideoResolutionHeights[index]);
            };

            _videoFrameRateSlider = PanelSlider.CreateNew(content);
            _videoFrameRateSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                "Stream Frame Rate", 15f, 120f, true, 0, ValueDisplayMode.Hz));
            _videoFrameRateSlider.OnValueChanged = v => _activeCamera?.SetVideoOutputFrameRate(v);

            // Web-only settings. Shown or hidden by RefreshTransportSelection so the group
            // only ever offers what the chosen transport actually uses.
            _webQualitySlider = PanelSlider.CreateNew(content);
            _webQualitySlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                "Stream Quality", 10f, 95f, true, 0, ValueDisplayMode.Raw));
            _webQualitySlider.OnValueChanged = v => _activeCamera?.SetWebStreamQuality((int)v);

            _webPortField = PanelTextField.CreateNewEntry(content);
            _webPortField.Descriptor.SetTitle("Stream Port");
            _webPortField.OnValueChanged = v =>
            {
                if (_activeCamera == null || !int.TryParse(v, out int port)) return;
                _activeCamera.SetWebStreamPort(port);
                RefreshVideoOutputState();
            };

            _openStreamButton = PanelButton.CreateNew(content);
            _openStreamButton.Descriptor.SetTitle("Open In Browser");
            _openStreamButton.OnClicked += () => _activeCamera?.OpenWebStreamInBrowser();

            if (!BasisHandHeldCamera.IsVideoOutputSupported) return;

            _videoSenderNameField = PanelTextField.CreateNewEntry(content);
            _videoSenderNameField.Descriptor.SetTitle("Sender Name");
            _videoSenderNameField.OnValueChanged = v =>
            {
                if (_activeCamera == null || string.IsNullOrWhiteSpace(v)) return;
                _activeCamera.VideoOutputSettings.SenderName = v;
            };
        }

        private void BuildFollowGroup(RectTransform parent)
        {
            _followSection = PanelSectionToggle.CreateNewEntry(parent);
            _followGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _followSection, parent, "Follow", false);
            RectTransform content = _followGroup.ContentParent;

            _autoFollowToggle = PanelToggle.CreateNewEntry(content);
            _autoFollowToggle.Descriptor.SetTitle("Auto Follow");
            _autoFollowToggle.OnValueChanged = v =>
            {
                if (_activeCamera == null) return;
                _activeCamera.SetAutoFollowEnabled(v);
                // Auto Follow is half of what decides whether Follow Subject focus can drive.
                RefreshDoFModeVisibility();
            };

            RectTransform teleportRow = PanelElementDescriptor.BuildActionRow(content, "CameraTeleportRow");
            PanelButton teleportButton = PanelButton.CreateNew(teleportRow);
            teleportButton.Descriptor.SetTitle("Teleport To Me");
            teleportButton.Descriptor.SetDescription("Bring the camera in front of you, where it would spawn, to retrieve it.");
            teleportButton.OnClicked += () => _activeCamera?.TeleportInFrontOfPlayer();

            _followMarkerDropdown = PanelDropdown.CreateNewEntry(content);
            _followMarkerDropdown.Descriptor.SetTitle("Detached Marker");
            _followMarkerDropdown.Descriptor.SetDescription("What marks the camera when it leaves your hand — a grabbable puck, a lightweight wireframe, or nothing.");
            _followMarkerDropdown.AssignEntries(new List<string>(DetachedMarkerLabels));
            _followMarkerDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _followMarkerDropdown == null) return;
                int index = _followMarkerDropdown.Index;
                if (index >= 0) _activeCamera.SetDetachedMarker((BasisCameraDetachedMarker)index);
            };

            _followTargetDropdown = PanelDropdown.CreateNewEntry(content);
            _followTargetDropdown.Descriptor.SetTitle("Follow Target");
            _followTargetDropdown.Descriptor.SetDescription("Who the camera follows — you, or any player in the instance.");
            _followTargetDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _followTargetDropdown == null) return;
                int index = _followTargetDropdown.Index;
                if (index >= 0 && index < _followTargetIds.Count)
                {
                    _activeCamera.SetFollowTargetPlayer(_followTargetIds[index]);
                }
            };

            _followPlayspaceToggle = PanelToggle.CreateNewEntry(content);
            _followPlayspaceToggle.Descriptor.SetTitle("Follow Playspace");
            _followPlayspaceToggle.Descriptor.SetDescription("Track your body's centre of mass, so walking around your room keeps you in frame. Off follows the playspace origin instead.");
            _followPlayspaceToggle.OnValueChanged = v =>
            {
                if (_activeCamera != null) _activeCamera.autoFollowPlayspace = v;
            };

            // "Look At Me" removed — auto follow always aims at the player now, so the toggle is gone.
            // _followLookAtToggle = PanelToggle.CreateNewEntry(content);
            // _followLookAtToggle.Descriptor.SetTitle("Look At Me");
            // _followLookAtToggle.Descriptor.SetDescription("Aim at you instead of matching the direction you face.");
            // _followLookAtToggle.OnValueChanged = v =>
            // {
            //     if (_activeCamera != null) _activeCamera.autoFollowLookAtPlayer = v;
            // };

            // Grouped by axis: everything acting on X, then Y, then Z, so the sideways controls sit
            // together, the vertical ones sit together, and depth is on its own.

            // --- X axis: sideways placement and the rotation about X ---
            _followSideSlider = PanelSlider.CreateNew(content);
            _followSideSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                "Side Offset (X)", -3f, 3f, false, 2, ValueDisplayMode.Meters));
            _followSideSlider.OnValueChanged = v => SetFollowPositionAxis(0, v);

            _followLateralSlider = PanelSlider.CreateNew(content);
            _followLateralSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                "Lateral Tracking (X)", 0f, 1f, false, 2, ValueDisplayMode.Raw));
            _followLateralSlider.Descriptor.SetDescription("How much side-to-side movement pulls the camera in to keep you framed. 0 holds the side offset.");
            _followLateralSlider.OnValueChanged = v =>
            {
                if (_activeCamera != null) _activeCamera.autoFollowLateralTracking = v;
            };

            _followPitchSlider = PanelSlider.CreateNew(content);
            _followPitchSlider.SetSliderSettings(PanelSlider.SliderSettings.Degrees("Pitch Offset (X)", -90f, 90f, false, 1));
            _followPitchSlider.OnValueChanged = v => SetFollowRotationAxis(0, v);

            // --- Y axis: vertical placement, aim height and the rotation about Y ---
            _followHeightSlider = PanelSlider.CreateNew(content);
            _followHeightSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                "Height Offset (Y)", -2f, 2f, false, 2, ValueDisplayMode.Meters));
            _followHeightSlider.Descriptor.SetDescription("Relative to your calibrated eye level. 0 films you at eyeline.");
            _followHeightSlider.OnValueChanged = v => SetFollowPositionAxis(1, v);

            _followLookAtHeightSlider = PanelSlider.CreateNew(content);
            _followLookAtHeightSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                "Look At Height (Y)", -3f, 3f, false, 2, ValueDisplayMode.Meters));
            _followLookAtHeightSlider.OnValueChanged = v =>
            {
                if (_activeCamera != null) _activeCamera.autoFollowLookAtHeightOffset = v;
            };

            _followYawSlider = PanelSlider.CreateNew(content);
            _followYawSlider.SetSliderSettings(PanelSlider.SliderSettings.Degrees("Yaw Offset (Y)", -180f, 180f, false, 1));
            _followYawSlider.OnValueChanged = v => SetFollowRotationAxis(1, v);

            // --- Z axis: depth ---
            _followDistanceSlider = PanelSlider.CreateNew(content);
            _followDistanceSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                "Distance (Z)", 0.3f, 6f, false, 2, ValueDisplayMode.Meters));
            _followDistanceSlider.Descriptor.SetDescription("How far the camera sits from you. Larger pulls back for a wider shot; use Field Of View for lens zoom.");
            _followDistanceSlider.OnValueChanged = v => SetFollowPositionAxis(2, v);
        }

        private void SetFollowPositionAxis(int axis, float value)
        {
            if (_activeCamera == null) return;
            Vector3 offset = _activeCamera.autoFollowPositionOffset;
            offset[axis] = value;
            _activeCamera.autoFollowPositionOffset = offset;
        }

        private void SetFollowRotationAxis(int axis, float value)
        {
            if (_activeCamera == null) return;
            Vector3 rotation = _activeCamera.autoFollowRotationOffset;
            rotation[axis] = value;
            _activeCamera.autoFollowRotationOffset = rotation;
        }

        private void BuildActionsGroup(RectTransform parent)
        {
            _actionSection = PanelSectionToggle.CreateNewEntry(parent);
            _actionGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _actionSection, parent, "Capture", false);
            RectTransform content = _actionGroup.ContentParent;

            _resolutionDropdown = PanelDropdown.CreateNewEntry(content);
            _resolutionDropdown.Descriptor.SetTitle("Photo Resolution");
            _resolutionDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _resolutionDropdown == null) return;
                int index = _resolutionDropdown.Index;
                if (index >= 0) _activeCamera.ChangeResolution(index);
            };

            _formatDropdown = PanelDropdown.CreateNewEntry(content);
            _formatDropdown.Descriptor.SetTitle("Photo Format");
            _formatDropdown.AssignEntries(new List<string> { "PNG", "EXR" });
            _formatDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _formatDropdown == null) return;
                int index = _formatDropdown.Index;
                if (index >= 0) _activeCamera.HandHeld.SetFormat(index);
            };

            _capture360Toggle = PanelToggle.CreateNewEntry(content);
            _capture360Toggle.Descriptor.SetTitle("360 Capture");
            _capture360Toggle.OnValueChanged = v => _activeCamera?.HandHeld.SetCapture360State(v);

            _selfieToggle = PanelToggle.CreateNewEntry(content);
            _selfieToggle.Descriptor.SetTitle("Selfie");
            _selfieToggle.Descriptor.SetDescription("Turn the camera to face you and mirror the preview.");
            _selfieToggle.OnValueChanged = v =>
            {
                if (_activeCamera == null) return;
                if (_activeCamera.HandHeld.IsSelfieMode != v) _activeCamera.HandHeld.ToggleSelfie();
            };

            _autoLevelToggle = PanelToggle.CreateNewEntry(content);
            _autoLevelToggle.Descriptor.SetTitle("Auto Level");
            _autoLevelToggle.Descriptor.SetDescription("Keep the horizon level while flying the camera.");
            _autoLevelToggle.OnValueChanged = v =>
            {
                if (_activeCamera != null) _activeCamera.useAutoLeveling = v;
            };

            _vrStabToggle = PanelToggle.CreateNewEntry(content);
            _vrStabToggle.Descriptor.SetTitle("VR Stabilization");
            _vrStabToggle.Descriptor.SetDescription("Smooth out hand shake when holding the camera in VR.");
            _vrStabToggle.OnValueChanged = v =>
            {
                if (_activeCamera != null) _activeCamera.useVRHandheldSmoothing = v;
            };

            // Desktop only: there is a real file browser to open, and the shot lands in a
            // browsable Pictures folder rather than the app's sandboxed data path.
            if (BasisHandHeldCamera.CanOpenPhotosFolder)
            {
                RectTransform folderRow = PanelElementDescriptor.BuildActionRow(content, "CameraPhotosRow");
                PanelButton openFolderButton = PanelButton.CreateNew(folderRow);
                openFolderButton.Descriptor.SetTitle("Open Photos Folder");
                openFolderButton.OnClicked += () => BasisHandHeldCamera.OpenPhotosFolder();
            }
        }

        /// <summary>
        /// One toggle per named, user-togglable layer, controlling whether the capture camera
        /// draws it. Built once from the project's layers; the camera refuses the ones it
        /// manages itself (OverlayUI, the prop HUD), so those never appear here. The UI layer
        /// (players' nameplates) is exposed here as its own toggle.
        /// </summary>
        private void BuildLayersGroup(RectTransform parent)
        {
            _layersSection = PanelSectionToggle.CreateNewEntry(parent);
            _layersGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _layersSection, parent, "Render Layers", false);
            RectTransform content = _layersGroup.ContentParent;

            _layerToggles.Clear();
            for (int Layer = 0; Layer < 32; Layer++)
            {
                if (!BasisHandHeldCamera.IsCaptureLayerUserTogglable(Layer)) continue;

                int captured = Layer;
                PanelToggle toggle = PanelToggle.CreateNewEntry(content);
                toggle.Descriptor.SetTitle(LayerMask.LayerToName(captured));
                toggle.OnValueChanged = v => _activeCamera?.SetCaptureLayerEnabled(captured, v);
                _layerToggles.Add(captured, toggle);
            }
        }

        /// <summary>
        /// Render-rate cap for the capture camera, moved here from the developer settings page.
        /// These are application settings shared by every handheld camera rather than per-camera
        /// state, so they bind straight to the settings system instead of going through
        /// _activeCamera — which also means they need no seeding in ApplyActiveCameraToControls.
        /// </summary>
        private void BuildPerformanceGroup(RectTransform parent)
        {
            _performanceSection = PanelSectionToggle.CreateNewEntry(parent);
            _performanceGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _performanceSection, parent, "Performance", false);
            RectTransform content = _performanceGroup.ContentParent;

            _limitRenderRateToggle = PanelToggle.CreateNewEntry(content);
            _limitRenderRateToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.handheldCameraRate.limit"));
            _limitRenderRateToggle.Descriptor.SetDescription("Applies to every handheld camera, not just this one.");
            _limitRenderRateToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.handheldCameraRate.limit.tooltip"));
            _limitRenderRateToggle.AssignBinding(BasisSettingsDefaults.LimitHandHeldCameraRate);

            _renderRateSlider = PanelSlider.CreateEntryAndBind(
                content,
                new PanelSlider.SliderSettings(
                    BasisLocalization.Get("settings.developer.handheldCameraRate"),
                    BasisLocalization.Get("settings.developer.handheldCameraRate.description"),
                    1, 120, true, 0, ValueDisplayMode.Hz),
                BasisSettingsDefaults.HandHeldCameraRenderHz);
            _renderRateSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.handheldCameraRate.tooltip"));
        }

        /// <summary>
        /// World-space debug representations of the selected camera. Each toggle drives one layer
        /// on that camera's own visualiser, so the drawing keeps running once the panel is closed
        /// and two open cameras can be inspected independently.
        /// </summary>
        private void BuildGizmoGroup(RectTransform parent)
        {
            _gizmoSection = PanelSectionToggle.CreateNewEntry(parent);
            _gizmoGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _gizmoSection, parent, "Debug Gizmos", false);
            RectTransform content = _gizmoGroup.ContentParent;
            _gizmoGroup.SetDescription("Drawn into the world, so photos taken while these are on will show them too.");

            _gizmoToggles.Clear();

            AddGizmoToggle(content, BasisCameraGizmoLayers.Frustum, "Frustum",
                "The capture frustum from the real projection: near and far planes, the optical axis, and a world-level guide line to read roll against.");

            AddGizmoToggle(content, BasisCameraGizmoLayers.DepthOfField, "Focus Planes",
                "The plane of sharp focus, and in Bokeh mode the near and far limits of acceptable sharpness derived from aperture, focal length and sensor size.");

            AddGizmoToggle(content, BasisCameraGizmoLayers.Follow, "Follow Rig",
                "How auto follow places the shot: the subject anchor, the offset broken into its X, Y and Z legs in yaw space, the aim line, and the snap radius. Shown as a preview when follow is off.");

            AddGizmoToggle(content, BasisCameraGizmoLayers.PinState, "Pin & Modes",
                "What the camera is pinned to and which modes are live, with the link back to the pin source and a plumb line to the floor.");

            AddGizmoToggle(content, BasisCameraGizmoLayers.Readouts, "Readouts",
                "Floating numbers for each layer above — angles, distances and the derived optics. Off draws the geometry alone.");
        }

        private void AddGizmoToggle(RectTransform parent, BasisCameraGizmoLayers layer, string title, string description)
        {
            PanelToggle toggle = PanelToggle.CreateNewEntry(parent);
            toggle.Descriptor.SetTitle(title);
            toggle.Descriptor.SetDescription(description);
            toggle.OnValueChanged = v => _activeCamera?.DebugGizmos.SetLayerEnabled(layer, v);
            _gizmoToggles.Add(layer, toggle);
        }

        /// <summary>Reflects the selected camera's gizmo layers into the toggles.</summary>
        private void RefreshGizmoToggles()
        {
            if (_activeCamera == null) return;
            foreach (KeyValuePair<BasisCameraGizmoLayers, PanelToggle> entry in _gizmoToggles)
            {
                entry.Value?.SetValueWithoutNotify(_activeCamera.DebugGizmos.IsLayerEnabled(entry.Key));
            }
        }

        /// <summary>Reflects the selected camera's culling mask into the per-layer toggles.</summary>
        private void RefreshLayerToggles()
        {
            if (_activeCamera == null) return;
            foreach (KeyValuePair<int, PanelToggle> entry in _layerToggles)
            {
                entry.Value?.SetValueWithoutNotify(_activeCamera.IsCaptureLayerEnabled(entry.Key));
            }
        }

        /// <summary>
        /// Banner shown when the selected camera is hidden (closed while "Close Hides Instead"
        /// was on). It replaces the normal controls with a single Bring Back Camera button so a
        /// hidden-but-still-running camera is never stranded with no way to get it back.
        /// </summary>
        private void BuildHiddenState(RectTransform parent)
        {
            _hiddenState = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, parent);
            _hiddenState.SetTitle("Camera Hidden");
            _hiddenState.SetDescription("This camera is hidden but still running. Bring it back to your hand to use it again.");

            RectTransform row = PanelElementDescriptor.BuildActionRow(_hiddenState.ContentParent, "BringBackRow");
            _bringBackButton = PanelButton.CreateNew(row);
            _bringBackButton.Descriptor.SetTitle("Bring Back Camera");
            _bringBackButton.OnClicked += () =>
            {
                if (_activeCamera == null) return;
                _activeCamera.RevealAsFreshSpawn();
                RebuildSelector();
            };

            _hiddenState.SetActive(false);
        }

        /// <summary>
        /// Swaps the panel between the normal controls and the Bring Back banner based on whether
        /// the selected camera is hidden. Cheap and idempotent, so it can run every tick.
        /// </summary>
        private void RefreshHiddenState()
        {
            if (_hiddenState == null) return;

            // No camera selected: leave the groups as RebuildSelector set them, just hide the banner.
            if (_activeCamera == null)
            {
                _hiddenState.SetActive(false);
                _lastHiddenState = null;
                return;
            }

            // Only the Close-to-hidden state shows the Bring Back banner. Merely hiding the camera
            // visuals from the Hide Camera toggle keeps the settings up so you can keep adjusting it.
            bool dismissed = _activeCamera.IsClosedHidden;
            // Edge-triggered: toggling groups runs a layout rebuild, too costly to do every tick.
            if (_lastHiddenState == dismissed) return;
            _lastHiddenState = dismissed;

            _hiddenState.SetActive(dismissed);
            // When dismissed, collapse everything else so the banner stands alone.
            SetGroupsActive(!dismissed);
        }

        private void BuildTopActions(RectTransform parent)
        {
            _topActions = PanelElementDescriptor.BuildActionRow(parent, "CameraTopActions");

            PanelButton photoButton = PanelButton.CreateNew(_topActions);
            photoButton.Descriptor.SetTitle("Take Photo");
            photoButton.OnClicked += () => _activeCamera?.CapturePhoto();

            _timerButton = PanelButton.CreateNew(_topActions);
            _timerButton.Descriptor.SetTitle(TimerIdleLabel);
            _timerButton.OnClicked += () =>
            {
                // Timer() is a toggle now: pressing it mid-countdown cancels the shot.
                if (_activeCamera == null) return;
                _activeCamera.Timer();
                RefreshTimerLabel();
            };

        }

        private void RebuildSelector()
        {
            if (_selector == null) return;

            // Also covers cameras spawned while the panel is already open.
            ApplyOnPropUIVisibility(_panel != null);

            _entries.Clear();
            List<string> labels = new List<string>();
            IReadOnlyList<BasisHandHeldCamera> cameras = BasisHandHeldCameraRegistry.Cameras;
            for (int Index = 0; Index < cameras.Count; Index++)
            {
                BasisHandHeldCamera camera = cameras[Index];
                if (camera == null) continue;
                _entries.Add(camera);
                labels.Add($"{_entries.Count}. {camera.gameObject.name}");
            }

            _selector.AssignEntries(labels);

            if (_entries.Count == 0)
            {
                _selector.gameObject.SetActive(false);
                _emptyState?.SetActive(true);
                SetGroupsActive(false);
                _activeCamera = null;
                return;
            }

            _selector.gameObject.SetActive(_entries.Count > 1);
            _emptyState?.SetActive(false);
            SetGroupsActive(true);

            int selected = _activeCamera != null ? _entries.IndexOf(_activeCamera) : 0;
            if (selected < 0) selected = 0;
            _activeCamera = _entries[selected];
            _selector.SetValueWithoutNotify(labels[selected]);

            ApplyActiveCameraToControls();
            ForceLayoutRebuild(null);
        }

        private void OnSelectionChanged()
        {
            if (_selector == null) return;
            int index = _selector.Index;
            if (index < 0 || index >= _entries.Count) return;
            _activeCamera = _entries[index];
            ApplyActiveCameraToControls();
        }

        private void SetGroupsActive(bool active)
        {
            if (_topActions != null) _topActions.gameObject.SetActive(active);
            SetSectionActive(_previewSection, _previewGroup, active);
            SetSectionActive(_lensSection, _lensGroup, active);
            SetSectionActive(_dofSection, _dofGroup, active);
            SetSectionActive(_colorSection, _colorGroup, active);
            SetSectionActive(_effectsSection, _effectsGroup, active);
            SetSectionActive(_outputSection, _outputGroup, active);
            SetSectionActive(_followSection, _followGroup, active);
            SetSectionActive(_actionSection, _actionGroup, active);
            SetSectionActive(_layersSection, _layersGroup, active);
            SetSectionActive(_performanceSection, _performanceGroup, active);
            SetSectionActive(_gizmoSection, _gizmoGroup, active);
            if (_resetPageButton != null) _resetPageButton.gameObject.SetActive(active);

            ForceLayoutRebuild(null);
        }

        private static void SetSectionActive(PanelSectionToggle section, PanelElementDescriptor group, bool active)
        {
            if (section != null) section.gameObject.SetActive(active);
            if (group != null) group.gameObject.SetActive(active && (section == null || section.Expanded));
        }

        private void ApplyActiveCameraToControls()
        {
            if (_activeCamera == null) return;

            BasisHandHeldCameraMetaData metaData = _activeCamera.MetaData;

            if (_activeCamera.captureCamera != null)
            {
                _fovSlider?.SetValueWithoutNotify(_activeCamera.captureCamera.fieldOfView);
            }

            _exposureSlider?.SetValueWithoutNotify(_activeCamera.HandHeld.ExposureIndex);
            SyncToggle(_exposureOnCameraToggle, _activeCamera.HandHeld.ShowExposureOnCamera, ref _lastExposureOnCamera);

            if (metaData.bloom != null)
            {
                _bloomIntensitySlider?.SetValueWithoutNotify(metaData.bloom.intensity.value);
                _bloomThresholdSlider?.SetValueWithoutNotify(metaData.bloom.threshold.value);
            }

            if (metaData.colorAdjustments != null)
            {
                _contrastSlider?.SetValueWithoutNotify(metaData.colorAdjustments.contrast.value);
                _saturationSlider?.SetValueWithoutNotify(metaData.colorAdjustments.saturation.value);
                _hueSlider?.SetValueWithoutNotify(metaData.colorAdjustments.hueShift.value);
            }

            if (metaData.depthOfField != null)
            {
                _apertureSlider?.SetValueWithoutNotify(metaData.depthOfField.aperture.value);
                _focusSlider?.SetValueWithoutNotify(metaData.depthOfField.focusDistance.value);
                _dofFocalLengthSlider?.SetValueWithoutNotify(metaData.depthOfField.focalLength.value);
                _dofBladeCountSlider?.SetValueWithoutNotify(metaData.depthOfField.bladeCount.value);
                if (_dofModeDropdown != null)
                {
                    int mode = Mathf.Clamp(_activeCamera.HandHeld.DoFMode, 0, 2);
                    _dofModeDropdown.SetValueWithoutNotify(new[] { "Off", "Gaussian", "Bokeh" }[mode]);
                }
                RefreshDoFModeVisibility();
            }

            _focusModeDropdown?.SetValueWithoutNotify(FocusModeLabels[_activeCamera.autoFocusFollowSubject ? 0 : 1]);

            if (metaData.vignette != null)
                _vignetteSlider?.SetValueWithoutNotify(metaData.vignette.intensity.value * 100f);
            if (metaData.chromaticAberration != null)
                _chromaticSlider?.SetValueWithoutNotify(metaData.chromaticAberration.intensity.value * 100f);
            if (metaData.filmGrain != null)
                _filmGrainSlider?.SetValueWithoutNotify(metaData.filmGrain.intensity.value * 100f);
            if (metaData.whiteBalance != null)
            {
                _whiteBalanceTempSlider?.SetValueWithoutNotify(metaData.whiteBalance.temperature.value);
                _whiteBalanceTintSlider?.SetValueWithoutNotify(metaData.whiteBalance.tint.value);
            }
            if (metaData.lensDistortion != null)
                _lensDistortionSlider?.SetValueWithoutNotify(metaData.lensDistortion.intensity.value * 100f);

            if (_msaaDropdown != null)
            {
                int msaaIndex = System.Array.IndexOf(MsaaSampleCounts, _activeCamera.msaaSamples);
                if (msaaIndex >= 0) _msaaDropdown.SetValueWithoutNotify(BuildMsaaLabels()[msaaIndex]);
            }

#if Basis_VOLUMETRIC_SUPPORTED
            if (metaData.VolumetricFogVolume != null)
            {
                _fogSlider?.SetValueWithoutNotify(metaData.VolumetricFogVolume.density.value);
            }
#endif

            if (_resolutionDropdown != null)
            {
                List<string> labels = new List<string>();
                for (int Index = 0; Index < metaData.resolutions.Length; Index++)
                {
                    labels.Add($"{metaData.resolutions[Index].width} x {metaData.resolutions[Index].height}");
                }
                _resolutionDropdown.AssignEntries(labels);
                int current = FindResolutionIndex(metaData, _activeCamera.captureWidth, _activeCamera.captureHeight);
                if (current >= 0 && current < labels.Count)
                {
                    _resolutionDropdown.SetValueWithoutNotify(labels[current]);
                }
            }

            SyncToggle(_recordToggle, _activeCamera.enableRecordingView, ref _lastRecordingView);
            SyncToggle(_previewScreenToggle, _activeCamera.IsPreviewScreenVisible, ref _lastPreviewScreenVisible);
            SyncToggle(_hideCameraToggle, _activeCamera.IsCameraHidden, ref _lastCameraHidden);
            SyncToggle(_audioListenerToggle, _activeCamera.IsAudioListener, ref _lastAudioListener);
            SyncToggle(_selfieToggle, _activeCamera.HandHeld.IsSelfieMode, ref _lastSelfie);
            SyncToggle(_closeHidesToggle, _activeCamera.HandHeld.CloseHidesCamera, ref _lastCloseHides);
            _autoLevelToggle?.SetValueWithoutNotify(_activeCamera.useAutoLeveling);
            _vrStabToggle?.SetValueWithoutNotify(_activeCamera.useVRHandheldSmoothing);
            _capture360Toggle?.SetValueWithoutNotify(_activeCamera.capture360Enabled);
            _formatDropdown?.SetValueWithoutNotify(
                _activeCamera.HandHeld.FormatIndex == BasisHandHeldCameraUI.FORMAT_EXR ? "EXR" : "PNG");

            SyncToggle(_autoFollowToggle, _activeCamera.IsAutoFollowing, ref _lastAutoFollow);
            if (_followMarkerDropdown != null)
            {
                int markerIndex = Mathf.Clamp((int)_activeCamera.detachedMarker, 0, DetachedMarkerLabels.Length - 1);
                _followMarkerDropdown.SetValueWithoutNotify(DetachedMarkerLabels[markerIndex]);
            }
            _followPlayspaceToggle?.SetValueWithoutNotify(_activeCamera.autoFollowPlayspace);
            _followLookAtHeightSlider?.SetValueWithoutNotify(_activeCamera.autoFollowLookAtHeightOffset);
            _followSideSlider?.SetValueWithoutNotify(_activeCamera.autoFollowPositionOffset.x);
            _followLateralSlider?.SetValueWithoutNotify(_activeCamera.autoFollowLateralTracking);
            _followHeightSlider?.SetValueWithoutNotify(_activeCamera.autoFollowPositionOffset.y);
            _followDistanceSlider?.SetValueWithoutNotify(_activeCamera.autoFollowPositionOffset.z);
            _followYawSlider?.SetValueWithoutNotify(_activeCamera.autoFollowRotationOffset.y);
            _followPitchSlider?.SetValueWithoutNotify(_activeCamera.autoFollowRotationOffset.x);

            RefreshVideoOutputState();
            RefreshLayerToggles();
            RefreshGizmoToggles();
            RefreshPreviewTexture();

            // Re-evaluate from scratch for the newly selected camera.
            _lastHiddenState = null;
            RefreshHiddenState();
        }

        private static int FindResolutionIndex(BasisHandHeldCameraMetaData metaData, int width, int height)
        {
            for (int Index = 0; Index < metaData.resolutions.Length; Index++)
            {
                if (metaData.resolutions[Index].width == width && metaData.resolutions[Index].height == height)
                {
                    return Index;
                }
            }
            return -1;
        }

        /// <summary>
        /// Points the dropdown at whatever transport the selected camera is set to, and shows
        /// only the rows that transport actually uses.
        /// </summary>
        private void RefreshTransportSelection()
        {
            if (_activeCamera == null) return;

            if (_transportDropdown != null)
            {
                int index = _transports.IndexOf(_activeCamera.VideoTransport);
                if (index >= 0)
                {
                    _transportDropdown.SetValueWithoutNotify(
                        BasisHandHeldCamera.GetVideoTransportName(_transports[index]));
                }
            }

            // Direct To Screen swaps the headset view for the camera's. On desktop there is no
            // second view to give up — OverrideDesktopOutput already no-ops there — so the row
            // would be a control that does nothing. Matches the prop HUD, which hides its own
            // button the same way.
            if (_recordToggle != null)
            {
                _recordToggle.gameObject.SetActive(BasisDeviceManagement.IsCurrentModeVR());
            }

            bool web = _activeCamera.VideoTransport == BasisVideoTransport.Web;
            if (_webQualitySlider != null) _webQualitySlider.gameObject.SetActive(web);
            if (_webPortField != null) _webPortField.gameObject.SetActive(web);
            // Nothing to open until it is actually serving, and the address only exists then.
            if (_openStreamButton != null) _openStreamButton.gameObject.SetActive(web && _activeCamera.IsWebStreamActive);
            if (_videoSenderNameField != null) _videoSenderNameField.gameObject.SetActive(!web);

            ForceLayoutRebuild(_outputGroup);
        }

        /// <summary>
        /// uGUI layout groups do not reflow when a child is shown or hidden, so every runtime
        /// SetActive on a panel row has to be followed by this. Without it a freed row leaves a
        /// gap and a newly shown one is laid out with no height at all — it is present and
        /// interactive but invisible, which reads as the control simply not existing.
        /// </summary>
        private void OnSectionExpanded(bool _) => ForceLayoutRebuild(null);

        private void ForceLayoutRebuild(PanelElementDescriptor group)
        {
            group?.ForceRebuild();
            _panel?.Descriptor?.ForceRebuild();
        }

        /// <summary>
        /// Keeps the toggle's caption describing the selected transport: what it needs on the
        /// receiving side while idle, and the live address once the web stream is serving —
        /// the port rolls forward when taken, so the URL isn't something the user can guess.
        /// </summary>
        private void RefreshLiveOutputDescription()
        {
            if (_videoOutputToggle == null || _activeCamera == null) return;

            string description = _activeCamera.IsWebStreamActive
                ? $"Serving at {_activeCamera.WebStreamUrl} — add that as a Browser source in OBS, or open it in a browser."
                : $"Publish this camera as a live video source. {BasisHandHeldCamera.GetVideoTransportRequirement(_activeCamera.VideoTransport)}";
            if (_lastWebStreamDescription == description) return;

            _lastWebStreamDescription = description;
            _videoOutputToggle.Descriptor.SetDescription(description);
        }

        private void RefreshVideoOutputState()
        {
            if (_activeCamera == null) return;

            SyncToggle(_videoOutputToggle, _activeCamera.IsAnyVideoOutputActive, ref _lastVideoOutputActive);
            _lastWebStreamActive = _activeCamera.IsWebStreamActive;
            RefreshTransportSelection();
            RefreshLiveOutputDescription();
            _videoFrameRateSlider?.SetValueWithoutNotify(_activeCamera.VideoOutputSettings.FrameRate);
            _webQualitySlider?.SetValueWithoutNotify(_activeCamera.VideoOutputSettings.WebQuality);
            _webPortField?.SetValueWithoutNotify(_activeCamera.VideoOutputSettings.WebPort.ToString());
            _videoSenderNameField?.SetValueWithoutNotify(_activeCamera.VideoOutputSettings.SenderName);

            if (_videoResolutionDropdown != null)
            {
                for (int Index = 0; Index < VideoResolutionWidths.Length; Index++)
                {
                    if (VideoResolutionWidths[Index] != _activeCamera.VideoOutputSettings.Width) continue;
                    _videoResolutionDropdown.SetValueWithoutNotify(
                        $"{VideoResolutionWidths[Index]} x {VideoResolutionHeights[Index]}");
                    break;
                }
            }
        }

        private void RefreshPreviewTexture()
        {
            if (_previewImage == null) return;
            Texture feed = _activeCamera != null ? _activeCamera.PreviewTexture : null;
            if (_previewImage.texture != feed) _previewImage.texture = feed;
            _previewImage.enabled = feed != null;
            ApplyPreviewAspect(feed);
        }

        /// <summary>
        /// The layout drives the preview's width but not its height, so height is ours to set.
        /// Derive it from the laid-out width and the feed's aspect instead of stretching a
        /// fixed row, change-gated so it isn't dirtying the layout every frame.
        /// </summary>
        private void ApplyPreviewAspect(Texture feed)
        {
            RectTransform rect = _previewImage.rectTransform;
            float width = rect.rect.width;
            // Width is still zero on the frame the row is built, before the first layout pass.
            if (width <= 1f) return;

            float aspect = (feed != null && feed.height > 0) ? (float)feed.width / feed.height : 16f / 9f;
            float height = width / aspect;
            if (Mathf.Abs(rect.sizeDelta.y - height) < 0.5f) return;
            rect.sizeDelta = new Vector2(rect.sizeDelta.x, height);
        }

        private void SetPanelTickSubscription(bool subscribe)
        {
            if (subscribe == _panelTickSubscribed) return;
            if (subscribe)
            {
                BasisFrameClock.AddRequest();
                BasisFrameClock.OnTick += OnPanelTick;
            }
            else
            {
                BasisFrameClock.OnTick -= OnPanelTick;
                BasisFrameClock.RemoveRequest();
            }
            _panelTickSubscribed = subscribe;
        }

        private void OnPanelTick()
        {
            if (_activeCamera == null) return;

            RefreshPreviewTexture();

            if (_activeCamera.IsVideoOutputActive != _lastVideoOutputActive ||
                _activeCamera.IsWebStreamActive != _lastWebStreamActive)
            {
                RefreshVideoOutputState();
            }

            SyncToggle(_recordToggle, _activeCamera.enableRecordingView, ref _lastRecordingView);
            SyncToggle(_previewScreenToggle, _activeCamera.IsPreviewScreenVisible, ref _lastPreviewScreenVisible);
            SyncToggle(_autoFollowToggle, _activeCamera.IsAutoFollowing, ref _lastAutoFollow);
            SyncSharedControls();
            RefreshFollowTargets();
            RefreshTimerLabel();
            RefreshHiddenState();
        }

        // Two-way binding with the prop's own HUD: both surfaces show the same live camera state,
        // so a change on either is reflected on the other within a frame. Only the controls the
        // prop also carries can drift (panel-only effects have a single writer), so only those are
        // re-seeded here; then the prop is re-seeded from the same state for the reverse direction.
        // All writes are SetValueWithoutNotify, so nothing re-drives the camera or loops.
        private void SyncSharedControls()
        {
            if (_activeCamera == null) return;

            if (_activeCamera.captureCamera != null)
                SyncSlider(_fovSlider, _activeCamera.captureCamera.fieldOfView, ref _lastFov);

            SyncSlider(_exposureSlider, _activeCamera.HandHeld.ExposureIndex, ref _lastExposure);

            if (_activeCamera.MetaData.depthOfField != null)
            {
                SyncSlider(_apertureSlider, _activeCamera.MetaData.depthOfField.aperture.value, ref _lastAperture);
                SyncSlider(_focusSlider, _activeCamera.MetaData.depthOfField.focusDistance.value, ref _lastFocus);
            }


            SyncToggle(_selfieToggle, _activeCamera.HandHeld.IsSelfieMode, ref _lastSelfie);
            SyncToggle(_autoLevelToggle, _activeCamera.useAutoLeveling, ref _lastAutoLevel);
            SyncToggle(_vrStabToggle, _activeCamera.useVRHandheldSmoothing, ref _lastVrStab);

            _activeCamera.HandHeld.SyncPropControlsFromState();
        }

        // The instance's roster changes without any panel event, so the target list is rebuilt on
        // the tick — but only when the roster actually moves, to avoid rebuilding a dropdown (and
        // fighting an open one) every frame. A same-tick join+leave holds the count still, so the
        // listed ids are checked as well as the count.
        private void RefreshFollowTargets()
        {
            if (_followTargetDropdown == null || _activeCamera == null) return;

            var remotes = Basis.Scripts.Networking.BasisNetworkPlayers.RemotePlayers;
            if (!FollowTargetRosterChanged(remotes)) return;

            _followTargetIds.Clear();
            // Entries are the net ids, not the names: PanelDropdown resolves its selection by
            // string-matching the entry, so two players sharing a display name (or one named "Me")
            // would both resolve to the first match and follow the wrong player.
            var keys = new List<string> { "0" };
            var labels = new List<string> { "Me" };
            _followTargetIds.Add(0);

            foreach (var pair in remotes)
            {
                if (pair.Value == null) continue;
                _followTargetIds.Add(pair.Key);
                keys.Add(pair.Key.ToString());
                string name = !string.IsNullOrEmpty(pair.Value.SafeDisplayName) ? pair.Value.SafeDisplayName : $"Player {pair.Key}";
                labels.Add(name);
            }

            _followTargetDropdown.AssignEntries(keys, labels);

            int selected = _followTargetIds.IndexOf(_activeCamera.followTargetPlayerId);
            if (selected < 0) selected = 0;
            _followTargetDropdown.SetValueWithoutNotify(keys[selected]);
            ForceLayoutRebuild(_followGroup);
        }

        private bool FollowTargetRosterChanged(
            System.Collections.Concurrent.ConcurrentDictionary<ushort, Basis.Scripts.BasisSdk.Players.BasisRemotePlayer> remotes)
        {
            if (remotes.Count + 1 != _followTargetIds.Count) return true;

            for (int index = 1; index < _followTargetIds.Count; index++)
            {
                if (!remotes.ContainsKey(_followTargetIds[index])) return true;
            }

            return false;
        }

        private void RefreshTimerLabel()
        {
            if (_timerButton == null) return;

            int remaining = _activeCamera != null ? _activeCamera.CountdownRemaining : 0;
            if (remaining == _lastCountdownShown) return;
            _lastCountdownShown = remaining;

            // Stays interactable while counting — pressing it cancels — so the label reads
            // "Cancel (n)" rather than a disabled countdown.
            _timerButton.Descriptor.SetTitle(remaining > 0 ? $"Cancel ({remaining})" : TimerIdleLabel);
            _timerButton.SetInteractable(true);
        }

        // Aperture / focal length / blades only affect Bokeh; hide them in Off/Gaussian so the
        // section only offers controls that do something. Gaussian has no focus distance of its
        // own, so ApplyFocusDistance maps it onto the far-blur ramp and the slider works in both.
        // Single owner of the focus mode. Follow = the depth of field tracks the subject's distance
        // every frame (UpdateAutoFocus); Manual = the focus-distance slider. Follow needs DoF on to
        // show anything, so it forces it on.
        private void SetFocusFollowsSubject(bool follows)
        {
            if (_activeCamera == null) return;

            _activeCamera.autoFocusFollowSubject = follows;
            _activeCamera.HandHeld.SetDepthMode(follows
                ? BasisHandHeldCameraUI.DepthMode.Auto
                : BasisHandHeldCameraUI.DepthMode.Manual);
            if (follows) _activeCamera.BasisDOFInteractionHandler?.SetDoFState(true);

            _focusModeDropdown?.SetValueWithoutNotify(FocusModeLabels[follows ? 0 : 1]);
            RefreshDoFModeVisibility();
        }

        private void RefreshDoFModeVisibility()
        {
            if (_activeCamera == null) return;
            int mode = _activeCamera.HandHeld.DoFMode;
            bool bokeh = mode == 2;
            bool anyBlur = mode != 0;

            // Follow Subject only drives the focus while there is something to track; without that
            // the slider is the only way to focus, so it has to stay reachable.
            _focusSlider?.gameObject.SetActive(anyBlur && !_activeCamera.HandHeld.AutoFocusIsDriving);
            _apertureSlider?.gameObject.SetActive(bokeh);
            _dofFocalLengthSlider?.gameObject.SetActive(bokeh);
            _dofBladeCountSlider?.gameObject.SetActive(bokeh);
            ForceLayoutRebuild(_dofGroup);
        }

        // PanelSlider.ApplyValue restarts a 0.15s fill-colour tween on every call, and the tween
        // only sets its final colour when it FINISHES — so re-seeding a slider every frame keeps
        // resetting the tween before it lands, leaving the fill stuck at its initial white. Only
        // write on an actual change (NaN cache guarantees the first write lands).
        private static void SyncSlider(PanelSlider slider, float value, ref float cached)
        {
            if (slider == null || cached == value) return;
            cached = value;
            slider.SetValueWithoutNotify(value);
        }

        private static void SyncToggle(PanelToggle toggle, bool value, ref bool? cached)
        {
            if (toggle == null || cached == value) return;
            cached = value;
            toggle.SetValueWithoutNotify(value);
        }
    }
}
