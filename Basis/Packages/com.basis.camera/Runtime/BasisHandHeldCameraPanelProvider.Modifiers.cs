using System.Collections.Generic;
using Basis.Cinematics;
using Basis.Scripts.Networking;
using UnityEngine;

namespace Basis.BasisUI.HandHeldCamera
{
    /// <summary>
    /// Panel surface for the modifier stack: the position slot, the rotation slot, the effects
    /// fitted on top, the placed dolly queue, and the capture background.
    ///
    /// <para>Every control here belongs to exactly one modifier and is shown only while that
    /// modifier is fitted, so there is no longer any such thing as a setting that is present but
    /// does nothing.</para>
    /// </summary>
    public partial class BasisHandHeldCameraPanelProvider
    {
        // Option lists are localization keys, not text. PanelDropdown resolves a selection by
        // string-matching the entry, so translated text as the value makes the selection follow the
        // language — and the key doubles as the stem its per-option tooltip is looked up from.
        private static readonly string[] DollyModeKeys =
        {
            "camera.dollyMode.manual", "camera.dollyMode.followSubject", "camera.dollyMode.play",
        };

        /// <summary>
        /// One entry per <see cref="BasisCameraEase"/>, in enum order — both ease dropdowns read
        /// their selection back as an index into this.
        /// </summary>
        private static readonly string[] DollyEaseKeys =
        {
            "camera.dollyEase.linear", "camera.dollyEase.sine", "camera.dollyEase.quad",
            "camera.dollyEase.cubic", "camera.dollyEase.quart", "camera.dollyEase.quint",
            "camera.dollyEase.expo", "camera.dollyEase.circ", "camera.dollyEase.back",
            "camera.dollyEase.elastic", "camera.dollyEase.bounce",
        };

        private static readonly string[] DollySyncKeys =
        {
            "camera.dollySync.local", "camera.dollySync.networked", "camera.dollySync.locked",
        };

        private static readonly string[] BindingModeKeys =
        {
            "camera.bindingMode.subjectYaw", "camera.bindingMode.worldSpace", "camera.bindingMode.simpleFollow",
        };

        private static readonly string[] NoiseProfileKeys =
        {
            "camera.noiseProfile.off", "camera.noiseProfile.handheld", "camera.noiseProfile.documentary",
            "camera.noiseProfile.drone", "camera.noiseProfile.shaky", "camera.noiseProfile.custom",
        };

        private static readonly string[] BackgroundModeKeys =
        {
            "camera.background.world", "camera.background.greenScreen", "camera.background.blueScreen",
            "camera.background.black", "camera.background.white", "camera.background.magenta",
            "camera.background.custom", "camera.background.transparent",
        };

        private PanelDropdown _subjectDropdown;
        private RectTransform _groupRefreshRow;
        private RectTransform _fixedPointRow;
        private PanelButton _lookAtPickButton;
        private bool? _lastLookAtArmed;
        private BasisCameraSubjectModifier? _lastSubjectModifier;

        private PanelSectionToggle _positionSection;
        private PanelElementDescriptor _positionGroup;
        private PanelSectionToggle _positionAdvancedSection;
        private PanelElementDescriptor _positionAdvancedGroup;
        private PanelDropdown _positionDropdown;
        private PanelDropdown _bindingModeDropdown;
        private PanelSlider _placeOffsetXSlider;
        private PanelSlider _placeOffsetYSlider;
        private PanelSlider _placeOffsetZSlider;
        private PanelSlider _placeDampXSlider;
        private PanelSlider _placeDampYSlider;
        private PanelSlider _placeDampZSlider;
        private PanelSlider _placeTeleportSlider;
        private PanelSlider _followLateralSlider;
        private PanelSlider _framingSizeSlider;
        private PanelToggle _framingZoomToggle;
        private PanelSlider _framingMinSlider;
        private PanelSlider _framingMaxSlider;
        private PanelToggle _orbitFollowHeadingToggle;
        private PanelSlider _orbitHeadingSlider;
        private PanelSlider _orbitVerticalSlider;
        private PanelSlider _orbitHeadingDampSlider;
        private PanelSlider _orbitTopHeightSlider;
        private PanelSlider _orbitTopRadiusSlider;
        private PanelSlider _orbitMidHeightSlider;
        private PanelSlider _orbitMidRadiusSlider;
        private PanelSlider _orbitBottomHeightSlider;
        private PanelSlider _orbitBottomRadiusSlider;
        private PanelDropdown _dollyModeDropdown;
        private RectTransform _dollyTransportRow;
        private PanelButton _dollyPlayButton;
        private bool? _lastDollyPlaying;
        private PanelSlider _dollyPositionSlider;
        private PanelSlider _dollySpeedSlider;
        private PanelDropdown _dollyEaseInDropdown;
        private PanelSlider _dollyEaseInPortionSlider;
        private PanelDropdown _dollyEaseOutDropdown;
        private PanelSlider _dollyEaseOutPortionSlider;
        private PanelSlider _dollyDampSlider;
        private PanelSlider _dollyOffsetXSlider;
        private PanelSlider _dollyOffsetYSlider;
        private PanelSlider _dollyOffsetZSlider;

        private PanelSectionToggle _rotationSection;
        private PanelElementDescriptor _rotationGroup;
        private PanelSectionToggle _rotationAdvancedSection;
        private PanelElementDescriptor _rotationAdvancedGroup;
        private PanelDropdown _rotationDropdown;
        private PanelSlider _aimPitchSlider;
        private PanelSlider _aimYawSlider;
        private PanelSlider _aimDampSlider;
        private PanelSlider _screenXSlider;
        private PanelSlider _screenYSlider;
        private PanelSlider _deadZoneWidthSlider;
        private PanelSlider _deadZoneHeightSlider;
        private PanelSlider _softZoneWidthSlider;
        private PanelSlider _softZoneHeightSlider;
        private PanelSlider _composerDampHSlider;
        private PanelSlider _composerDampVSlider;
        private PanelSlider _composerBiasXSlider;
        private PanelSlider _composerBiasYSlider;
        private PanelToggle _guidesToggle;
        private const bool ShowGuidesDefault = true;
        private bool _showGuides = ShowGuidesDefault;

        private PanelSectionToggle _modifierEffectsSection;
        private PanelElementDescriptor _modifierEffectsGroup;
        private PanelDropdown _effectAddDropdown;
        private readonly List<BasisCameraEffectModifier> _addableEffects = new List<BasisCameraEffectModifier>();
        private readonly List<BasisCameraPositionModifier> _positionChoices = new List<BasisCameraPositionModifier>(BasisCameraModifiers.PositionModifiers);
        private readonly List<BasisCameraRotationModifier> _rotationChoices = new List<BasisCameraRotationModifier>(BasisCameraModifiers.RotationModifiers);
        private readonly List<BasisCameraDollyMode> _dollyModeChoices = new List<BasisCameraDollyMode> { BasisCameraDollyMode.Manual, BasisCameraDollyMode.FollowSubject, BasisCameraDollyMode.Play };
        private int _lastChoiceSignature = -1;
        private PanelElementDescriptor _modifierEffectsEmptyState;
        private PanelSlider _lookAheadTimeSlider;
        private PanelSlider _lookAheadLimitSlider;
        private PanelSlider _occlusionPaddingSlider;
        private PanelSlider _occlusionMinSlider;
        private PanelSlider _occlusionReturnSlider;
        private PanelSlider _occlusionRadiusSlider;
        private PanelDropdown _noiseProfileDropdown;
        private PanelSlider _noiseAmplitudeSlider;
        private PanelSlider _noiseFrequencySlider;
        private PanelSlider _lensFovSlider;
        private PanelSlider _lensDampSlider;
        private PanelSlider _steadySmoothingSlider;
        private PanelSlider _steadyDeadZoneSlider;
        private PanelSlider _collisionRadiusSlider;
        private PanelSlider _collisionPaddingSlider;
        private PanelSlider _dollyZoomMinSlider;
        private PanelSlider _dollyZoomMaxSlider;
        private PanelSlider _rigWeightResponseSlider;
        private PanelSlider _rigWeightBounceSlider;
        private readonly Dictionary<BasisCameraEffectModifier, PanelSectionToggle> _effectSections =
            new Dictionary<BasisCameraEffectModifier, PanelSectionToggle>();
        private readonly Dictionary<BasisCameraEffectModifier, PanelElementDescriptor> _effectGroups =
            new Dictionary<BasisCameraEffectModifier, PanelElementDescriptor>();
        private int _lastEffectSignature = -1;
        private BasisCameraPositionModifier? _lastPositionModifier;
        private BasisCameraRotationModifier? _lastRotationModifier;

        private PanelElementDescriptor _dollyGroup;
        private PanelDropdown _waypointDropdown;
        private PanelSlider _waypointOrderSlider;
        private PanelToggle _dollyLoopToggle;
        private PanelToggle _dollyVisibleToggle;
        private PanelDropdown _dollySyncDropdown;
        private PanelToggle _dollyGridSnapToggle;
        private PanelSlider _dollyGridSizeSlider;
        private PanelToggle _dollySpeedColorToggle;
        private PanelElementDescriptor _dollyEmptyState;

        private PanelElementDescriptor _dollyPresetStatus;
        private PanelDropdown _dollyPresetDropdown;
        private PanelTextField _dollyPresetNameField;
        private PanelButton _dollyPresetSaveButton;
        private PanelButton _dollyPresetLoadButton;
        private PanelButton _dollyPresetLoadInPlaceButton;
        private PanelButton _dollyPresetRemoveButton;
        private PanelButton _dollyPresetExportButton;
        private PanelButton _dollyPresetShareButton;
        private readonly List<string> _dollyPresetKeys = new List<string>();
        private int _lastDollyPresetRevision = -1;
        private readonly List<string> _waypointKeys = new List<string>();
        private int _selectedWaypointIndex;
        private int _lastWaypointCount = -1;

        private PanelSectionToggle _backgroundSection;
        private PanelElementDescriptor _backgroundGroup;
        private PanelDropdown _backgroundModeDropdown;
        private PanelToggle _backgroundKeepWorldToggle;
        private PanelSlider _backgroundRedSlider;
        private PanelSlider _backgroundGreenSlider;
        private PanelSlider _backgroundBlueSlider;

        /// <summary>The stack the panel edits, or null while no camera is selected.</summary>
        private BasisCameraModifierStack Stack => _activeCamera?.Modifiers;

        private static readonly string[] SubjectLabelKeys = BuildSubjectLabelKeys();
        private static readonly string[] PositionLabelKeys = BuildPositionLabelKeys();
        private static readonly string[] RotationLabelKeys = BuildRotationLabelKeys();
        private static readonly string[] AimPointKeys = BuildAimPointKeys();

        private static string[] BuildSubjectLabelKeys()
        {
            var keys = new string[BasisCameraModifiers.SubjectModifiers.Length];
            for (int Index = 0; Index < keys.Length; Index++)
            {
                keys[Index] = BasisCameraModifiers.NameKey(BasisCameraModifiers.SubjectModifiers[Index]);
            }
            return keys;
        }

        private static string[] BuildPositionLabelKeys()
        {
            var keys = new string[BasisCameraModifiers.PositionModifiers.Length];
            for (int Index = 0; Index < keys.Length; Index++)
            {
                keys[Index] = BasisCameraModifiers.NameKey(BasisCameraModifiers.PositionModifiers[Index]);
            }
            return keys;
        }

        private static string[] BuildRotationLabelKeys()
        {
            var keys = new string[BasisCameraModifiers.RotationModifiers.Length];
            for (int Index = 0; Index < keys.Length; Index++)
            {
                keys[Index] = BasisCameraModifiers.NameKey(BasisCameraModifiers.RotationModifiers[Index]);
            }
            return keys;
        }

        private static string[] BuildAimPointKeys()
        {
            var keys = new string[BasisCameraModifiers.AimPoints.Length];
            for (int Index = 0; Index < keys.Length; Index++)
            {
                keys[Index] = BasisCameraModifiers.NameKey(BasisCameraModifiers.AimPoints[Index]);
            }
            return keys;
        }

        /// <summary>The matching <c>.description</c> key for each option, used as its hover text.</summary>
        private static List<string> DescriptionKeys(string[] keys)
        {
            var list = new List<string>(keys.Length);
            for (int Index = 0; Index < keys.Length; Index++)
            {
                list.Add(keys[Index] + ".description");
            }
            return list;
        }

        private static List<string> LocalizedList(string[] keys)
        {
            var list = new List<string>(keys.Length);
            for (int Index = 0; Index < keys.Length; Index++)
            {
                list.Add(BasisLocalization.Get(keys[Index]));
            }
            return list;
        }

        private void BuildModifierSections(RectTransform parent)
        {
            BuildAnchorGroup(parent);
            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_anchorSection, _anchorGroup, true, OnSectionExpanded);

            BuildPositionGroup(parent);

            // The track is what Dolly Track rides, so it is built into the position slot's own
            // content as one more block of that slot's rows — shown and hidden with them rather
            // than behind a header of its own. Built before the position group is finalized so its
            // rows are added while that group is still active.
            BuildDollyGroup(_positionGroup.ContentParent);

            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_positionAdvancedSection, _positionAdvancedGroup, false, OnSectionExpanded);
            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_positionSection, _positionGroup, true, OnSectionExpanded);

            BuildRotationGroup(parent);
            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_rotationAdvancedSection, _rotationAdvancedGroup, false, OnSectionExpanded);
            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_rotationSection, _rotationGroup, true, OnSectionExpanded);

            BuildSubjectGroup(parent);
            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_followSection, _followGroup, true, OnSectionExpanded);
        }

        /// <summary>The effects page: everything layered on top of whatever the slots are doing.</summary>
        private void BuildEffectSections(RectTransform parent)
        {
            BuildModifierEffectsGroup(parent);
            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(_modifierEffectsSection, _modifierEffectsGroup, true, OnSectionExpanded);
        }

        // ---- Position slot ---------------------------------------------------------------------

        private void BuildPositionGroup(RectTransform parent)
        {
            _positionSection = PanelSectionToggle.CreateNewEntry(parent);
            _positionGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _positionSection, parent, BasisLocalization.Get(BasisCameraModifiers.PositionSlotKey), false);
            RectTransform content = _positionGroup.ContentParent;

            _positionDropdown = PanelDropdown.CreateNewEntry(content);
            _positionDropdown.Descriptor.SetTitle(BasisLocalization.Get(BasisCameraModifiers.PositionSlotKey));
            _positionDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.modifier.position.description"));
            _positionDropdown.AssignLocalizedEntries(
                new List<string>(PositionLabelKeys), new List<string>(PositionLabelKeys),
                DescriptionKeys(PositionLabelKeys));
            _positionDropdown.OnValueChanged = _ =>
            {
                int index = _positionDropdown != null ? _positionDropdown.Index : -1;
                if (_activeCamera == null || index < 0 || index >= _positionChoices.Count) return;

                _activeCamera.SetPositionModifier(_positionChoices[index]);
                RefreshDoFModeVisibility();
                RefreshModifierVisibility();
            };

            _positionAdvancedSection = PanelSectionToggle.CreateNewEntry(content);
            _positionAdvancedGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _positionAdvancedSection, content, BasisLocalization.Get("camera.modifier.advanced"), false);
            _positionAdvancedSection.Descriptor.SetTooltip(BasisLocalization.Get("camera.modifier.advanced.description"));
            content = _positionAdvancedGroup.ContentParent;

            _bindingModeDropdown = PanelDropdown.CreateNewEntry(content);
            _bindingModeDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.bindingMode"));
            _bindingModeDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.bindingMode.description"));
            _bindingModeDropdown.AssignLocalizedEntries(
                new List<string>(BindingModeKeys), new List<string>(BindingModeKeys));
            _bindingModeDropdown.OnValueChanged = _ =>
            {
                int index = _bindingModeDropdown != null ? _bindingModeDropdown.Index : -1;
                if (index < 0 || Stack == null) return;

                if (Stack.positionModifier == BasisCameraPositionModifier.FrameSubject)
                {
                    Stack.framing.bindingMode = (BasisCameraBindingMode)index;
                }
                else
                {
                    Stack.follow.bindingMode = (BasisCameraBindingMode)index;
                }
            };

            _placeOffsetXSlider = PanelSlider.CreateNew(content);
            _placeOffsetXSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.sideOffsetX"), -5f, 5f, false, 2, ValueDisplayMode.Meters));
            _placeOffsetXSlider.OnValueChanged = v => SetPlacementOffsetAxis(0, v);

            _followLateralSlider = PanelSlider.CreateNew(content);
            _followLateralSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.lateralTrackingX"), 0f, 1f, false, 2, ValueDisplayMode.Raw));
            _followLateralSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.lateralTrackingX.description"));
            _followLateralSlider.OnValueChanged = v =>
            {
                if (Stack != null) Stack.follow.lateralTracking = v;
            };

            _placeOffsetYSlider = PanelSlider.CreateNew(content);
            _placeOffsetYSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.heightOffsetY"), -3f, 3f, false, 2, ValueDisplayMode.Meters));
            _placeOffsetYSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.heightOffsetY.description"));
            _placeOffsetYSlider.OnValueChanged = v => SetPlacementOffsetAxis(1, v);

            _placeOffsetZSlider = PanelSlider.CreateNew(content);
            _placeOffsetZSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.distanceZ"), -8f, 8f, false, 2, ValueDisplayMode.Meters));
            _placeOffsetZSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.distanceZ.description"));
            _placeOffsetZSlider.OnValueChanged = v => SetPlacementOffsetAxis(2, v);

            _placeDampXSlider = PanelSlider.CreateNew(content);
            _placeDampXSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.dampSideways"), 0f, 4f, false, 2, ValueDisplayMode.Raw));
            _placeDampXSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.damp.description"));
            _placeDampXSlider.OnValueChanged = v => SetPlacementDampingAxis(0, v);

            _placeDampYSlider = PanelSlider.CreateNew(content);
            _placeDampYSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.dampVertical"), 0f, 4f, false, 2, ValueDisplayMode.Raw));
            _placeDampYSlider.OnValueChanged = v => SetPlacementDampingAxis(1, v);

            _placeDampZSlider = PanelSlider.CreateNew(content);
            _placeDampZSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.dampApproach"), 0f, 4f, false, 2, ValueDisplayMode.Raw));
            _placeDampZSlider.OnValueChanged = v => SetPlacementDampingAxis(2, v);

            _placeTeleportSlider = PanelSlider.CreateNew(content);
            _placeTeleportSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.teleportDistance"), 1f, 40f, false, 1, ValueDisplayMode.Meters));
            _placeTeleportSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.teleportDistance.description"));
            _placeTeleportSlider.OnValueChanged = v =>
            {
                if (Stack == null) return;
                if (Stack.positionModifier == BasisCameraPositionModifier.FrameSubject)
                {
                    Stack.framing.teleportDistance = v;
                }
                else
                {
                    Stack.follow.teleportDistance = v;
                }
            };

            _framingSizeSlider = PanelSlider.CreateNew(content);
            _framingSizeSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.framingSize"), 0.05f, 1f, false, 2, ValueDisplayMode.Raw));
            _framingSizeSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.framingSize.description"));
            _framingSizeSlider.OnValueChanged = v =>
            {
                if (Stack != null) Stack.framing.screenFraction = v;
            };

            _framingZoomToggle = PanelToggle.CreateNewEntry(content);
            _framingZoomToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.framingZoom"));
            _framingZoomToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.framingZoom.description"));
            _framingZoomToggle.OnValueChanged = v =>
            {
                if (Stack != null) Stack.framing.usesZoom = v;
            };

            _framingMinSlider = PanelSlider.CreateNew(content);
            _framingMinSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.framingMin"), 0.1f, 5f, false, 2, ValueDisplayMode.Meters));
            _framingMinSlider.OnValueChanged = v =>
            {
                if (Stack != null) Stack.framing.minDistance = v;
            };

            _framingMaxSlider = PanelSlider.CreateNew(content);
            _framingMaxSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.framingMax"), 1f, 30f, false, 2, ValueDisplayMode.Meters));
            _framingMaxSlider.OnValueChanged = v =>
            {
                if (Stack != null) Stack.framing.maxDistance = v;
            };

            _orbitFollowHeadingToggle = PanelToggle.CreateNewEntry(content);
            _orbitFollowHeadingToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.orbitFollowHeading"));
            _orbitFollowHeadingToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.orbitFollowHeading.description"));
            _orbitFollowHeadingToggle.OnValueChanged = v =>
            {
                if (Stack != null) Stack.orbit.followSubjectHeading = v;
            };

            _orbitHeadingSlider = PanelSlider.CreateNew(content);
            _orbitHeadingSlider.SetSliderSettings(PanelSlider.SliderSettings.Degrees(
                BasisLocalization.Get("camera.orbitHeading"), -180f, 180f, false, 1));
            _orbitHeadingSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.orbitHeading.description"));
            _orbitHeadingSlider.OnValueChanged = v =>
            {
                if (Stack != null) Stack.orbit.heading = v;
            };

            _orbitVerticalSlider = PanelSlider.CreateNew(content);
            _orbitVerticalSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.orbitVertical"), 0f, 1f, false, 2, ValueDisplayMode.Raw));
            _orbitVerticalSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.orbitVertical.description"));
            _orbitVerticalSlider.OnValueChanged = v =>
            {
                if (Stack != null) Stack.orbit.verticalAxis = v;
            };

            _orbitHeadingDampSlider = PanelSlider.CreateNew(content);
            _orbitHeadingDampSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.orbitDamping"), 0f, 4f, false, 2, ValueDisplayMode.Raw));
            _orbitHeadingDampSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.orbitDamping.description"));
            _orbitHeadingDampSlider.OnValueChanged = v =>
            {
                if (Stack == null) return;
                Stack.orbit.headingDamping = v;
                Stack.orbit.verticalDamping = v;
            };

            _orbitTopHeightSlider = PanelSlider.CreateNew(content);
            _orbitTopHeightSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.orbitTopHeight"), -1f, 5f, false, 2, ValueDisplayMode.Meters));
            _orbitTopHeightSlider.OnValueChanged = v =>
            {
                if (Stack != null) Stack.orbit.top.height = v;
            };

            _orbitTopRadiusSlider = PanelSlider.CreateNew(content);
            _orbitTopRadiusSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.orbitTopRadius"), 0f, 8f, false, 2, ValueDisplayMode.Meters));
            _orbitTopRadiusSlider.OnValueChanged = v =>
            {
                if (Stack != null) Stack.orbit.top.radius = v;
            };

            _orbitMidHeightSlider = PanelSlider.CreateNew(content);
            _orbitMidHeightSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.orbitMidHeight"), -2f, 4f, false, 2, ValueDisplayMode.Meters));
            _orbitMidHeightSlider.OnValueChanged = v =>
            {
                if (Stack != null) Stack.orbit.middle.height = v;
            };

            _orbitMidRadiusSlider = PanelSlider.CreateNew(content);
            _orbitMidRadiusSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.orbitMidRadius"), 0f, 8f, false, 2, ValueDisplayMode.Meters));
            _orbitMidRadiusSlider.OnValueChanged = v =>
            {
                if (Stack != null) Stack.orbit.middle.radius = v;
            };

            _orbitBottomHeightSlider = PanelSlider.CreateNew(content);
            _orbitBottomHeightSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.orbitBottomHeight"), -3f, 3f, false, 2, ValueDisplayMode.Meters));
            _orbitBottomHeightSlider.OnValueChanged = v =>
            {
                if (Stack != null) Stack.orbit.bottom.height = v;
            };

            _orbitBottomRadiusSlider = PanelSlider.CreateNew(content);
            _orbitBottomRadiusSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.orbitBottomRadius"), 0f, 8f, false, 2, ValueDisplayMode.Meters));
            _orbitBottomRadiusSlider.OnValueChanged = v =>
            {
                if (Stack != null) Stack.orbit.bottom.radius = v;
            };

            _dollyModeDropdown = PanelDropdown.CreateNewEntry(content);
            _dollyModeDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.dollyMode"));
            _dollyModeDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.dollyMode.description"));
            _dollyModeDropdown.AssignLocalizedEntries(
                new List<string>(DollyModeKeys), new List<string>(DollyModeKeys));
            _dollyModeDropdown.OnValueChanged = _ =>
            {
                int index = _dollyModeDropdown != null ? _dollyModeDropdown.Index : -1;
                if (Stack == null || index < 0 || index >= _dollyModeChoices.Count) return;

                Stack.dolly.mode = _dollyModeChoices[index];
                if (Stack.dolly.mode != BasisCameraDollyMode.Play)
                {
                    Stack.dolly.playing = false;
                }
                RefreshDollyTransport();
                RefreshModifierVisibility();
            };

            _dollyTransportRow = PanelElementDescriptor.BuildActionRow(content, "CameraDollyTransportRow");

            _dollyPlayButton = PanelButton.CreateNew(_dollyTransportRow);
            _dollyPlayButton.Descriptor.SetTitle(BasisLocalization.Get("camera.dollyPlay"));
            _dollyPlayButton.Descriptor.SetTooltip(BasisLocalization.Get("camera.dollyPlay.description"));
            _dollyPlayButton.OnClicked += () =>
            {
                if (_activeCamera == null) return;
                _activeCamera.SetDollyPlaying(!_activeCamera.IsDollyPlaying);
                RefreshDollyTransport();
            };

            PanelButton restart = PanelButton.CreateNew(_dollyTransportRow);
            restart.Descriptor.SetTitle(BasisLocalization.Get("camera.dollyRestart"));
            restart.Descriptor.SetTooltip(BasisLocalization.Get("camera.dollyRestart.description"));
            restart.OnClicked += () =>
            {
                _activeCamera?.RestartDolly();
                RefreshDollyTransport();
            };

            _dollyPositionSlider = PanelSlider.CreateNew(content);
            _dollyPositionSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.dollyPosition"), 0f, 32f, false, 2, ValueDisplayMode.Raw));
            _dollyPositionSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.dollyPosition.description"));
            _dollyPositionSlider.OnValueChanged = v =>
            {
                if (Stack != null) Stack.dolly.position = v;
            };

            _dollySpeedSlider = PanelSlider.CreateNew(content);
            _dollySpeedSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.dollySpeed"), -100f, 100f, false, 2, ValueDisplayMode.Raw));
            _dollySpeedSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.dollySpeed.description"));
            _dollySpeedSlider.OnValueChanged = v =>
            {
                if (Stack != null) Stack.dolly.speed = v;
            };

            _dollyEaseInDropdown = PanelDropdown.CreateNewEntry(content);
            _dollyEaseInDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.dollyEaseIn"));
            _dollyEaseInDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.dollyEaseIn.description"));
            _dollyEaseInDropdown.AssignLocalizedEntries(
                new List<string>(DollyEaseKeys), new List<string>(DollyEaseKeys), DescriptionKeys(DollyEaseKeys));
            _dollyEaseInDropdown.OnValueChanged = _ =>
            {
                int index = _dollyEaseInDropdown != null ? _dollyEaseInDropdown.Index : -1;
                if (Stack == null || index < 0 || index >= DollyEaseKeys.Length) return;

                Stack.dolly.easeIn = (BasisCameraEase)index;
            };

            _dollyEaseInPortionSlider = PanelSlider.CreateNew(content);
            _dollyEaseInPortionSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.dollyEaseInPortion"), 0f, BasisCameraDollySpeed.MaximumEasePortion,
                false, 2, ValueDisplayMode.percentageFromZero));
            _dollyEaseInPortionSlider.Descriptor.SetTooltip(
                BasisLocalization.Get("camera.dollyEaseInPortion.description"));
            _dollyEaseInPortionSlider.OnValueChanged = v =>
            {
                if (Stack != null) Stack.dolly.easeInPortion = v;
            };

            _dollyEaseOutDropdown = PanelDropdown.CreateNewEntry(content);
            _dollyEaseOutDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.dollyEaseOut"));
            _dollyEaseOutDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.dollyEaseOut.description"));
            _dollyEaseOutDropdown.AssignLocalizedEntries(
                new List<string>(DollyEaseKeys), new List<string>(DollyEaseKeys), DescriptionKeys(DollyEaseKeys));
            _dollyEaseOutDropdown.OnValueChanged = _ =>
            {
                int index = _dollyEaseOutDropdown != null ? _dollyEaseOutDropdown.Index : -1;
                if (Stack == null || index < 0 || index >= DollyEaseKeys.Length) return;

                Stack.dolly.easeOut = (BasisCameraEase)index;
            };

            _dollyEaseOutPortionSlider = PanelSlider.CreateNew(content);
            _dollyEaseOutPortionSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.dollyEaseOutPortion"), 0f, BasisCameraDollySpeed.MaximumEasePortion,
                false, 2, ValueDisplayMode.percentageFromZero));
            _dollyEaseOutPortionSlider.Descriptor.SetTooltip(
                BasisLocalization.Get("camera.dollyEaseOutPortion.description"));
            _dollyEaseOutPortionSlider.OnValueChanged = v =>
            {
                if (Stack != null) Stack.dolly.easeOutPortion = v;
            };

            _dollyDampSlider = PanelSlider.CreateNew(content);
            _dollyDampSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.dollyDamping"), 0f, 4f, false, 2, ValueDisplayMode.Raw));
            _dollyDampSlider.OnValueChanged = v =>
            {
                if (Stack != null) Stack.dolly.damping = v;
            };

            _dollyOffsetXSlider = PanelSlider.CreateNew(content);
            _dollyOffsetXSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.dollyOffsetX"), -5f, 5f, false, 2, ValueDisplayMode.Meters));
            _dollyOffsetXSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.dollyOffset.description"));
            _dollyOffsetXSlider.OnValueChanged = v => SetDollyOffsetAxis(0, v);

            _dollyOffsetYSlider = PanelSlider.CreateNew(content);
            _dollyOffsetYSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.dollyOffsetY"), -5f, 5f, false, 2, ValueDisplayMode.Meters));
            _dollyOffsetYSlider.OnValueChanged = v => SetDollyOffsetAxis(1, v);

            _dollyOffsetZSlider = PanelSlider.CreateNew(content);
            _dollyOffsetZSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.dollyOffsetZ"), -5f, 5f, false, 2, ValueDisplayMode.Meters));
            _dollyOffsetZSlider.OnValueChanged = v => SetDollyOffsetAxis(2, v);
        }

        private void SetDollyOffsetAxis(int axis, float value)
        {
            if (Stack == null) return;
            Vector3 offset = Stack.dolly.offset;
            offset[axis] = value;
            Stack.dolly.offset = offset;
        }

        /// <summary>
        /// Writes a placement offset axis onto whichever modifier is fitted. Follow and Frame both
        /// author an offset in a binding frame, so they share the three sliders — the control is
        /// live for whatever is in the slot rather than being one modifier's control sitting inert
        /// under another.
        /// </summary>
        private void SetPlacementOffsetAxis(int axis, float value)
        {
            if (Stack == null) return;

            if (Stack.positionModifier == BasisCameraPositionModifier.FrameSubject)
            {
                Vector3 offset = Stack.framing.directionOffset;
                offset[axis] = value;
                Stack.framing.directionOffset = offset;
            }
            else
            {
                Vector3 offset = Stack.follow.positionOffset;
                offset[axis] = value;
                Stack.follow.positionOffset = offset;
            }
        }

        private void SetPlacementDampingAxis(int axis, float value)
        {
            if (Stack == null) return;

            if (Stack.positionModifier == BasisCameraPositionModifier.FrameSubject)
            {
                Vector3 damping = Stack.framing.damping;
                damping[axis] = value;
                Stack.framing.damping = damping;
            }
            else
            {
                Vector3 damping = Stack.follow.damping;
                damping[axis] = value;
                Stack.follow.damping = damping;
            }
        }

        // ---- Rotation slot ---------------------------------------------------------------------

        private void BuildRotationGroup(RectTransform parent)
        {
            _rotationSection = PanelSectionToggle.CreateNewEntry(parent);
            _rotationGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _rotationSection, parent, BasisLocalization.Get(BasisCameraModifiers.RotationSlotKey), false);
            RectTransform content = _rotationGroup.ContentParent;

            _rotationDropdown = PanelDropdown.CreateNewEntry(content);
            _rotationDropdown.Descriptor.SetTitle(BasisLocalization.Get(BasisCameraModifiers.RotationSlotKey));
            _rotationDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.modifier.rotation.description"));
            _rotationDropdown.AssignLocalizedEntries(
                new List<string>(RotationLabelKeys), new List<string>(RotationLabelKeys),
                DescriptionKeys(RotationLabelKeys));
            _rotationDropdown.OnValueChanged = _ =>
            {
                int index = _rotationDropdown != null ? _rotationDropdown.Index : -1;
                if (_activeCamera == null || index < 0 || index >= _rotationChoices.Count) return;

                _activeCamera.SetRotationModifier(_rotationChoices[index]);
                RefreshDoFModeVisibility();
                RefreshModifierVisibility();
            };

            _rotationAdvancedSection = PanelSectionToggle.CreateNewEntry(content);
            _rotationAdvancedGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _rotationAdvancedSection, content, BasisLocalization.Get("camera.modifier.advanced"), false);
            _rotationAdvancedSection.Descriptor.SetTooltip(BasisLocalization.Get("camera.modifier.advanced.description"));
            content = _rotationAdvancedGroup.ContentParent;

            _aimPitchSlider = PanelSlider.CreateNew(content);
            _aimPitchSlider.SetSliderSettings(PanelSlider.SliderSettings.Degrees(
                BasisLocalization.Get("camera.pitchOffsetX"), -90f, 90f, false, 1));
            _aimPitchSlider.OnValueChanged = v => SetAimRotationAxis(0, v);

            _aimYawSlider = PanelSlider.CreateNew(content);
            _aimYawSlider.SetSliderSettings(PanelSlider.SliderSettings.Degrees(
                BasisLocalization.Get("camera.yawOffsetY"), -180f, 180f, false, 1));
            _aimYawSlider.OnValueChanged = v => SetAimRotationAxis(1, v);

            _aimDampSlider = PanelSlider.CreateNew(content);
            _aimDampSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.dampRotation"), 0f, 4f, false, 2, ValueDisplayMode.Raw));
            _aimDampSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.dampRotation.description"));
            _aimDampSlider.OnValueChanged = v =>
            {
                if (Stack == null) return;
                Vector3 damping = new Vector3(v, v, v * 2f);
                switch (Stack.rotationModifier)
                {
                    case BasisCameraRotationModifier.MatchSubject:
                        Stack.matchSubject.damping = damping;
                        break;
                    case BasisCameraRotationModifier.AimAlongTrack:
                        Stack.trackAim.damping = damping;
                        break;
                    default:
                        Stack.lookAt.damping = damping;
                        break;
                }
            };

            _guidesToggle = PanelToggle.CreateNewEntry(content);
            _guidesToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.showGuides"));
            _guidesToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.showGuides.description"));
            _guidesToggle.OnValueChanged = v =>
            {
                _showGuides = v;
                RefreshCompositionGuides();
            };

            _screenXSlider = PanelSlider.CreateNew(content);
            _screenXSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.screenX"), 0f, 1f, false, 2, ValueDisplayMode.Raw));
            _screenXSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.screenX.description"));
            _screenXSlider.OnValueChanged = v =>
            {
                if (Stack != null) Stack.compose.composer.screenX = v;
            };

            _screenYSlider = PanelSlider.CreateNew(content);
            _screenYSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.screenY"), 0f, 1f, false, 2, ValueDisplayMode.Raw));
            _screenYSlider.OnValueChanged = v =>
            {
                if (Stack != null) Stack.compose.composer.screenY = v;
            };

            _deadZoneWidthSlider = PanelSlider.CreateNew(content);
            _deadZoneWidthSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.deadZoneWidth"), 0f, 1f, false, 2, ValueDisplayMode.Raw));
            _deadZoneWidthSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.deadZone.description"));
            _deadZoneWidthSlider.OnValueChanged = v =>
            {
                if (Stack != null) Stack.compose.composer.deadZoneWidth = v;
            };

            _deadZoneHeightSlider = PanelSlider.CreateNew(content);
            _deadZoneHeightSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.deadZoneHeight"), 0f, 1f, false, 2, ValueDisplayMode.Raw));
            _deadZoneHeightSlider.OnValueChanged = v =>
            {
                if (Stack != null) Stack.compose.composer.deadZoneHeight = v;
            };

            _softZoneWidthSlider = PanelSlider.CreateNew(content);
            _softZoneWidthSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.softZoneWidth"), 0f, 2f, false, 2, ValueDisplayMode.Raw));
            _softZoneWidthSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.softZone.description"));
            _softZoneWidthSlider.OnValueChanged = v =>
            {
                if (Stack != null) Stack.compose.composer.softZoneWidth = v;
            };

            _softZoneHeightSlider = PanelSlider.CreateNew(content);
            _softZoneHeightSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.softZoneHeight"), 0f, 2f, false, 2, ValueDisplayMode.Raw));
            _softZoneHeightSlider.OnValueChanged = v =>
            {
                if (Stack != null) Stack.compose.composer.softZoneHeight = v;
            };

            _composerDampHSlider = PanelSlider.CreateNew(content);
            _composerDampHSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.composerDampH"), 0f, 4f, false, 2, ValueDisplayMode.Raw));
            _composerDampHSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.composerDamp.description"));
            _composerDampHSlider.OnValueChanged = v =>
            {
                if (Stack != null) Stack.compose.composer.horizontalDamping = v;
            };

            _composerDampVSlider = PanelSlider.CreateNew(content);
            _composerDampVSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.composerDampV"), 0f, 4f, false, 2, ValueDisplayMode.Raw));
            _composerDampVSlider.OnValueChanged = v =>
            {
                if (Stack != null) Stack.compose.composer.verticalDamping = v;
            };

            _composerBiasXSlider = PanelSlider.CreateNew(content);
            _composerBiasXSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.composerBiasX"), -0.5f, 0.5f, false, 2, ValueDisplayMode.Raw));
            _composerBiasXSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.composerBias.description"));
            _composerBiasXSlider.OnValueChanged = v =>
            {
                if (Stack != null) Stack.compose.composer.biasX = v;
            };

            _composerBiasYSlider = PanelSlider.CreateNew(content);
            _composerBiasYSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.composerBiasY"), -0.5f, 0.5f, false, 2, ValueDisplayMode.Raw));
            _composerBiasYSlider.OnValueChanged = v =>
            {
                if (Stack != null) Stack.compose.composer.biasY = v;
            };
        }

        private void SetAimRotationAxis(int axis, float value)
        {
            if (Stack == null) return;

            switch (Stack.rotationModifier)
            {
                case BasisCameraRotationModifier.Compose:
                {
                    Vector3 offset = Stack.compose.rotationOffset;
                    offset[axis] = value;
                    Stack.compose.rotationOffset = offset;
                    break;
                }
                case BasisCameraRotationModifier.MatchSubject:
                {
                    Vector3 offset = Stack.matchSubject.rotationOffset;
                    offset[axis] = value;
                    Stack.matchSubject.rotationOffset = offset;
                    break;
                }
                case BasisCameraRotationModifier.AimAlongTrack:
                {
                    Vector3 offset = Stack.trackAim.rotationOffset;
                    offset[axis] = value;
                    Stack.trackAim.rotationOffset = offset;
                    break;
                }
                default:
                {
                    Vector3 offset = Stack.lookAt.rotationOffset;
                    offset[axis] = value;
                    Stack.lookAt.rotationOffset = offset;
                    break;
                }
            }
        }

        // ---- Effects ---------------------------------------------------------------------------

        private void BuildModifierEffectsGroup(RectTransform parent)
        {
            _modifierEffectsSection = PanelSectionToggle.CreateNewEntry(parent);
            _modifierEffectsGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _modifierEffectsSection, parent, BasisLocalization.Get(BasisCameraModifiers.EffectsKey), false);
            RectTransform header = _modifierEffectsGroup.ContentParent;

            _effectAddDropdown = PanelDropdown.CreateNewEntry(header);
            _effectAddDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.modifier.addEffect"));
            _effectAddDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.modifier.addEffect.description"));
            _effectAddDropdown.OnValueChanged = _ =>
            {
                int index = _effectAddDropdown != null ? _effectAddDropdown.Index : -1;
                if (_activeCamera == null || index <= 0 || index > _addableEffects.Count) return;

                _activeCamera.AddModifierEffect(_addableEffects[index - 1]);
                _lastEffectSignature = -1;
                RefreshEffectList();
                RefreshModifierVisibility();
            };

            _modifierEffectsEmptyState = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, header);
            _modifierEffectsEmptyState.SetTitle(BasisLocalization.Get("camera.modifier.noEffects"));
            _modifierEffectsEmptyState.SetTooltip(BasisLocalization.Get("camera.modifier.noEffects.description"));
            _modifierEffectsEmptyState.SetDescription(string.Empty);

            BuildEffectBlock(parent, BasisCameraEffectModifier.SteadySubject, content =>
            {
                _steadySmoothingSlider = PanelSlider.CreateNew(content);
                _steadySmoothingSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                    BasisLocalization.Get("camera.steadySmoothing"), 0f, 1.5f, false, 2, ValueDisplayMode.Raw));
                _steadySmoothingSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.steadySmoothing.description"));
                _steadySmoothingSlider.OnValueChanged = v =>
                {
                    if (Stack != null) Stack.steady.smoothing = v;
                };

                _steadyDeadZoneSlider = PanelSlider.CreateNew(content);
                _steadyDeadZoneSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                    BasisLocalization.Get("camera.steadyDeadZone"), 0f, 1f, false, 2, ValueDisplayMode.Meters));
                _steadyDeadZoneSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.steadyDeadZone.description"));
                _steadyDeadZoneSlider.OnValueChanged = v =>
                {
                    if (Stack != null) Stack.steady.verticalDeadZone = v;
                };
            });

            BuildEffectBlock(parent, BasisCameraEffectModifier.LookAhead, content =>
            {
                _lookAheadTimeSlider = PanelSlider.CreateNew(content);
                _lookAheadTimeSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                    BasisLocalization.Get("camera.lookAhead"), 0f, 1.5f, false, 2, ValueDisplayMode.Raw));
                _lookAheadTimeSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.lookAhead.description"));
                _lookAheadTimeSlider.OnValueChanged = v =>
                {
                    if (Stack != null) Stack.lookAhead.time = v;
                };

                _lookAheadLimitSlider = PanelSlider.CreateNew(content);
                _lookAheadLimitSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                    BasisLocalization.Get("camera.lookAheadLimit"), 0f, 6f, false, 2, ValueDisplayMode.Meters));
                _lookAheadLimitSlider.OnValueChanged = v =>
                {
                    if (Stack != null) Stack.lookAhead.limit = v;
                };
            });

            BuildEffectBlock(parent, BasisCameraEffectModifier.AvoidOcclusion, content =>
            {
                _occlusionPaddingSlider = PanelSlider.CreateNew(content);
                _occlusionPaddingSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                    BasisLocalization.Get("camera.occlusionPadding"), 0f, 1.5f, false, 2, ValueDisplayMode.Meters));
                _occlusionPaddingSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.occlusionPadding.description"));
                _occlusionPaddingSlider.OnValueChanged = v =>
                {
                    if (Stack != null) Stack.occlusion.padding = v;
                };

                _occlusionMinSlider = PanelSlider.CreateNew(content);
                _occlusionMinSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                    BasisLocalization.Get("camera.occlusionMin"), 0.1f, 3f, false, 2, ValueDisplayMode.Meters));
                _occlusionMinSlider.OnValueChanged = v =>
                {
                    if (Stack != null) Stack.occlusion.minDistance = v;
                };

                _occlusionReturnSlider = PanelSlider.CreateNew(content);
                _occlusionReturnSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                    BasisLocalization.Get("camera.occlusionReturn"), 0f, 3f, false, 2, ValueDisplayMode.Raw));
                _occlusionReturnSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.occlusionReturn.description"));
                _occlusionReturnSlider.OnValueChanged = v =>
                {
                    if (Stack != null) Stack.occlusion.returnDamping = v;
                };

                _occlusionRadiusSlider = PanelSlider.CreateNew(content);
                _occlusionRadiusSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                    BasisLocalization.Get("camera.occlusionRadius"), 0.01f, 0.5f, false, 3, ValueDisplayMode.Meters));
                _occlusionRadiusSlider.OnValueChanged = v =>
                {
                    if (Stack != null) Stack.occlusion.probeRadius = v;
                };
            });

            BuildEffectBlock(parent, BasisCameraEffectModifier.AvoidCollision, content =>
            {
                _collisionRadiusSlider = PanelSlider.CreateNew(content);
                _collisionRadiusSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                    BasisLocalization.Get("camera.collisionRadius"), 0.01f, 1f, false, 2, ValueDisplayMode.Meters));
                _collisionRadiusSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.collisionRadius.description"));
                _collisionRadiusSlider.OnValueChanged = v =>
                {
                    if (Stack != null) Stack.collision.radius = v;
                };

                _collisionPaddingSlider = PanelSlider.CreateNew(content);
                _collisionPaddingSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                    BasisLocalization.Get("camera.collisionPadding"), 0f, 1f, false, 2, ValueDisplayMode.Meters));
                _collisionPaddingSlider.OnValueChanged = v =>
                {
                    if (Stack != null) Stack.collision.padding = v;
                };
            });

            BuildEffectBlock(parent, BasisCameraEffectModifier.LensOverride, content =>
            {
                _lensFovSlider = PanelSlider.CreateNew(content);
                _lensFovSlider.SetSliderSettings(PanelSlider.SliderSettings.Degrees(
                    BasisLocalization.Get("camera.lensFov"), 5f, 120f, false, 1));
                _lensFovSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.lensFov.description"));
                _lensFovSlider.OnValueChanged = v =>
                {
                    if (Stack != null) Stack.lens.fov = v;
                };

                _lensDampSlider = PanelSlider.CreateNew(content);
                _lensDampSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                    BasisLocalization.Get("camera.lensDamping"), 0f, 4f, false, 2, ValueDisplayMode.Raw));
                _lensDampSlider.OnValueChanged = v =>
                {
                    if (Stack != null) Stack.lens.damping = v;
                };
            });

            BuildEffectBlock(parent, BasisCameraEffectModifier.DollyZoom, content =>
            {
                _dollyZoomMinSlider = PanelSlider.CreateNew(content);
                _dollyZoomMinSlider.SetSliderSettings(PanelSlider.SliderSettings.Degrees(
                    BasisLocalization.Get("camera.dollyZoomMin"), 5f, 120f, false, 1));
                _dollyZoomMinSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.dollyZoomMin.description"));
                _dollyZoomMinSlider.OnValueChanged = v =>
                {
                    if (Stack != null) Stack.dollyZoom.minFov = v;
                };

                _dollyZoomMaxSlider = PanelSlider.CreateNew(content);
                _dollyZoomMaxSlider.SetSliderSettings(PanelSlider.SliderSettings.Degrees(
                    BasisLocalization.Get("camera.dollyZoomMax"), 5f, 120f, false, 1));
                _dollyZoomMaxSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.dollyZoomMax.description"));
                _dollyZoomMaxSlider.OnValueChanged = v =>
                {
                    if (Stack != null) Stack.dollyZoom.maxFov = v;
                };
            });

            BuildEffectBlock(parent, BasisCameraEffectModifier.RigWeight, content =>
            {
                _rigWeightResponseSlider = PanelSlider.CreateNew(content);
                _rigWeightResponseSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                    BasisLocalization.Get("camera.rigWeightResponse"), 0.5f, 12f, false, 1, ValueDisplayMode.Raw));
                _rigWeightResponseSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.rigWeightResponse.description"));
                _rigWeightResponseSlider.OnValueChanged = v =>
                {
                    if (Stack != null) Stack.rigWeight.responsiveness = v;
                };

                _rigWeightBounceSlider = PanelSlider.CreateNew(content);
                _rigWeightBounceSlider.SetSliderSettings(PanelSlider.SliderSettings.Percentage(
                    BasisLocalization.Get("camera.rigWeightBounce")));
                _rigWeightBounceSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.rigWeightBounce.description"));
                _rigWeightBounceSlider.OnValueChanged = v =>
                {
                    if (Stack != null) Stack.rigWeight.bounce = v / 100f;
                };
            });

            BuildEffectBlock(parent, BasisCameraEffectModifier.Shake, content =>
            {
                _noiseProfileDropdown = PanelDropdown.CreateNewEntry(content);
                _noiseProfileDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.noiseProfile"));
                _noiseProfileDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.noiseProfile.description"));
                _noiseProfileDropdown.AssignLocalizedEntries(
                    new List<string>(NoiseProfileKeys), new List<string>(NoiseProfileKeys));
                _noiseProfileDropdown.OnValueChanged = _ =>
                {
                    int index = _noiseProfileDropdown != null ? _noiseProfileDropdown.Index : -1;
                    if (index < 0 || Stack == null) return;

                    // The gains are the operator's, not the profile's, so a change of character
                    // keeps how hard it was dialled in.
                    float amplitude = Stack.shake.amplitudeGain;
                    float frequency = Stack.shake.frequencyGain;
                    Stack.shake = BasisCameraNoiseSettings.ForProfile((BasisCameraNoiseProfile)index);
                    Stack.shake.amplitudeGain = amplitude;
                    Stack.shake.frequencyGain = frequency;
                };

                _noiseAmplitudeSlider = PanelSlider.CreateNew(content);
                _noiseAmplitudeSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                    BasisLocalization.Get("camera.noiseAmplitude"), 0f, 3f, false, 2, ValueDisplayMode.Raw));
                _noiseAmplitudeSlider.OnValueChanged = v =>
                {
                    if (Stack != null) Stack.shake.amplitudeGain = v;
                };

                _noiseFrequencySlider = PanelSlider.CreateNew(content);
                _noiseFrequencySlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                    BasisLocalization.Get("camera.noiseFrequency"), 0f, 3f, false, 2, ValueDisplayMode.Raw));
                _noiseFrequencySlider.OnValueChanged = v =>
                {
                    if (Stack != null) Stack.shake.frequencyGain = v;
                };
            });
        }

        /// <summary>
        /// Builds one effect's section: its controls, then a remove button. Built once for every
        /// effect and shown by <see cref="RefreshModifierVisibility"/>, rather than spawned and
        /// destroyed as effects are fitted — a rebuild under an open panel moves the row the
        /// pointer is over.
        /// </summary>
        private void BuildEffectBlock(RectTransform parent, BasisCameraEffectModifier effect, System.Action<RectTransform> buildControls)
        {
            PanelSectionToggle section = PanelSectionToggle.CreateNewEntry(parent);
            PanelElementDescriptor group = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                section, parent, BasisLocalization.Get(BasisCameraModifiers.NameKey(effect)), false);
            section.Descriptor.SetTooltip(BasisLocalization.Get(BasisCameraModifiers.DescriptionKey(effect)));

            buildControls(group.ContentParent);

            PanelButton remove = PanelButton.CreateNew(group.ContentParent);
            remove.Descriptor.SetTitle(BasisLocalization.Get("camera.modifier.removeEffect"));
            remove.Descriptor.SetTooltip(BasisLocalization.Get("camera.modifier.removeEffect.description"));
            remove.OnClicked += () =>
            {
                if (_activeCamera == null) return;
                _activeCamera.RemoveModifierEffect(effect);
                _lastEffectSignature = -1;
                RefreshEffectList();
                RefreshModifierVisibility();
            };

            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(section, group, true, OnSectionExpanded);
            _effectSections[effect] = section;
            _effectGroups[effect] = group;
        }

        /// <summary>
        /// Rebuilds the add dropdown so it only ever offers effects that are not already fitted.
        /// Refused while it is expanded: swapping the options under a spawned item list leaves the
        /// toggles holding their build-time indices, so the click lands on the wrong row.
        /// </summary>
        private void RefreshEffectList()
        {
            if (_activeCamera == null || _effectAddDropdown == null) return;
            if (_effectAddDropdown.DropdownComponent != null && _effectAddDropdown.DropdownComponent.IsExpanded) return;

            BasisCameraModifierStack stack = Stack;
            int signature = 0;
            for (int Index = 0; Index < BasisCameraModifiers.Effects.Length; Index++)
            {
                if (stack != null && stack.HasEffect(BasisCameraModifiers.Effects[Index].Effect))
                {
                    signature |= 1 << Index;
                }
            }
            if (stack != null && stack.ResolvesSubject) signature |= 1 << 30;
            if (signature == _lastEffectSignature) return;
            _lastEffectSignature = signature;

            _addableEffects.Clear();
            List<string> entries = new List<string> { "camera.modifier.addEffect" };
            List<string> tooltipKeys = new List<string> { "camera.modifier.addEffect.description" };

            for (int Index = 0; Index < BasisCameraModifiers.Effects.Length; Index++)
            {
                BasisCameraEffectDescriptor descriptor = BasisCameraModifiers.Effects[Index];
                if (stack != null && stack.HasEffect(descriptor.Effect)) continue;
                if (stack != null && !stack.ResolvesSubject && BasisCameraModifiers.NeedsSubject(descriptor.Effect)) continue;

                _addableEffects.Add(descriptor.Effect);
                entries.Add(descriptor.NameKey);
                tooltipKeys.Add(descriptor.DescriptionKey);
            }

            _effectAddDropdown.AssignLocalizedEntries(entries, entries, tooltipKeys);
            _effectAddDropdown.SetValueWithoutNotify(entries[0]);
        }

        // ---- Dolly track -----------------------------------------------------------------------

        private void BuildDollyGroup(RectTransform parent)
        {
            // A plain titled card, not a section: it is already inside the position slot's own
            // section, and a second header there would collapse away the only reason Dolly Track
            // is fitted at all.
            _dollyGroup = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, parent);
            _dollyGroup.SetTitle(BasisLocalization.Get("camera.dolly"));
            _dollyGroup.SetTooltip(BasisLocalization.Get("camera.dolly.description"));
            _dollyGroup.SetDescription(string.Empty);
            RectTransform content = _dollyGroup.ContentParent;

            _dollyEmptyState = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, content);
            _dollyEmptyState.SetTitle(BasisLocalization.Get("camera.dollyEmpty"));
            _dollyEmptyState.SetTooltip(BasisLocalization.Get("camera.dollyEmpty.description"));
            _dollyEmptyState.SetDescription(string.Empty);

            RectTransform placeRow = PanelElementDescriptor.BuildActionRow(content, "CameraDollyPlaceRow");

            PanelButton placeAtCamera = PanelButton.CreateNew(placeRow);
            placeAtCamera.Descriptor.SetTitle(BasisLocalization.Get("camera.placeWaypointHere"));
            placeAtCamera.Descriptor.SetTooltip(BasisLocalization.Get("camera.placeWaypointHere.description"));
            placeAtCamera.OnClicked += () =>
            {
                if (_activeCamera == null) return;
                _activeCamera.SpawnWaypointAtCamera();
                _selectedWaypointIndex = _activeCamera.DollyWaypointCount - 1;
                _lastWaypointCount = -1;
                RefreshWaypointList();
            };

            PanelButton placeAtPlayer = PanelButton.CreateNew(placeRow);
            placeAtPlayer.Descriptor.SetTitle(BasisLocalization.Get("camera.placeWaypointAtMe"));
            placeAtPlayer.Descriptor.SetTooltip(BasisLocalization.Get("camera.placeWaypointAtMe.description"));
            placeAtPlayer.OnClicked += () =>
            {
                if (_activeCamera == null) return;
                _activeCamera.SpawnWaypointInFrontOfPlayer();
                _selectedWaypointIndex = _activeCamera.DollyWaypointCount - 1;
                _lastWaypointCount = -1;
                RefreshWaypointList();
            };

            _waypointDropdown = PanelDropdown.CreateNewEntry(content);
            _waypointDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.waypoint"));
            _waypointDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.waypoint.description"));
            _waypointDropdown.OnValueChanged = _ =>
            {
                if (_waypointDropdown == null) return;
                int index = _waypointDropdown.Index;
                if (index >= 0)
                {
                    _selectedWaypointIndex = index;
                    SeedWaypointOrderSlider();
                }
            };

            _waypointOrderSlider = PanelSlider.CreateNew(content);
            _waypointOrderSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.waypointOrder"), 1f, 32f, true, 0, ValueDisplayMode.Raw));
            _waypointOrderSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.waypointOrder.description"));
            _waypointOrderSlider.OnValueChanged = v =>
            {
                if (_activeCamera == null) return;
                int target = Mathf.RoundToInt(v) - 1;
                if (_activeCamera.SetWaypointQueuePosition(_selectedWaypointIndex, target))
                {
                    _selectedWaypointIndex = Mathf.Clamp(target, 0, _activeCamera.DollyWaypointCount - 1);
                    _lastWaypointCount = -1;
                    RefreshWaypointList();
                }
            };

            RectTransform manageRow = PanelElementDescriptor.BuildActionRow(content, "CameraDollyManageRow");

            PanelButton deleteWaypoint = PanelButton.CreateNew(manageRow);
            deleteWaypoint.Descriptor.SetTitle(BasisLocalization.Get("camera.deleteWaypoint"));
            deleteWaypoint.OnClicked += () =>
            {
                if (_activeCamera == null) return;
                if (_activeCamera.RemoveWaypoint(_selectedWaypointIndex))
                {
                    _selectedWaypointIndex = Mathf.Max(0, _selectedWaypointIndex - 1);
                    _lastWaypointCount = -1;
                    RefreshWaypointList();
                }
            };

            PanelButton clearTrack = PanelButton.CreateNew(manageRow);
            clearTrack.Descriptor.SetTitle(BasisLocalization.Get("camera.clearTrack"));
            clearTrack.OnClicked += () =>
            {
                if (_activeCamera == null) return;
                _activeCamera.ClearDollyTrack();
                _selectedWaypointIndex = 0;
                _lastWaypointCount = -1;
                RefreshWaypointList();
            };

            _dollySyncDropdown = PanelDropdown.CreateNewEntry(content);
            _dollySyncDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.dollySync"));
            _dollySyncDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.dollySync.description"));
            _dollySyncDropdown.AssignLocalizedEntries(
                new List<string>(DollySyncKeys), new List<string>(DollySyncKeys));
            _dollySyncDropdown.OnValueChanged = _ =>
            {
                int index = _dollySyncDropdown != null ? _dollySyncDropdown.Index : -1;
                if (_activeCamera == null || index < 0 || index >= DollySyncKeys.Length) return;

                _activeCamera.SetDollySync((BasisCameraDollySync)index);
            };

            _dollyLoopToggle = PanelToggle.CreateNewEntry(content);
            _dollyLoopToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.dollyLoop"));
            _dollyLoopToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.dollyLoop.description"));
            _dollyLoopToggle.OnValueChanged = v =>
            {
                if (_activeCamera?.DollyTrack != null) _activeCamera.DollyTrack.Looped = v;
            };

            _dollyVisibleToggle = PanelToggle.CreateNewEntry(content);
            _dollyVisibleToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.dollyVisible"));
            _dollyVisibleToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.dollyVisible.description"));
            _dollyVisibleToggle.OnValueChanged = v => _activeCamera?.DollyTrack?.SetVisible(v);

            _dollyGridSnapToggle = PanelToggle.CreateNewEntry(content);
            _dollyGridSnapToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.dollyGridSnap"));
            _dollyGridSnapToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.dollyGridSnap.description"));
            _dollyGridSnapToggle.OnValueChanged = v =>
            {
                if (_activeCamera?.DollyTrack == null) return;
                _activeCamera.DollyTrack.SetGridSnap(v, _activeCamera.DollyTrack.GridSize);
            };

            _dollyGridSizeSlider = PanelSlider.CreateNew(content);
            _dollyGridSizeSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.dollyGridSize"), 0.05f, 2f, false, 2, ValueDisplayMode.Meters));
            _dollyGridSizeSlider.OnValueChanged = v =>
            {
                if (_activeCamera?.DollyTrack == null) return;
                _activeCamera.DollyTrack.SetGridSnap(_activeCamera.DollyTrack.GridSnap, v);
            };

            _dollySpeedColorToggle = PanelToggle.CreateNewEntry(content);
            _dollySpeedColorToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.dollySpeedColor"));
            _dollySpeedColorToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.dollySpeedColor.description"));
            _dollySpeedColorToggle.OnValueChanged = v =>
            {
                if (_activeCamera?.DollyTrack != null) _activeCamera.DollyTrack.ColorBySpeed = v;
            };

            BuildDollyPresetControls(content);
        }

        // ---- Dolly presets ---------------------------------------------------------------------

        /// <summary>
        /// The saved-track editor: pick one, name one, and the buttons that move a track between
        /// the world and the list. Laid out in the order the job is done — choose or type a name,
        /// then say what to do with it.
        /// </summary>
        private void BuildDollyPresetControls(RectTransform content)
        {
            _dollyPresetStatus = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, content);
            _dollyPresetStatus.SetTitle(BasisLocalization.Get("camera.dollyPreset"));
            _dollyPresetStatus.SetTooltip(BasisLocalization.Get("camera.dollyPreset.help"));
            _dollyPresetStatus.SetDescription(string.Empty);

            _dollyPresetDropdown = PanelDropdown.CreateNewEntry(content);
            _dollyPresetDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.dollyPreset.list"));
            _dollyPresetDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.dollyPreset.list.description"));
            _dollyPresetDropdown.OnValueChanged = _ =>
            {
                if (_dollyPresetDropdown == null) return;

                int index = _dollyPresetDropdown.Index;
                if (index < 0 || index >= _dollyPresetKeys.Count) return;

                _dollyPresetNameField?.SetValueWithoutNotify(_dollyPresetKeys[index]);
                RefreshDollyPresetButtons();
            };

            _dollyPresetNameField = PanelTextField.CreateNewEntry(content);
            _dollyPresetNameField.Descriptor.SetTitle(BasisLocalization.Get("camera.dollyPreset.name"));
            _dollyPresetNameField.Descriptor.SetTooltip(BasisLocalization.Get("camera.dollyPreset.name.tooltip"));
            if (_dollyPresetNameField._inputField != null)
            {
                _dollyPresetNameField._inputField.characterLimit = BasisCameraDollyPreset.MaxNameLength;
            }
            _dollyPresetNameField.OnValueChanged += _ => RefreshDollyPresetButtons();

            RectTransform storeRow = PanelElementDescriptor.BuildActionRow(content, "CameraDollyPresetStoreRow");

            _dollyPresetSaveButton = PanelButton.CreateNew(storeRow);
            _dollyPresetSaveButton.Descriptor.SetTitle(BasisLocalization.Get("camera.dollyPreset.save"));
            _dollyPresetSaveButton.Descriptor.SetTooltip(BasisLocalization.Get("camera.dollyPreset.save.tooltip"));
            _dollyPresetSaveButton.OnClicked += SaveDollyPreset;

            _dollyPresetRemoveButton = PanelButton.CreateNew(storeRow);
            _dollyPresetRemoveButton.Descriptor.SetTitle(BasisLocalization.Get("camera.dollyPreset.remove"));
            _dollyPresetRemoveButton.Descriptor.SetTooltip(BasisLocalization.Get("camera.dollyPreset.remove.tooltip"));
            _dollyPresetRemoveButton.OnClicked += PromptRemoveDollyPreset;

            RectTransform loadRow = PanelElementDescriptor.BuildActionRow(content, "CameraDollyPresetLoadRow");

            _dollyPresetLoadButton = PanelButton.CreateNew(loadRow);
            _dollyPresetLoadButton.Descriptor.SetTitle(BasisLocalization.Get("camera.dollyPreset.load"));
            _dollyPresetLoadButton.Descriptor.SetTooltip(BasisLocalization.Get("camera.dollyPreset.load.tooltip"));
            _dollyPresetLoadButton.OnClicked += () => LoadDollyPreset(inPlace: false);

            _dollyPresetLoadInPlaceButton = PanelButton.CreateNew(loadRow);
            _dollyPresetLoadInPlaceButton.Descriptor.SetTitle(BasisLocalization.Get("camera.dollyPreset.loadInPlace"));
            _dollyPresetLoadInPlaceButton.Descriptor.SetTooltip(
                BasisLocalization.Get("camera.dollyPreset.loadInPlace.tooltip"));
            _dollyPresetLoadInPlaceButton.OnClicked += () => LoadDollyPreset(inPlace: true);

            RectTransform fileRow = PanelElementDescriptor.BuildActionRow(content, "CameraDollyPresetFileRow");

            _dollyPresetExportButton = PanelButton.CreateNew(fileRow);
            _dollyPresetExportButton.Descriptor.SetTitle(BasisLocalization.Get("camera.dollyPreset.export"));
            _dollyPresetExportButton.Descriptor.SetTooltip(BasisLocalization.Get("camera.dollyPreset.export.tooltip"));
            _dollyPresetExportButton.OnClicked += ExportDollyPreset;

            PanelButton import = PanelButton.CreateNew(fileRow);
            import.Descriptor.SetTitle(BasisLocalization.Get("camera.dollyPreset.import"));
            import.Descriptor.SetTooltip(BasisLocalization.Get("camera.dollyPreset.import.tooltip"));
            import.OnClicked += ImportDollyPresets;

            PanelButton folder = PanelButton.CreateNew(fileRow);
            folder.Descriptor.SetTitle(BasisLocalization.Get("camera.dollyPreset.folder"));
            folder.Descriptor.SetTooltip(BasisLocalization.Get("camera.dollyPreset.folder.tooltip"));
            folder.OnClicked += () => BasisCameraDollyPresets.RevealExportFolder();

            RectTransform shareRow = PanelElementDescriptor.BuildActionRow(content, "CameraDollyPresetShareRow");

            _dollyPresetShareButton = PanelButton.CreateNew(shareRow);
            _dollyPresetShareButton.Descriptor.SetTitle(BasisLocalization.Get("camera.dollyPreset.share"));
            _dollyPresetShareButton.Descriptor.SetTooltip(BasisLocalization.Get("camera.dollyPreset.share.tooltip"));
            _dollyPresetShareButton.OnClicked += ShareDollyPreset;

            RebuildDollyPresetList();
        }

        /// <summary>The preset the buttons act on: whatever is typed, which picking one fills in.</summary>
        private string EditedDollyPresetName() =>
            BasisCameraDollyPreset.SanitizeName(_dollyPresetNameField?.Value);

        /// <summary>
        /// Rebuilds the saved list. Gated on the store's revision rather than run every tick: a
        /// rebuild throws away the entries an open dropdown is showing.
        /// </summary>
        private void RebuildDollyPresetList()
        {
            if (_dollyPresetDropdown == null) return;

            _lastDollyPresetRevision = BasisCameraDollyPresets.Revision;

            IReadOnlyList<BasisCameraDollyPreset> saved = BasisCameraDollyPresets.Presets;
            _dollyPresetKeys.Clear();
            var labels = new List<string>(saved.Count);
            for (int Index = 0; Index < saved.Count; Index++)
            {
                _dollyPresetKeys.Add(saved[Index].name);
                labels.Add($"{saved[Index].name} ({saved[Index].Count})");
            }

            bool any = _dollyPresetKeys.Count > 0;
            _dollyPresetDropdown.gameObject.SetActive(any);
            if (any)
            {
                _dollyPresetDropdown.AssignEntries(_dollyPresetKeys, labels);

                string selected = EditedDollyPresetName();
                int match = -1;
                for (int Index = 0; Index < _dollyPresetKeys.Count; Index++)
                {
                    if (!BasisCameraDollyPreset.NamesMatch(_dollyPresetKeys[Index], selected)) continue;

                    match = Index;
                    break;
                }
                _dollyPresetDropdown.SetValueWithoutNotify(_dollyPresetKeys[match < 0 ? 0 : match]);
            }

            RefreshDollyPresetButtons();
            ForceLayoutRebuild(_dollyGroup);
        }

        /// <summary>
        /// Only the buttons that would do something are live. A load with nothing saved under that
        /// name, or a save with no track laid out, would answer a click with nothing happening.
        /// </summary>
        private void RefreshDollyPresetButtons()
        {
            string name = EditedDollyPresetName();
            bool named = name != null;
            bool exists = named && BasisCameraDollyPresets.Exists(name);
            bool hasTrack = _activeCamera != null && _activeCamera.DollyWaypointCount > 0;

            SetButtonInteractable(_dollyPresetSaveButton, named && hasTrack);
            SetButtonInteractable(_dollyPresetExportButton, exists);
            SetButtonInteractable(_dollyPresetShareButton, exists && BasisNetworkConnection.LocalPlayerIsConnected);
            SetButtonInteractable(_dollyPresetRemoveButton, exists);
            SetButtonInteractable(_dollyPresetLoadButton, exists);
            SetButtonInteractable(_dollyPresetLoadInPlaceButton, exists);
        }

        private static void SetButtonInteractable(PanelButton button, bool interactable)
        {
            if (button?.ButtonComponent != null) button.ButtonComponent.interactable = interactable;
        }

        private void SaveDollyPreset()
        {
            if (_activeCamera == null) return;

            string name = EditedDollyPresetName();
            if (name == null)
            {
                ShowDollyPresetMessage("camera.dollyPreset.error.empty");
                return;
            }

            BasisCameraDollyPreset preset = _activeCamera.CaptureDollyPreset(name);
            if (!BasisCameraDollyPresets.Store(preset, out string error))
            {
                ShowDollyPresetMessage(error);
                return;
            }

            RebuildDollyPresetList();
            _dollyPresetNameField?.SetValueWithoutNotify(preset.name);
            RefreshDollyPresetButtons();
            ShowDollyPresetMessage("camera.dollyPreset.saved");
        }

        private void LoadDollyPreset(bool inPlace)
        {
            if (_activeCamera == null) return;

            BasisCameraDollyPreset preset = BasisCameraDollyPresets.Find(EditedDollyPresetName());
            if (preset == null)
            {
                ShowDollyPresetMessage("camera.dollyPreset.error.missing");
                return;
            }

            if (!_activeCamera.ApplyDollyPreset(preset, inPlace))
            {
                ShowDollyPresetMessage("camera.dollyPreset.error.noPoints");
                return;
            }

            // The track it replaced is gone, so everything keyed off the old one has to be told.
            _selectedWaypointIndex = 0;
            _lastWaypointCount = -1;
            RefreshWaypointList();
            SeedModifierControls();
            SeedModifierCameraControls();
            ShowDollyPresetMessage(inPlace ? "camera.dollyPreset.loadedInPlace" : "camera.dollyPreset.loaded");
        }

        private void PromptRemoveDollyPreset()
        {
            string name = EditedDollyPresetName();
            if (name == null || !BasisCameraDollyPresets.Exists(name)) return;

            BasisMainMenu.Instance.OpenDialogue(
                BasisLocalization.Get("camera.dollyPreset.remove"),
                BasisLocalization.Get("camera.dollyPreset.remove.confirm", name),
                BasisLocalization.Get("camera.dollyPreset.remove"),
                BasisLocalization.Get("ui.cancel"),
                confirmed =>
                {
                    if (!confirmed) return;
                    if (!BasisCameraDollyPresets.Remove(name)) return;

                    RebuildDollyPresetList();
                    ShowDollyPresetMessage("camera.dollyPreset.removed");
                });
        }

        private void ExportDollyPreset()
        {
            BasisCameraDollyPreset preset = BasisCameraDollyPresets.Find(EditedDollyPresetName());
            if (preset == null)
            {
                ShowDollyPresetMessage("camera.dollyPreset.error.missing");
                return;
            }

            if (!BasisCameraDollyPresets.Export(preset, out string path, out string error))
            {
                ShowDollyPresetMessage(error);
                return;
            }

            ShowDollyPresetMessage(BasisLocalization.Get("camera.dollyPreset.exported",
                System.IO.Path.GetFileName(path)));
        }

        /// <summary>
        /// Puts the selected track in the room as an orb for anyone to keep. Placement takes over
        /// from here, so the panel says nothing more until it comes back.
        /// </summary>
        private void ShareDollyPreset()
        {
            BasisCameraDollyPreset preset = BasisCameraDollyPresets.Find(EditedDollyPresetName());
            if (preset == null)
            {
                ShowDollyPresetMessage("camera.dollyPreset.error.missing");
                return;
            }

            if (!BasisNetworkConnection.LocalPlayerIsConnected)
            {
                ShowDollyPresetMessage("camera.dollyPreset.error.shareOffline");
                return;
            }

            if (!BasisCameraDollyShare.Share(preset, out string error))
            {
                ShowDollyPresetMessage(error);
                return;
            }

            ShowDollyPresetMessage(BasisLocalization.Get("camera.dollyPreset.shared", preset.name));
        }

        private void ImportDollyPresets()
        {
            if (!BasisCameraDollyPresets.Import(out int imported, out string error))
            {
                ShowDollyPresetMessage(error);
                return;
            }

            RebuildDollyPresetList();
            ShowDollyPresetMessage(imported == 0
                ? BasisLocalization.Get("camera.dollyPreset.importedNone")
                : BasisLocalization.Get("camera.dollyPreset.imported", imported.ToString()));
        }

        /// <summary>
        /// Says what just happened, on the card above the controls. Takes a localization key or a
        /// line that has already been built, since two of these carry a file name or a count.
        /// </summary>
        private void ShowDollyPresetMessage(string keyOrText)
        {
            if (_dollyPresetStatus == null) return;

            string text = string.IsNullOrEmpty(keyOrText)
                ? string.Empty
                : (BasisLocalization.TryGet(keyOrText, out string localized) ? localized : keyOrText);

            _dollyPresetStatus.SetDescription(text);
            ForceLayoutRebuild(_dollyGroup);
        }

        // ---- Background ------------------------------------------------------------------------

        private void BuildBackgroundGroup(RectTransform parent)
        {
            _backgroundSection = PanelSectionToggle.CreateNewEntry(parent);
            _backgroundGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _backgroundSection, parent, BasisLocalization.Get("camera.background"), false);
            RectTransform content = _backgroundGroup.ContentParent;

            _backgroundModeDropdown = PanelDropdown.CreateNewEntry(content);
            _backgroundModeDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.backgroundMode"));
            _backgroundModeDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.backgroundMode.description"));
            _backgroundModeDropdown.AssignLocalizedEntries(
                new List<string>(BackgroundModeKeys), new List<string>(BackgroundModeKeys));
            _backgroundModeDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _backgroundModeDropdown == null) return;
                int index = _backgroundModeDropdown.Index;
                if (index < 0) return;
                _activeCamera.SetBackgroundMode((BasisCameraBackgroundMode)index);
                RefreshBackgroundVisibility();
            };

            _backgroundKeepWorldToggle = PanelToggle.CreateNewEntry(content);
            _backgroundKeepWorldToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.backgroundKeepWorld"));
            _backgroundKeepWorldToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.backgroundKeepWorld.description"));
            _backgroundKeepWorldToggle.OnValueChanged = v => _activeCamera?.SetBackgroundKeepsWorld(v);

            _backgroundRedSlider = PanelSlider.CreateNew(content);
            _backgroundRedSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.backgroundRed"), 0f, 1f, false, 3, ValueDisplayMode.Raw));
            _backgroundRedSlider.OnValueChanged = v => SetBackgroundChannel(0, v);

            _backgroundGreenSlider = PanelSlider.CreateNew(content);
            _backgroundGreenSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.backgroundGreen"), 0f, 1f, false, 3, ValueDisplayMode.Raw));
            _backgroundGreenSlider.OnValueChanged = v => SetBackgroundChannel(1, v);

            _backgroundBlueSlider = PanelSlider.CreateNew(content);
            _backgroundBlueSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.backgroundBlue"), 0f, 1f, false, 3, ValueDisplayMode.Raw));
            _backgroundBlueSlider.OnValueChanged = v => SetBackgroundChannel(2, v);
        }

        private void SetBackgroundChannel(int channel, float value)
        {
            if (_activeCamera == null) return;
            Color color = _activeCamera.backgroundCustomColor;
            color[channel] = value;
            _activeCamera.SetBackgroundCustomColor(color);
        }

        /// <summary>Fills the target group with everyone currently in the instance, local included.</summary>
        private void RebuildTargetGroupFromRoster()
        {
            if (_activeCamera == null) return;

            _activeCamera.RebuildTargetGroup();
        }

        // ---- Reset defaults --------------------------------------------------------------------

        /// <summary>
        /// Where the options gesture returns each modifier control. Read off a default stack — the
        /// same thing <see cref="BasisCameraModifierStack.ResetToDefaults"/> writes — rather than
        /// written out by hand, so a reset lands exactly where a fresh camera starts.
        ///
        /// <para>These controls are callback-driven, with no settings binding to derive a default
        /// from, so without an explicit one they count as having nothing to go back to. That took
        /// them out of a section reset as well as their own gesture: pressing reset on the Position
        /// or Advanced header walked straight past the dolly rows and left every one of them where
        /// it was.</para>
        /// </summary>
        private void AssignModifierResetDefaults(BasisHandHeldCameraUI.CameraSettings defaults)
        {
            BasisCameraModifierStack stack = defaults.modifiers;

            _positionDropdown?.SetResetDefault(BasisCameraModifiers.NameKey(stack.positionModifier));
            _rotationDropdown?.SetResetDefault(BasisCameraModifiers.NameKey(stack.rotationModifier));

            _bindingModeDropdown?.SetResetDefault(BindingModeKeys[(int)stack.follow.bindingMode]);
            _placeDampXSlider?.SetResetDefault(stack.follow.damping.x);
            _placeDampYSlider?.SetResetDefault(stack.follow.damping.y);
            _placeDampZSlider?.SetResetDefault(stack.follow.damping.z);
            _placeTeleportSlider?.SetResetDefault(stack.follow.teleportDistance);

            _framingSizeSlider?.SetResetDefault(stack.framing.screenFraction);
            _framingZoomToggle?.SetResetDefault(stack.framing.usesZoom);
            _framingMinSlider?.SetResetDefault(stack.framing.minDistance);
            _framingMaxSlider?.SetResetDefault(stack.framing.maxDistance);

            _orbitFollowHeadingToggle?.SetResetDefault(stack.orbit.followSubjectHeading);
            _orbitHeadingSlider?.SetResetDefault(stack.orbit.heading);
            _orbitVerticalSlider?.SetResetDefault(stack.orbit.verticalAxis);
            _orbitHeadingDampSlider?.SetResetDefault(stack.orbit.headingDamping);
            _orbitTopHeightSlider?.SetResetDefault(stack.orbit.top.height);
            _orbitTopRadiusSlider?.SetResetDefault(stack.orbit.top.radius);
            _orbitMidHeightSlider?.SetResetDefault(stack.orbit.middle.height);
            _orbitMidRadiusSlider?.SetResetDefault(stack.orbit.middle.radius);
            _orbitBottomHeightSlider?.SetResetDefault(stack.orbit.bottom.height);
            _orbitBottomRadiusSlider?.SetResetDefault(stack.orbit.bottom.radius);

            _dollyModeDropdown?.SetResetDefault(DollyModeKeys[(int)stack.dolly.mode]);
            _dollyPositionSlider?.SetResetDefault(stack.dolly.position);
            _dollySpeedSlider?.SetResetDefault(stack.dolly.speed);
            _dollyEaseInDropdown?.SetResetDefault(DollyEaseKeys[(int)stack.dolly.easeIn]);
            _dollyEaseInPortionSlider?.SetResetDefault(stack.dolly.easeInPortion);
            _dollyEaseOutDropdown?.SetResetDefault(DollyEaseKeys[(int)stack.dolly.easeOut]);
            _dollyEaseOutPortionSlider?.SetResetDefault(stack.dolly.easeOutPortion);
            _dollyDampSlider?.SetResetDefault(stack.dolly.damping);
            _dollyOffsetXSlider?.SetResetDefault(stack.dolly.offset.x);
            _dollyOffsetYSlider?.SetResetDefault(stack.dolly.offset.y);
            _dollyOffsetZSlider?.SetResetDefault(stack.dolly.offset.z);

            // The track editor's own options belong to the track, not the stack. The waypoint
            // picker and the order slider are deliberately left out: they address the points that
            // happen to be placed rather than carry a setting, so they have no default to return to.
            _dollySyncDropdown?.SetResetDefault(DollySyncKeys[(int)stack.dolly.syncMode]);
            _dollyLoopToggle?.SetResetDefault(BasisCameraDollyTrack.DefaultLooped);
            _dollyVisibleToggle?.SetResetDefault(BasisCameraDollyTrack.DefaultVisible);
            _dollyGridSnapToggle?.SetResetDefault(BasisCameraDollyTrack.DefaultGridSnap);
            _dollyGridSizeSlider?.SetResetDefault(BasisCameraDollyTrack.DefaultGridSize);
            _dollySpeedColorToggle?.SetResetDefault(BasisCameraDollyTrack.DefaultColorBySpeed);

            _aimDampSlider?.SetResetDefault(stack.lookAt.damping.x);
            _guidesToggle?.SetResetDefault(ShowGuidesDefault);
            _screenXSlider?.SetResetDefault(stack.compose.composer.screenX);
            _screenYSlider?.SetResetDefault(stack.compose.composer.screenY);
            _deadZoneWidthSlider?.SetResetDefault(stack.compose.composer.deadZoneWidth);
            _deadZoneHeightSlider?.SetResetDefault(stack.compose.composer.deadZoneHeight);
            _softZoneWidthSlider?.SetResetDefault(stack.compose.composer.softZoneWidth);
            _softZoneHeightSlider?.SetResetDefault(stack.compose.composer.softZoneHeight);
            _composerDampHSlider?.SetResetDefault(stack.compose.composer.horizontalDamping);
            _composerDampVSlider?.SetResetDefault(stack.compose.composer.verticalDamping);
            _composerBiasXSlider?.SetResetDefault(stack.compose.composer.biasX);
            _composerBiasYSlider?.SetResetDefault(stack.compose.composer.biasY);

            _lookAheadTimeSlider?.SetResetDefault(stack.lookAhead.time);
            _lookAheadLimitSlider?.SetResetDefault(stack.lookAhead.limit);

            _occlusionPaddingSlider?.SetResetDefault(stack.occlusion.padding);
            _occlusionMinSlider?.SetResetDefault(stack.occlusion.minDistance);
            _occlusionReturnSlider?.SetResetDefault(stack.occlusion.returnDamping);
            _occlusionRadiusSlider?.SetResetDefault(stack.occlusion.probeRadius);

            _noiseProfileDropdown?.SetResetDefault(NoiseProfileKeys[(int)stack.shake.profile]);
            _noiseAmplitudeSlider?.SetResetDefault(stack.shake.amplitudeGain);
            _noiseFrequencySlider?.SetResetDefault(stack.shake.frequencyGain);

            _lensFovSlider?.SetResetDefault(stack.lens.fov);
            _lensDampSlider?.SetResetDefault(stack.lens.damping);
        }

        // ---- Seeding and visibility --------------------------------------------------------------

        private void RefreshSlotChoices(BasisCameraModifierStack stack)
        {
            bool resolves = stack.ResolvesSubject;
            bool facing = resolves && stack.subject.modifier != BasisCameraSubjectModifier.FixedPoint;
            int signature = (resolves ? 1 : 0) | (facing ? 2 : 0);
            if (signature == _lastChoiceSignature) return;
            if (_positionDropdown?.DropdownComponent != null && _positionDropdown.DropdownComponent.IsExpanded) return;
            if (_rotationDropdown?.DropdownComponent != null && _rotationDropdown.DropdownComponent.IsExpanded) return;
            if (_dollyModeDropdown?.DropdownComponent != null && _dollyModeDropdown.DropdownComponent.IsExpanded) return;
            _lastChoiceSignature = signature;

            _positionChoices.Clear();
            List<string> positionKeys = new List<string>();
            for (int Index = 0; Index < BasisCameraModifiers.PositionModifiers.Length; Index++)
            {
                BasisCameraPositionModifier modifier = BasisCameraModifiers.PositionModifiers[Index];
                if (!resolves && BasisCameraModifiers.NeedsSubject(modifier)) continue;
                _positionChoices.Add(modifier);
                positionKeys.Add(BasisCameraModifiers.NameKey(modifier));
            }
            _positionDropdown?.AssignLocalizedEntries(positionKeys, new List<string>(positionKeys), DescriptionKeys(positionKeys.ToArray()));

            _rotationChoices.Clear();
            List<string> rotationKeys = new List<string>();
            for (int Index = 0; Index < BasisCameraModifiers.RotationModifiers.Length; Index++)
            {
                BasisCameraRotationModifier modifier = BasisCameraModifiers.RotationModifiers[Index];
                if (!resolves && BasisCameraModifiers.NeedsSubject(modifier)) continue;
                if (!facing && modifier == BasisCameraRotationModifier.MatchSubject) continue;
                _rotationChoices.Add(modifier);
                rotationKeys.Add(BasisCameraModifiers.NameKey(modifier));
            }
            _rotationDropdown?.AssignLocalizedEntries(rotationKeys, new List<string>(rotationKeys), DescriptionKeys(rotationKeys.ToArray()));

            _dollyModeChoices.Clear();
            List<string> dollyKeys = new List<string>();
            for (int Index = 0; Index < DollyModeKeys.Length; Index++)
            {
                BasisCameraDollyMode mode = (BasisCameraDollyMode)Index;
                if (!resolves && mode == BasisCameraDollyMode.FollowSubject) continue;
                _dollyModeChoices.Add(mode);
                dollyKeys.Add(DollyModeKeys[Index]);
            }
            _dollyModeDropdown?.AssignLocalizedEntries(dollyKeys, new List<string>(dollyKeys));
        }

        /// <summary>Pushes the live stack into every modifier control.</summary>
        private void SeedModifierControls()
        {
            BasisCameraModifierStack stack = Stack;
            if (stack == null) return;

            RefreshSlotChoices(stack);

            _subjectDropdown?.SetValueWithoutNotify(BasisCameraModifiers.NameKey(stack.subject.modifier));
            _positionDropdown?.SetValueWithoutNotify(BasisCameraModifiers.NameKey(stack.positionModifier));
            _rotationDropdown?.SetValueWithoutNotify(BasisCameraModifiers.NameKey(stack.rotationModifier));

            bool framing = stack.positionModifier == BasisCameraPositionModifier.FrameSubject;
            Vector3 offset = framing ? stack.framing.directionOffset : stack.follow.positionOffset;
            Vector3 damping = framing ? stack.framing.damping : stack.follow.damping;
            BasisCameraBindingMode binding = framing ? stack.framing.bindingMode : stack.follow.bindingMode;
            float teleport = framing ? stack.framing.teleportDistance : stack.follow.teleportDistance;

            _bindingModeDropdown?.SetValueWithoutNotify(BindingModeKeys[(int)binding]);
            _placeOffsetXSlider?.SetValueWithoutNotify(offset.x);
            _placeOffsetYSlider?.SetValueWithoutNotify(offset.y);
            _placeOffsetZSlider?.SetValueWithoutNotify(offset.z);
            _placeDampXSlider?.SetValueWithoutNotify(damping.x);
            _placeDampYSlider?.SetValueWithoutNotify(damping.y);
            _placeDampZSlider?.SetValueWithoutNotify(damping.z);
            _placeTeleportSlider?.SetValueWithoutNotify(teleport);
            _followLateralSlider?.SetValueWithoutNotify(stack.follow.lateralTracking);

            _framingSizeSlider?.SetValueWithoutNotify(stack.framing.screenFraction);
            _framingZoomToggle?.SetValueWithoutNotify(stack.framing.usesZoom);
            _framingMinSlider?.SetValueWithoutNotify(stack.framing.minDistance);
            _framingMaxSlider?.SetValueWithoutNotify(stack.framing.maxDistance);

            _orbitFollowHeadingToggle?.SetValueWithoutNotify(stack.orbit.followSubjectHeading);
            _orbitHeadingSlider?.SetValueWithoutNotify(stack.orbit.heading);
            _orbitVerticalSlider?.SetValueWithoutNotify(stack.orbit.verticalAxis);
            _orbitHeadingDampSlider?.SetValueWithoutNotify(stack.orbit.headingDamping);
            _orbitTopHeightSlider?.SetValueWithoutNotify(stack.orbit.top.height);
            _orbitTopRadiusSlider?.SetValueWithoutNotify(stack.orbit.top.radius);
            _orbitMidHeightSlider?.SetValueWithoutNotify(stack.orbit.middle.height);
            _orbitMidRadiusSlider?.SetValueWithoutNotify(stack.orbit.middle.radius);
            _orbitBottomHeightSlider?.SetValueWithoutNotify(stack.orbit.bottom.height);
            _orbitBottomRadiusSlider?.SetValueWithoutNotify(stack.orbit.bottom.radius);

            _dollyModeDropdown?.SetValueWithoutNotify(DollyModeKeys[(int)stack.dolly.mode]);
            _dollyPositionSlider?.SetValueWithoutNotify(stack.dolly.position);
            _dollySpeedSlider?.SetValueWithoutNotify(stack.dolly.speed);
            _dollyEaseInDropdown?.SetValueWithoutNotify(DollyEaseKeys[(int)stack.dolly.easeIn]);
            _dollyEaseInPortionSlider?.SetValueWithoutNotify(stack.dolly.easeInPortion);
            _dollyEaseOutDropdown?.SetValueWithoutNotify(DollyEaseKeys[(int)stack.dolly.easeOut]);
            _dollyEaseOutPortionSlider?.SetValueWithoutNotify(stack.dolly.easeOutPortion);
            _dollyDampSlider?.SetValueWithoutNotify(stack.dolly.damping);
            _dollyOffsetXSlider?.SetValueWithoutNotify(stack.dolly.offset.x);
            _dollyOffsetYSlider?.SetValueWithoutNotify(stack.dolly.offset.y);
            _dollyOffsetZSlider?.SetValueWithoutNotify(stack.dolly.offset.z);

            Vector3 aim = stack.rotationModifier switch
            {
                BasisCameraRotationModifier.Compose => stack.compose.rotationOffset,
                BasisCameraRotationModifier.MatchSubject => stack.matchSubject.rotationOffset,
                BasisCameraRotationModifier.AimAlongTrack => stack.trackAim.rotationOffset,
                _ => stack.lookAt.rotationOffset,
            };
            _aimPitchSlider?.SetValueWithoutNotify(aim.x);
            _aimYawSlider?.SetValueWithoutNotify(aim.y);
            _aimDampSlider?.SetValueWithoutNotify(stack.rotationModifier switch
            {
                BasisCameraRotationModifier.MatchSubject => stack.matchSubject.damping.x,
                BasisCameraRotationModifier.AimAlongTrack => stack.trackAim.damping.x,
                _ => stack.lookAt.damping.x,
            });

            _screenXSlider?.SetValueWithoutNotify(stack.compose.composer.screenX);
            _screenYSlider?.SetValueWithoutNotify(stack.compose.composer.screenY);
            _deadZoneWidthSlider?.SetValueWithoutNotify(stack.compose.composer.deadZoneWidth);
            _deadZoneHeightSlider?.SetValueWithoutNotify(stack.compose.composer.deadZoneHeight);
            _softZoneWidthSlider?.SetValueWithoutNotify(stack.compose.composer.softZoneWidth);
            _softZoneHeightSlider?.SetValueWithoutNotify(stack.compose.composer.softZoneHeight);
            _composerDampHSlider?.SetValueWithoutNotify(stack.compose.composer.horizontalDamping);
            _composerDampVSlider?.SetValueWithoutNotify(stack.compose.composer.verticalDamping);
            _composerBiasXSlider?.SetValueWithoutNotify(stack.compose.composer.biasX);
            _composerBiasYSlider?.SetValueWithoutNotify(stack.compose.composer.biasY);

            _lookAheadTimeSlider?.SetValueWithoutNotify(stack.lookAhead.time);
            _lookAheadLimitSlider?.SetValueWithoutNotify(stack.lookAhead.limit);

            _occlusionPaddingSlider?.SetValueWithoutNotify(stack.occlusion.padding);
            _occlusionMinSlider?.SetValueWithoutNotify(stack.occlusion.minDistance);
            _occlusionReturnSlider?.SetValueWithoutNotify(stack.occlusion.returnDamping);
            _occlusionRadiusSlider?.SetValueWithoutNotify(stack.occlusion.probeRadius);

            _noiseProfileDropdown?.SetValueWithoutNotify(NoiseProfileKeys[(int)stack.shake.profile]);
            _noiseAmplitudeSlider?.SetValueWithoutNotify(stack.shake.amplitudeGain);
            _noiseFrequencySlider?.SetValueWithoutNotify(stack.shake.frequencyGain);

            _lensFovSlider?.SetValueWithoutNotify(stack.lens.fov);
            _lensDampSlider?.SetValueWithoutNotify(stack.lens.damping);

            _steadySmoothingSlider?.SetValueWithoutNotify(stack.steady.smoothing);
            _steadyDeadZoneSlider?.SetValueWithoutNotify(stack.steady.verticalDeadZone);

            _collisionRadiusSlider?.SetValueWithoutNotify(stack.collision.radius);
            _collisionPaddingSlider?.SetValueWithoutNotify(stack.collision.padding);

            _dollyZoomMinSlider?.SetValueWithoutNotify(stack.dollyZoom.minFov);
            _dollyZoomMaxSlider?.SetValueWithoutNotify(stack.dollyZoom.maxFov);

            _rigWeightResponseSlider?.SetValueWithoutNotify(stack.rigWeight.responsiveness);
            _rigWeightBounceSlider?.SetValueWithoutNotify(stack.rigWeight.bounce * 100f);

            _lastSubjectModifier = stack.subject.modifier;
            _lastPositionModifier = stack.positionModifier;
            _lastRotationModifier = stack.rotationModifier;
            _lastEffectSignature = -1;
            RefreshEffectList();
            RefreshModifierVisibility();
        }

        /// <summary>Seeds the controls that read camera state rather than stack state.</summary>
        private void SeedModifierCameraControls()
        {
            if (_activeCamera == null) return;

            _guidesToggle?.SetValueWithoutNotify(_showGuides);

            if (_activeCamera.DollyTrack != null)
            {
                _dollyLoopToggle?.SetValueWithoutNotify(_activeCamera.DollyTrack.Looped);
                _dollyVisibleToggle?.SetValueWithoutNotify(_activeCamera.DollyTrack.Visible);
                _dollyGridSnapToggle?.SetValueWithoutNotify(_activeCamera.DollyTrack.GridSnap);
                _dollyGridSizeSlider?.SetValueWithoutNotify(_activeCamera.DollyTrack.GridSize);
                _dollySpeedColorToggle?.SetValueWithoutNotify(_activeCamera.DollyTrack.ColorBySpeed);
                _dollySyncDropdown?.SetValueWithoutNotify(DollySyncKeys[(int)_activeCamera.Modifiers.dolly.syncMode]);
            }

            _backgroundModeDropdown?.SetValueWithoutNotify(BackgroundModeKeys[(int)_activeCamera.backgroundMode]);
            _backgroundKeepWorldToggle?.SetValueWithoutNotify(_activeCamera.backgroundKeepsWorld);
            _backgroundRedSlider?.SetValueWithoutNotify(_activeCamera.backgroundCustomColor.r);
            _backgroundGreenSlider?.SetValueWithoutNotify(_activeCamera.backgroundCustomColor.g);
            _backgroundBlueSlider?.SetValueWithoutNotify(_activeCamera.backgroundCustomColor.b);

            RefreshBackgroundVisibility();
        }

        /// <summary>
        /// Shows exactly the controls the fitted modifiers read, and nothing else. This is the whole
        /// point of the slots: a control that is visible is a control that does something.
        /// </summary>
        private void RefreshModifierVisibility()
        {
            BasisCameraModifierStack stack = Stack;
            if (stack == null) return;

            bool player = stack.subject.modifier == BasisCameraSubjectModifier.FollowPlayer;
            bool group = stack.subject.modifier == BasisCameraSubjectModifier.TargetGroup;
            bool fixedPoint = stack.subject.modifier == BasisCameraSubjectModifier.FixedPoint;
            bool hasSubject = stack.ResolvesSubject;

            bool body = player || group;
            bool fullBody = body && stack.subject.aimPoint == BasisCameraAimPoint.FullBody;
            _followTargetDropdown?.gameObject.SetActive(player);
            _followPlayspaceToggle?.gameObject.SetActive(body);
            _aimPointDropdown?.gameObject.SetActive(body);
            _followLookAtHeightSlider?.gameObject.SetActive(hasSubject);
            _subjectRadiusSlider?.gameObject.SetActive(hasSubject && !fullBody);
            _targetGroupToggle?.gameObject.SetActive(group);
            if (_groupRefreshRow != null) _groupRefreshRow.gameObject.SetActive(group);
            if (_fixedPointRow != null) _fixedPointRow.gameObject.SetActive(fixedPoint);

            _followSection?.Descriptor.SetTooltip(BasisLocalization.Get(
                BasisCameraModifiers.DescriptionKey(stack.subject.modifier)));

            bool follow = stack.positionModifier == BasisCameraPositionModifier.FollowSubject;
            bool framing = stack.positionModifier == BasisCameraPositionModifier.FrameSubject;
            bool orbit = stack.positionModifier == BasisCameraPositionModifier.Orbit;
            bool dolly = stack.positionModifier == BasisCameraPositionModifier.DollyTrack;
            bool alongTrack = stack.rotationModifier == BasisCameraRotationModifier.AimAlongTrack;
            bool placement = follow || framing;

            PanelSectionToggleHelpers.SetSectionVisible(_positionAdvancedSection, _positionAdvancedGroup, placement || orbit || dolly);

            _bindingModeDropdown?.gameObject.SetActive(placement);
            _placeOffsetXSlider?.gameObject.SetActive(placement);
            _placeOffsetYSlider?.gameObject.SetActive(placement);
            _placeOffsetZSlider?.gameObject.SetActive(placement);
            _placeDampXSlider?.gameObject.SetActive(placement);
            _placeDampYSlider?.gameObject.SetActive(placement);
            _placeDampZSlider?.gameObject.SetActive(placement);
            _placeTeleportSlider?.gameObject.SetActive(placement);
            _followLateralSlider?.gameObject.SetActive(follow);

            _framingSizeSlider?.gameObject.SetActive(framing);
            _framingZoomToggle?.gameObject.SetActive(framing);
            _framingMinSlider?.gameObject.SetActive(framing && !stack.framing.usesZoom);
            _framingMaxSlider?.gameObject.SetActive(framing && !stack.framing.usesZoom);

            _orbitFollowHeadingToggle?.gameObject.SetActive(orbit);
            _orbitHeadingSlider?.gameObject.SetActive(orbit);
            _orbitVerticalSlider?.gameObject.SetActive(orbit);
            _orbitHeadingDampSlider?.gameObject.SetActive(orbit);
            _orbitTopHeightSlider?.gameObject.SetActive(orbit);
            _orbitTopRadiusSlider?.gameObject.SetActive(orbit);
            _orbitMidHeightSlider?.gameObject.SetActive(orbit);
            _orbitMidRadiusSlider?.gameObject.SetActive(orbit);
            _orbitBottomHeightSlider?.gameObject.SetActive(orbit);
            _orbitBottomRadiusSlider?.gameObject.SetActive(orbit);

            _dollyModeDropdown?.gameObject.SetActive(dolly);
            if (_dollyTransportRow != null)
            {
                _dollyTransportRow.gameObject.SetActive(dolly && stack.dolly.mode == BasisCameraDollyMode.Play);
            }
            _dollyPositionSlider?.gameObject.SetActive(dolly && stack.dolly.mode == BasisCameraDollyMode.Manual);
            bool moving = dolly && stack.dolly.mode == BasisCameraDollyMode.Play;
            _dollySpeedSlider?.gameObject.SetActive(moving);
            _dollyEaseInDropdown?.gameObject.SetActive(moving);
            _dollyEaseInPortionSlider?.gameObject.SetActive(moving);
            _dollyEaseOutDropdown?.gameObject.SetActive(moving);
            _dollyEaseOutPortionSlider?.gameObject.SetActive(moving);
            _dollyDampSlider?.gameObject.SetActive(dolly);
            _dollyOffsetXSlider?.gameObject.SetActive(dolly);
            _dollyOffsetYSlider?.gameObject.SetActive(dolly);
            _dollyOffsetZSlider?.gameObject.SetActive(dolly);

            // The track editor rides with whichever slot reads the track — the position slot riding
            // it, or the rotation slot aiming down it. Aiming along a track with nothing to build
            // one from would otherwise leave the page with no way to lay the points it needs.
            _dollyGroup?.SetActive(dolly || alongTrack);

            _positionSection?.Descriptor.SetTooltip(BasisLocalization.Get(
                BasisCameraModifiers.DescriptionKey(stack.positionModifier)));

            bool compose = stack.rotationModifier == BasisCameraRotationModifier.Compose;
            bool aims = stack.rotationModifier == BasisCameraRotationModifier.LookAtSubject ||
                        stack.rotationModifier == BasisCameraRotationModifier.MatchSubject ||
                        compose || alongTrack;
            bool damps = stack.rotationModifier == BasisCameraRotationModifier.LookAtSubject ||
                         stack.rotationModifier == BasisCameraRotationModifier.MatchSubject ||
                         alongTrack;

            PanelSectionToggleHelpers.SetSectionVisible(_rotationAdvancedSection, _rotationAdvancedGroup, aims);

            _aimPitchSlider?.gameObject.SetActive(aims);
            _aimYawSlider?.gameObject.SetActive(aims);
            _aimDampSlider?.gameObject.SetActive(damps);

            _guidesToggle?.gameObject.SetActive(compose);
            _screenXSlider?.gameObject.SetActive(compose);
            _screenYSlider?.gameObject.SetActive(compose);
            _deadZoneWidthSlider?.gameObject.SetActive(compose);
            _deadZoneHeightSlider?.gameObject.SetActive(compose);
            _softZoneWidthSlider?.gameObject.SetActive(compose);
            _softZoneHeightSlider?.gameObject.SetActive(compose);
            _composerDampHSlider?.gameObject.SetActive(compose);
            _composerDampVSlider?.gameObject.SetActive(compose);
            _composerBiasXSlider?.gameObject.SetActive(compose);
            _composerBiasYSlider?.gameObject.SetActive(compose);

            _rotationSection?.Descriptor.SetTooltip(BasisLocalization.Get(
                BasisCameraModifiers.DescriptionKey(stack.rotationModifier)));

            bool lookAhead = stack.HasEffect(BasisCameraEffectModifier.LookAhead);
            bool occlusion = stack.HasEffect(BasisCameraEffectModifier.AvoidOcclusion);
            bool shake = stack.HasEffect(BasisCameraEffectModifier.Shake);
            bool lens = stack.HasEffect(BasisCameraEffectModifier.LensOverride);
            bool steady = stack.HasEffect(BasisCameraEffectModifier.SteadySubject);
            bool collision = stack.HasEffect(BasisCameraEffectModifier.AvoidCollision);
            bool dollyZoom = stack.HasEffect(BasisCameraEffectModifier.DollyZoom);
            bool rigWeight = stack.HasEffect(BasisCameraEffectModifier.RigWeight);

            SetEffectRowActive(BasisCameraEffectModifier.LookAhead, lookAhead);
            SetEffectRowActive(BasisCameraEffectModifier.AvoidOcclusion, occlusion);
            SetEffectRowActive(BasisCameraEffectModifier.Shake, shake);
            SetEffectRowActive(BasisCameraEffectModifier.LensOverride, lens);
            SetEffectRowActive(BasisCameraEffectModifier.SteadySubject, steady);
            SetEffectRowActive(BasisCameraEffectModifier.AvoidCollision, collision);
            SetEffectRowActive(BasisCameraEffectModifier.DollyZoom, dollyZoom);
            SetEffectRowActive(BasisCameraEffectModifier.RigWeight, rigWeight);

            RefreshEffectSubjectNotices(stack);

            _modifierEffectsEmptyState?.gameObject.SetActive(stack.EffectCount == 0);

            ForceLayoutRebuild(_followGroup);
            ForceLayoutRebuild(_positionAdvancedGroup);
            ForceLayoutRebuild(_rotationAdvancedGroup);
            ForceLayoutRebuild(_modifierEffectsGroup);
            ForceLayoutRebuild(null);
        }

        /// <summary>
        /// Keeps the play button honest. The move can end on its own when an open track runs out,
        /// so the label is driven by what the camera is actually doing rather than by the click.
        /// </summary>
        private void RefreshDollyTransport()
        {
            if (_activeCamera == null || _dollyPlayButton == null) return;

            bool playing = _activeCamera.IsDollyPlaying;
            if (_lastDollyPlaying == playing) return;
            _lastDollyPlaying = playing;

            _dollyPlayButton.Descriptor.SetTitle(BasisLocalization.Get(
                playing ? "camera.dollyPause" : "camera.dollyPlay"));
        }

        /// <summary>
        /// Re-labels the pointing button from the camera's own armed state. Polled rather than
        /// pushed, because pointing ends by pulling a trigger out in the world — the panel is not
        /// told, and a button still reading "Stop Pointing" after the point has landed is a
        /// control that looks broken.
        /// </summary>
        private void RefreshLookAtPointer()
        {
            if (_activeCamera == null || _lookAtPickButton == null) return;

            bool armed = _activeCamera.LookAtPointerArmed;
            if (_lastLookAtArmed == armed) return;
            _lastLookAtArmed = armed;

            _lookAtPickButton.Descriptor.SetTitle(BasisLocalization.Get(
                armed ? "camera.lookAtPick.cancel" : "camera.lookAtPick"));
        }

        private void SetEffectRowActive(BasisCameraEffectModifier effect, bool active)
        {
            _effectSections.TryGetValue(effect, out PanelSectionToggle section);
            _effectGroups.TryGetValue(effect, out PanelElementDescriptor group);
            SetSectionActive(section, group, active);
        }

        private void SetEffectSectionsActive(bool active)
        {
            foreach (KeyValuePair<BasisCameraEffectModifier, PanelSectionToggle> pair in _effectSections)
            {
                SetEffectRowActive(pair.Key, active && Stack != null && Stack.HasEffect(pair.Key));
            }
        }

        /// <summary>
        /// Says so on the section header when a fitted effect has nothing to work on. The subject slot is on
        /// another page, so an effect that has quietly stopped mattering because the slot was
        /// emptied would otherwise look exactly like one that is running.
        /// </summary>
        private void RefreshEffectSubjectNotices(BasisCameraModifierStack stack)
        {
            bool hasSubject = stack.ResolvesSubject;

            foreach (KeyValuePair<BasisCameraEffectModifier, PanelSectionToggle> pair in _effectSections)
            {
                if (pair.Value == null || pair.Value.Descriptor == null) continue;

                bool idle = !hasSubject && BasisCameraModifiers.NeedsSubject(pair.Key);
                pair.Value.Descriptor.SetDescription(idle ? BasisLocalization.Get("camera.modifier.needsSubject") : string.Empty);
            }
        }

        private void RefreshBackgroundVisibility()
        {
            if (_activeCamera == null) return;

            bool colour = _activeCamera.backgroundMode != BasisCameraBackgroundMode.World;
            bool custom = _activeCamera.backgroundMode == BasisCameraBackgroundMode.Custom;

            _backgroundKeepWorldToggle?.gameObject.SetActive(colour);
            _backgroundRedSlider?.gameObject.SetActive(custom);
            _backgroundGreenSlider?.gameObject.SetActive(custom);
            _backgroundBlueSlider?.gameObject.SetActive(custom);

            ForceLayoutRebuild(_backgroundGroup);
        }

        private void TickModifierSections()
        {
            if (_activeCamera == null) return;

            TickAnchorSection();
            SyncModifierSlots();
            RefreshDollyTransport();
            RefreshLookAtPointer();
            RefreshEffectList();
            RefreshWaypointList();
            RefreshCompositionGuides();

            if (_lastDollyPresetRevision != BasisCameraDollyPresets.Revision &&
                (_dollyPresetDropdown?.DropdownComponent == null || !_dollyPresetDropdown.DropdownComponent.IsExpanded))
            {
                RebuildDollyPresetList();
            }
        }

        /// <summary>
        /// Re-seeds the two slots when something else has changed them — picking a camera mode,
        /// loading a saved one, or arming flight, which hands the position channel back. Gated on
        /// the value actually moving, and refused while a dropdown is expanded: swapping the
        /// options under a spawned item list moves the row the pointer is over.
        /// </summary>
        private void SyncModifierSlots()
        {
            BasisCameraModifierStack stack = Stack;
            if (stack == null) return;

            if (_lastSubjectModifier == stack.subject.modifier &&
                _lastPositionModifier == stack.positionModifier &&
                _lastRotationModifier == stack.rotationModifier)
            {
                return;
            }

            if (_subjectDropdown?.DropdownComponent != null && _subjectDropdown.DropdownComponent.IsExpanded) return;
            if (_positionDropdown?.DropdownComponent != null && _positionDropdown.DropdownComponent.IsExpanded) return;
            if (_rotationDropdown?.DropdownComponent != null && _rotationDropdown.DropdownComponent.IsExpanded) return;

            SeedModifierControls();
        }

        private void RefreshWaypointList()
        {
            if (_waypointDropdown == null || _activeCamera == null) return;

            // A camera whose track has not been built yet reads as no waypoints rather than
            // skipping the refresh: returning early left the dropdown on screen holding the
            // placeholder options its prefab shipped with, and they name no waypoint.
            int count = _activeCamera.DollyTrack != null ? _activeCamera.DollyWaypointCount : 0;
            if (count == _lastWaypointCount) return;
            _lastWaypointCount = count;

            _waypointKeys.Clear();
            var labels = new List<string>();
            for (int Index = 0; Index < count; Index++)
            {
                _waypointKeys.Add(Index.ToString());
                labels.Add($"{BasisLocalization.Get("camera.waypoint")} {Index + 1}");
            }

            bool hasAny = count > 0;
            RefreshDollyPresetButtons();
            _dollyEmptyState?.gameObject.SetActive(!hasAny);
            _waypointDropdown.gameObject.SetActive(hasAny);
            _waypointOrderSlider?.gameObject.SetActive(hasAny);

            if (hasAny)
            {
                _waypointDropdown.AssignEntries(_waypointKeys, labels);
                _selectedWaypointIndex = Mathf.Clamp(_selectedWaypointIndex, 0, count - 1);
                _waypointDropdown.SetValueWithoutNotify(_waypointKeys[_selectedWaypointIndex]);
                SeedWaypointOrderSlider();
            }

            ForceLayoutRebuild(_dollyGroup);
        }

        private void SeedWaypointOrderSlider()
        {
            if (_waypointOrderSlider == null || _activeCamera == null) return;
            _waypointOrderSlider.SetValueWithoutNotify(_selectedWaypointIndex + 1);
        }

        private void ClearModifierReferences()
        {
            // The guides are children of the preview image, which the panel destroys with itself.
            // Dropping the handles without clearing _guidesBuilt would leave the next open
            // believing it already had them and drawing nothing.
            DestroyCompositionGuides();
            _guidesToggle = null;

            _positionSection = null;
            _positionGroup = null;
            _positionAdvancedSection = null;
            _positionAdvancedGroup = null;
            _positionDropdown = null;
            _bindingModeDropdown = null;
            _placeOffsetXSlider = null;
            _placeOffsetYSlider = null;
            _placeOffsetZSlider = null;
            _placeDampXSlider = null;
            _placeDampYSlider = null;
            _placeDampZSlider = null;
            _placeTeleportSlider = null;
            _followLateralSlider = null;
            _framingSizeSlider = null;
            _framingZoomToggle = null;
            _framingMinSlider = null;
            _framingMaxSlider = null;
            _orbitFollowHeadingToggle = null;
            _orbitHeadingSlider = null;
            _orbitVerticalSlider = null;
            _orbitHeadingDampSlider = null;
            _orbitTopHeightSlider = null;
            _orbitTopRadiusSlider = null;
            _orbitMidHeightSlider = null;
            _orbitMidRadiusSlider = null;
            _orbitBottomHeightSlider = null;
            _orbitBottomRadiusSlider = null;
            _dollyModeDropdown = null;
            _dollyTransportRow = null;
            _dollyPlayButton = null;
            _lastDollyPlaying = null;
            _dollyPositionSlider = null;
            _dollySpeedSlider = null;
            _dollyEaseInDropdown = null;
            _dollyEaseInPortionSlider = null;
            _dollyEaseOutDropdown = null;
            _dollyEaseOutPortionSlider = null;
            _dollyDampSlider = null;
            _dollyOffsetXSlider = null;
            _dollyOffsetYSlider = null;
            _dollyOffsetZSlider = null;

            _rotationSection = null;
            _rotationGroup = null;
            _rotationAdvancedSection = null;
            _rotationAdvancedGroup = null;
            _rotationDropdown = null;
            _aimPitchSlider = null;
            _aimYawSlider = null;
            _aimDampSlider = null;
            _screenXSlider = null;
            _screenYSlider = null;
            _deadZoneWidthSlider = null;
            _deadZoneHeightSlider = null;
            _softZoneWidthSlider = null;
            _softZoneHeightSlider = null;
            _composerDampHSlider = null;
            _composerDampVSlider = null;
            _composerBiasXSlider = null;
            _composerBiasYSlider = null;

            _modifierEffectsSection = null;
            _modifierEffectsGroup = null;
            _effectAddDropdown = null;
            _modifierEffectsEmptyState = null;
            _lookAheadTimeSlider = null;
            _lookAheadLimitSlider = null;
            _occlusionPaddingSlider = null;
            _occlusionMinSlider = null;
            _occlusionReturnSlider = null;
            _occlusionRadiusSlider = null;
            _noiseProfileDropdown = null;
            _noiseAmplitudeSlider = null;
            _noiseFrequencySlider = null;
            _lensFovSlider = null;
            _lensDampSlider = null;
            _steadySmoothingSlider = null;
            _steadyDeadZoneSlider = null;
            _collisionRadiusSlider = null;
            _collisionPaddingSlider = null;
            _dollyZoomMinSlider = null;
            _dollyZoomMaxSlider = null;
            _rigWeightResponseSlider = null;
            _rigWeightBounceSlider = null;
            _effectSections.Clear();
            _effectGroups.Clear();
            _addableEffects.Clear();
            _lastEffectSignature = -1;
            _subjectDropdown = null;
            _groupRefreshRow = null;
            _fixedPointRow = null;
            _lookAtPickButton = null;
            _lastLookAtArmed = null;
            _lastSubjectModifier = null;
            _lastPositionModifier = null;
            _lastRotationModifier = null;

            _dollyGroup = null;
            _waypointDropdown = null;
            _waypointOrderSlider = null;
            _dollyLoopToggle = null;
            _dollyVisibleToggle = null;
            _dollySyncDropdown = null;
            _dollyGridSnapToggle = null;
            _dollyGridSizeSlider = null;
            _dollySpeedColorToggle = null;
            _dollyPresetStatus = null;
            _dollyPresetDropdown = null;
            _dollyPresetNameField = null;
            _dollyPresetSaveButton = null;
            _dollyPresetLoadButton = null;
            _dollyPresetLoadInPlaceButton = null;
            _dollyPresetRemoveButton = null;
            _dollyPresetExportButton = null;
            _dollyPresetKeys.Clear();
            _lastDollyPresetRevision = -1;
            _dollyEmptyState = null;
            _waypointKeys.Clear();
            _selectedWaypointIndex = 0;
            _lastWaypointCount = -1;

            _backgroundSection = null;
            _backgroundGroup = null;
            _backgroundModeDropdown = null;
            _backgroundKeepWorldToggle = null;
            _backgroundRedSlider = null;
            _backgroundGreenSlider = null;
            _backgroundBlueSlider = null;
        }
    }
}
