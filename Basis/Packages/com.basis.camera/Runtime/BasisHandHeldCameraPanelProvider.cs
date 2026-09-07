using System;
using System.Collections.Generic;
using Basis.BasisUI.Styling;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Cinematics;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI.HandHeldCamera
{
    public partial class BasisHandHeldCameraPanelProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        public const string StaticTitleKey = "menu.provider.cameraSettings";
        public static string StaticTitle => BasisLocalization.Get(StaticTitleKey);

        private static readonly int[] VideoResolutionWidths = { 1280, 1920, 2560, 3840 };
        private static readonly int[] VideoResolutionHeights = { 720, 1080, 1440, 2160 };
        private static readonly int[] MsaaSampleCounts = { 1, 2, 4, 8 };

        // Index 0 follows the subject's depth automatically; index 1 uses the Focus Distance slider.
        private static readonly string[] FocusModeKeys = { "camera.focusAuto", "camera.focusManual" };
        private static readonly string[] DoFModeValues = { "Off", "Gaussian", "Bokeh" };
        private static readonly string[] DoFModeKeys = { "ui.option.off", "camera.mode.gaussian", "camera.mode.bokeh" };

        // Ordered to match BasisCameraDetachedMarker (Off / Puck / Wireframe).
        private static readonly string[] DetachedMarkerKeys =
        {
            "camera.detachedMarker.off", "camera.detachedMarker.puck", "camera.detachedMarker.wireframe",
        };

        // Ordered to match URP's MotionBlurQuality and MotionBlurMode, which the UI stores as their
        // index — a label reordered here silently picks a different enum entry.
        private static readonly string[] MotionBlurQualityLabels = { "Low", "Medium", "High" };
        private static readonly string[] MotionBlurQualityKeys =
        {
            "camera.motionBlurQuality.low", "camera.motionBlurQuality.medium", "camera.motionBlurQuality.high"
        };
        private static readonly string[] MotionBlurModeLabels = { "Camera Only", "Camera And Objects" };
        private static readonly string[] MotionBlurModeKeys =
        {
            "camera.motionBlurMode.cameraOnly", "camera.motionBlurMode.cameraAndObjects"
        };

        // In URP's TonemappingMode order, which is what the setting stores.
        private static readonly string[] TonemappingKeys =
        {
            "camera.tonemapping.none", "camera.tonemapping.neutral", "camera.tonemapping.aces",
        };

        // Ordered to match the PhotoTagging_* values the binding stores.
        private static readonly string[] PhotoTaggingKeys =
        {
            "settings.chat.camera.photoMetadata.noOne",
            "settings.chat.camera.photoMetadata.everyone",
            "settings.chat.camera.photoMetadata.justMe",
        };

        /// <summary>Preview height used until the row has a laid-out width to derive one from.</summary>
        private const float PreviewFallbackHeight = 320f;

        private static BasisHandHeldCameraPanelProvider _instance;

        public override string Title => StaticTitle;
        public override string IconAddress => AddressableAssets.Sprites.CameraSettings;
        public override int Order => 9;
        public override bool Hidden => BasisHandHeldCameraRegistry.Count == 0;

        private BasisMenuPanel _panel;
        private PanelTabGroup _tabGroup;
        private RectTransform _navColumn;
        private readonly List<RectTransform> _pageContents = new List<RectTransform>();
        private readonly List<BasisPanelSearch> _searches = new List<BasisPanelSearch>();
        private readonly List<string> _searchTabKeys = new List<string>();
        private readonly List<BasisPanelSearchHit> _searchHits = new List<BasisPanelSearchHit>();

        /// <summary>Tab the panel was left on, so reopening it lands back where the user was.</summary>
        private static int _lastTabIndex;

        private PanelDropdown _selector;
        private PanelElementDescriptor _emptyState;
        private PanelElementDescriptor _hiddenState;
        private PanelButton _bringBackButton;
        private bool? _lastHiddenState;
        private PanelElementDescriptor _previewGroup;
        private LayoutElement _previewLayout;
        private PanelElementDescriptor _lensGroup;
        private PanelElementDescriptor _dofGroup;
        private PanelElementDescriptor _colorGroup;
        private PanelElementDescriptor _gridGroup;
        private PanelElementDescriptor _peakingGroup;
        private PanelElementDescriptor _exposureGroup;
        private PanelElementDescriptor _filmLookGroup;
        private PanelElementDescriptor _bloomGroup;
        private PanelElementDescriptor _vignetteGroup;
        private PanelElementDescriptor _grainGroup;
        private PanelElementDescriptor _lensEffectsGroup;
        private PanelElementDescriptor _motionBlurGroup;
#if Basis_VOLUMETRIC_SUPPORTED
        private PanelElementDescriptor _fogGroup;
#endif
        private PanelElementDescriptor _outputGroup;
        private PanelElementDescriptor _actionGroup;
        private PanelElementDescriptor _handlingGroup;
        private PanelElementDescriptor _streamGroup;
        private PanelSectionToggle _lensSection;
        private PanelSectionToggle _dofSection;
        private PanelSectionToggle _colorSection;
        private PanelSectionToggle _gridSection;
        private PanelSectionToggle _peakingSection;
        private PanelSectionToggle _exposureSection;
        private PanelSectionToggle _filmLookSection;
        private PanelSectionToggle _bloomSection;
        private PanelSectionToggle _vignetteSection;
        private PanelSectionToggle _grainSection;
        private PanelSectionToggle _lensEffectsSection;
        private PanelSectionToggle _motionBlurSection;
#if Basis_VOLUMETRIC_SUPPORTED
        private PanelSectionToggle _fogSection;
#endif
        private PanelSectionToggle _outputSection;
        private PanelSectionToggle _followSection;
        private PanelSectionToggle _actionSection;
        private PanelSectionToggle _handlingSection;
        private PanelSectionToggle _streamSection;
        private PanelElementDescriptor _layersGroup;
        private PanelSectionToggle _layersSection;
        private readonly Dictionary<int, PanelToggle> _layerToggles = new Dictionary<int, PanelToggle>();
        private PanelElementDescriptor _gizmoGroup;
        private PanelSectionToggle _gizmoSection;
        private PanelElementDescriptor _photoMetadataGroup;
        private PanelSectionToggle _photoMetadataSection;
        private readonly Dictionary<BasisCameraGizmoLayers, PanelToggle> _gizmoToggles =
            new Dictionary<BasisCameraGizmoLayers, PanelToggle>();
        private PanelElementDescriptor _performanceGroup;
        private PanelSectionToggle _performanceSection;
        private PanelToggle _limitRenderRateToggle;
        private PanelSlider _renderRateSlider;
        private PanelElementDescriptor _renderRateLockNotice;
        private bool? _renderRatePinned;
        private bool? _lastRenderRateLimit;
        private float _lastRenderRateHz = float.NaN;
        private PanelButton _resetTopButton;
        private PanelButton _timerButton;
        private int _lastCountdownShown = -1;
        private const string TimerIdleLabel = "Timer";
        private readonly List<PanelButton> _topButtons = new List<PanelButton>();
        private RectTransform _topActionRow;
        private RawImage _previewImage;
        private GameObject _expandedOverlay;
        private RawImage _expandedImage;
        private AspectRatioFitter _expandedFitter;
        private bool _previewExpanded;
        private readonly List<GameObject> _hiddenByPreview = new List<GameObject>();

        private PanelSlider _fovSlider;
        private PanelSlider _exposureSlider;
        private PanelToggle _exposureOnCameraToggle;
        private PanelSlider _bloomIntensitySlider;
        private PanelSlider _bloomThresholdSlider;
        private PanelSlider _contrastSlider;
        private PanelSlider _saturationSlider;
        private PanelToggle _dofEnabledToggle;
        private PanelDropdown _dofModeDropdown;
        private PanelSlider _apertureSlider;
        private PanelSlider _focusSlider;
        private PanelSlider _dofFocalLengthSlider;
        private PanelSlider _dofBladeCountSlider;
        private PanelElementDescriptor _focusSubjectNotice;
        private bool? _lastFocusHasNoSubject;
        private PanelToggle _autoBrightnessToggle;
        private PanelDropdown _autoBrightnessMeteringDropdown;
        private PanelSlider _autoBrightnessTargetSlider;
        private PanelSlider _autoBrightnessSpeedSlider;
        private PanelSlider _autoBrightnessRangeSlider;

        /// <summary>
        /// Metering modes in <see cref="BasisCameraMeteringMode"/> order — the dropdown hands its
        /// row number straight to the enum, so a table out of step picks a different meter.
        /// </summary>
        private static readonly string[] MeteringKeys =
        {
            "camera.metering.average",
            "camera.metering.centre",
            "camera.metering.spot",
        };
        private PanelToggle _focusPeakingToggle;
        private PanelSlider _focusPeakingSensitivitySlider;
        private PanelDropdown _focusPeakingColourDropdown;
        private PanelToggle _focusPeakingGreyToggle;
        private PanelToggle _viewfinderGridToggle;
        private PanelDropdown _viewfinderGridPatternDropdown;
        private PanelSlider _viewfinderGridOpacitySlider;
        private PanelSlider _hueSlider;
        private PanelSlider _vignetteSlider;
        private PanelSlider _chromaticSlider;
        private PanelSlider _filmGrainSlider;
        private PanelSlider _whiteBalanceTempSlider;
        private PanelSlider _whiteBalanceTintSlider;
        private PanelSlider _lensDistortionSlider;
        private PanelSlider _lensDistortionScaleSlider;
        private PanelSlider _bloomScatterSlider;
        private PanelSlider _vignetteSmoothnessSlider;
        private PanelSlider _paniniSlider;
        private PanelSlider _paniniCropSlider;
        private PanelDropdown _captureTonemappingDropdown;
        private PanelSlider _motionBlurSlider;
        private PanelSlider _motionBlurClampSlider;
        private PanelDropdown _motionBlurQualityDropdown;
        private PanelDropdown _motionBlurModeDropdown;
        private PanelDropdown _msaaDropdown;
#if Basis_VOLUMETRIC_SUPPORTED
        private PanelToggle _fogOverrideToggle;
        private PanelSlider _fogSlider;
#endif

        private PanelElementDescriptor _followGroup;
        private PanelDropdown _followMarkerDropdown;
        private PanelSlider _markerScaleSlider;
        private PanelToggle _puckPreviewToggle;
        private PanelDropdown _followTargetDropdown;
        // Networked members only, in dropdown order. Row 0 of the dropdown is always "Me", so row
        // n maps to _followTargetIds[n - 1]. The local player is not an id: net id 0 is a real
        // player, so "Me" cannot be represented as a reserved value in here.
        private readonly List<ushort> _followTargetIds = new List<ushort>();

        /// <summary>Whether the target dropdown has ever been given its entries. See RefreshFollowTargets.</summary>
        private bool _followTargetsBuilt;
        private PanelDropdown _focusModeDropdown;
        private PanelToggle _followPlayspaceToggle;
        private PanelDropdown _aimPointDropdown;
        private PanelSlider _followLookAtHeightSlider;
        private PanelSlider _subjectRadiusSlider;
        private PanelToggle _targetGroupToggle;

        private PanelDropdown _resolutionDropdown;
        private PanelDropdown _formatDropdown;
        private PanelToggle _flyToggle;
        private PanelToggle _flyOnMenuToggle;
        private PanelSlider _flySpeedSlider;
        private PanelSlider _flyClimbSpeedSlider;
        private PanelSlider _flyFastMultiplierSlider;
        private PanelSlider _flyTurnSpeedSlider;
        private PanelSlider _flyMouseSensitivitySlider;
        private PanelToggle _flyMomentumToggle;
        private PanelToggle _movementFollowsPitchToggle;
        private PanelToggle _leftHandFlyToggle;
        private PanelToggle _rightHandFlyRotateToggle;
        private PanelSlider _handFlyMoveDeadzoneSlider;
        private PanelSlider _handFlyMoveReachSlider;
        private PanelSlider _handFlyMoveSensitivitySlider;
        private PanelSlider _handFlyTurnDeadzoneSlider;
        private PanelSlider _handFlyTurnReachSlider;
        private PanelSlider _handFlyTurnSensitivitySlider;
        private PanelToggle _rollToggle;
        private PanelToggle _autoLevelToggle;
        private PanelToggle _vrStabToggle;
        private PanelSlider _vrStabPositionSlider;
        private PanelSlider _vrStabYawSlider;
        private PanelSlider _vrStabPitchSlider;
        private PanelSlider _vrStabRollSlider;
        private PanelToggle _zoomStabToggle;
        private PanelSlider _zoomStabResponseSlider;
        private PanelSlider _zoomStabMinSlider;
        private PanelSlider _zoomStabMaxSlider;
        private PanelToggle _smoothDragToggle;
        private PanelSlider _smoothDragPositionSlider;
        private PanelSlider _smoothDragRotationSlider;
        private PanelSlider _smoothDragLeashSlider;
        private PanelToggle _resizeToggle;
        private PanelToggle _capture360Toggle;
        private PanelToggle _printPhotoToggle;
        private PanelElementDescriptor _photoStatus;
        private PanelButton _revealPhotoButton;
        private string _lastPhotoStatusText;
        private bool? _lastRevealPhotoInteractable;
        private PanelToggle _audioListenerToggle;
        private PanelToggle _selfieToggle;
        private PanelToggle _hideCameraToggle;
        private PanelToggle _closeHidesToggle;
        private PanelToggle _videoOutputToggle;
        private PanelDropdown _streamPresetDropdown;
        private List<BasisCameraStreamPreset> _streamPresets = new List<BasisCameraStreamPreset>();
        private string _lastStreamPresetKey;
        private PanelDropdown _transportDropdown;
        private List<BasisVideoTransport> _transports = new List<BasisVideoTransport>();
        private PanelDropdown _videoResolutionDropdown;
        private PanelSlider _videoFrameRateSlider;
        private PanelSlider _webQualitySlider;
        /// <summary>Matches the clamp BasisHandHeldCameraVideoOutput.SetWebStreamPort applies.</summary>
        private const int WebPortMin = 1024;
        private const int WebPortMax = 65500;

        private PanelTextField _webPortField;
        private PanelButton _openStreamButton;
        private PanelTextField _videoSenderNameField;

        private BasisHandHeldCamera _activeCamera;

        /// <summary>
        /// The camera the panel is bound to, for anything outside the panel that has to drive the
        /// same one. Survives the panel closing, so the last selection is still the answer.
        /// </summary>
        public static BasisHandHeldCamera SelectedCamera => _instance?._activeCamera;
        private readonly List<BasisHandHeldCamera> _entries = new List<BasisHandHeldCamera>();
        private bool _panelTickSubscribed;
        private bool? _lastVideoOutputActive;
        private PanelToggle _directToScreenToggle;
        private bool? _lastDirectToScreen;
        private string _lastDirectToScreenDescription;
        private bool? _lastWebStreamActive;
        private string _lastWebStreamDescription;
        private bool? _lastCameraHidden;
        private bool? _lastAudioListener;
        private float _lastFov = float.NaN;
        private float _lastExposure = float.NaN;
        private float _lastAperture = float.NaN;
        private float _lastFocus = float.NaN;
        private bool? _lastDofEnabled;
        private bool? _lastSelfie;
        private bool? _lastFly;
        private bool? _lastFlyOnMenu;
        private float _lastFlySpeed = float.NaN;
        private float _lastFlyClimbSpeed = float.NaN;
        private float _lastFlyFastMultiplier = float.NaN;
        private float _lastFlyTurnSpeed = float.NaN;
        private float _lastFlyMouseSensitivity = float.NaN;
        private bool? _lastFlyMomentum;
        private bool? _lastMovementFollowsPitch;
        private bool? _lastLeftHandFly;
        private bool? _lastRightHandFlyRotate;
        private float _lastHandFlyMoveDeadzone = float.NaN;
        private float _lastHandFlyMoveReach = float.NaN;
        private float _lastHandFlyMoveSensitivity = float.NaN;
        private float _lastHandFlyTurnDeadzone = float.NaN;
        private float _lastHandFlyTurnReach = float.NaN;
        private float _lastHandFlyTurnSensitivity = float.NaN;
        private bool? _lastCameraRoll;
        private bool? _lastAutoLevel;
        private bool? _lastVrStab;
        private float _lastVrStabPosition = float.NaN;
        private float _lastVrStabYaw = float.NaN;
        private float _lastVrStabPitch = float.NaN;
        private float _lastVrStabRoll = float.NaN;
        private bool? _lastZoomStab;
        private float _lastZoomStabResponse = float.NaN;
        private float _lastZoomStabMin = float.NaN;
        private float _lastZoomStabMax = float.NaN;
        private bool? _lastSmoothDrag;
        private float _lastSmoothDragPosition = float.NaN;
        private float _lastSmoothDragRotation = float.NaN;
        private float _lastSmoothDragLeash = float.NaN;
        private bool? _lastResize;
        private bool? _lastCloseHides;
        private bool? _lastExposureOnCamera;
        private bool? _lastFocusFollows;
        private bool? _lastPuckPreview;
        private float _lastMarkerScale = float.NaN;

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

            panel.OnInstanceReleased += () =>
            {
                if (_panel == null || _panel == panel) OnPanelClosed();
            };

            // Search lives on the header. The popup must not outlive this panel — it navigates into
            // tabs that go away with it.
            BasisPanelMoveHandle.SetPanelSearch(panel, Title, CollectSearchResults);
            panel.OnInstanceReleased += BasisPanelSearchPopup.Close;

            _tabGroup = PanelTabGroup.CreateNew(panel.Descriptor.ContentParent, LayoutDirection.Vertical);
            _navColumn = _tabGroup.ExtrasContainer;
            BuildExpandedPreview((RectTransform)panel.transform);
            _pageContents.Clear();
            _searches.Clear();
            _searchTabKeys.Clear();

            BuildNavigationColumn(_navColumn);

            // First, because a mode is the decision every other tab is downstream of: it sets them
            // up for a job and colours them by the part each one plays in it.
            AddTab("camera.modePreset", BuildModeTab);

            AddTab("camera.tab.photo", content =>
            {
                BuildActionsGroup(content);
                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_actionSection, _actionGroup, true, OnSectionExpanded);

                BuildPhotoMetadataGroup(content);
                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_photoMetadataSection, _photoMetadataGroup, false, OnSectionExpanded);
            });

            AddTab("camera.tab.image", content =>
            {
                _bodyPageContent = content;
                BuildBodySection(content);

                BuildLensGroup(content);
                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_lensSection, _lensGroup, true, OnSectionExpanded);

                BuildGridGroup(content);
                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_gridSection, _gridGroup, false, OnSectionExpanded);

                BuildDofGroup(content);
                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_dofSection, _dofGroup, false, OnSectionExpanded);

                BuildFocusPeakingGroup(content);
                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_peakingSection, _peakingGroup, false, OnSectionExpanded);

                BuildExposureGroup(content);
                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_exposureSection, _exposureGroup, false, OnSectionExpanded);

                BuildColorGroup(content);
                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_colorSection, _colorGroup, false, OnSectionExpanded);

                BuildFilmLookGroup(content);
                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_filmLookSection, _filmLookGroup, false, OnSectionExpanded);

                BuildBloomGroup(content);
                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_bloomSection, _bloomGroup, false, OnSectionExpanded);

                BuildVignetteGroup(content);
                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_vignetteSection, _vignetteGroup, false, OnSectionExpanded);

                BuildGrainGroup(content);
                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_grainSection, _grainGroup, false, OnSectionExpanded);

                BuildLensEffectsGroup(content);
                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_lensEffectsSection, _lensEffectsGroup, false, OnSectionExpanded);

                BuildMotionBlurGroup(content);
                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_motionBlurSection, _motionBlurGroup, false, OnSectionExpanded);

#if Basis_VOLUMETRIC_SUPPORTED
                BuildFogGroup(content);
                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_fogSection, _fogGroup, false, OnSectionExpanded);
#endif

                BuildLayersGroup(content);
                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_layersSection, _layersGroup, false, OnSectionExpanded);

                BuildBackgroundGroup(content);
                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_backgroundSection, _backgroundGroup, false, OnSectionExpanded);

#if BASIS_HAS_GI && !UNITY_ANDROID
                BuildGlobalIlluminationGroup(content);
                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_giSection, _giGroup, false, OnSectionExpanded);
#endif
#if BASIS_HAS_RTAO && !UNITY_ANDROID
                BuildRTAOGroup(content);
                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_rtaoSection, _rtaoGroup, false, OnSectionExpanded);
#endif
            });

            AddTab("camera.tab.streaming", content =>
            {
                BuildStreamGroup(content);
                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_streamSection, _streamGroup, true, OnSectionExpanded);

                BuildOutputGroup(content);
                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_outputSection, _outputGroup, false, OnSectionExpanded);
            });

            AddTab("camera.tab.recording", content =>
            {
                BuildVideoGroup(content);
                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_videoSection, _videoGroup, true, OnSectionExpanded);

                BuildGifGroup(content);
                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_gifSection, _gifGroup, false, OnSectionExpanded);

                BuildPhotogrammetryGroup(content);
                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_photogrammetrySection, _photogrammetryGroup, false, OnSectionExpanded);

                BuildPerformanceGroup(content);
                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_performanceSection, _performanceGroup, false, OnSectionExpanded);
            });

            AddTab("camera.tab.handling", content =>
            {
                BuildHandlingGroup(content);
                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_handlingSection, _handlingGroup, true, OnSectionExpanded);
            });

            AddTab("camera.modifiers", content =>
            {
                BuildModifierSections(content);

                BuildGizmoGroup(content);
                PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_gizmoSection, _gizmoGroup, false, OnSectionExpanded);
            });

            // Its own page rather than a fifth section under the slots: the slots are one choice
            // each and the effects are a list that grows, so together on one tab the thing you are
            // adding to sits several screens below the thing you are adding.
            AddTab("camera.tab.effects", BuildEffectSections);

            MakeSlidersLive((RectTransform)_tabGroup.transform);
            AssignSliderResetDefaults();

            RebuildSelector();

            if (_lastTabIndex > 0 && _lastTabIndex < _tabGroup.SelectionButtons.Count)
            {
                _tabGroup.SelectionButtons[_lastTabIndex]?.OnClicked?.Invoke();
            }

            BasisDeviceManagement.OnBootModeChanged -= OnBootModeChanged;
            BasisDeviceManagement.OnBootModeChanged += OnBootModeChanged;
            SetPanelTickSubscription(true);
        }

        /// <summary>
        /// The column down the left of the panel: the tab buttons, then the rows that frame every
        /// page rather than belong to one. The preview lives here so the feed — and the composition
        /// guides drawn over it — stay in view whichever tab is being edited.
        /// </summary>
        private void BuildNavigationColumn(RectTransform parent)
        {
            BuildPreviewGroup(parent);

            BuildTopActions(parent);

            // The label-carrying entry prefab reserves 500 units for its control beside the title,
            // which does not fit the navigation column at all. The no-title variant drops that
            // reservation — the same one the Library panel uses in this container.
            _selector = PanelDropdown.CreateNew(PanelDropdown.DropdownStyles.EntryNoLabel, parent);
            _selector.Descriptor.SetSize(new Vector2(60, 80));
            FitToNavColumn(_selector.Descriptor, releaseControlSlot: false);
            _selector.OnValueChanged = _ => OnSelectionChanged();

            _emptyState = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, parent);
            _emptyState.SetTitle(BasisLocalization.Get("camera.noCamerasOpen"));
            _emptyState.SetDescription(BasisLocalization.Get("camera.noCamerasOpen.description"));
            FitToNavColumn(_emptyState, releaseControlSlot: true);

            BuildHiddenState(parent);
            FitToNavColumn(_hiddenState, releaseControlSlot: true);
        }

        /// <summary>
        /// Card prefabs keep an icon slot and a control slot beside their labels, both sized for the
        /// full-width page. Together they are wider than the navigation column, so the layout falls
        /// back to minimums and hands the labels a width of zero — which renders their text one
        /// character per line. Drop the icon, and on the cards that carry no control, the slot too.
        /// </summary>
        private static void FitToNavColumn(PanelElementDescriptor element, bool releaseControlSlot)
        {
            if (element == null) return;

            if (element.IconBackground != null) element.IconBackground.SetActive(false);
            if (releaseControlSlot) ReleaseControlSlot(element);
        }

        /// <summary>
        /// Hands back the control slot a card reserves beside its title whether or not it puts a
        /// control in it. Text-only cards need it back: the title and the description share what is
        /// left over, so a card that keeps the reservation wraps a long readout into a narrow
        /// column with 300 units of nothing beside it.
        /// </summary>
        private static void ReleaseControlSlot(PanelElementDescriptor element)
        {
            if (element == null || element.Header == null) return;

            Transform slot = element.Header.Find("Title/Element");
            if (slot != null) slot.gameObject.SetActive(false);
        }

        /// <summary>
        /// Builds one tab and files it under the left-hand navigation. The page is populated before
        /// it is handed to the group, so every row is instantiated while the page is still active and
        /// its deferred Awake cannot come back later and overwrite the titles set here.
        /// </summary>
        private void AddTab(string tabKey, Action<RectTransform> build)
        {
            PanelTabPage page = PanelTabPage.CreateVertical(_tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = page.Descriptor;
            descriptor.SetIcon(AddressableAssets.Sprites.CameraSettings);
            descriptor.SetTitle(BasisLocalization.Get(tabKey));

            RectTransform content = descriptor.ContentParent;
            ClampScrollViewport(content);
            string scope = "camera/" + tabKey;

            BasisMenuStateMemory.BeginScope(scope);
            try
            {
                build(content);
            }
            finally
            {
                BasisMenuStateMemory.EndScope();
            }

            int index = _tabGroup.Pages.Count;
            _pageContents.Add(content);

            // Headless: this page is searched from the panel header's Search button, so it keeps its
            // whole height for controls instead of spending the first row on a field.
            _searches.Add(BasisPanelSearch.AttachHeadless(content, page));
            _searchTabKeys.Add(tabKey);

            PanelScrollMemory.Attach(content, scope);
            _tabGroup.AddTab(BasisLocalization.Get(tabKey), () => OnTabShown(index), page);
        }

        /// <summary>
        /// The shared scroll-view prefab ships a bare, zero-anchored viewport with no mask, so
        /// content taller than the page draws straight past its bounds (Page-style panels have no
        /// panel-level mask to catch it). Bound the viewport to the scroll rect and mask it — the
        /// standard scroll-view construction — so a tab clips and scrolls like the settings pages.
        /// </summary>
        private static void ClampScrollViewport(RectTransform content)
        {
            if (content == null) return;

            ScrollRect scroll = content.GetComponentInParent<ScrollRect>();
            if (scroll == null || scroll.viewport == null) return;

            RectTransform viewport = scroll.viewport;
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = Vector2.zero;
            viewport.offsetMax = new Vector2(-25f, 0f);
            if (!viewport.TryGetComponent(out RectMask2D _))
            {
                viewport.gameObject.AddComponent<RectMask2D>();
            }
        }

        /// <summary>
        /// Search is scoped to this panel by design — the camera's controls are its own, and a query
        /// here is not meant to reach the main Settings tabs. Each tab carries its own field; typing
        /// in one carries the query to the others, which only record it and filter when shown.
        /// </summary>
        /// <summary>
        /// Everything on this panel matching the query, for the header search popup. Scoped to the
        /// camera's own tabs on purpose — a search opened here is about framing a shot, and should
        /// not start turning up microphone settings.
        /// </summary>
        private void CollectSearchResults(string query, List<BasisPanelSearchResult> results)
        {
            for (int Index = 0; Index < _searches.Count; Index++)
            {
                BasisPanelSearch search = _searches[Index];
                if (search == null) continue;

                search.Prepare();
                search.CollectHits(query, _searchHits);

                int tabIndex = Index;
                string tabName = BasisLocalization.Get(_searchTabKeys[Index]);
                for (int Hit = 0; Hit < _searchHits.Count; Hit++)
                {
                    BasisPanelSearchHit hit = _searchHits[Hit];
                    string title = hit.Title;
                    string section = hit.SectionTitle;
                    PanelSectionToggle targetSection = hit.Section;

                    results.Add(new BasisPanelSearchResult(
                        title,
                        string.IsNullOrEmpty(section) || section == title ? tabName : $"{tabName} › {section}",
                        () => OpenSearchResult(tabIndex, targetSection, title)));
                }
            }

            _searchHits.Clear();
        }

        /// <summary>
        /// Goes to the tab a result lives on and puts the row under the user's eye. Expanding comes
        /// after the tab switch: a collapsed section rebuilds its rows on open, so the descriptor the
        /// hit was collected from is already gone, which is why the scroll matches by title.
        /// </summary>
        private void OpenSearchResult(int tabIndex, PanelSectionToggle section, string title)
        {
            if (_tabGroup != null) _tabGroup.SetValue(tabIndex);
            if (section != null) section.SetExpanded(true);

            if (tabIndex >= 0 && tabIndex < _searches.Count) _searches[tabIndex]?.ScrollTo(title);
        }

        private void OnTabShown(int index)
        {
            _lastTabIndex = index;
        }

        private BasisPanelSearch ActiveSearch()
        {
            if (_tabGroup == null || _searches.Count == 0) return null;
            return _searches[Mathf.Clamp(_tabGroup.Value, 0, _searches.Count - 1)];
        }

        private RectTransform ActivePageContent()
        {
            if (_tabGroup == null || _pageContents.Count == 0) return null;
            return _pageContents[Mathf.Clamp(_tabGroup.Value, 0, _pageContents.Count - 1)];
        }

        private void RefreshSearch() => ActiveSearch()?.Refresh();

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
            _flySpeedSlider?.SetResetDefault(defaults.flySpeed);
            _flyClimbSpeedSlider?.SetResetDefault(defaults.flyClimbSpeed);
            _flyFastMultiplierSlider?.SetResetDefault(defaults.flyFastMultiplier);
            _flyTurnSpeedSlider?.SetResetDefault(defaults.flyTurnSpeed);
            _flyMouseSensitivitySlider?.SetResetDefault(defaults.flyMouseSensitivity);
            _vrStabPositionSlider?.SetResetDefault(defaults.vrStabilizationPositionDamping);
            _vrStabYawSlider?.SetResetDefault(defaults.vrStabilizationYawDamping);
            _vrStabPitchSlider?.SetResetDefault(defaults.vrStabilizationPitchDamping);
            _vrStabRollSlider?.SetResetDefault(defaults.vrStabilizationRollDamping);
            _zoomStabResponseSlider?.SetResetDefault(defaults.zoomStabilizationResponse);
            _zoomStabMinSlider?.SetResetDefault(defaults.zoomStabilizationMinScale);
            _zoomStabMaxSlider?.SetResetDefault(defaults.zoomStabilizationMaxScale);
            _apertureSlider?.SetResetDefault(defaults.depthAperture);
            _focusSlider?.SetResetDefault(defaults.depthFocusDistance);
            _dofFocalLengthSlider?.SetResetDefault(defaults.dofFocalLength);
            _dofBladeCountSlider?.SetResetDefault(defaults.dofBladeCount);
            _focusPeakingSensitivitySlider?.SetResetDefault(defaults.focusPeakingSensitivity * 100f);
            _viewfinderGridOpacitySlider?.SetResetDefault(defaults.viewfinderGridOpacity * 100f);
            _autoBrightnessTargetSlider?.SetResetDefault(defaults.autoBrightnessTarget * 100f);
            _autoBrightnessSpeedSlider?.SetResetDefault(defaults.autoBrightnessSpeed);
            _autoBrightnessRangeSlider?.SetResetDefault(defaults.autoBrightnessRange);

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
            _lensDistortionScaleSlider?.SetResetDefault(defaults.lensDistortionScale);
            _bloomScatterSlider?.SetResetDefault(defaults.bloomScatter * 100f);
            _vignetteSmoothnessSlider?.SetResetDefault(defaults.vignetteSmoothness * 100f);
            _paniniSlider?.SetResetDefault(defaults.paniniDistance * 100f);
            _paniniCropSlider?.SetResetDefault(defaults.paniniCropToFit * 100f);
            _motionBlurSlider?.SetResetDefault(defaults.motionBlurIntensity * 100f);
            _motionBlurClampSlider?.SetResetDefault(defaults.motionBlurClamp * 100f);
#if Basis_VOLUMETRIC_SUPPORTED
            _fogSlider?.SetResetDefault(defaults.VolumetricFogVolumedensity);
#endif
#if BASIS_HAS_GI && !UNITY_ANDROID
            _giIntensitySlider?.SetResetDefault(defaults.giIntensity);
            _giSaturationSlider?.SetResetDefault(defaults.giSaturation);
            _giObscuranceSlider?.SetResetDefault(defaults.giObscurance);
            _giRayLengthSlider?.SetResetDefault(defaults.giRayLength);
            _giSmoothingSlider?.SetResetDefault(defaults.giSmoothing);
            _giEmitterIntensitySlider?.SetResetDefault(defaults.giEmitterIntensity);
            _giObscuranceRadiusSlider?.SetResetDefault(defaults.giObscuranceRadius);
            _giFadeDistanceSlider?.SetResetDefault(defaults.giFadeDistance);
            _giNormalBiasSlider?.SetResetDefault(defaults.giNormalBias);
            _giDistanceBiasSlider?.SetResetDefault(defaults.giDistanceBias);
            _giBounceThresholdSlider?.SetResetDefault(defaults.giBounceThreshold);
            _giFireflyClampSlider?.SetResetDefault(defaults.giFireflyClamp);
#endif
#if BASIS_HAS_RTAO && !UNITY_ANDROID
            _rtaoIntensitySlider?.SetResetDefault(defaults.rtaoIntensity);
            _rtaoRadiusSlider?.SetResetDefault(defaults.rtaoRadius);
            _rtaoDirectStrengthSlider?.SetResetDefault(defaults.rtaoDirectStrength);
            _rtaoNormalBiasSlider?.SetResetDefault(defaults.rtaoNormalBias);
            _rtaoDistanceBiasSlider?.SetResetDefault(defaults.rtaoDistanceBias);
            _rtaoFalloffSlider?.SetResetDefault(defaults.rtaoFalloff);
            _rtaoPowerSlider?.SetResetDefault(defaults.rtaoPower);
            _rtaoFadeStartSlider?.SetResetDefault(defaults.rtaoFadeStart);
            _rtaoFadeEndSlider?.SetResetDefault(defaults.rtaoFadeEnd);
            _rtaoSpecularReliefSlider?.SetResetDefault(defaults.rtaoSpecularRelief);
#endif

            _videoFrameRateSlider?.SetResetDefault(30f);
            _webQualitySlider?.SetResetDefault(70f);

            // Follow — from the interactable's field initializers.
            _placeOffsetXSlider?.SetResetDefault(defaults.modifiers.follow.positionOffset.x);
            _placeOffsetYSlider?.SetResetDefault(defaults.modifiers.follow.positionOffset.y);
            _placeOffsetZSlider?.SetResetDefault(defaults.modifiers.follow.positionOffset.z);
            _aimYawSlider?.SetResetDefault(defaults.modifiers.lookAt.rotationOffset.y);
            _aimPitchSlider?.SetResetDefault(defaults.modifiers.lookAt.rotationOffset.x);
            _markerScaleSlider?.SetResetDefault(defaults.detachedMarkerScale * 100f);
            _followLookAtHeightSlider?.SetResetDefault(defaults.modifiers.subject.aimHeightOffset);
            _subjectRadiusSlider?.SetResetDefault(defaults.modifiers.subject.framingRadius);
            _followLateralSlider?.SetResetDefault(defaults.modifiers.follow.lateralTracking);
            _steadySmoothingSlider?.SetResetDefault(defaults.modifiers.steady.smoothing);
            _steadyDeadZoneSlider?.SetResetDefault(defaults.modifiers.steady.verticalDeadZone);
            _collisionRadiusSlider?.SetResetDefault(defaults.modifiers.collision.radius);
            _collisionPaddingSlider?.SetResetDefault(defaults.modifiers.collision.padding);
            _dollyZoomMinSlider?.SetResetDefault(defaults.modifiers.dollyZoom.minFov);
            _dollyZoomMaxSlider?.SetResetDefault(defaults.modifiers.dollyZoom.maxFov);
            _rigWeightResponseSlider?.SetResetDefault(defaults.modifiers.rigWeight.responsiveness);
            _rigWeightBounceSlider?.SetResetDefault(defaults.modifiers.rigWeight.bounce * 100f);

            AssignFilmResetDefaults(defaults);
            AssignModifierResetDefaults(defaults);
        }

        private void PromptResetSettings()
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
            BasisDeviceManagement.OnBootModeChanged -= OnBootModeChanged;
            ApplyOnPropUIVisibility(false);
            SetPanelTickSubscription(false);
            ClearModifierReferences();
            ClearModeReferences();
            ClearGifReferences();
            ClearVideoReferences();
            ClearPhotogrammetryReferences();
            _panel = null;
            _tabGroup = null;
            _navColumn = null;
            _pageContents.Clear();
            _searches.Clear();
            _searchTabKeys.Clear();
            _selector = null;
            _emptyState = null;
            _hiddenState = null;
            _bringBackButton = null;
            _lastHiddenState = null;
            _previewGroup = null;
            _previewLayout = null;
            _lensGroup = null;
            _dofGroup = null;
            _colorGroup = null;
            _gridGroup = null;
            _peakingGroup = null;
            _exposureGroup = null;
            _filmLookGroup = null;
            _bloomGroup = null;
            _vignetteGroup = null;
            _grainGroup = null;
            _lensEffectsGroup = null;
            _motionBlurGroup = null;
#if Basis_VOLUMETRIC_SUPPORTED
            _fogGroup = null;
#endif
            _outputGroup = null;
            _handlingGroup = null;
            _streamGroup = null;
            _actionGroup = null;
            _previewImage = null;
            _expandedOverlay = null;
            _expandedImage = null;
            _expandedFitter = null;
            _previewExpanded = false;
            _hiddenByPreview.Clear();
            _fovSlider = null;
            _exposureSlider = null;
            _exposureOnCameraToggle = null;
            _bloomIntensitySlider = null;
            _bloomThresholdSlider = null;
            _contrastSlider = null;
            _saturationSlider = null;
            _apertureSlider = null;
            _focusSlider = null;
            _dofEnabledToggle = null;
            _dofModeDropdown = null;
            _dofFocalLengthSlider = null;
            _dofBladeCountSlider = null;
            _focusSubjectNotice = null;
            _lastFocusHasNoSubject = null;
            _autoBrightnessToggle = null;
            _autoBrightnessMeteringDropdown = null;
            _autoBrightnessTargetSlider = null;
            _autoBrightnessSpeedSlider = null;
            _autoBrightnessRangeSlider = null;
            _focusPeakingToggle = null;
            _focusPeakingColourDropdown = null;
            _focusPeakingSensitivitySlider = null;
            _focusPeakingGreyToggle = null;
            _viewfinderGridToggle = null;
            _viewfinderGridPatternDropdown = null;
            _viewfinderGridOpacitySlider = null;
            _hueSlider = null;
            _vignetteSlider = null;
            _chromaticSlider = null;
            _filmGrainSlider = null;
            ClearFilmReferences();
            _whiteBalanceTempSlider = null;
            _whiteBalanceTintSlider = null;
            _lensDistortionSlider = null;
            _lensDistortionScaleSlider = null;
            _bloomScatterSlider = null;
            _vignetteSmoothnessSlider = null;
            _paniniSlider = null;
            _paniniCropSlider = null;
            _captureTonemappingDropdown = null;
            _motionBlurSlider = null;
            _motionBlurClampSlider = null;
            _motionBlurQualityDropdown = null;
            _motionBlurModeDropdown = null;
            _focusModeDropdown = null;
            _followTargetDropdown = null;
            _followTargetIds.Clear();
            _followTargetsBuilt = false;
            ClearAnchorReferences();
            _msaaDropdown = null;
#if Basis_VOLUMETRIC_SUPPORTED
            _fogOverrideToggle = null;
            _fogSlider = null;
#endif
#if BASIS_HAS_GI && !UNITY_ANDROID
            _giOverrideToggle = null;
            _giModeDropdown = null;
            _giLayersDropdown = null;
            _giSkinnedMeshesDropdown = null;
            _giQualityDropdown = null;
            _giFallbackDropdown = null;
            _giIgnoreBakedEmissionToggle = null;
            _giIntensitySlider = null;
            _giSaturationSlider = null;
            _giObscuranceSlider = null;
            _giRayLengthSlider = null;
            _giSmoothingSlider = null;
            _giWideBlurToggle = null;
            _giRayReuseToggle = null;
            _giEmittersToggle = null;
            _giEmitterIntensitySlider = null;
            _giSpecularToggle = null;
            _giObscuranceRadiusSlider = null;
            _giFadeDistanceSlider = null;
            _giNormalBiasSlider = null;
            _giDistanceBiasSlider = null;
            _giBounceThresholdSlider = null;
            _giFireflyClampSlider = null;
            _giReflectionProbesToggle = null;
            _giMirrorsToggle = null;
#endif
#if BASIS_HAS_RTAO && !UNITY_ANDROID
            _rtaoOverrideToggle = null;
            _rtaoModeDropdown = null;
            _rtaoIntensitySlider = null;
            _rtaoRadiusSlider = null;
            _rtaoApplyModeDropdown = null;
            _rtaoDenoiseDropdown = null;
            _rtaoDirectStrengthSlider = null;
            _rtaoLayersDropdown = null;
            _rtaoSkinnedMeshesDropdown = null;
            _rtaoNormalBiasSlider = null;
            _rtaoDistanceBiasSlider = null;
            _rtaoFalloffSlider = null;
            _rtaoPowerSlider = null;
            _rtaoFadeStartSlider = null;
            _rtaoFadeEndSlider = null;
            _rtaoSpecularReliefSlider = null;
#endif
            _followGroup = null;
            _resetTopButton = null;
            _topButtons.Clear();
            _topActionRow = null;
            _timerButton = null;
            _lastCountdownShown = -1;
            _lensSection = null;
            _dofSection = null;
            _colorSection = null;
            _gridSection = null;
            _peakingSection = null;
            _exposureSection = null;
            _filmLookSection = null;
            _bloomSection = null;
            _vignetteSection = null;
            _grainSection = null;
            _lensEffectsSection = null;
            _motionBlurSection = null;
#if Basis_VOLUMETRIC_SUPPORTED
            _fogSection = null;
#endif
#if BASIS_HAS_GI && !UNITY_ANDROID
            _giSection = null;
            _giGroup = null;
#endif
#if BASIS_HAS_RTAO && !UNITY_ANDROID
            _rtaoSection = null;
            _rtaoGroup = null;
#endif
            _outputSection = null;
            _handlingSection = null;
            _streamSection = null;
            _followSection = null;
            _actionSection = null;
            _layersGroup = null;
            _layersSection = null;
            _layerToggles.Clear();
            _gizmoGroup = null;
            _gizmoSection = null;
            _photoMetadataGroup = null;
            _photoMetadataSection = null;
            _gizmoToggles.Clear();
            _performanceGroup = null;
            _performanceSection = null;
            _limitRenderRateToggle = null;
            _renderRateSlider = null;
            _renderRateLockNotice = null;
            _renderRatePinned = null;
            _lastRenderRateLimit = null;
            _lastRenderRateHz = float.NaN;
            _followMarkerDropdown = null;
            _markerScaleSlider = null;
            _lastMarkerScale = float.NaN;
            _puckPreviewToggle = null;
            _lastPuckPreview = null;
            _followPlayspaceToggle = null;
            _aimPointDropdown = null;
            _followLookAtHeightSlider = null;
            _subjectRadiusSlider = null;
            _targetGroupToggle = null;
            _resolutionDropdown = null;
            _formatDropdown = null;
            _flyToggle = null;
            _flyOnMenuToggle = null;
            _flySpeedSlider = null;
            _flyClimbSpeedSlider = null;
            _flyFastMultiplierSlider = null;
            _flyTurnSpeedSlider = null;
            _flyMouseSensitivitySlider = null;
            _flyMomentumToggle = null;
            _movementFollowsPitchToggle = null;
            _leftHandFlyToggle = null;
            _rightHandFlyRotateToggle = null;
            _handFlyMoveDeadzoneSlider = null;
            _handFlyMoveReachSlider = null;
            _handFlyMoveSensitivitySlider = null;
            _handFlyTurnDeadzoneSlider = null;
            _handFlyTurnReachSlider = null;
            _handFlyTurnSensitivitySlider = null;
            _rollToggle = null;
            _autoLevelToggle = null;
            _vrStabToggle = null;
            _vrStabPositionSlider = null;
            _vrStabYawSlider = null;
            _vrStabPitchSlider = null;
            _vrStabRollSlider = null;
            _zoomStabToggle = null;
            _zoomStabResponseSlider = null;
            _zoomStabMinSlider = null;
            _zoomStabMaxSlider = null;
            _smoothDragToggle = null;
            _smoothDragPositionSlider = null;
            _smoothDragRotationSlider = null;
            _smoothDragLeashSlider = null;
            _resizeToggle = null;
            _lastFly = null;
            _lastFlyOnMenu = null;
            _lastFlySpeed = float.NaN;
            _lastFlyClimbSpeed = float.NaN;
            _lastFlyFastMultiplier = float.NaN;
            _lastFlyTurnSpeed = float.NaN;
            _lastFlyMouseSensitivity = float.NaN;
            _lastFlyMomentum = null;
            _lastMovementFollowsPitch = null;
            _lastLeftHandFly = null;
            _lastRightHandFlyRotate = null;
            _lastHandFlyMoveDeadzone = float.NaN;
            _lastHandFlyMoveReach = float.NaN;
            _lastHandFlyMoveSensitivity = float.NaN;
            _lastHandFlyTurnDeadzone = float.NaN;
            _lastHandFlyTurnReach = float.NaN;
            _lastHandFlyTurnSensitivity = float.NaN;
            _lastCameraRoll = null;
            _lastAutoLevel = null;
            _lastVrStab = null;
            _lastVrStabPosition = float.NaN;
            _lastVrStabYaw = float.NaN;
            _lastVrStabPitch = float.NaN;
            _lastVrStabRoll = float.NaN;
            _lastZoomStab = null;
            _lastZoomStabResponse = float.NaN;
            _lastZoomStabMin = float.NaN;
            _lastZoomStabMax = float.NaN;
            _lastSmoothDrag = null;
            _lastSmoothDragPosition = float.NaN;
            _lastSmoothDragRotation = float.NaN;
            _lastSmoothDragLeash = float.NaN;
            _lastResize = null;
            _lastFov = float.NaN;
            _lastExposure = float.NaN;
            _lastAperture = float.NaN;
            _lastFocus = float.NaN;
            _lastDofEnabled = null;
            _capture360Toggle = null;
            _printPhotoToggle = null;
            _photoStatus = null;
            _revealPhotoButton = null;
            _lastPhotoStatusText = null;
            _lastRevealPhotoInteractable = null;
            _audioListenerToggle = null;
            _selfieToggle = null;
            _hideCameraToggle = null;
            _closeHidesToggle = null;
            _videoOutputToggle = null;
            _streamPresetDropdown = null;
            _transportDropdown = null;
            _videoResolutionDropdown = null;
            _videoFrameRateSlider = null;
            _webQualitySlider = null;
            _webPortField = null;
            _openStreamButton = null;
            _videoSenderNameField = null;
            // Releases the preview request as well: the panel is gone, so the camera goes back to
            // rendering only for the surfaces that are still showing it.
            SetActiveCamera(null);
            _lastVideoOutputActive = null;
            _lastWebStreamActive = null;
            _lastWebStreamDescription = null;
            _lastStreamPresetKey = null;
            _directToScreenToggle = null;
            _lastDirectToScreen = null;
            _lastDirectToScreenDescription = null;
            _lastCameraHidden = null;
            _lastAudioListener = null;
            _lastSelfie = null;
            _lastCloseHides = null;
            _lastExposureOnCamera = null;
            _lastFocusFollows = null;
            _entries.Clear();
        }

        public override void OnReleaseEvent() => OnPanelClosed();

        private void BuildPreviewGroup(RectTransform parent)
        {
            _previewGroup = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, parent);
            if (_previewGroup.Header != null) _previewGroup.Header.gameObject.SetActive(false);

            // A live RenderTexture can only be drawn by a RawImage, and the element's base
            // image is a UnityEngine.UI.Image, which draws sprites only. Graphic is
            // DisallowMultipleComponent, so the Image has to come off before the RawImage can
            // take its place on the card — and UiStyleImage goes first, since it requires the
            // Image and Unity refuses the removal while it's attached. DestroyImmediate
            // rather than Destroy: a deferred destroy would still hold the slot when
            // AddComponent runs later in this frame.
            RectTransform card = (RectTransform)_previewGroup.transform;
            if (card.TryGetComponent(out UiStyleImage styleImage)) UnityEngine.Object.DestroyImmediate(styleImage);

            // Take the card's material with us: it's the overlay-variant UI material the rest
            // of the menu draws with, so the preview keeps sorting on top the way the card
            // did. Read it before the destroy, and don't take the colour — the card is tinted
            // and the feed wants a plain white pass-through.
            Material cardMaterial = null;
            if (card.TryGetComponent(out Graphic baseGraphic))
            {
                cardMaterial = baseGraphic.material;
                UnityEngine.Object.DestroyImmediate(baseGraphic);
            }

            _previewImage = card.gameObject.AddComponent<RawImage>();
            _previewImage.raycastTarget = true;
            if (cardMaterial != null) _previewImage.material = cardMaterial;

            UnityEngine.UI.Button expand = card.gameObject.AddComponent<UnityEngine.UI.Button>();
            expand.transition = Selectable.Transition.None;
            expand.onClick.AddListener(ExpandPreview);

            // The card shrink-wraps its rows; the header is hidden and Content is empty for
            // this group, so its fitter would collapse the preview flat. Own the height and
            // let ApplyPreviewAspect drive it from the feed.
            if (card.TryGetComponent(out ContentSizeFitter fitter)) fitter.enabled = false;
            // The navigation column drives its children's heights, so the height has to be
            // published as a layout preference as well as written to the rect.
            if (!card.TryGetComponent(out _previewLayout)) _previewLayout = card.gameObject.AddComponent<LayoutElement>();
            _previewLayout.preferredHeight = PreviewFallbackHeight;
            card.sizeDelta = new Vector2(card.sizeDelta.x, PreviewFallbackHeight);
        }

        private void BuildExpandedPreview(RectTransform parent)
        {
            _expandedOverlay = new GameObject("CameraPreviewExpanded", typeof(RectTransform));
            RectTransform overlay = (RectTransform)_expandedOverlay.transform;
            overlay.SetParent(parent, false);
            overlay.anchorMin = Vector2.zero;
            overlay.anchorMax = Vector2.one;
            overlay.offsetMin = Vector2.zero;
            overlay.offsetMax = Vector2.zero;
            _expandedOverlay.AddComponent<LayoutElement>().ignoreLayout = true;

            Material material = _previewImage != null ? _previewImage.material : null;

            Image backdrop = _expandedOverlay.AddComponent<Image>();
            backdrop.color = new Color(0.05f, 0.05f, 0.06f, 0.97f);
            if (material != null) backdrop.material = material;

            UnityEngine.UI.Button collapse = _expandedOverlay.AddComponent<UnityEngine.UI.Button>();
            collapse.transition = Selectable.Transition.None;
            collapse.onClick.AddListener(CollapsePreview);

            GameObject imageGo = new GameObject("Feed", typeof(RectTransform));
            RectTransform imageRect = (RectTransform)imageGo.transform;
            imageRect.SetParent(overlay, false);
            imageRect.anchorMin = Vector2.zero;
            imageRect.anchorMax = Vector2.one;
            imageRect.offsetMin = Vector2.zero;
            imageRect.offsetMax = Vector2.zero;
            _expandedImage = imageGo.AddComponent<RawImage>();
            _expandedImage.raycastTarget = false;
            if (material != null) _expandedImage.material = material;
            _expandedFitter = imageGo.AddComponent<AspectRatioFitter>();
            _expandedFitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            _expandedFitter.aspectRatio = 16f / 9f;

            _expandedOverlay.SetActive(false);
        }

        private void ExpandPreview()
        {
            if (_expandedOverlay == null || _activeCamera == null || _previewExpanded) return;
            _previewExpanded = true;

            Transform root = _expandedOverlay.transform.parent;
            _hiddenByPreview.Clear();
            for (int Index = 0; Index < root.childCount; Index++)
            {
                GameObject child = root.GetChild(Index).gameObject;
                if (child == _expandedOverlay || !child.activeSelf) continue;
                _hiddenByPreview.Add(child);
                child.SetActive(false);
            }

            _expandedOverlay.transform.SetAsLastSibling();
            _expandedOverlay.SetActive(true);
            RefreshPreviewTexture();
        }

        private void CollapsePreview()
        {
            if (!_previewExpanded) return;
            _previewExpanded = false;
            if (_expandedOverlay != null) _expandedOverlay.SetActive(false);
            for (int Index = 0; Index < _hiddenByPreview.Count; Index++)
            {
                if (_hiddenByPreview[Index] != null) _hiddenByPreview[Index].SetActive(true);
            }
            _hiddenByPreview.Clear();
        }

        private void BuildLensGroup(RectTransform parent)
        {
            _lensSection = PanelSectionToggle.CreateNewEntry(parent);
            _lensGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _lensSection, parent, BasisLocalization.Get("camera.lens"), false);
            RectTransform content = _lensGroup.ContentParent;

            _fovSlider = PanelSlider.CreateNew(content);
            _fovSlider.SetSliderSettings(PanelSlider.SliderSettings.Degrees(BasisLocalization.Get("camera.fieldOfView"),
                BasisHandHeldCameraUI.MinFov, BasisHandHeldCameraUI.MaxFov, false, 1));
            _fovSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeFOV(v);

            _msaaDropdown = PanelDropdown.CreateNewEntry(content);
            _msaaDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.msaa"));
            _msaaDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.msaa.description"));
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

        /// <summary>
        /// The alignment grid, in the lens section because framing is what the lens above it
        /// decides and the grid is how that framing is judged.
        /// </summary>
        private void BuildViewfinderGridControls(RectTransform content)
        {
            _viewfinderGridToggle = PanelToggle.CreateNewEntry(content);
            _viewfinderGridToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.grid.show"));
            _viewfinderGridToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.grid.description"));
            _viewfinderGridToggle.OnValueChanged = v =>
            {
                _activeCamera?.SetViewfinderGridEnabled(v);
                RefreshViewfinderGridVisibility();
            };

            _viewfinderGridPatternDropdown = PanelDropdown.CreateNewEntry(content);
            _viewfinderGridPatternDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.grid.pattern"));
            _viewfinderGridPatternDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.grid.pattern.description"));
            _viewfinderGridPatternDropdown.AssignLocalizedEntries(
                new List<string>(BasisHandHeldCamera.GridPatternKeys),
                new List<string>(BasisHandHeldCamera.GridPatternKeys));
            _viewfinderGridPatternDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _viewfinderGridPatternDropdown == null) return;
                int index = _viewfinderGridPatternDropdown.Index;
                if (index >= 0) _activeCamera.SetViewfinderGridPattern(index);
            };

            _viewfinderGridOpacitySlider = PanelSlider.CreateNew(content);
            _viewfinderGridOpacitySlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.grid.opacity"),
                BasisHandHeldCamera.MinGridOpacity * 100f, BasisHandHeldCamera.MaxGridOpacity * 100f,
                false, 0, ValueDisplayMode.Percentage));
            _viewfinderGridOpacitySlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.grid.opacity.description"));
            _viewfinderGridOpacitySlider.OnValueChanged = v => _activeCamera?.SetViewfinderGridOpacity(v / 100f);
        }

        private void BuildDofGroup(RectTransform parent)
        {
            _dofSection = PanelSectionToggle.CreateNewEntry(parent);
            _dofGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _dofSection, parent, BasisLocalization.Get("camera.depthOfField"), false);
            RectTransform content = _dofGroup.ContentParent;

            // The same switch the camera prop carries on its own HUD, and the master for
            // everything under it: with the effect off, none of the controls below do anything.
            _dofEnabledToggle = PanelToggle.CreateNewEntry(content);
            _dofEnabledToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.depthOfField.enable"));
            _dofEnabledToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.depthOfField.enable.description"));
            _dofEnabledToggle.OnValueChanged = SetDepthOfFieldEnabled;

            _dofModeDropdown = PanelDropdown.CreateNewEntry(content);
            _dofModeDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.mode"));
            _dofModeDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.mode.description"));
            _dofModeDropdown.AssignLocalizedEntries(new List<string>(DoFModeValues), new List<string>(DoFModeKeys));
            _dofModeDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _dofModeDropdown == null) return;
                _activeCamera.HandHeld.SetDoFMode(_dofModeDropdown.Index);
                SyncDoFEnabledToggle();
                RefreshDoFModeVisibility();
            };

            _focusModeDropdown = PanelDropdown.CreateNewEntry(content);
            _focusModeDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.focus"));
            _focusModeDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.focus.description"));
            _focusModeDropdown.AssignLocalizedEntries(new List<string>(FocusModeKeys), new List<string>(FocusModeKeys));
            _focusModeDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _focusModeDropdown == null) return;
                SetFocusFollowsSubject(_focusModeDropdown.Index == 0);
            };

            // Directly under the dropdown it explains, because the state it reports is invisible
            // from here: who the camera films is chosen on another page entirely.
            _focusSubjectNotice = BuildRecordingStatusCard(content, "camera.focus.noSubject", null);
            _focusSubjectNotice.SetTooltip(BasisLocalization.Get("camera.focus.noSubject.description"));
            _focusSubjectNotice.gameObject.SetActive(false);

            _focusSlider = PanelSlider.CreateNew(content);
            _focusSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.focusDistance"), BasisHandHeldCameraUI.MinFocusDistance,
                BasisHandHeldCameraUI.MaxFocusDistance, false, 1, ValueDisplayMode.Meters));
            _focusSlider.OnValueChanged = v => _activeCamera?.HandHeld.DepthChangeFocusDistance(v);

            _apertureSlider = PanelSlider.CreateNew(content);
            _apertureSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.aperture"), BasisHandHeldCameraUI.MinAperture, BasisHandHeldCameraUI.MaxAperture, false, 2, ValueDisplayMode.Raw));
            _apertureSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.aperture.description"));
            _apertureSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeAperture(v);

            _dofFocalLengthSlider = PanelSlider.CreateNew(content);
            _dofFocalLengthSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.focalLength"), BasisHandHeldCameraUI.MinFocalLength,
                BasisHandHeldCameraUI.MaxFocalLength, false, 0, ValueDisplayMode.Raw));
            _dofFocalLengthSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.focalLength.description"));
            _dofFocalLengthSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeDoFFocalLength(v);

            _dofBladeCountSlider = PanelSlider.CreateNew(content);
            _dofBladeCountSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.bokehBlades"), BasisHandHeldCameraUI.MinBladeCount,
                BasisHandHeldCameraUI.MaxBladeCount, true, 0, ValueDisplayMode.Raw));
            _dofBladeCountSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.bokehBlades.description"));
            _dofBladeCountSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeDoFBladeCount(v);

        }

        /// <summary>
        /// The focus aid, in the depth of field section because that is where focus is set from —
        /// and because it is what makes a focus distance judgeable on a preview this small. It is
        /// not gated on the blur being on: a shot with depth of field off is still one that can be
        /// out of focus, and peaking answers that too.
        /// </summary>
        private void BuildFocusPeakingControls(RectTransform content)
        {
            _focusPeakingToggle = PanelToggle.CreateNewEntry(content);
            _focusPeakingToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.focusPeaking.show"));
            _focusPeakingToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.focusPeaking.description"));
            _focusPeakingToggle.OnValueChanged = v =>
            {
                _activeCamera?.SetFocusPeakingEnabled(v);
                RefreshFocusPeakingVisibility();
            };

            _focusPeakingColourDropdown = PanelDropdown.CreateNewEntry(content);
            _focusPeakingColourDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.focusPeaking.colour"));
            _focusPeakingColourDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.focusPeaking.colour.description"));
            _focusPeakingColourDropdown.AssignLocalizedEntries(
                new List<string>(BasisHandHeldCamera.FocusPeakingColourKeys),
                new List<string>(BasisHandHeldCamera.FocusPeakingColourKeys));
            _focusPeakingColourDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _focusPeakingColourDropdown == null) return;
                int index = _focusPeakingColourDropdown.Index;
                if (index >= 0) _activeCamera.SetFocusPeakingColour(index);
            };

            _focusPeakingSensitivitySlider = PanelSlider.CreateNew(content);
            _focusPeakingSensitivitySlider.SetSliderSettings(PanelSlider.SliderSettings.Percentage(
                BasisLocalization.Get("camera.focusPeaking.sensitivity")));
            _focusPeakingSensitivitySlider.Descriptor.SetTooltip(
                BasisLocalization.Get("camera.focusPeaking.sensitivity.description"));
            _focusPeakingSensitivitySlider.OnValueChanged = v => _activeCamera?.SetFocusPeakingSensitivity(v / 100f);

            _focusPeakingGreyToggle = PanelToggle.CreateNewEntry(content);
            _focusPeakingGreyToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.focusPeaking.grey"));
            _focusPeakingGreyToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.focusPeaking.grey.description"));
            _focusPeakingGreyToggle.OnValueChanged = v => _activeCamera?.SetFocusPeakingGreyPicture(v);
        }

        /// <summary>
        /// The meter, directly under the exposure control it shares its output with. Sitting there
        /// is the explanation: with it on, the slider above stops being the exposure and becomes
        /// the compensation applied on top of whatever the meter decides.
        /// </summary>
        private void BuildAutoBrightnessControls(RectTransform content)
        {
            _autoBrightnessToggle = PanelToggle.CreateNewEntry(content);
            _autoBrightnessToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.autoBrightness"));
            _autoBrightnessToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.autoBrightness.description"));
            _autoBrightnessToggle.OnValueChanged = v =>
            {
                _activeCamera?.SetAutoBrightnessEnabled(v);
                RefreshAutoBrightnessVisibility();
            };

            _autoBrightnessMeteringDropdown = PanelDropdown.CreateNewEntry(content);
            _autoBrightnessMeteringDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.autoBrightness.metering"));
            _autoBrightnessMeteringDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.autoBrightness.metering.description"));
            _autoBrightnessMeteringDropdown.AssignLocalizedEntries(
                new List<string>(MeteringKeys), new List<string>(MeteringKeys));
            _autoBrightnessMeteringDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _autoBrightnessMeteringDropdown == null) return;
                int index = _autoBrightnessMeteringDropdown.Index;
                if (index >= 0) _activeCamera.SetAutoBrightnessMetering(index);
            };

            _autoBrightnessTargetSlider = PanelSlider.CreateNew(content);
            _autoBrightnessTargetSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.autoBrightness.target"),
                BasisHandHeldCamera.MinBrightnessTarget * 100f, BasisHandHeldCamera.MaxBrightnessTarget * 100f,
                false, 0, ValueDisplayMode.Percentage));
            _autoBrightnessTargetSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.autoBrightness.target.description"));
            _autoBrightnessTargetSlider.OnValueChanged = v => _activeCamera?.SetAutoBrightnessTarget(v / 100f);

            _autoBrightnessSpeedSlider = PanelSlider.CreateNew(content);
            _autoBrightnessSpeedSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.autoBrightness.speed"),
                BasisHandHeldCamera.MinBrightnessSpeed, BasisHandHeldCamera.MaxBrightnessSpeed,
                false, 1, ValueDisplayMode.Raw));
            _autoBrightnessSpeedSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.autoBrightness.speed.description"));
            _autoBrightnessSpeedSlider.OnValueChanged = v => _activeCamera?.SetAutoBrightnessSpeed(v);

            _autoBrightnessRangeSlider = PanelSlider.CreateNew(content);
            _autoBrightnessRangeSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.autoBrightness.range"),
                BasisHandHeldCamera.MinBrightnessRange, BasisHandHeldCamera.MaxBrightnessRange,
                false, 1, ValueDisplayMode.Raw));
            _autoBrightnessRangeSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.autoBrightness.range.description"));
            _autoBrightnessRangeSlider.OnValueChanged = v => _activeCamera?.SetAutoBrightnessRange(v);
        }

        /// <summary>
        /// The four controls that shape the meter follow the toggle that runs it, the way the motion
        /// blur shape controls follow its strength.
        /// </summary>
        private void RefreshAutoBrightnessVisibility()
        {
            bool metering = _activeCamera != null && _activeCamera.autoBrightnessEnabled;

            _autoBrightnessMeteringDropdown?.gameObject.SetActive(metering);
            _autoBrightnessTargetSlider?.gameObject.SetActive(metering);
            _autoBrightnessSpeedSlider?.gameObject.SetActive(metering);
            _autoBrightnessRangeSlider?.gameObject.SetActive(metering);
            RefreshSearch();
            ForceLayoutRebuild(_exposureGroup);
        }

        private void BuildExposureGroup(RectTransform parent)
        {
            _exposureSection = PanelSectionToggle.CreateNewEntry(parent);
            _exposureGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _exposureSection, parent, BasisLocalization.Get("camera.exposure"), false);
            RectTransform content = _exposureGroup.ContentParent;

            _exposureSlider = PanelSlider.CreateNew(content);
            _exposureSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.exposure"), 0f, BasisHandHeldCameraUI.ExposureStopCount - 1, true, 0, ValueDisplayMode.Raw));
            _exposureSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeExposureCompensation(v);

            BuildAutoBrightnessControls(content);

            _captureTonemappingDropdown = PanelDropdown.CreateNewEntry(content);
            _captureTonemappingDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.tonemapping"));
            _captureTonemappingDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.tonemapping.description"));
            _captureTonemappingDropdown.AssignLocalizedEntries(
                new List<string>(TonemappingKeys), new List<string>(TonemappingKeys));
            _captureTonemappingDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _captureTonemappingDropdown == null) return;
                _activeCamera.SetCaptureTonemapping(_captureTonemappingDropdown.Index);
            };

            _exposureOnCameraToggle = PanelToggle.CreateNewEntry(content);
            _exposureOnCameraToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.exposureOnCamera"));
            _exposureOnCameraToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.exposureOnCamera.description"));
            _exposureOnCameraToggle.OnValueChanged = v =>
            {
                _activeCamera?.HandHeld.SetExposureOnCameraVisible(v);
                // Keep the cache tracking what the widget shows, or switching to a camera
                // that already holds this value would skip the push and strand the knob.
                _lastExposureOnCamera = v;
            };
        }

        private void BuildColorGroup(RectTransform parent)
        {
            _colorSection = PanelSectionToggle.CreateNewEntry(parent);
            _colorGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _colorSection, parent, BasisLocalization.Get("camera.colour"), false);
            RectTransform content = _colorGroup.ContentParent;

            _contrastSlider = PanelSlider.CreateNew(content);
            _contrastSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.contrast"), -100f, 100f, false, 1, ValueDisplayMode.Raw));
            _contrastSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeContrast(v);

            _saturationSlider = PanelSlider.CreateNew(content);
            _saturationSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.saturation"), -100f, 100f, false, 1, ValueDisplayMode.Raw));
            _saturationSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeSaturation(v);

            _hueSlider = PanelSlider.CreateNew(content);
            _hueSlider.SetSliderSettings(PanelSlider.SliderSettings.Degrees(BasisLocalization.Get("camera.hueShift"), -180f, 180f, false, 0));
            _hueSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeHueShift(v);

            _whiteBalanceTempSlider = PanelSlider.CreateNew(content);
            _whiteBalanceTempSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.whiteBalanceTemp"), -100f, 100f, false, 0, ValueDisplayMode.Raw));
            _whiteBalanceTempSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeWhiteBalanceTemperature(v);

            _whiteBalanceTintSlider = PanelSlider.CreateNew(content);
            _whiteBalanceTintSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.whiteBalanceTint"), -100f, 100f, false, 0, ValueDisplayMode.Raw));
            _whiteBalanceTintSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeWhiteBalanceTint(v);
        }

        private void BuildFilmLookGroup(RectTransform parent)
        {
            _filmLookSection = PanelSectionToggle.CreateNewEntry(parent);
            _filmLookGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _filmLookSection, parent, BasisLocalization.Get("camera.filmLook"), false);
            RectTransform content = _filmLookGroup.ContentParent;

            BuildFilmColourControls(content);
        }


        private void BuildBloomGroup(RectTransform parent)
        {
            _bloomSection = PanelSectionToggle.CreateNewEntry(parent);
            _bloomGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _bloomSection, parent, BasisLocalization.Get("camera.section.bloom"), false);
            RectTransform content = _bloomGroup.ContentParent;

            _bloomIntensitySlider = PanelSlider.CreateNew(content);
            _bloomIntensitySlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.bloomIntensity"), 0f, 5f, false, 2, ValueDisplayMode.Raw));
            _bloomIntensitySlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeBloomIntensity(v);

            _bloomThresholdSlider = PanelSlider.CreateNew(content);
            _bloomThresholdSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.bloomThreshold"), 0.1f, 2f, false, 2, ValueDisplayMode.Raw));
            _bloomThresholdSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeBloomThreshold(v);

            _bloomScatterSlider = PanelSlider.CreateNew(content);
            _bloomScatterSlider.SetSliderSettings(PanelSlider.SliderSettings.Percentage(
                BasisLocalization.Get("camera.bloomScatter")));
            _bloomScatterSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.bloomScatter.description"));
            _bloomScatterSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeBloomScatter(v / 100f);

            BuildBloomTintControls(content);
        }

        private void BuildVignetteGroup(RectTransform parent)
        {
            _vignetteSection = PanelSectionToggle.CreateNewEntry(parent);
            _vignetteGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _vignetteSection, parent, BasisLocalization.Get("camera.section.vignette"), false);
            RectTransform content = _vignetteGroup.ContentParent;

            _vignetteSlider = PanelSlider.CreateNew(content);
            _vignetteSlider.SetSliderSettings(PanelSlider.SliderSettings.Percentage(BasisLocalization.Get("camera.vignette")));
            _vignetteSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeVignette(v / 100f);

            _vignetteSmoothnessSlider = PanelSlider.CreateNew(content);
            _vignetteSmoothnessSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.vignetteSmoothness"),
                BasisHandHeldCameraUI.MinVignetteSmoothness * 100f, BasisHandHeldCameraUI.MaxVignetteSmoothness * 100f,
                false, 0, ValueDisplayMode.Percentage));
            _vignetteSmoothnessSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.vignetteSmoothness.description"));
            _vignetteSmoothnessSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeVignetteSmoothness(v / 100f);

            BuildVignetteColourControls(content);
        }

        private void BuildGrainGroup(RectTransform parent)
        {
            _grainSection = PanelSectionToggle.CreateNewEntry(parent);
            _grainGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _grainSection, parent, BasisLocalization.Get("camera.section.filmGrain"), false);
            RectTransform content = _grainGroup.ContentParent;

            _filmGrainSlider = PanelSlider.CreateNew(content);
            _filmGrainSlider.SetSliderSettings(PanelSlider.SliderSettings.Percentage(BasisLocalization.Get("camera.filmGrain")));
            _filmGrainSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeFilmGrain(v / 100f);

            BuildGrainShapeControls(content);
        }

        private void BuildLensEffectsGroup(RectTransform parent)
        {
            _lensEffectsSection = PanelSectionToggle.CreateNewEntry(parent);
            _lensEffectsGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _lensEffectsSection, parent, BasisLocalization.Get("camera.section.lensEffects"), false);
            RectTransform content = _lensEffectsGroup.ContentParent;

            _chromaticSlider = PanelSlider.CreateNew(content);
            _chromaticSlider.SetSliderSettings(PanelSlider.SliderSettings.Percentage(BasisLocalization.Get("camera.chromaticAberration")));
            _chromaticSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeChromaticAberration(v / 100f);

            _lensDistortionSlider = PanelSlider.CreateNew(content);
            _lensDistortionSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.lensDistortion"), -100f, 100f, false, 0, ValueDisplayMode.Raw));
            _lensDistortionSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeLensDistortion(v / 100f);

            _lensDistortionScaleSlider = PanelSlider.CreateNew(content);
            _lensDistortionScaleSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.lensDistortionScale"),
                BasisHandHeldCameraUI.MinLensDistortionScale, 2f, false, 2, ValueDisplayMode.Raw));
            _lensDistortionScaleSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.lensDistortionScale.description"));
            _lensDistortionScaleSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeLensDistortionScale(v);

            _paniniSlider = PanelSlider.CreateNew(content);
            _paniniSlider.SetSliderSettings(PanelSlider.SliderSettings.Percentage(BasisLocalization.Get("camera.panini")));
            _paniniSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.panini.description"));
            _paniniSlider.OnValueChanged = v =>
            {
                _activeCamera?.HandHeld.ChangePaniniDistance(v / 100f);
                RefreshPaniniVisibility();
            };

            // Follows the projection the way the motion blur shape controls follow its strength:
            // there is nothing to crop back until something has been unwrapped.
            _paniniCropSlider = PanelSlider.CreateNew(content);
            _paniniCropSlider.SetSliderSettings(PanelSlider.SliderSettings.Percentage(BasisLocalization.Get("camera.paniniCrop")));
            _paniniCropSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.paniniCrop.description"));
            _paniniCropSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangePaniniCropToFit(v / 100f);
        }

        private void BuildMotionBlurGroup(RectTransform parent)
        {
            _motionBlurSection = PanelSectionToggle.CreateNewEntry(parent);
            _motionBlurGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _motionBlurSection, parent, BasisLocalization.Get("camera.section.motionBlur"), false);
            RectTransform content = _motionBlurGroup.ContentParent;

            _motionBlurSlider = PanelSlider.CreateNew(content);
            _motionBlurSlider.SetSliderSettings(PanelSlider.SliderSettings.Percentage(BasisLocalization.Get("camera.motionBlur")));
            _motionBlurSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.motionBlur.description"));
            _motionBlurSlider.OnValueChanged = v =>
            {
                _activeCamera?.HandHeld.ChangeMotionBlur(v / 100f);
                RefreshMotionBlurVisibility();
            };

            // The shape controls only mean anything once there is blur to shape, so they follow the
            // strength the way the depth of field controls follow its mode.
            _motionBlurClampSlider = PanelSlider.CreateNew(content);
            _motionBlurClampSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.motionBlurClamp"),
                BasisHandHeldCameraUI.MinMotionBlurClamp * 100f, BasisHandHeldCameraUI.MaxMotionBlurClamp * 100f,
                false, 1, ValueDisplayMode.Percentage));
            _motionBlurClampSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.motionBlurClamp.description"));
            _motionBlurClampSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeMotionBlurClamp(v / 100f);

            _motionBlurQualityDropdown = PanelDropdown.CreateNewEntry(content);
            _motionBlurQualityDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.motionBlurQuality"));
            _motionBlurQualityDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.motionBlurQuality.description"));
            _motionBlurQualityDropdown.AssignLocalizedEntries(
                new List<string>(MotionBlurQualityLabels), new List<string>(MotionBlurQualityKeys));
            _motionBlurQualityDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _motionBlurQualityDropdown == null) return;
                _activeCamera.HandHeld.SetMotionBlurQuality(_motionBlurQualityDropdown.Index);
            };

            _motionBlurModeDropdown = PanelDropdown.CreateNewEntry(content);
            _motionBlurModeDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.motionBlurMode"));
            _motionBlurModeDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.motionBlurMode.description"));
            _motionBlurModeDropdown.AssignLocalizedEntries(
                new List<string>(MotionBlurModeLabels), new List<string>(MotionBlurModeKeys));
            _motionBlurModeDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _motionBlurModeDropdown == null) return;
                _activeCamera.HandHeld.SetMotionBlurMode(_motionBlurModeDropdown.Index);
            };
        }

#if Basis_VOLUMETRIC_SUPPORTED
        private void BuildFogGroup(RectTransform parent)
        {
            _fogSection = PanelSectionToggle.CreateNewEntry(parent);
            _fogGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _fogSection, parent, BasisLocalization.Get("camera.section.fog"), false);
            RectTransform content = _fogGroup.ContentParent;

            _fogOverrideToggle = PanelToggle.CreateNewEntry(content);
            _fogOverrideToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.fog.override"));
            _fogOverrideToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.fog.override.description"));
            _fogOverrideToggle.OnValueChanged = v =>
            {
                _activeCamera?.SetOverrideVolumetricFog(v);
                RefreshVolumetricFogVisibility();
            };

            _fogSlider = PanelSlider.CreateNew(content);
            _fogSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("settings.graphics.fog.density"), 0f, 1f, false, 2, ValueDisplayMode.Raw));
            _fogSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.fog.density.tooltip"));
            _fogSlider.OnValueChanged = v => _activeCamera?.HandHeld.ChangeVolumetricDensity(v);
        }

#endif

        private void BuildGridGroup(RectTransform parent)
        {
            _gridSection = PanelSectionToggle.CreateNewEntry(parent);
            _gridGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _gridSection, parent, BasisLocalization.Get("camera.grid"), false);
            RectTransform content = _gridGroup.ContentParent;

            BuildViewfinderGridControls(content);
        }

        private void BuildFocusPeakingGroup(RectTransform parent)
        {
            _peakingSection = PanelSectionToggle.CreateNewEntry(parent);
            _peakingGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _peakingSection, parent, BasisLocalization.Get("camera.focusPeaking"), false);
            RectTransform content = _peakingGroup.ContentParent;

            BuildFocusPeakingControls(content);
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
                _outputSection, parent, BasisLocalization.Get("camera.whileStreaming"), false);
            RectTransform content = _outputGroup.ContentParent;

            _hideCameraToggle = PanelToggle.CreateNewEntry(content);
            _hideCameraToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.hideCamera"));
            _hideCameraToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.hideCamera.description"));
            _hideCameraToggle.OnValueChanged = v =>
            {
                _activeCamera?.SetCameraHidden(v);
                _lastCameraHidden = v;
            };

            _closeHidesToggle = PanelToggle.CreateNewEntry(content);
            _closeHidesToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.closeHidesInstead"));
            _closeHidesToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.closeHidesInstead.description"));
            _closeHidesToggle.OnValueChanged = v =>
            {
                if (_activeCamera != null) _activeCamera.HandHeld.CloseHidesCamera = v;
                _lastCloseHides = v;
            };

            _audioListenerToggle = PanelToggle.CreateNewEntry(content);
            _audioListenerToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.hearFromCamera"));
            _audioListenerToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.hearFromCamera.description"));
            _audioListenerToggle.OnValueChanged = v =>
            {
                _activeCamera?.SetAudioListener(v);
                // One listener exists, so enabling here disables it on every other camera —
                // drop the caches so the panel re-reads them for the current camera next tick.
                _lastAudioListener = null;
            };
        }

        private void BuildStreamGroup(RectTransform parent)
        {
            _streamPresets = BasisCameraStreamPresets.Available();
            List<string> presetKeys = new List<string>(_streamPresets.Count + 1);
            List<string> presetLabels = new List<string>(_streamPresets.Count + 1);
            List<string> presetTooltips = new List<string>(_streamPresets.Count + 1);
            for (int Index = 0; Index < _streamPresets.Count; Index++)
            {
                presetKeys.Add(_streamPresets[Index].Key);
                presetLabels.Add(BasisCameraStreamPresets.Label(_streamPresets[Index].Key));
                presetTooltips.Add(BasisCameraStreamPresets.Tooltip(_streamPresets[Index].Key));
            }
            presetKeys.Add(BasisCameraStreamPresets.CustomKey);
            presetLabels.Add(BasisCameraStreamPresets.Label(BasisCameraStreamPresets.CustomKey));
            presetTooltips.Add(BasisCameraStreamPresets.Tooltip(BasisCameraStreamPresets.CustomKey));

            _streamPresetDropdown = PanelDropdown.CreateNewEntry(parent);
            _streamPresetDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.streamPreset"));
            _streamPresetDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.streamPreset.description"));
            _streamPresetDropdown.AssignEntries(presetKeys, presetLabels, presetTooltips);
            _streamPresetDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _streamPresetDropdown == null) return;
                _lastStreamPresetKey = null;
                int index = _streamPresetDropdown.Index;
                if (index < 0 || index >= _streamPresets.Count) return;
                _activeCamera.ApplyStreamPreset(_streamPresets[index]);
                if (!_activeCamera.IsAnyVideoOutputActive) _activeCamera.StartLiveOutput();
                _lastVideoOutputActive = null;
                _lastWebStreamActive = null;
                RefreshVideoOutputState();
            };

            _videoOutputToggle = PanelToggle.CreateNewEntry(parent);
            _videoOutputToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.liveOutput"));
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

            // The monitor as an output, beside the stream that is the other way off the camera. Not
            // offered where there is no monitor: a standalone headset's window is the headset.
            if (BasisHandHeldCamera.IsDirectToScreenSupported)
            {
                _directToScreenToggle = PanelToggle.CreateNewEntry(parent);
                _directToScreenToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.directToScreen"));
                _directToScreenToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.directToScreen.description"));
                _directToScreenToggle.OnValueChanged = v =>
                {
                    if (_activeCamera == null) return;
                    _activeCamera.SetDirectToScreen(v);
                    _lastDirectToScreen = v;
                    // The line under the toggle depends on the device mode and the body, not just
                    // the click — and the click may have taken the window off another camera.
                    RefreshDirectToScreenState();
                };
            }

            _streamSection = PanelSectionToggle.CreateNewEntry(parent);
            _streamGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _streamSection, parent, BasisLocalization.Get("camera.stream"), false);
            RectTransform content = _streamGroup.ContentParent;

            // No platform gate here: the web stream is pure sockets, so there is always at
            // least one transport to choose from, even where no shared-texture backend exists.
            _transports = BasisHandHeldCamera.AvailableVideoTransports();
            List<string> transportLabels = new List<string>();
            for (int Index = 0; Index < _transports.Count; Index++)
            {
                transportLabels.Add(BasisHandHeldCamera.GetVideoTransportName(_transports[Index]));
            }

            _transportDropdown = PanelDropdown.CreateNewEntry(content);
            _transportDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.transport"));
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

            // Resolution and frame rate apply to every transport, so they sit above the
            // platform gate — otherwise a machine with no shared-texture backend would get the
            // web stream with no controls at all.
            _videoResolutionDropdown = PanelDropdown.CreateNewEntry(content);
            _videoResolutionDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.streamResolution"));
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
                BasisLocalization.Get("camera.streamFrameRate"), 15f, 120f, true, 0, ValueDisplayMode.Hz));
            _videoFrameRateSlider.OnValueChanged = v => _activeCamera?.SetVideoOutputFrameRate(v);

            // Web-only settings. Shown or hidden by RefreshTransportSelection so the group
            // only ever offers what the chosen transport actually uses.
            _webQualitySlider = PanelSlider.CreateNew(content);
            _webQualitySlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.streamQuality"), 10f, 95f, true, 0, ValueDisplayMode.Raw));
            _webQualitySlider.OnValueChanged = v => _activeCamera?.SetWebStreamQuality((int)v);

            _webPortField = PanelTextField.CreateNewEntry(content);
            _webPortField.Descriptor.SetTitle(BasisLocalization.Get("camera.streamPort"));
            _webPortField.SetValidator(text =>
            {
                if (int.TryParse(text, out int port) && port >= WebPortMin && port <= WebPortMax)
                {
                    return null;
                }
                return BasisLocalization.Get("ui.validation.port", WebPortMin, WebPortMax);
            });
            _webPortField.OnValueChanged = v =>
            {
                if (_activeCamera == null || !int.TryParse(v, out int port)) return;
                _activeCamera.SetWebStreamPort(port);
                RefreshVideoOutputState();
            };

            _openStreamButton = PanelButton.CreateNew(content);
            _openStreamButton.Descriptor.SetTitle(BasisLocalization.Get("camera.openInBrowser"));
            _openStreamButton.OnClicked += () => _activeCamera?.OpenWebStreamInBrowser();

            if (!BasisHandHeldCamera.IsVideoOutputSupported) return;

            _videoSenderNameField = PanelTextField.CreateNewEntry(content);
            _videoSenderNameField.Descriptor.SetTitle(BasisLocalization.Get("camera.senderName"));
            _videoSenderNameField.SetRequired(BasisLocalization.Get("ui.validation.requiredNamed",
                BasisLocalization.Get("camera.senderName")));
            _videoSenderNameField.OnValueChanged = v =>
            {
                if (_activeCamera == null || string.IsNullOrWhiteSpace(v)) return;
                _activeCamera.VideoOutputSettings.SenderName = v;
            };
        }

        /// <summary>
        /// Who the camera films and where on them it reads. Held apart from the modifiers because
        /// every one of them resolves the same subject, so these keep meaning something whichever
        /// modifier is fitted - and depth auto-focus reads the aim point even with none.
        /// </summary>
        private void BuildSubjectGroup(RectTransform parent)
        {
            _followSection = PanelSectionToggle.CreateNewEntry(parent);
            _followGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _followSection, parent, BasisLocalization.Get("camera.subject"), false);
            RectTransform content = _followGroup.ContentParent;

            _subjectDropdown = PanelDropdown.CreateNewEntry(content);
            _subjectDropdown.Descriptor.SetTitle(BasisLocalization.Get(BasisCameraModifiers.SubjectSlotKey));
            _subjectDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.modifier.subject.description"));
            _subjectDropdown.AssignLocalizedEntries(
                new List<string>(SubjectLabelKeys), new List<string>(SubjectLabelKeys),
                DescriptionKeys(SubjectLabelKeys));
            _subjectDropdown.OnValueChanged = _ =>
            {
                int index = _subjectDropdown != null ? _subjectDropdown.Index : -1;
                if (_activeCamera == null || index < 0 || index >= BasisCameraModifiers.SubjectModifiers.Length) return;

                _activeCamera.SetSubjectModifier(BasisCameraModifiers.SubjectModifiers[index]);
                RefreshDoFModeVisibility();
                RefreshModifierVisibility();
            };

            RectTransform teleportRow = PanelElementDescriptor.BuildActionRow(content, "CameraTeleportRow");
            PanelButton teleportButton = PanelButton.CreateNew(teleportRow);
            teleportButton.Descriptor.SetTitle(BasisLocalization.Get("camera.teleportToMe"));
            teleportButton.Descriptor.SetTooltip(BasisLocalization.Get("camera.teleportToMe.description"));
            teleportButton.OnClicked += () => _activeCamera?.TeleportInFrontOfPlayer();

            _followMarkerDropdown = PanelDropdown.CreateNewEntry(content);
            _followMarkerDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.detachedMarker"));
            _followMarkerDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.detachedMarker.description"));
            _followMarkerDropdown.AssignLocalizedEntries(
                new List<string>(DetachedMarkerKeys), new List<string>(DetachedMarkerKeys));
            _followMarkerDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _followMarkerDropdown == null) return;
                int index = _followMarkerDropdown.Index;
                if (index >= 0) _activeCamera.SetDetachedMarker((BasisCameraDetachedMarker)index);
                RefreshDetachedMarkerVisibility();
            };

            _markerScaleSlider = PanelSlider.CreateNew(content);
            _markerScaleSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.detachedMarker.size"),
                BasisHandHeldCamera.MinDetachedMarkerScale * 100f,
                BasisHandHeldCamera.MaxDetachedMarkerScale * 100f,
                false, 0, ValueDisplayMode.Percentage));
            _markerScaleSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.detachedMarker.size.description"));
            // The same number the two-hand pinch on the puck writes, which is why it is a share of
            // the marker's natural size rather than a distance: this is the desktop way to the
            // control VR already has in hand.
            _markerScaleSlider.OnValueChanged = v => _activeCamera?.SetDetachedMarkerScale(v / 100f);

            _puckPreviewToggle = PanelToggle.CreateNewEntry(content);
            _puckPreviewToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.puckPreview"));
            _puckPreviewToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.puckPreview.description"));
            _puckPreviewToggle.OnValueChanged = v => _activeCamera?.SetPuckLookAtPreview(v);

            // With the marker, because it is the marker's grab handle that carries the roll: take
            // hold of the puck with this on and twisting your wrist tips the shot with it.
            _rollToggle = PanelToggle.CreateNewEntry(content);
            _rollToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.rollControl"));
            _rollToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.rollControl.description"));
            _rollToggle.OnValueChanged = v => _activeCamera?.SetCameraRollEnabled(v);

            _followTargetDropdown = PanelDropdown.CreateNewEntry(content);
            _followTargetDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.followTarget"));
            _followTargetDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.followTarget.description"));
            _followTargetDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _followTargetDropdown == null) return;
                int index = _followTargetDropdown.Index;
                if (index == 0)
                {
                    _activeCamera.ClearFollowTargetPlayer();
                }
                else if (index > 0 && index <= _followTargetIds.Count)
                {
                    _activeCamera.SetFollowTargetPlayer(_followTargetIds[index - 1]);
                }
            };

            _followPlayspaceToggle = PanelToggle.CreateNewEntry(content);
            _followPlayspaceToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.followPlayspace"));
            _followPlayspaceToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.followPlayspace.description"));
            _followPlayspaceToggle.OnValueChanged = v =>
            {
                if (_activeCamera != null) _activeCamera.subjectSettings.anchorToBody = v;
            };

            _aimPointDropdown = PanelDropdown.CreateNewEntry(content);
            _aimPointDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.aimPoint"));
            _aimPointDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.aimPoint.description"));
            _aimPointDropdown.AssignLocalizedEntries(new List<string>(AimPointKeys), new List<string>(AimPointKeys));
            _aimPointDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _aimPointDropdown == null) return;
                int index = _aimPointDropdown.Index;
                if (index < 0 || index >= BasisCameraModifiers.AimPoints.Length) return;
                _activeCamera.subjectSettings.aimPoint = BasisCameraModifiers.AimPoints[index];
                RefreshModifierVisibility();
            };

            _followLookAtHeightSlider = PanelSlider.CreateNew(content);
            _followLookAtHeightSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.lookAtHeightY"), -3f, 3f, false, 2, ValueDisplayMode.Meters));
            _followLookAtHeightSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.lookAtHeightY.description"));
            _followLookAtHeightSlider.OnValueChanged = v =>
            {
                if (_activeCamera != null) _activeCamera.subjectSettings.aimHeightOffset = v;
            };

            _subjectRadiusSlider = PanelSlider.CreateNew(content);
            _subjectRadiusSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.subjectRadius"), 0.1f, 2f, false, 2, ValueDisplayMode.Meters));
            _subjectRadiusSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.subjectRadius.description"));
            _subjectRadiusSlider.OnValueChanged = v =>
            {
                if (_activeCamera != null) _activeCamera.subjectSettings.framingRadius = v;
            };

            _targetGroupToggle = PanelToggle.CreateNewEntry(content);
            _targetGroupToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.groupIncludesMe"));
            _targetGroupToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.groupIncludesMe.description"));
            _targetGroupToggle.OnValueChanged = v =>
            {
                if (_activeCamera == null) return;
                _activeCamera.subjectSettings.groupIncludesLocal = v;
            };

            _groupRefreshRow = PanelElementDescriptor.BuildActionRow(content, "CameraGroupRow");

            PanelButton fillGroup = PanelButton.CreateNew(_groupRefreshRow);
            fillGroup.Descriptor.SetTitle(BasisLocalization.Get("camera.groupFill"));
            fillGroup.Descriptor.SetTooltip(BasisLocalization.Get("camera.groupFill.description"));
            fillGroup.OnClicked += () => _activeCamera?.RebuildTargetGroup();

            PanelButton clearGroup = PanelButton.CreateNew(_groupRefreshRow);
            clearGroup.Descriptor.SetTitle(BasisLocalization.Get("camera.groupClear"));
            clearGroup.OnClicked += () => _activeCamera?.TargetGroup.Clear();

            // Its own row, above the two that only appear once Fixed Point is already chosen:
            // this is how somebody gets to a fixed point rather than a setting on one. A camera out
            // on a dolly is filming from somewhere the operator cannot reach, and this is the only
            // control here that answers "film that" without them naming what "that" is first.
            RectTransform lookAtRow = PanelElementDescriptor.BuildActionRow(content, "CameraLookAtPickRow");

            _lookAtPickButton = PanelButton.CreateNew(lookAtRow);
            _lookAtPickButton.Descriptor.SetTitle(BasisLocalization.Get("camera.lookAtPick"));
            _lookAtPickButton.Descriptor.SetTooltip(BasisLocalization.Get("camera.lookAtPick.description"));
            _lookAtPickButton.OnClicked += () =>
            {
                _activeCamera?.ToggleLookAtPointer();
                RefreshLookAtPointer();
            };

            _fixedPointRow = PanelElementDescriptor.BuildActionRow(content, "CameraFixedPointRow");

            PanelButton pointAtCamera = PanelButton.CreateNew(_fixedPointRow);
            pointAtCamera.Descriptor.SetTitle(BasisLocalization.Get("camera.fixedPointHere"));
            pointAtCamera.Descriptor.SetTooltip(BasisLocalization.Get("camera.fixedPointHere.description"));
            pointAtCamera.OnClicked += () => _activeCamera?.SetFixedPointToCamera();

            PanelButton pointAtPlayer = PanelButton.CreateNew(_fixedPointRow);
            pointAtPlayer.Descriptor.SetTitle(BasisLocalization.Get("camera.fixedPointAtMe"));
            pointAtPlayer.Descriptor.SetTooltip(BasisLocalization.Get("camera.fixedPointAtMe.description"));
            pointAtPlayer.OnClicked += () => _activeCamera?.SetFixedPointToPlayer();
        }

        
        private void BuildHandlingGroup(RectTransform parent)
        {
            _handlingSection = PanelSectionToggle.CreateNewEntry(parent);
            _handlingGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _handlingSection, parent, BasisLocalization.Get("camera.tab.handling"), false);
            RectTransform content = _handlingGroup.ContentParent;

            // Sits with Auto Level and VR Stabilization because those two only do anything while
            // the camera is flying, and this is the switch that puts it there. Until now flight
            // was reachable only by holding middle click or clicking the VR thumbstick, neither of
            // which is written down anywhere the user can find.
            _flyToggle = PanelToggle.CreateNewEntry(content);
            _flyToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.flyMode"));
            _flyToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.flyMode.description"));
            _flyToggle.OnValueChanged = v => _activeCamera?.SetFlyModeEnabled(v);

            // Sits under the switch it copies: flight is most often wanted while the camera is out
            // in the world and this panel is not, and the hotbar is reachable from anywhere.
            _flyOnMenuToggle = PanelToggle.CreateNewEntry(content);
            _flyOnMenuToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.flyOnMenu"));
            _flyOnMenuToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.flyOnMenu.description"));
            _flyOnMenuToggle.OnValueChanged = v => _activeCamera?.SetShowFlyOnMainMenu(v);

            _flySpeedSlider = PanelSlider.CreateNew(content);
            _flySpeedSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.flySpeed"),
                BasisHandHeldCameraInteractable.MinFlySpeed,
                BasisHandHeldCameraInteractable.MaxFlySpeed,
                false, 2, ValueDisplayMode.Raw));
            _flySpeedSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.flySpeed.description"));
            _flySpeedSlider.OnValueChanged = v => _activeCamera?.SetFlySpeed(v);

            _flyClimbSpeedSlider = PanelSlider.CreateNew(content);
            _flyClimbSpeedSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.flyClimbSpeed"),
                BasisHandHeldCameraInteractable.MinFlyClimbSpeed,
                BasisHandHeldCameraInteractable.MaxFlyClimbSpeed,
                false, 2, ValueDisplayMode.Raw));
            _flyClimbSpeedSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.flyClimbSpeed.description"));
            _flyClimbSpeedSlider.OnValueChanged = v => _activeCamera?.SetFlyClimbSpeed(v);

            _flyFastMultiplierSlider = PanelSlider.CreateNew(content);
            _flyFastMultiplierSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.flyFastMultiplier"),
                BasisHandHeldCameraInteractable.MinFlyFastMultiplier,
                BasisHandHeldCameraInteractable.MaxFlyFastMultiplier,
                false, 1, ValueDisplayMode.Raw));
            _flyFastMultiplierSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.flyFastMultiplier.description"));
            _flyFastMultiplierSlider.OnValueChanged = v => _activeCamera?.SetFlyFastMultiplier(v);

            _flyTurnSpeedSlider = PanelSlider.CreateNew(content);
            _flyTurnSpeedSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.flyTurnSpeed"),
                BasisHandHeldCameraInteractable.MinFlyTurnSpeed,
                BasisHandHeldCameraInteractable.MaxFlyTurnSpeed,
                true, 0, ValueDisplayMode.Raw));
            _flyTurnSpeedSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.flyTurnSpeed.description"));
            _flyTurnSpeedSlider.OnValueChanged = v => _activeCamera?.SetFlyTurnSpeed(v);

            _flyMouseSensitivitySlider = PanelSlider.CreateNew(content);
            _flyMouseSensitivitySlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.flyMouseSensitivity"),
                BasisHandHeldCameraInteractable.MinFlyMouseSensitivity,
                BasisHandHeldCameraInteractable.MaxFlyMouseSensitivity,
                false, 2, ValueDisplayMode.Raw));
            _flyMouseSensitivitySlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.flyMouseSensitivity.description"));
            _flyMouseSensitivitySlider.OnValueChanged = v => _activeCamera?.SetFlyMouseSensitivity(v);

            _flyMomentumToggle = PanelToggle.CreateNewEntry(content);
            _flyMomentumToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.flyMomentum"));
            _flyMomentumToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.flyMomentum.description"));
            _flyMomentumToggle.OnValueChanged = v =>
            {
                if (_activeCamera != null) _activeCamera.useMomentum = v;
            };

            // VR-only: desktop's WASD has always moved relative to the full look direction, so this
            // is purely the VR stick catching up to that. Hidden on desktop by RefreshPlatformVisibility.
            _movementFollowsPitchToggle = PanelToggle.CreateNewEntry(content);
            _movementFollowsPitchToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.flyMovementFollowsPitch"));
            _movementFollowsPitchToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.flyMovementFollowsPitch.description"));
            _movementFollowsPitchToggle.OnValueChanged = v => _activeCamera?.SetVRFlyMovementFollowsPitch(v);

            // VR-only: physically move/turn a hand to fly instead of the sticks. Hidden on desktop
            // by RefreshPlatformVisibility, same as the stabilizer toggle just below.
            _leftHandFlyToggle = PanelToggle.CreateNewEntry(content);
            _leftHandFlyToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.leftHandFly"));
            _leftHandFlyToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.leftHandFly.description"));
            _leftHandFlyToggle.OnValueChanged = v =>
            {
                _activeCamera?.SetVRLeftHandFlyEnabled(v);
                RefreshHandFlyVisibility();
            };

            _rightHandFlyRotateToggle = PanelToggle.CreateNewEntry(content);
            _rightHandFlyRotateToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.rightHandFlyRotate"));
            _rightHandFlyRotateToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.rightHandFlyRotate.description"));
            _rightHandFlyRotateToggle.OnValueChanged = v =>
            {
                _activeCamera?.SetVRRightHandFlyRotateEnabled(v);
                RefreshHandFlyVisibility();
            };

            // Move (Left Hand Fly only) and turn (either hand) each get their own deadzone/reach
            // pair plus a gain, matching the deadzone→reach curve in HandFlyRotationFraction /
            // HandleHandFlyMovementInput. Visibility follows the toggle(s) that actually use them,
            // via RefreshHandFlyVisibility below.
            _handFlyMoveDeadzoneSlider = PanelSlider.CreateNew(content);
            _handFlyMoveDeadzoneSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.handFlyMoveDeadzone"),
                BasisHandHeldCameraInteractable.MinHandFlyMoveDeadzone,
                BasisHandHeldCameraInteractable.MaxHandFlyMoveDeadzone,
                false, 3, ValueDisplayMode.Raw));
            _handFlyMoveDeadzoneSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.handFlyMoveDeadzone.description"));
            _handFlyMoveDeadzoneSlider.OnValueChanged = v => _activeCamera?.SetHandFlyMoveDeadzone(v);

            _handFlyMoveReachSlider = PanelSlider.CreateNew(content);
            _handFlyMoveReachSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.handFlyMoveReach"),
                BasisHandHeldCameraInteractable.MinHandFlyMoveReach,
                BasisHandHeldCameraInteractable.MaxHandFlyMoveReach,
                false, 2, ValueDisplayMode.Raw));
            _handFlyMoveReachSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.handFlyMoveReach.description"));
            _handFlyMoveReachSlider.OnValueChanged = v => _activeCamera?.SetHandFlyMoveReach(v);

            _handFlyMoveSensitivitySlider = PanelSlider.CreateNew(content);
            _handFlyMoveSensitivitySlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.handFlyMoveSensitivity"),
                BasisHandHeldCameraInteractable.MinHandFlySensitivity,
                BasisHandHeldCameraInteractable.MaxHandFlySensitivity,
                false, 2, ValueDisplayMode.Raw));
            _handFlyMoveSensitivitySlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.handFlyMoveSensitivity.description"));
            _handFlyMoveSensitivitySlider.OnValueChanged = v => _activeCamera?.SetHandFlyMoveSensitivity(v);

            _handFlyTurnDeadzoneSlider = PanelSlider.CreateNew(content);
            _handFlyTurnDeadzoneSlider.SetSliderSettings(PanelSlider.SliderSettings.Degrees(
                BasisLocalization.Get("camera.handFlyTurnDeadzone"),
                BasisHandHeldCameraInteractable.MinHandFlyTurnDeadzone,
                BasisHandHeldCameraInteractable.MaxHandFlyTurnDeadzone,
                false, 1));
            _handFlyTurnDeadzoneSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.handFlyTurnDeadzone.description"));
            _handFlyTurnDeadzoneSlider.OnValueChanged = v => _activeCamera?.SetHandFlyTurnDeadzone(v);

            _handFlyTurnReachSlider = PanelSlider.CreateNew(content);
            _handFlyTurnReachSlider.SetSliderSettings(PanelSlider.SliderSettings.Degrees(
                BasisLocalization.Get("camera.handFlyTurnReach"),
                BasisHandHeldCameraInteractable.MinHandFlyTurnReach,
                BasisHandHeldCameraInteractable.MaxHandFlyTurnReach,
                false, 1));
            _handFlyTurnReachSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.handFlyTurnReach.description"));
            _handFlyTurnReachSlider.OnValueChanged = v => _activeCamera?.SetHandFlyTurnReach(v);

            _handFlyTurnSensitivitySlider = PanelSlider.CreateNew(content);
            _handFlyTurnSensitivitySlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.handFlyTurnSensitivity"),
                BasisHandHeldCameraInteractable.MinHandFlySensitivity,
                BasisHandHeldCameraInteractable.MaxHandFlySensitivity,
                false, 2, ValueDisplayMode.Raw));
            _handFlyTurnSensitivitySlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.handFlyTurnSensitivity.description"));
            _handFlyTurnSensitivitySlider.OnValueChanged = v => _activeCamera?.SetHandFlyTurnSensitivity(v);

            _autoLevelToggle = PanelToggle.CreateNewEntry(content);
            _autoLevelToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.autoLevel"));
            _autoLevelToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.autoLevel.description"));
            _autoLevelToggle.OnValueChanged = v =>
            {
                if (_activeCamera != null) _activeCamera.useAutoLeveling = v;
            };

            _vrStabToggle = PanelToggle.CreateNewEntry(content);
            _vrStabToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.vrStabilization"));
            _vrStabToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.vrStabilization.description"));
            _vrStabToggle.OnValueChanged = v =>
            {
                if (_activeCamera != null) _activeCamera.useVRHandheldSmoothing = v;
                RefreshStabilizationVisibility();
            };

            _vrStabPositionSlider = PanelSlider.CreateNew(content);
            _vrStabPositionSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.vrStabilization.position"),
                BasisHandHeldCameraInteractable.MinStabilizationDamping,
                BasisHandHeldCameraInteractable.MaxStabilizationDamping,
                false, 2, ValueDisplayMode.Raw));
            _vrStabPositionSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.vrStabilization.position.description"));
            _vrStabPositionSlider.OnValueChanged = v => _activeCamera?.SetVRStabilizationPositionDamping(v);

            // Three axes rather than one turn number: the shot a handheld camera is usually after is
            // one that swings with you but does not tilt or tip while it does.
            _vrStabYawSlider = PanelSlider.CreateNew(content);
            _vrStabYawSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.vrStabilization.yaw"),
                BasisHandHeldCameraInteractable.MinStabilizationDamping,
                BasisHandHeldCameraInteractable.MaxStabilizationDamping,
                false, 2, ValueDisplayMode.Raw));
            _vrStabYawSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.vrStabilization.yaw.description"));
            _vrStabYawSlider.OnValueChanged = v => _activeCamera?.SetVRStabilizationYawDamping(v);

            _vrStabPitchSlider = PanelSlider.CreateNew(content);
            _vrStabPitchSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.vrStabilization.pitch"),
                BasisHandHeldCameraInteractable.MinStabilizationDamping,
                BasisHandHeldCameraInteractable.MaxStabilizationDamping,
                false, 2, ValueDisplayMode.Raw));
            _vrStabPitchSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.vrStabilization.pitch.description"));
            _vrStabPitchSlider.OnValueChanged = v => _activeCamera?.SetVRStabilizationPitchDamping(v);

            _vrStabRollSlider = PanelSlider.CreateNew(content);
            _vrStabRollSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.vrStabilization.roll"),
                BasisHandHeldCameraInteractable.MinStabilizationDamping,
                BasisHandHeldCameraInteractable.MaxStabilizationDamping,
                false, 2, ValueDisplayMode.Raw));
            _vrStabRollSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.vrStabilization.roll.description"));
            _vrStabRollSlider.OnValueChanged = v => _activeCamera?.SetVRStabilizationRollDamping(v);

            // Not gated on the VR toggle above and not hidden on desktop: it scales whichever of the
            // two stabilizers is running, and the smooth drag is one the desktop camera has too.
            _zoomStabToggle = PanelToggle.CreateNewEntry(content);
            _zoomStabToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.zoomStabilization"));
            _zoomStabToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.zoomStabilization.description"));
            _zoomStabToggle.OnValueChanged = v =>
            {
                if (_activeCamera != null) _activeCamera.zoomStabilization = v;
                RefreshStabilizationVisibility();
            };

            _zoomStabResponseSlider = PanelSlider.CreateNew(content);
            _zoomStabResponseSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.zoomStabilization.response"),
                BasisHandHeldCameraInteractable.MinZoomStabilizationResponse,
                BasisHandHeldCameraInteractable.MaxZoomStabilizationResponse,
                false, 2, ValueDisplayMode.Raw));
            _zoomStabResponseSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.zoomStabilization.response.description"));
            _zoomStabResponseSlider.OnValueChanged = v => _activeCamera?.SetZoomStabilizationResponse(v);

            _zoomStabMinSlider = PanelSlider.CreateNew(content);
            _zoomStabMinSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.zoomStabilization.min"),
                BasisHandHeldCameraInteractable.MinZoomStabilizationScale, 1f,
                false, 2, ValueDisplayMode.Raw));
            _zoomStabMinSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.zoomStabilization.min.description"));
            _zoomStabMinSlider.OnValueChanged = v => _activeCamera?.SetZoomStabilizationMinScale(v);

            _zoomStabMaxSlider = PanelSlider.CreateNew(content);
            _zoomStabMaxSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.zoomStabilization.max"),
                1f, BasisHandHeldCameraInteractable.MaxZoomStabilizationScale,
                false, 1, ValueDisplayMode.Raw));
            _zoomStabMaxSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.zoomStabilization.max.description"));
            _zoomStabMaxSlider.OnValueChanged = v => _activeCamera?.SetZoomStabilizationMaxScale(v);

            _smoothDragToggle = PanelToggle.CreateNewEntry(content);
            _smoothDragToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.smoothDrag"));
            _smoothDragToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.smoothDrag.description"));
            _smoothDragToggle.OnValueChanged = v =>
            {
                if (_activeCamera != null) _activeCamera.useSmoothDrag = v;
                RefreshSmoothDragVisibility();
            };

            _smoothDragPositionSlider = PanelSlider.CreateNew(content);
            _smoothDragPositionSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.smoothDrag.position"),
                BasisHandHeldCameraInteractable.MinSmoothDragDamping,
                BasisHandHeldCameraInteractable.MaxSmoothDragDamping,
                false, 2, ValueDisplayMode.Raw));
            _smoothDragPositionSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.smoothDrag.position.description"));
            _smoothDragPositionSlider.OnValueChanged = v => _activeCamera?.SetSmoothDragPositionDamping(v);

            _smoothDragRotationSlider = PanelSlider.CreateNew(content);
            _smoothDragRotationSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.smoothDrag.rotation"),
                BasisHandHeldCameraInteractable.MinSmoothDragDamping,
                BasisHandHeldCameraInteractable.MaxSmoothDragDamping,
                false, 2, ValueDisplayMode.Raw));
            _smoothDragRotationSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.smoothDrag.rotation.description"));
            _smoothDragRotationSlider.OnValueChanged = v => _activeCamera?.SetSmoothDragRotationDamping(v);

            _smoothDragLeashSlider = PanelSlider.CreateNew(content);
            _smoothDragLeashSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.smoothDrag.leash"),
                BasisHandHeldCameraInteractable.MinSmoothDragDistance,
                BasisHandHeldCameraInteractable.MaxSmoothDragDistance,
                false, 2, ValueDisplayMode.Meters));
            _smoothDragLeashSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.smoothDrag.leash.description"));
            _smoothDragLeashSlider.OnValueChanged = v => _activeCamera?.SetSmoothDragMaxDistance(v);

            _resizeToggle = PanelToggle.CreateNewEntry(content);
            _resizeToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.resize"));
            _resizeToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.resize.description"));
            _resizeToggle.OnValueChanged = v => _activeCamera?.SetResizeWithGesture(v);

            RefreshPlatformVisibility();
        }

        private void BuildActionsGroup(RectTransform parent)
        {
            _actionSection = PanelSectionToggle.CreateNewEntry(parent);
            _actionGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _actionSection, parent, BasisLocalization.Get("camera.photoHandling"), false);
            RectTransform content = _actionGroup.ContentParent;

            _resolutionDropdown = PanelDropdown.CreateNewEntry(content);
            _resolutionDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.photoResolution"));
            _resolutionDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _resolutionDropdown == null) return;
                // Through the UI, not straight to the camera: the UI owns the preset index that a
                // save records and that the prop's sprites and cycle button read.
                int index = _resolutionDropdown.Index;
                if (index >= 0) _activeCamera.HandHeld.SetResolutionIndex(index);
            };

            _formatDropdown = PanelDropdown.CreateNewEntry(content);
            _formatDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.photoFormat"));
            _formatDropdown.AssignEntries(new List<string> { "PNG", "EXR" });
            _formatDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _formatDropdown == null) return;
                int index = _formatDropdown.Index;
                if (index >= 0) _activeCamera.HandHeld.SetFormat(index);
            };

            _capture360Toggle = PanelToggle.CreateNewEntry(content);
            _capture360Toggle.Descriptor.SetTitle(BasisLocalization.Get("camera.n360Capture"));
            _capture360Toggle.OnValueChanged = v => _activeCamera?.HandHeld.SetCapture360State(v);

            _printPhotoToggle = PanelToggle.CreateNewEntry(content);
            _printPhotoToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.printPhoto"));
            _printPhotoToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.printPhoto.description"));
            _printPhotoToggle.OnValueChanged = v =>
            {
                if (_activeCamera != null) _activeCamera.printPhotoEnabled = v;
            };

            _selfieToggle = PanelToggle.CreateNewEntry(content);
            _selfieToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.selfie"));
            _selfieToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.selfie.description"));
            _selfieToggle.OnValueChanged = v =>
            {
                if (_activeCamera == null) return;
                if (_activeCamera.HandHeld.IsSelfieMode != v) _activeCamera.HandHeld.ToggleSelfie();
            };

            // Desktop only: there is a real file browser to open, and the shot lands in a
            // browsable Pictures folder rather than the app's sandboxed data path.
            if (BasisHandHeldCamera.CanOpenPhotosFolder)
            {
                // Names the shot that just landed and gives it a one-click way back. Nothing else
                // tells the shooter where a photo went, and the shutter is on the prop, so the
                // answer has to survive until they come looking for it here.
                _photoStatus = BuildRecordingStatusCard(content, "camera.photo.status", "camera.photo.status.idle");

                RectTransform folderRow = PanelElementDescriptor.BuildActionRow(content, "CameraPhotosRow");

                _revealPhotoButton = PanelButton.CreateNew(folderRow);
                _revealPhotoButton.Descriptor.SetTitle(BasisLocalization.Get("camera.openSavedPhoto"));
                _revealPhotoButton.OnClicked += () => _activeCamera?.RevealLastPhoto();

                PanelButton openFolderButton = PanelButton.CreateNew(folderRow);
                openFolderButton.Descriptor.SetTitle(BasisLocalization.Get("camera.openPhotosFolder"));
                openFolderButton.OnClicked += () => BasisHandHeldCamera.OpenPhotosFolder();
            }
        }

        /// <summary>
        /// The marker's size follows the marker: with the marker off there is nothing on screen for
        /// it to size, and a slider that visibly does nothing reads as a broken one.
        /// </summary>
        private void RefreshDetachedMarkerVisibility()
        {
            bool marked = _activeCamera != null && _activeCamera.detachedMarker != BasisCameraDetachedMarker.Off;

            _markerScaleSlider?.gameObject.SetActive(marked);
            // No marker is no grab handle, and a desktop grab is made by a head that cannot roll —
            // so the switch is shown where there is actually a wrist to turn the grip with.
            _rollToggle?.gameObject.SetActive(marked && !BasisDeviceManagement.IsUserInDesktop());
            RefreshSearch();
            ForceLayoutRebuild(_followGroup);
        }

        /// <summary>
        /// The three numbers that shape the trail follow the toggle that runs it, the way the auto
        /// brightness meter's controls follow theirs.
        /// </summary>
        private void RefreshSmoothDragVisibility()
        {
            bool dragging = _activeCamera != null && _activeCamera.useSmoothDrag;

            _smoothDragPositionSlider?.gameObject.SetActive(dragging);
            _smoothDragRotationSlider?.gameObject.SetActive(dragging);
            _smoothDragLeashSlider?.gameObject.SetActive(dragging);
            RefreshSearch();
            ForceLayoutRebuild(_handlingGroup);
        }

        /// <summary>
        /// The stabilizer's two damping numbers follow its own toggle, and the zoom link's three
        /// follow theirs. The damping pair is VR-only because the stabilizer it shapes is; the zoom
        /// link is not, since it also scales the smooth drag the desktop camera can run.
        /// </summary>
        private void RefreshStabilizationVisibility()
        {
            bool stabilizing = !BasisDeviceManagement.IsUserInDesktop() &&
                _activeCamera != null && _activeCamera.useVRHandheldSmoothing;

            _vrStabPositionSlider?.gameObject.SetActive(stabilizing);
            _vrStabYawSlider?.gameObject.SetActive(stabilizing);
            _vrStabPitchSlider?.gameObject.SetActive(stabilizing);
            _vrStabRollSlider?.gameObject.SetActive(stabilizing);

            bool zoomLinked = _activeCamera != null && _activeCamera.zoomStabilization;

            _zoomStabResponseSlider?.gameObject.SetActive(zoomLinked);
            _zoomStabMinSlider?.gameObject.SetActive(zoomLinked);
            _zoomStabMaxSlider?.gameObject.SetActive(zoomLinked);
            RefreshSearch();
            ForceLayoutRebuild(_handlingGroup);
        }

        /// <summary>
        /// The move sliders only mean anything with Left Hand Fly armed; the turn sliders are
        /// shared by both hands, so they follow either one. Desktop hides all six regardless — hand
        /// tracking is a VR concept — via the <c>!IsUserInDesktop()</c> term below rather than
        /// leaving that to <see cref="RefreshPlatformVisibility"/> alone, so a toggle flipped while
        /// already on desktop (from a settings file, say) can't leave them shown.
        /// </summary>
        private void RefreshHandFlyVisibility()
        {
            bool vr = !BasisDeviceManagement.IsUserInDesktop() && _activeCamera != null;
            bool moveActive = vr && _activeCamera.vrLeftHandFlyEnabled;
            bool turnActive = vr && (_activeCamera.vrLeftHandFlyEnabled || _activeCamera.vrRightHandFlyRotateEnabled);

            _handFlyMoveDeadzoneSlider?.gameObject.SetActive(moveActive);
            _handFlyMoveReachSlider?.gameObject.SetActive(moveActive);
            _handFlyMoveSensitivitySlider?.gameObject.SetActive(moveActive);
            _handFlyTurnDeadzoneSlider?.gameObject.SetActive(turnActive);
            _handFlyTurnReachSlider?.gameObject.SetActive(turnActive);
            _handFlyTurnSensitivitySlider?.gameObject.SetActive(turnActive);
            RefreshSearch();
            ForceLayoutRebuild(_handlingGroup);
        }

        private void RefreshPlatformVisibility()
        {
            bool desktop = BasisDeviceManagement.IsUserInDesktop();

            _flyMouseSensitivitySlider?.gameObject.SetActive(desktop);
            _flyClimbSpeedSlider?.gameObject.SetActive(!desktop);
            _flyTurnSpeedSlider?.gameObject.SetActive(!desktop);
            _vrStabToggle?.gameObject.SetActive(!desktop);
            _movementFollowsPitchToggle?.gameObject.SetActive(!desktop);
            _leftHandFlyToggle?.gameObject.SetActive(!desktop);
            _rightHandFlyRotateToggle?.gameObject.SetActive(!desktop);
            _resizeToggle?.gameObject.SetActive(!desktop);
            RefreshStabilizationVisibility();
            RefreshHandFlyVisibility();
            // Carries the grip's roll switch, which is VR-only for the same reason the ones above are.
            RefreshDetachedMarkerVisibility();
            RefreshSearch();
            ForceLayoutRebuild(_handlingGroup);
        }

        private void OnBootModeChanged(string mode) => RefreshPlatformVisibility();

        /// <summary>
        /// Keeps the last-photo card and its reveal button honest. Photos are taken from the prop
        /// while this panel is open, so the state is polled rather than pushed; edge-gated, so an
        /// unchanged label never restarts the widget's tweens.
        /// </summary>
        private void TickPhotoStatus()
        {
            if (_activeCamera == null || _photoStatus == null) return;

            string failure = _activeCamera.LastPhotoFailure;
            string fileName = _activeCamera.LastPhotoFileName;

            string statusText;
            if (failure != null)
            {
                statusText = BasisLocalization.Get("camera.photo.status.failed", failure);
            }
            else if (fileName != null)
            {
                statusText = BasisLocalization.Get("camera.photo.status.saved", fileName);
            }
            else
            {
                statusText = BasisLocalization.Get("camera.photo.status.idle");
            }

            if (statusText != _lastPhotoStatusText)
            {
                _lastPhotoStatusText = statusText;
                _photoStatus.SetDescription(statusText);
            }

            // A failed save can still leave an earlier photo worth revealing, so the button
            // follows the path rather than the failure.
            bool canReveal = fileName != null;
            if (_revealPhotoButton != null && canReveal != _lastRevealPhotoInteractable)
            {
                _lastRevealPhotoInteractable = canReveal;
                _revealPhotoButton.SetInteractable(
                    canReveal, canReveal ? null : BasisLocalization.Get("camera.openSavedPhoto.none"));
            }
        }

        /// <summary>
        /// One toggle per named, user-togglable layer, controlling whether the capture camera
        /// draws it. Built once from the project's layers; the camera refuses the ones it
        /// manages itself (OverlayUI, its own world markers), so those never appear here. The
        /// UI layer (players' nameplates) and HandHeldCameraUI (the prop's HUD) are exposed
        /// here as their own toggles.
        /// </summary>
        private void BuildLayersGroup(RectTransform parent)
        {
            _layersSection = PanelSectionToggle.CreateNewEntry(parent);
            _layersGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _layersSection, parent, BasisLocalization.Get("camera.renderLayers"), false);
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
                _performanceSection, parent, BasisLocalization.Get("camera.performance"), false);
            RectTransform content = _performanceGroup.ContentParent;

            _renderRateLockNotice = BuildRecordingStatusCard(content, "camera.rateLocked", null);
            _renderRateLockNotice.SetTooltip(BasisLocalization.Get("camera.rateLocked.description"));
            _renderRateLockNotice.gameObject.SetActive(false);

            _limitRenderRateToggle = PanelToggle.CreateNewEntry(content);
            _limitRenderRateToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.handheldCameraRate.limit"));
            _limitRenderRateToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.handheldCameraRate.limit.tooltip"));
            _limitRenderRateToggle.AssignBinding(BasisSettingsDefaults.LimitHandHeldCameraRate);

            _renderRateSlider = PanelSlider.CreateEntryAndBind(
                content,
                new PanelSlider.SliderSettings(
                    BasisLocalization.Get("settings.developer.handheldCameraRate"),
                    string.Empty,
                    BasisHandHeldCamera.MinHandHeldRenderHz, BasisHandHeldCamera.MaxHandHeldRenderHz, true, 0, ValueDisplayMode.Hz),
                BasisSettingsDefaults.HandHeldCameraRenderHz);
            _renderRateSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.handheldCameraRate.tooltip"));
        }

        /// <summary>
        /// World-space debug representations of the selected camera. Each toggle drives one layer
        /// on that camera's own visualiser, so the drawing keeps running once the panel is closed
        /// and two open cameras can be inspected independently.
        /// </summary>
        /// <summary>
        /// What gets written into the photo file itself. These are account-wide preferences bound
        /// straight to the settings store rather than per-camera values, so they are deliberately
        /// untouched by the page's Reset button — a privacy choice should not be undone by resetting
        /// a camera. They lived under the Chat tab of the settings menu until they were moved here,
        /// which is why their localization keys still read <c>settings.chat.camera.*</c>: the strings
        /// are translated into sixteen languages and renaming the keys would discard all of them.
        /// </summary>
        private void BuildPhotoMetadataGroup(RectTransform parent)
        {
            _photoMetadataSection = PanelSectionToggle.CreateNewEntry(parent);
            _photoMetadataGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _photoMetadataSection, parent, BasisLocalization.Get("camera.photoMetadata"), false);
            RectTransform content = _photoMetadataGroup.ContentParent;
            _photoMetadataSection.Descriptor.SetTooltip(BasisLocalization.Get("camera.photoMetadata.description"));

            PanelDropdown tagging = PanelDropdown.CreateNewEntry(content);
            tagging.Descriptor.SetTitle(BasisLocalization.Get("settings.chat.camera.photoMetadata"));
            tagging.Descriptor.SetTooltip(BasisLocalization.Get("settings.chat.camera.photoMetadata.description"));
            tagging.AssignLocalizedEntries(
                new List<string>
                {
                    BasisSettingsDefaults.PhotoTagging_NoOne,
                    BasisSettingsDefaults.PhotoTagging_EveryoneInPhoto,
                    BasisSettingsDefaults.PhotoTagging_JustMe,
                },
                new List<string>(PhotoTaggingKeys));
            tagging.AssignBinding(BasisSettingsDefaults.PhotoMetadataTagging);

            AddPhotoMetadataToggle(content, "settings.chat.camera.personDetails", BasisSettingsDefaults.PhotoEmbedPersonDetails);
            AddPhotoMetadataToggle(content, "settings.chat.camera.cameraSettings", BasisSettingsDefaults.PhotoEmbedCameraSettings);
            AddPhotoMetadataToggle(content, "settings.chat.camera.captureInfo", BasisSettingsDefaults.PhotoEmbedCaptureInfo);
            AddPhotoMetadataToggle(content, "settings.chat.camera.photographer", BasisSettingsDefaults.PhotoEmbedPhotographer);
            AddPhotoMetadataToggle(content, "settings.chat.camera.world", BasisSettingsDefaults.PhotoEmbedWorld);
        }

        private static void AddPhotoMetadataToggle(RectTransform content, string key, BasisSettingsBinding<bool> binding)
        {
            PanelToggle toggle = PanelToggle.CreateNewEntry(content);
            toggle.Descriptor.SetTitle(BasisLocalization.Get(key));
            toggle.Descriptor.SetTooltip(BasisLocalization.Get(key + ".description"));
            toggle.AssignBinding(binding);
        }

        private void BuildGizmoGroup(RectTransform parent)
        {
            _gizmoSection = PanelSectionToggle.CreateNewEntry(parent);
            _gizmoGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _gizmoSection, parent, BasisLocalization.Get("camera.debugGizmos"), false);
            RectTransform content = _gizmoGroup.ContentParent;
            _gizmoSection.Descriptor.SetTooltip(BasisLocalization.Get("camera.debugGizmos.description"));

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
            toggle.Descriptor.SetTooltip(description);
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
            _hiddenState.SetTitle(BasisLocalization.Get("camera.cameraHidden"));
            _hiddenState.SetDescription(BasisLocalization.Get("camera.cameraHidden.description"));

            RectTransform row = PanelElementDescriptor.BuildActionRow(_hiddenState.ContentParent, "BringBackRow");
            _bringBackButton = PanelButton.CreateNew(row);
            _bringBackButton.Descriptor.SetTitle(BasisLocalization.Get("camera.bringBackCamera"));
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

        /// <summary>
        /// The shot buttons. Take Photo has the column to itself; Timer and Reset share a row. Split
        /// three ways the column leaves each button about as wide as two characters, so only the two
        /// short ones pair up.
        /// </summary>
        private void BuildTopActions(RectTransform parent)
        {
            _topButtons.Clear();

            PanelButton photoButton = PanelButton.CreateNew(parent);
            photoButton.Descriptor.SetTitle(BasisLocalization.Get("camera.takePhoto"));
            photoButton.OnClicked += () => _activeCamera?.CapturePhoto();
            _topButtons.Add(photoButton);

            _topActionRow = PanelElementDescriptor.BuildActionRow(parent, "CameraTimerResetRow");

            _timerButton = PanelButton.CreateNew(_topActionRow);
            _timerButton.Descriptor.SetTitle(TimerIdleLabel);
            _timerButton.OnClicked += () =>
            {
                // Timer() is a toggle now: pressing it mid-countdown cancels the shot.
                if (_activeCamera == null) return;
                _activeCamera.Timer();
                RefreshTimerLabel();
            };
            _topButtons.Add(_timerButton);

            _resetTopButton = PanelButton.CreateNew(_topActionRow);
            _resetTopButton.Descriptor.SetTitle(BasisLocalization.Get("ui.reset"));
            _resetTopButton.OnClicked += PromptResetSettings;
            _topButtons.Add(_resetTopButton);
        }

        /// <summary>
        /// Binds the panel to a camera, and tells that camera its feed is being watched from here.
        /// <para>
        /// The preview draws the camera's render texture, but the camera only renders while its
        /// own prop is in view — so a camera that has been flown away, sent to follow from behind,
        /// or simply left facing the wrong way stops rendering and the preview sits on its last
        /// frame. Every other camera is cleared as well as the outgoing one, so a request can
        /// never be left behind on a camera the panel has moved off.
        /// </para>
        /// </summary>
        private void SetActiveCamera(BasisHandHeldCamera camera)
        {
            _activeCamera = camera;

            IReadOnlyList<BasisHandHeldCamera> cameras = BasisHandHeldCameraRegistry.Cameras;
            for (int Index = 0; Index < cameras.Count; Index++)
            {
                BasisHandHeldCamera entry = cameras[Index];
                if (entry != null) entry.SetPanelPreviewActive(entry == camera);
            }
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
                SetActiveCamera(null);
                return;
            }

            _selector.gameObject.SetActive(_entries.Count > 1);
            _emptyState?.SetActive(false);
            SetGroupsActive(true);

            int selected = _activeCamera != null ? _entries.IndexOf(_activeCamera) : 0;
            if (selected < 0) selected = 0;
            SetActiveCamera(_entries[selected]);
            _selector.SetValueWithoutNotify(labels[selected]);

            ApplyActiveCameraToControls();
            ForceLayoutRebuild(null);
        }

        private void OnSelectionChanged()
        {
            if (_selector == null) return;
            int index = _selector.Index;
            if (index < 0 || index >= _entries.Count) return;
            SetActiveCamera(_entries[index]);
            ApplyActiveCameraToControls();
        }

        private void SetGroupsActive(bool active)
        {
            for (int Index = 0; Index < _topButtons.Count; Index++)
            {
                PanelButton button = _topButtons[Index];
                if (button != null) button.gameObject.SetActive(active);
            }
            if (_topActionRow != null) _topActionRow.gameObject.SetActive(active);
            if (_previewGroup != null) _previewGroup.SetActive(active);
            if (!active) CollapsePreview();
            if (_modeDropdown != null) _modeDropdown.gameObject.SetActive(active);
            if (_streamPresetDropdown != null) _streamPresetDropdown.gameObject.SetActive(active);
            if (_videoOutputToggle != null) _videoOutputToggle.gameObject.SetActive(active);
            if (_directToScreenToggle != null) _directToScreenToggle.gameObject.SetActive(active);
            // The pages have nothing to drive without a camera, so the navigation goes with them.
            if (_tabGroup != null && _tabGroup.TabButtonParent != null)
            {
                _tabGroup.TabButtonParent.gameObject.SetActive(active);
            }
            SetSectionActive(_lensSection, _lensGroup, active);
            SetSectionActive(_dofSection, _dofGroup, active);
            SetSectionActive(_colorSection, _colorGroup, active);
            SetSectionActive(_gridSection, _gridGroup, active);
            SetSectionActive(_peakingSection, _peakingGroup, active);
            SetSectionActive(_exposureSection, _exposureGroup, active);
            SetSectionActive(_filmLookSection, _filmLookGroup, active);
            SetSectionActive(_bloomSection, _bloomGroup, active);
            SetSectionActive(_vignetteSection, _vignetteGroup, active);
            SetSectionActive(_grainSection, _grainGroup, active);
            SetSectionActive(_lensEffectsSection, _lensEffectsGroup, active);
            SetSectionActive(_motionBlurSection, _motionBlurGroup, active);
#if Basis_VOLUMETRIC_SUPPORTED
            SetSectionActive(_fogSection, _fogGroup, active);
#endif
            SetSectionActive(_outputSection, _outputGroup, active);
            SetSectionActive(_streamSection, _streamGroup, active);
            SetSectionActive(_followSection, _followGroup, active);
            SetSectionActive(_positionSection, _positionGroup, active);
            SetSectionActive(_rotationSection, _rotationGroup, active);
            SetSectionActive(_modifierEffectsSection, _modifierEffectsGroup, active);
            SetEffectSectionsActive(active);
            // The dolly track block is not listed: it is a card inside the position group and goes
            // with it, and RefreshModifierVisibility owns whether the fitted slot wants it at all.
            SetSectionActive(_backgroundSection, _backgroundGroup, active);
            SetSectionActive(_actionSection, _actionGroup, active);
            SetSectionActive(_handlingSection, _handlingGroup, active);
            SetSectionActive(_layersSection, _layersGroup, active);
            SetSectionActive(_performanceSection, _performanceGroup, active);
            SetSectionActive(_gizmoSection, _gizmoGroup, active);
#if BASIS_HAS_GI && !UNITY_ANDROID
            SetSectionActive(_giSection, _giGroup, active);
#endif
#if BASIS_HAS_RTAO && !UNITY_ANDROID
            SetSectionActive(_rtaoSection, _rtaoGroup, active);
#endif

            if (active) RefreshSearch();
            ForceLayoutRebuild(null);
        }

        /// <summary>
        /// Takes a whole section off the page and puts it back, leaving its open flag alone so it
        /// returns to whatever the user last left it at. The dividers go with it, or the rule above
        /// a hidden header stays behind separating nothing.
        /// </summary>
        private static void SetSectionActive(PanelSectionToggle section, PanelElementDescriptor group, bool active)
            => PanelSectionToggleHelpers.SetSectionVisible(section, group, active);

        private void ApplyActiveCameraToControls()
        {
            if (_activeCamera == null) return;

            _lastWaypointCount = -1;
            _lastEffectSignature = -1;

            // Cameras hold their modes independently, so switching between two of them has to
            // repaint rather than trust the cache from the one that was showing.
            _activeCamera.RefreshCameraMode();
            RefreshModeVisuals(force: true);

            SeedModifierCameraControls();
            SeedGifControls();
            SeedVideoControls();
            SeedPhotogrammetryControls();

            // The bound camera changed, so the cached labels describe someone else's last shot.
            _lastPhotoStatusText = null;
            _lastRevealPhotoInteractable = null;
            TickPhotoStatus();

            RefreshEffectList();
            RefreshWaypointList();

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
                SyncDoFEnabledToggle();
                SeedDoFModeDropdown();
                RefreshDoFModeVisibility();
            }

            _lastFocusFollows = _activeCamera.autoFocusFollowSubject;
            _focusModeDropdown?.SetValueWithoutNotify(FocusModeKeys[_activeCamera.autoFocusFollowSubject ? 0 : 1]);

            _autoBrightnessToggle?.SetValueWithoutNotify(_activeCamera.autoBrightnessEnabled);
            _autoBrightnessTargetSlider?.SetValueWithoutNotify(_activeCamera.autoBrightnessTarget * 100f);
            _autoBrightnessSpeedSlider?.SetValueWithoutNotify(_activeCamera.autoBrightnessSpeed);
            _autoBrightnessRangeSlider?.SetValueWithoutNotify(_activeCamera.autoBrightnessRange);
            _autoBrightnessMeteringDropdown?.SetValueWithoutNotify(
                MeteringKeys[Mathf.Clamp(_activeCamera.autoBrightnessMetering, 0, MeteringKeys.Length - 1)]);
            RefreshAutoBrightnessVisibility();

            _focusPeakingToggle?.SetValueWithoutNotify(_activeCamera.focusPeakingEnabled);
            _focusPeakingGreyToggle?.SetValueWithoutNotify(_activeCamera.focusPeakingGreyPicture);
            _focusPeakingSensitivitySlider?.SetValueWithoutNotify(_activeCamera.focusPeakingSensitivity * 100f);
            _focusPeakingColourDropdown?.SetValueWithoutNotify(
                BasisHandHeldCamera.FocusPeakingColourKeys[
                    Mathf.Clamp(_activeCamera.focusPeakingColour, 0, BasisHandHeldCamera.FocusPeakingColourKeys.Length - 1)]);
            RefreshFocusPeakingVisibility();

            _viewfinderGridToggle?.SetValueWithoutNotify(_activeCamera.viewfinderGridEnabled);
            _viewfinderGridOpacitySlider?.SetValueWithoutNotify(_activeCamera.viewfinderGridOpacity * 100f);
            _viewfinderGridPatternDropdown?.SetValueWithoutNotify(
                BasisHandHeldCamera.GridPatternKeys[
                    Mathf.Clamp(_activeCamera.viewfinderGridPattern, 0, BasisHandHeldCamera.GridPatternKeys.Length - 1)]);
            RefreshViewfinderGridVisibility();

            if (metaData.vignette != null)
            {
                _vignetteSlider?.SetValueWithoutNotify(metaData.vignette.intensity.value * 100f);
                _vignetteSmoothnessSlider?.SetValueWithoutNotify(metaData.vignette.smoothness.value * 100f);
            }
            if (metaData.bloom != null)
                _bloomScatterSlider?.SetValueWithoutNotify(metaData.bloom.scatter.value * 100f);
            if (metaData.paniniProjection != null)
            {
                _paniniSlider?.SetValueWithoutNotify(metaData.paniniProjection.distance.value * 100f);
                _paniniCropSlider?.SetValueWithoutNotify(metaData.paniniProjection.cropToFit.value * 100f);
            }
            RefreshPaniniVisibility();
            _captureTonemappingDropdown?.SetValueWithoutNotify(
                TonemappingKeys[Mathf.Clamp((int)_activeCamera.CaptureTonemapping, 0, TonemappingKeys.Length - 1)]);
            if (metaData.chromaticAberration != null)
                _chromaticSlider?.SetValueWithoutNotify(metaData.chromaticAberration.intensity.value * 100f);
            if (metaData.filmGrain != null)
                _filmGrainSlider?.SetValueWithoutNotify(metaData.filmGrain.intensity.value * 100f);
            SeedFilmControls(metaData);
            if (metaData.whiteBalance != null)
            {
                _whiteBalanceTempSlider?.SetValueWithoutNotify(metaData.whiteBalance.temperature.value);
                _whiteBalanceTintSlider?.SetValueWithoutNotify(metaData.whiteBalance.tint.value);
            }
            if (metaData.lensDistortion != null)
            {
                _lensDistortionSlider?.SetValueWithoutNotify(metaData.lensDistortion.intensity.value * 100f);
                _lensDistortionScaleSlider?.SetValueWithoutNotify(metaData.lensDistortion.scale.value);
            }
            if (metaData.motionBlur != null)
            {
                _motionBlurSlider?.SetValueWithoutNotify(metaData.motionBlur.intensity.value * 100f);
                _motionBlurClampSlider?.SetValueWithoutNotify(metaData.motionBlur.clamp.value * 100f);
                int quality = Mathf.Clamp(_activeCamera.HandHeld.MotionBlurQuality, 0, MotionBlurQualityLabels.Length - 1);
                _motionBlurQualityDropdown?.SetValueWithoutNotify(MotionBlurQualityLabels[quality]);
                int blurMode = Mathf.Clamp(_activeCamera.HandHeld.MotionBlurMode, 0, MotionBlurModeLabels.Length - 1);
                _motionBlurModeDropdown?.SetValueWithoutNotify(MotionBlurModeLabels[blurMode]);
            }
            RefreshMotionBlurVisibility();

            if (_msaaDropdown != null)
            {
                int msaaIndex = System.Array.IndexOf(MsaaSampleCounts, _activeCamera.msaaSamples);
                if (msaaIndex >= 0) _msaaDropdown.SetValueWithoutNotify(BuildMsaaLabels()[msaaIndex]);
            }

#if Basis_VOLUMETRIC_SUPPORTED
            if (metaData.VolumetricFogVolume != null)
            {
                _fogOverrideToggle?.SetValueWithoutNotify(_activeCamera.OverrideVolumetricFog);
                _fogSlider?.SetValueWithoutNotify(metaData.VolumetricFogVolume.density.value);
            }
            RefreshVolumetricFogVisibility();
#endif

#if BASIS_HAS_GI && !UNITY_ANDROID
            SeedGlobalIlluminationControls();
#endif
#if BASIS_HAS_RTAO && !UNITY_ANDROID
            SeedRTAOControls();
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

            SyncToggle(_hideCameraToggle, _activeCamera.IsCameraHidden, ref _lastCameraHidden);
            SyncToggle(_audioListenerToggle, _activeCamera.IsAudioListener, ref _lastAudioListener);
            SyncToggle(_selfieToggle, _activeCamera.HandHeld.IsSelfieMode, ref _lastSelfie);
            SyncToggle(_closeHidesToggle, _activeCamera.HandHeld.CloseHidesCamera, ref _lastCloseHides);
            SyncToggle(_flyToggle, _activeCamera.IsFlyModeEnabled, ref _lastFly);
            SyncToggle(_flyOnMenuToggle, _activeCamera.showFlyOnMainMenu, ref _lastFlyOnMenu);
            _lastFlySpeed = _activeCamera.flySpeed;
            _flySpeedSlider?.SetValueWithoutNotify(_activeCamera.flySpeed);
            _lastFlyClimbSpeed = _activeCamera.vrFlyElevationSpeed;
            _flyClimbSpeedSlider?.SetValueWithoutNotify(_activeCamera.vrFlyElevationSpeed);
            _lastFlyFastMultiplier = _activeCamera.flyFastMultiplier;
            _flyFastMultiplierSlider?.SetValueWithoutNotify(_activeCamera.flyFastMultiplier);
            _lastFlyTurnSpeed = _activeCamera.vrFlyTurnSpeed;
            _flyTurnSpeedSlider?.SetValueWithoutNotify(_activeCamera.vrFlyTurnSpeed);
            _lastFlyMouseSensitivity = _activeCamera.mouseSensitivity;
            _flyMouseSensitivitySlider?.SetValueWithoutNotify(_activeCamera.mouseSensitivity);
            _lastFlyMomentum = _activeCamera.useMomentum;
            _flyMomentumToggle?.SetValueWithoutNotify(_activeCamera.useMomentum);
            _lastMovementFollowsPitch = _activeCamera.vrFlyMovementFollowsPitch;
            _movementFollowsPitchToggle?.SetValueWithoutNotify(_activeCamera.vrFlyMovementFollowsPitch);
            _lastLeftHandFly = _activeCamera.vrLeftHandFlyEnabled;
            _leftHandFlyToggle?.SetValueWithoutNotify(_activeCamera.vrLeftHandFlyEnabled);
            _lastRightHandFlyRotate = _activeCamera.vrRightHandFlyRotateEnabled;
            _rightHandFlyRotateToggle?.SetValueWithoutNotify(_activeCamera.vrRightHandFlyRotateEnabled);
            _lastHandFlyMoveDeadzone = _activeCamera.vrHandFlyMoveDeadzone;
            _handFlyMoveDeadzoneSlider?.SetValueWithoutNotify(_activeCamera.vrHandFlyMoveDeadzone);
            _lastHandFlyMoveReach = _activeCamera.vrHandFlyMoveReach;
            _handFlyMoveReachSlider?.SetValueWithoutNotify(_activeCamera.vrHandFlyMoveReach);
            _lastHandFlyMoveSensitivity = _activeCamera.vrHandFlyMoveSensitivity;
            _handFlyMoveSensitivitySlider?.SetValueWithoutNotify(_activeCamera.vrHandFlyMoveSensitivity);
            _lastHandFlyTurnDeadzone = _activeCamera.vrHandFlyTurnDeadzone;
            _handFlyTurnDeadzoneSlider?.SetValueWithoutNotify(_activeCamera.vrHandFlyTurnDeadzone);
            _lastHandFlyTurnReach = _activeCamera.vrHandFlyTurnReach;
            _handFlyTurnReachSlider?.SetValueWithoutNotify(_activeCamera.vrHandFlyTurnReach);
            _lastHandFlyTurnSensitivity = _activeCamera.vrHandFlyTurnSensitivity;
            _handFlyTurnSensitivitySlider?.SetValueWithoutNotify(_activeCamera.vrHandFlyTurnSensitivity);
            RefreshHandFlyVisibility();
            _autoLevelToggle?.SetValueWithoutNotify(_activeCamera.useAutoLeveling);
            _vrStabToggle?.SetValueWithoutNotify(_activeCamera.useVRHandheldSmoothing);
            _lastVrStabPosition = _activeCamera.vrHandheldPositionDamping;
            _vrStabPositionSlider?.SetValueWithoutNotify(_activeCamera.vrHandheldPositionDamping);
            _lastVrStabYaw = _activeCamera.vrHandheldYawDamping;
            _vrStabYawSlider?.SetValueWithoutNotify(_activeCamera.vrHandheldYawDamping);
            _lastVrStabPitch = _activeCamera.vrHandheldPitchDamping;
            _vrStabPitchSlider?.SetValueWithoutNotify(_activeCamera.vrHandheldPitchDamping);
            _lastVrStabRoll = _activeCamera.vrHandheldRollDamping;
            _vrStabRollSlider?.SetValueWithoutNotify(_activeCamera.vrHandheldRollDamping);
            _lastZoomStab = _activeCamera.zoomStabilization;
            _zoomStabToggle?.SetValueWithoutNotify(_activeCamera.zoomStabilization);
            _lastZoomStabResponse = _activeCamera.zoomStabilizationResponse;
            _zoomStabResponseSlider?.SetValueWithoutNotify(_activeCamera.zoomStabilizationResponse);
            _lastZoomStabMin = _activeCamera.zoomStabilizationMinScale;
            _zoomStabMinSlider?.SetValueWithoutNotify(_activeCamera.zoomStabilizationMinScale);
            _lastZoomStabMax = _activeCamera.zoomStabilizationMaxScale;
            _zoomStabMaxSlider?.SetValueWithoutNotify(_activeCamera.zoomStabilizationMaxScale);
            RefreshStabilizationVisibility();
            _lastSmoothDrag = _activeCamera.useSmoothDrag;
            _smoothDragToggle?.SetValueWithoutNotify(_activeCamera.useSmoothDrag);
            _smoothDragPositionSlider?.SetValueWithoutNotify(_activeCamera.smoothDragPositionDamping);
            _smoothDragRotationSlider?.SetValueWithoutNotify(_activeCamera.smoothDragRotationDamping);
            _smoothDragLeashSlider?.SetValueWithoutNotify(_activeCamera.smoothDragMaxDistance);
            _lastResize = _activeCamera.ResizeWithGesture;
            _resizeToggle?.SetValueWithoutNotify(_activeCamera.ResizeWithGesture);
            RefreshSmoothDragVisibility();
            _capture360Toggle?.SetValueWithoutNotify(_activeCamera.capture360Enabled);
            _printPhotoToggle?.SetValueWithoutNotify(_activeCamera.printPhotoEnabled);
            _formatDropdown?.SetValueWithoutNotify(
                _activeCamera.HandHeld.FormatIndex == BasisHandHeldCameraUI.FORMAT_EXR ? "EXR" : "PNG");

            if (_followMarkerDropdown != null)
            {
                int markerIndex = Mathf.Clamp((int)_activeCamera.detachedMarker, 0, DetachedMarkerKeys.Length - 1);
                _followMarkerDropdown.SetValueWithoutNotify(DetachedMarkerKeys[markerIndex]);
            }
            _lastMarkerScale = _activeCamera.DetachedMarkerScale * 100f;
            _markerScaleSlider?.SetValueWithoutNotify(_lastMarkerScale);
            RefreshDetachedMarkerVisibility();
            _lastPuckPreview = _activeCamera.puckLookAtPreview;
            _puckPreviewToggle?.SetValueWithoutNotify(_activeCamera.puckLookAtPreview);
            _lastCameraRoll = _activeCamera.cameraRollEnabled;
            _rollToggle?.SetValueWithoutNotify(_activeCamera.cameraRollEnabled);
            // Each camera holds its own follow target, and the roster has not changed just because
            // the selected camera has — so drop the cached list to force the rebuild. Without it
            // the dropdown kept showing the previous camera's target, and picking the name already
            // on screen raises no change event, so the new camera silently stayed on "Me".
            _followTargetIds.Clear();
            _followTargetsBuilt = false;
            RefreshFollowTargets();

            _followPlayspaceToggle?.SetValueWithoutNotify(_activeCamera.subjectSettings.anchorToBody);
            _aimPointDropdown?.SetValueWithoutNotify(AimPointKeys[Mathf.Clamp((int)_activeCamera.subjectSettings.aimPoint, 0, AimPointKeys.Length - 1)]);
            _followLookAtHeightSlider?.SetValueWithoutNotify(_activeCamera.subjectSettings.aimHeightOffset);
            _subjectRadiusSlider?.SetValueWithoutNotify(_activeCamera.subjectSettings.framingRadius);
            _targetGroupToggle?.SetValueWithoutNotify(_activeCamera.subjectSettings.groupIncludesLocal);
            SeedModifierControls();

            RefreshVideoOutputState();
            RefreshLayerToggles();
            RefreshGizmoToggles();
            RefreshPreviewTexture();

            // Re-evaluate from scratch for the newly selected camera.
            _lastHiddenState = null;
            RefreshHiddenState();

            // Swapping cameras re-shows the rows the previous one had hidden, so re-apply any query
            // on top rather than leaving unrelated settings sitting in a filtered page.
            RefreshSearch();
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

            bool web = _activeCamera.VideoTransport == BasisVideoTransport.Web;
            if (_webQualitySlider != null) _webQualitySlider.gameObject.SetActive(web);
            if (_webPortField != null) _webPortField.gameObject.SetActive(web);
            // Nothing to open until it is actually serving, and the address only exists then.
            if (_openStreamButton != null) _openStreamButton.gameObject.SetActive(web && _activeCamera.IsWebStreamActive);
            if (_videoSenderNameField != null) _videoSenderNameField.gameObject.SetActive(!web);

            ForceLayoutRebuild(_streamGroup);
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
            RectTransform page = ActivePageContent();

            if (group != null)
            {
                // Out from the rows that changed: the group's own root is measured by its parent
                // before its content has resized, so rebuilding that alone leaves the page holding
                // the stale height. The page root carries no layout controller, so the chain stops
                // at the scroll content, which is the rect the rows actually sit in.
                PanelElementDescriptor.RebuildLayoutChain(group.ContentParent, page);
            }
            else
            {
                if (page != null) LayoutRebuilder.ForceRebuildLayoutImmediate(page);
                _panel?.Descriptor?.ForceRebuild();
            }

            if (_navColumn != null) LayoutRebuilder.ForceRebuildLayoutImmediate(_navColumn);
        }

        /// <summary>
        /// Keeps the toggle's tooltip describing what the selected transport needs on the
        /// receiving side, and its caption showing the live address once the web stream is
        /// serving — the port rolls forward when taken, so the URL isn't something the user can guess.
        /// </summary>
        private void RefreshLiveOutputDescription()
        {
            if (_videoOutputToggle == null || _activeCamera == null) return;

            // A refusal springs the toggle back on its own, so without this the only thing the
            // operator is told is that the control does not work.
            _videoOutputToggle.Descriptor.SetTooltip(
                $"Publish this camera as a live video source. {BasisHandHeldCamera.GetVideoTransportRequirement(_activeCamera.VideoTransport)}");
            string description = !string.IsNullOrEmpty(_activeCamera.LiveOutputFailure)
                ? _activeCamera.LiveOutputFailure
                : _activeCamera.IsWebStreamActive
                    ? $"Serving at {_activeCamera.WebStreamUrl} — add that as a Browser source in OBS, or open it in a browser."
                    : string.Empty;
            if (_lastWebStreamDescription == description) return;

            _lastWebStreamDescription = description;
            _videoOutputToggle.Descriptor.SetDescription(description);
        }

        /// <summary>
        /// Keeps the Direct To Screen row honest. The toggle follows the camera's setting — switching
        /// it on for one camera switches it off for the rest — and the line under it says whether the
        /// monitor is actually showing the feed and, if not, what it is waiting on. Polled: a device
        /// switch and a body change both move it, and neither raises a panel event.
        /// </summary>
        private void RefreshDirectToScreenState()
        {
            if (_directToScreenToggle == null || _activeCamera == null) return;

            SyncToggle(_directToScreenToggle, _activeCamera.DirectToScreen, ref _lastDirectToScreen);

            string description = DescribeDirectToScreen(_activeCamera);
            if (_lastDirectToScreenDescription == description) return;

            _lastDirectToScreenDescription = description;
            _directToScreenToggle.Descriptor.SetDescription(description);
        }

        private static string DescribeDirectToScreen(BasisHandHeldCamera camera)
        {
            switch (camera.DirectToScreenState)
            {
                case BasisCameraDirectToScreenState.Presenting:
                    return BasisLocalization.Get("camera.directToScreen.presenting");
                case BasisCameraDirectToScreenState.WaitingForVR:
                    return BasisLocalization.Get("camera.directToScreen.waitingForVR");
                case BasisCameraDirectToScreenState.NoOutputSocket:
                    return BasisLocalization.Get("camera.directToScreen.noSocket", camera.BodyTraits.Kind);
                case BasisCameraDirectToScreenState.Unsupported:
                    return BasisLocalization.Get("camera.directToScreen.unsupported");
                default:
                    return string.Empty;
            }
        }

        private void RefreshStreamPresetSelection()
        {
            if (_streamPresetDropdown == null || _activeCamera == null) return;
            if (_streamPresetDropdown.DropdownComponent != null && _streamPresetDropdown.DropdownComponent.IsExpanded) return;

            int index = BasisCameraStreamPresets.IndexOf(_streamPresets, _activeCamera.VideoTransport, _activeCamera.VideoOutputSettings);
            string key = index >= 0 ? _streamPresets[index].Key : BasisCameraStreamPresets.CustomKey;
            if (_lastStreamPresetKey == key) return;

            _lastStreamPresetKey = key;
            _streamPresetDropdown.SetValueWithoutNotify(key);
        }

        private void RefreshVideoOutputState()
        {
            if (_activeCamera == null) return;

            SyncToggle(_videoOutputToggle, _activeCamera.IsAnyVideoOutputActive, ref _lastVideoOutputActive);
            _lastWebStreamActive = _activeCamera.IsWebStreamActive;
            RefreshTransportSelection();
            RefreshStreamPresetSelection();
            RefreshLiveOutputDescription();
            RefreshDirectToScreenState();
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
            Texture feed = _activeCamera != null ? _activeCamera.ViewfinderTexture : null;
            if (_previewImage.texture != feed) _previewImage.texture = feed;
            _previewImage.enabled = feed != null;
            ApplyPreviewAspect(feed);

            if (_expandedImage == null || !_previewExpanded) return;
            if (_expandedImage.texture != feed) _expandedImage.texture = feed;
            _expandedImage.enabled = feed != null;
            if (_expandedFitter != null && feed != null && feed.height > 0)
            {
                _expandedFitter.aspectRatio = (float)feed.width / feed.height;
            }
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
            if (_previewLayout != null)
            {
                if (Mathf.Abs(_previewLayout.preferredHeight - height) < 0.5f) return;
                _previewLayout.preferredHeight = height;
            }
            else if (Mathf.Abs(rect.sizeDelta.y - height) < 0.5f)
            {
                return;
            }
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

            // Against IsAnyVideoOutputActive, which is what the cache is filled from. Read as
            // IsVideoOutputActive the two could never agree while the web transport was the one
            // running, so this fired a full refresh — and the layout rebuild inside it — every
            // frame for as long as the stream was up.
            if (_activeCamera.IsAnyVideoOutputActive != _lastVideoOutputActive ||
                _activeCamera.IsWebStreamActive != _lastWebStreamActive)
            {
                RefreshVideoOutputState();
            }
            RefreshStreamPresetSelection();
            RefreshDirectToScreenState();

            SyncSharedControls();
            RefreshFocusSubjectNotice();
            RefreshFollowTargets();
            TickModeState();
            TickModifierSections();
            TickGifSection();
            TickVideoSection();
            TickPhotogrammetrySection();
            TickRenderRateLock();
            TickPhotoStatus();
            TickBodySection();
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

                // The prop carries the same switch, and the mode presets and a settings load write
                // the effect straight. It carries the rest of the section on and off screen, so it
                // is edge-gated like the stabilizer toggles rather than pushed through SyncToggle —
                // and the style it promotes on the way on has to be re-read with it.
                bool dofEnabled = _activeCamera.MetaData.depthOfField.active;
                if (_lastDofEnabled != dofEnabled)
                {
                    _lastDofEnabled = dofEnabled;
                    _dofEnabledToggle?.SetValueWithoutNotify(dofEnabled);
                    SeedDoFModeDropdown();
                    RefreshDoFModeVisibility();
                }
            }


            SyncToggle(_selfieToggle, _activeCamera.HandHeld.IsSelfieMode, ref _lastSelfie);
            // Fly is still reachable from middle click and the VR thumbstick, and auto-follow and
            // the mode presets switch it off from underneath — so the toggle has to follow the
            // camera rather than assume it is the only writer.
            SyncToggle(_flyToggle, _activeCamera.IsFlyModeEnabled, ref _lastFly);
            SyncToggle(_flyOnMenuToggle, _activeCamera.showFlyOnMainMenu, ref _lastFlyOnMenu);
            SyncSlider(_flySpeedSlider, _activeCamera.flySpeed, ref _lastFlySpeed);
            SyncSlider(_flyClimbSpeedSlider, _activeCamera.vrFlyElevationSpeed, ref _lastFlyClimbSpeed);
            SyncSlider(_flyFastMultiplierSlider, _activeCamera.flyFastMultiplier, ref _lastFlyFastMultiplier);
            SyncSlider(_flyTurnSpeedSlider, _activeCamera.vrFlyTurnSpeed, ref _lastFlyTurnSpeed);
            SyncSlider(_flyMouseSensitivitySlider, _activeCamera.mouseSensitivity, ref _lastFlyMouseSensitivity);
            SyncToggle(_flyMomentumToggle, _activeCamera.useMomentum, ref _lastFlyMomentum);
            SyncToggle(_movementFollowsPitchToggle, _activeCamera.vrFlyMovementFollowsPitch, ref _lastMovementFollowsPitch);
            // Left Hand Fly / Right Hand Rotate carry six sliders on and off screen between them, so
            // the change is captured before SyncToggle overwrites the cache, the same shape as the
            // vrStab/zoomStab edge-gate just below.
            bool leftHandFlyChanged = _lastLeftHandFly != _activeCamera.vrLeftHandFlyEnabled;
            bool rightHandFlyRotateChanged = _lastRightHandFlyRotate != _activeCamera.vrRightHandFlyRotateEnabled;
            SyncToggle(_leftHandFlyToggle, _activeCamera.vrLeftHandFlyEnabled, ref _lastLeftHandFly);
            SyncToggle(_rightHandFlyRotateToggle, _activeCamera.vrRightHandFlyRotateEnabled, ref _lastRightHandFlyRotate);
            if (leftHandFlyChanged || rightHandFlyRotateChanged)
            {
                RefreshHandFlyVisibility();
            }
            SyncSlider(_handFlyMoveDeadzoneSlider, _activeCamera.vrHandFlyMoveDeadzone, ref _lastHandFlyMoveDeadzone);
            SyncSlider(_handFlyMoveReachSlider, _activeCamera.vrHandFlyMoveReach, ref _lastHandFlyMoveReach);
            SyncSlider(_handFlyMoveSensitivitySlider, _activeCamera.vrHandFlyMoveSensitivity, ref _lastHandFlyMoveSensitivity);
            SyncSlider(_handFlyTurnDeadzoneSlider, _activeCamera.vrHandFlyTurnDeadzone, ref _lastHandFlyTurnDeadzone);
            SyncSlider(_handFlyTurnReachSlider, _activeCamera.vrHandFlyTurnReach, ref _lastHandFlyTurnReach);
            SyncSlider(_handFlyTurnSensitivitySlider, _activeCamera.vrHandFlyTurnSensitivity, ref _lastHandFlyTurnSensitivity);
            SyncToggle(_autoLevelToggle, _activeCamera.useAutoLeveling, ref _lastAutoLevel);
            // The stabilizer toggles carry their sliders on and off screen, so they are edge-gated
            // the way the drag toggle below is rather than pushed through SyncToggle.
            bool vrStab = _activeCamera.useVRHandheldSmoothing;
            bool zoomStab = _activeCamera.zoomStabilization;
            if (_lastVrStab != vrStab || _lastZoomStab != zoomStab)
            {
                _lastVrStab = vrStab;
                _lastZoomStab = zoomStab;
                _vrStabToggle?.SetValueWithoutNotify(vrStab);
                _zoomStabToggle?.SetValueWithoutNotify(zoomStab);
                RefreshStabilizationVisibility();
            }
            SyncSlider(_vrStabPositionSlider, _activeCamera.vrHandheldPositionDamping, ref _lastVrStabPosition);
            SyncSlider(_vrStabYawSlider, _activeCamera.vrHandheldYawDamping, ref _lastVrStabYaw);
            SyncSlider(_vrStabPitchSlider, _activeCamera.vrHandheldPitchDamping, ref _lastVrStabPitch);
            SyncSlider(_vrStabRollSlider, _activeCamera.vrHandheldRollDamping, ref _lastVrStabRoll);
            SyncSlider(_zoomStabResponseSlider, _activeCamera.zoomStabilizationResponse, ref _lastZoomStabResponse);
            SyncSlider(_zoomStabMinSlider, _activeCamera.zoomStabilizationMinScale, ref _lastZoomStabMin);
            SyncSlider(_zoomStabMaxSlider, _activeCamera.zoomStabilizationMaxScale, ref _lastZoomStabMax);

            // Applying a saved mode or resetting the page rewrites these under an open panel, and
            // the drag numbers are only on screen while the toggle is on.
            bool smoothDrag = _activeCamera.useSmoothDrag;
            if (_lastSmoothDrag != smoothDrag)
            {
                _lastSmoothDrag = smoothDrag;
                _smoothDragToggle?.SetValueWithoutNotify(smoothDrag);
                RefreshSmoothDragVisibility();
            }
            SyncSlider(_smoothDragPositionSlider, _activeCamera.smoothDragPositionDamping, ref _lastSmoothDragPosition);
            SyncSlider(_smoothDragRotationSlider, _activeCamera.smoothDragRotationDamping, ref _lastSmoothDragRotation);
            SyncSlider(_smoothDragLeashSlider, _activeCamera.smoothDragMaxDistance, ref _lastSmoothDragLeash);
            SyncToggle(_resizeToggle, _activeCamera.ResizeWithGesture, ref _lastResize);
            // Loading a settings file rewrites this under an open panel, the same way the drag
            // numbers above are rewritten.
            SyncToggle(_puckPreviewToggle, _activeCamera.puckLookAtPreview, ref _lastPuckPreview);
            SyncToggle(_rollToggle, _activeCamera.cameraRollEnabled, ref _lastCameraRoll);
            // The marker size has a second writer that is not this panel at all — the two-hand
            // pinch on the puck itself — so the slider has to follow the camera rather than assume
            // it set the value it is showing.
            SyncSlider(_markerScaleSlider, _activeCamera.DetachedMarkerScale * 100f, ref _lastMarkerScale);

            // The prop carries its own Auto/Manual focus buttons, so this is a shared control like
            // the ones above — and it also decides whether the focus slider is on screen at all.
            bool focusFollows = _activeCamera.autoFocusFollowSubject;
            if (_lastFocusFollows != focusFollows)
            {
                _lastFocusFollows = focusFollows;
                _focusModeDropdown?.SetValueWithoutNotify(FocusModeKeys[focusFollows ? 0 : 1]);
                RefreshDoFModeVisibility();
            }

            _activeCamera.HandHeld.SyncPropControlsFromState();
        }

        // The instance's roster changes without any panel event, so the target list is rebuilt on
        // the tick — but only when the roster actually moves, to avoid rebuilding a dropdown (and
        // fighting an open one) every frame. A same-tick join+leave holds the count still, so the
        // listed ids are checked as well as the count.
        private void RefreshFollowTargets()
        {
            if (_followTargetDropdown == null || _activeCamera == null) return;

            // Rebuilding swaps the option list out from under an open one. Unity has already
            // spawned the item toggles by then and they keep the indices they were built with, so
            // the click lands on whatever now sits at that row — nothing, or the wrong player.
            // The roster is re-read the moment it closes, so nothing is lost by waiting.
            if (_followTargetDropdown.DropdownComponent != null &&
                _followTargetDropdown.DropdownComponent.IsExpanded) return;

            var remotes = Basis.Scripts.Networking.BasisNetworkPlayers.RemotePlayers;

            // The first build is never optional. An empty roster and an empty list agree, so the
            // change check answers "nothing moved" and the dropdown would keep the placeholder
            // options its prefab shipped with — which is what an instance with nobody else in it
            // looks like, and the rows stand for no player at all.
            if (_followTargetsBuilt && !FollowTargetRosterChanged(remotes)) return;
            _followTargetsBuilt = true;

            _followTargetIds.Clear();
            // Entries are the net ids, not the names: PanelDropdown resolves its selection by
            // string-matching the entry, so two players sharing a display name (or one named "Me")
            // would both resolve to the first match and follow the wrong player. "Me" is keyed
            // "local" rather than "0" because 0 is the net id of the first player to join.
            var keys = new List<string> { "local" };
            var labels = new List<string> { "Me" };

            foreach (var pair in remotes)
            {
                if (pair.Value == null) continue;
                _followTargetIds.Add(pair.Key);
            }

            // A ConcurrentDictionary enumerates in bucket order, which reshuffles as players come
            // and go — so the same roster could list in a different order on every rebuild and
            // move a name out from under the cursor. Net id is stable and is join order.
            _followTargetIds.Sort();

            for (int index = 0; index < _followTargetIds.Count; index++)
            {
                ushort id = _followTargetIds[index];
                keys.Add(id.ToString());
                labels.Add(remotes.TryGetValue(id, out var remote) && !string.IsNullOrEmpty(remote.SafeDisplayName)
                    ? remote.SafeDisplayName
                    : $"Player {id}");
            }

            _followTargetDropdown.AssignEntries(keys, labels);

            int selected = 0;
            if (_activeCamera.TryGetFollowTargetPlayer(out ushort bound))
            {
                int row = _followTargetIds.IndexOf(bound);
                if (row >= 0) selected = row + 1;
            }
            _followTargetDropdown.SetValueWithoutNotify(keys[selected]);
            ForceLayoutRebuild(_followGroup);
        }

        private bool FollowTargetRosterChanged(
            System.Collections.Concurrent.ConcurrentDictionary<ushort, Basis.Scripts.BasisSdk.Players.BasisRemotePlayer> remotes)
        {
            // Counted the way the rebuild lists them. The rebuild skips null values — a player
            // part-way through teardown — so counting the raw dictionary reported a change on
            // every tick for as long as one sat in the map, rebuilding the dropdown and forcing a
            // layout pass every frame, which is also what made an open list impossible to click.
            int live = 0;
            foreach (var pair in remotes)
            {
                if (pair.Value != null) live++;
            }

            if (live != _followTargetIds.Count) return true;

            for (int index = 0; index < _followTargetIds.Count; index++)
            {
                if (!remotes.TryGetValue(_followTargetIds[index], out var remote) || remote == null) return true;
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
        // The focus mode itself is owned by BasisHandHeldCameraUI, so the panel and the prop's own
        // Auto/Manual buttons cannot leave the two halves of it disagreeing. Follow = the depth of
        // field tracks the subject's distance every frame (UpdateAutoFocus); Manual = the
        // focus-distance slider. Follow needs DoF on to show anything, so it forces it on.
        private void SetFocusFollowsSubject(bool follows)
        {
            if (_activeCamera == null) return;

            _activeCamera.HandHeld.SetFocusFollowsSubject(follows);
            if (follows)
            {
                _activeCamera.BasisDOFInteractionHandler?.SetDoFState(true);
                SyncDoFEnabledToggle();
                SeedDoFModeDropdown();
            }

            _focusModeDropdown?.SetValueWithoutNotify(FocusModeKeys[follows ? 0 : 1]);
            RefreshDoFModeVisibility();
        }

        /// <summary>
        /// The panel half of the camera's own depth of field switch. It writes through the same
        /// interaction handler the prop's toggle does, so the two surfaces can never disagree —
        /// and that handler promotes a stored blur style of Off to Bokeh on the way on, which is
        /// why the style dropdown is re-seeded afterwards rather than left showing Off.
        /// </summary>
        private void SetDepthOfFieldEnabled(bool enabled)
        {
            if (_activeCamera == null) return;

            _activeCamera.BasisDOFInteractionHandler?.SetDoFState(enabled);
            SyncDoFEnabledToggle();
            SeedDoFModeDropdown();
            RefreshDoFModeVisibility();
        }

        // The effect is the state, not the widget: a camera with no interaction handler never took
        // the write, so the toggle is re-seeded from what actually happened rather than from the click.
        private void SyncDoFEnabledToggle()
        {
            if (_activeCamera == null) return;
            _lastDofEnabled = DepthOfFieldIsEnabled;
            _dofEnabledToggle?.SetValueWithoutNotify(DepthOfFieldIsEnabled);
        }

        private bool DepthOfFieldIsEnabled =>
            _activeCamera != null && _activeCamera.MetaData.depthOfField != null && _activeCamera.MetaData.depthOfField.active;

        private void SeedDoFModeDropdown()
        {
            if (_activeCamera == null || _dofModeDropdown == null) return;
            _dofModeDropdown.SetValueWithoutNotify(DoFModeValues[Mathf.Clamp(_activeCamera.HandHeld.DoFMode, 0, DoFModeValues.Length - 1)]);
        }

        private void RefreshDoFModeVisibility()
        {
            if (_activeCamera == null) return;
            bool enabled = DepthOfFieldIsEnabled;
            int mode = _activeCamera.HandHeld.DoFMode;
            bool bokeh = enabled && mode == 2;
            bool anyBlur = enabled && mode != 0;

            // With the effect off nothing below the switch changes the picture, so the section
            // shrinks to the switch — the same thing the prop's HUD does with its own sliders.
            _dofModeDropdown?.gameObject.SetActive(enabled);
            _focusModeDropdown?.gameObject.SetActive(enabled);
            // Follow Subject only drives the focus while there is something to track; without that
            // the slider is the only way to focus, so it has to stay reachable.
            _focusSlider?.gameObject.SetActive(anyBlur && !_activeCamera.HandHeld.AutoFocusIsDriving);
            _apertureSlider?.gameObject.SetActive(bokeh);
            _dofFocalLengthSlider?.gameObject.SetActive(bokeh);
            _dofBladeCountSlider?.gameObject.SetActive(bokeh);
            RefreshFocusSubjectNotice(false);
            RefreshSearch();
            ForceLayoutRebuild(_dofGroup);
        }

        /// <summary>
        /// Says so when Follow Subject focus has nobody to keep sharp. Polled rather than pushed:
        /// the state turns over when a slot is fitted or a follow target is picked, both of which
        /// happen on the Modifiers page with this one built and out of sight. Edge-gated, so an
        /// unchanged notice never dirties the layout.
        /// </summary>
        /// <param name="rebuildLayout">
        /// False while a caller is about to rebuild the group anyway, so the row is not measured twice.
        /// </param>
        private void RefreshFocusSubjectNotice(bool rebuildLayout = true)
        {
            if (_focusSubjectNotice == null) return;

            bool warn = _activeCamera != null && _activeCamera.AutoFocusHasNoSubject && DepthOfFieldIsEnabled;
            if (_lastFocusHasNoSubject == warn) return;
            _lastFocusHasNoSubject = warn;

            _focusSubjectNotice.gameObject.SetActive(warn);
            RefreshSearch();
            if (rebuildLayout) ForceLayoutRebuild(_dofGroup);
        }

        /// <summary>
        /// The three controls that shape the overlay follow the toggle that produces it, the way
        /// the motion blur shape controls follow its strength.
        /// </summary>
        private void RefreshFocusPeakingVisibility()
        {
            bool peaking = _activeCamera != null && _activeCamera.focusPeakingEnabled;

            _focusPeakingColourDropdown?.gameObject.SetActive(peaking);
            _focusPeakingSensitivitySlider?.gameObject.SetActive(peaking);
            _focusPeakingGreyToggle?.gameObject.SetActive(peaking);
            RefreshSearch();
            ForceLayoutRebuild(_peakingGroup);
        }

        /// <summary>
        /// Which grid and how strongly it is drawn only describe a grid that is being drawn, so
        /// they follow the toggle that produces it the way the peaking controls follow theirs.
        /// </summary>
        private void RefreshViewfinderGridVisibility()
        {
            bool gridding = _activeCamera != null && _activeCamera.viewfinderGridEnabled;

            _viewfinderGridPatternDropdown?.gameObject.SetActive(gridding);
            _viewfinderGridOpacitySlider?.gameObject.SetActive(gridding);
            RefreshSearch();
            ForceLayoutRebuild(_gridGroup);
        }

        /// <summary>
        /// The clamp, quality and mode only describe blur that is already happening — at zero
        /// strength URP does not run the pass at all, so leaving them on screen offers three
        /// controls that visibly do nothing.
        /// </summary>
        private void RefreshMotionBlurVisibility()
        {
            if (_activeCamera == null) return;

            bool blurring = _activeCamera.MetaData.motionBlur != null
                && _activeCamera.MetaData.motionBlur.intensity.value > 0f;

            _motionBlurClampSlider?.gameObject.SetActive(blurring);
            _motionBlurQualityDropdown?.gameObject.SetActive(blurring);
            _motionBlurModeDropdown?.gameObject.SetActive(blurring);
            RefreshSearch();
            ForceLayoutRebuild(_motionBlurGroup);
        }

#if Basis_VOLUMETRIC_SUPPORTED
        private void RefreshVolumetricFogVisibility()
        {
            bool overriding = _activeCamera != null && _activeCamera.OverrideVolumetricFog;
            _fogSlider?.gameObject.SetActive(overriding);
            RefreshSearch();
            ForceLayoutRebuild(_fogGroup);
        }
#endif

        private void RefreshPaniniVisibility()
        {
            if (_activeCamera == null) return;

            bool projecting = _activeCamera.MetaData.paniniProjection != null
                && _activeCamera.MetaData.paniniProjection.distance.value > 0f;

            _paniniCropSlider?.gameObject.SetActive(projecting);
            RefreshSearch();
            ForceLayoutRebuild(_lensEffectsGroup);
        }

        private void TickRenderRateLock()
        {
            if (_limitRenderRateToggle == null) return;

            SyncToggle(_limitRenderRateToggle, BasisSettingsDefaults.LimitHandHeldCameraRate.RawValue, ref _lastRenderRateLimit);
            SyncSlider(_renderRateSlider, BasisSettingsDefaults.HandHeldCameraRenderHz.RawValue, ref _lastRenderRateHz);

            bool pinned = BasisHandHeldCamera.IsRenderRatePinnedByRecording;
            if (_renderRatePinned == pinned) return;
            _renderRatePinned = pinned;

            if (_limitRenderRateToggle.ToggleComponent != null)
            {
                _limitRenderRateToggle.ToggleComponent.interactable = !pinned;
            }
            if (_renderRateLockNotice != null) _renderRateLockNotice.gameObject.SetActive(pinned);

            RefreshSearch();
            ForceLayoutRebuild(_performanceGroup);
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

#if UNITY_INCLUDE_TESTS
        // The dropdowns resolve a selection by its position in these tables, so a table that has
        // drifted from the enum it stands in for silently selects the wrong entry rather than
        // failing. Exposed so that correspondence can be asserted.
        public static string[] DetachedMarkerKeysForTest => DetachedMarkerKeys;

        /// <summary>Every option key the camera panel's concept dropdowns offer, for the text sweep.</summary>
        public static string[] OptionKeysForTest
        {
            get
            {
                var all = new System.Collections.Generic.List<string>();
                all.AddRange(DetachedMarkerKeys);
                all.AddRange(AnchorSpaceKeys);
                all.Add(AnchorTargetNoneKey);
                all.Add(AnchorTargetLocalKey);
                all.AddRange(SubjectLabelKeys);
                all.AddRange(AimPointKeys);
                all.AddRange(PositionLabelKeys);
                all.AddRange(RotationLabelKeys);
                all.AddRange(BindingModeKeys);
                all.AddRange(NoiseProfileKeys);
                all.AddRange(DollyModeKeys);
                all.AddRange(BackgroundModeKeys);
                all.AddRange(DollySyncKeys);
                all.AddRange(DollyEaseKeys);
                all.AddRange(TonemappingKeys);
                all.AddRange(PhotoTaggingKeys);
                all.AddRange(BasisHandHeldCamera.FocusPeakingColourKeys);
                all.AddRange(BasisHandHeldCamera.GridPatternKeys);
                all.AddRange(MeteringKeys);
                all.AddRange(GrainTypeKeys);
                all.AddRange(BasisCameraStreamPresets.OptionKeys);
                for (int Index = 0; Index < BasisCameraModifiers.Effects.Length; Index++)
                {
                    all.Add(BasisCameraModifiers.Effects[Index].NameKey);
                }
                return all.ToArray();
            }
        }

        public static string[] DollyEaseKeysForTest => DollyEaseKeys;
        public static string[] AimPointKeysForTest => AimPointKeys;
        public static string[] FocusModeLabelsForTest => FocusModeKeys;
        public static int[] MsaaSampleCountsForTest => MsaaSampleCounts;
        public static int[] VideoResolutionWidthsForTest => VideoResolutionWidths;
        public static int[] VideoResolutionHeightsForTest => VideoResolutionHeights;
        public const int WebPortMinForTest = WebPortMin;
        public const int WebPortMaxForTest = WebPortMax;
#endif
    }
}
