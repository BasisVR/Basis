using Basis;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public static class SettingsProviderIK
{
    public const string SeatedMode_Seated = "Seated Mode";
    public const string SeatedMode_Standing = "Standing Mode";

    private static readonly List<PanelToggle> _euroToggleUIs = new();
    private static readonly List<PanelToggle> _trackerLerpToggleUIs = new();

    private static readonly string[] PlayspaceMoverInputValues =
    {
        BasisLocalPlayspaceMover.InputGrip,
        BasisLocalPlayspaceMover.InputTrigger,
        BasisLocalPlayspaceMover.InputSecondaryTrigger,
        BasisLocalPlayspaceMover.InputPrimary,
        BasisLocalPlayspaceMover.InputSecondary,
        BasisLocalPlayspaceMover.InputJoystick,
        BasisLocalPlayspaceMover.InputTrackpad,
        BasisLocalPlayspaceMover.InputMenu,
    };

    private static readonly string[] PlayspaceMoverInputKeys =
    {
        "settings.bodyTracking.playspaceMover.input.grip",
        "settings.bodyTracking.playspaceMover.input.trigger",
        "settings.bodyTracking.playspaceMover.input.secondaryTrigger",
        "settings.bodyTracking.playspaceMover.input.primary",
        "settings.bodyTracking.playspaceMover.input.secondary",
        "settings.bodyTracking.playspaceMover.input.joystick",
        "settings.bodyTracking.playspaceMover.input.trackpad",
        "settings.bodyTracking.playspaceMover.input.menu",
    };

    private static PanelDropdown _boneDropdown;

    private static PanelToggle _uiUseCalibration;
    private static PanelToggle _uiSmoothPos;
    private static PanelToggle _uiSmoothRot;
    private static PanelToggle _uiEuroPos;
    private static PanelToggle _uiEuroRot;
    private static PanelSlider _uiCalibSphereScale;
    private static PanelSlider _avatarScaleSlider;
    private static PanelSlider _perAvatarSizeSlider;
    private static PanelElementDescriptor _boneEuroEditorGroup;

    private struct BoneBindings
    {
        public string Name;
        public BasisSettingsBinding<bool> UseCalibration;
        public BasisSettingsBinding<bool> SmoothPos;
        public BasisSettingsBinding<bool> SmoothRot;
        public BasisSettingsBinding<bool> EuroPos;
        public BasisSettingsBinding<bool> EuroRot;
        public BasisSettingsBinding<float> CalibSphereScale;
    }

    private static readonly List<BoneBindings> _bones = new();

    // ------------------
    // IK & Input
    // ------------------
    public static PanelTabPage IKTab(PanelTabGroup tabGroup)
    {
        // --- Tab (replaces BasisTabBuilder) ---
        var tabPage = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
        var tabDesc = tabPage.Descriptor;
        tabDesc.SetTitle(BasisLocalization.Get("settings.tab.bodytracking"));
        tabDesc.SetIcon(AddressableAssets.Sprites.Settings);

        // ------------------
        // Custom Scale
        // ------------------
        CreateCollapsibleSection(tabDesc, tabDesc,
            BasisLocalization.Get("settings.bodyTracking.customScale"),
            BasisLocalization.Get("settings.bodyTracking.customScale.description"), false, scaleParent =>
        {
            var customScaleToggle = PanelToggle.CreateNewEntry(scaleParent);
            customScaleToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.customScale"));
            customScaleToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.customScale.tooltip"));
            customScaleToggle.AssignBinding(BasisSettingsDefaults.CustomScale);

            var avatarScaleSlider = PanelSlider.CreateAndBind(
                scaleParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.avatarHeightScale"), 0.1f, 5f, false, 2, ValueDisplayMode.Meters),
                BasisSettingsDefaults.SelectedScale);
            _avatarScaleSlider = avatarScaleSlider;

            if (avatarScaleSlider != null)
            {
                avatarScaleSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.avatarHeightScale.tooltip"));

                avatarScaleSlider.gameObject.SetActive(BasisSettingsDefaults.CustomScale.RawValue);
                customScaleToggle.OnValueChanged += visible =>
                {
                    avatarScaleSlider.gameObject.SetActive(visible);
                    RebuildLayoutChain(scaleParent, tabDesc);
                };
            }

            // Escape hatch for an avatar whose proportions no automatic fit can rescue. Per-avatar, so
            // correcting a chibi never distorts the body size you carry onto everything else.
            BasisPerAvatarScale.RefreshForCurrentAvatar();
            var perAvatarSizeSlider = PanelSlider.CreateNew(scaleParent);
            _perAvatarSizeSlider = perAvatarSizeSlider;
            if (perAvatarSizeSlider != null)
            {
                perAvatarSizeSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                    BasisLocalization.Get("calibration.avatarNudge"),
                    BasisPerAvatarScale.Min, BasisPerAvatarScale.Max,
                    false, 2, ValueDisplayMode.percentageFromZero));
                perAvatarSizeSlider.Descriptor.SetTooltip(BasisLocalization.Get("calibration.avatarNudge.tooltip"));
                perAvatarSizeSlider.SetValueWithoutNotify(BasisPerAvatarScale.Current);
                perAvatarSizeSlider.OnValueChanged += value => BasisPerAvatarScale.SetForCurrentAvatar(value);
            }
        });

        _trackerLerpToggleUIs.Clear();
        _euroToggleUIs.Clear();

        // ------------------
        // Per-Bone Settings
        // ------------------
        CreateCollapsibleSection(tabDesc, tabDesc,
            BasisLocalization.Get("settings.bodyTracking.section.perBone.title"),
            BasisLocalization.Get("settings.bodyTracking.section.perBone.description"), false,
            AddFBIKTogglesCompact);

        SyncMasterEuroFromChildren();

        // ------------------
        // Playspace Mover
        // ------------------
        CreateCollapsibleSection(tabDesc, tabDesc,
            BasisLocalization.Get("settings.bodyTracking.playspaceMover.title"),
            BasisLocalization.Get("settings.bodyTracking.playspaceMover.description"), false, moverParent =>
        {
            var enableToggle = PanelToggle.CreateNewEntry(moverParent);
            enableToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.playspaceMover.enable"));
            enableToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.playspaceMover.enable.tooltip"));
            enableToggle.AssignBinding(BasisSettingsDefaults.EnablePlayspaceMover);

            var inputDropdown = PanelDropdown.CreateNewEntry(moverParent);
            inputDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.playspaceMover.input"));
            inputDropdown.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.playspaceMover.input.tooltip"));
            inputDropdown.AssignLocalizedEntries(
                new List<string>(PlayspaceMoverInputValues),
                new List<string>(PlayspaceMoverInputKeys));
            inputDropdown.AssignBinding(BasisSettingsDefaults.PlayspaceMoverInput);

            var handDropdown = PanelDropdown.CreateNewEntry(moverParent);
            handDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.playspaceMover.hand"));
            handDropdown.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.playspaceMover.hand.tooltip"));
            handDropdown.AssignLocalizedEntries(
                new List<string> { BasisLocalPlayspaceMover.HandBoth, BasisLocalPlayspaceMover.HandLeft, BasisLocalPlayspaceMover.HandRight },
                new List<string> { "settings.bodyTracking.playspaceMover.hand.both", "settings.bodyTracking.playspaceMover.hand.left", "settings.bodyTracking.playspaceMover.hand.right" });
            handDropdown.AssignBinding(BasisSettingsDefaults.PlayspaceMoverHand);

            var rotateToggle = PanelToggle.CreateNewEntry(moverParent);
            rotateToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.playspaceMover.rotate"));
            rotateToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.playspaceMover.rotate.tooltip"));
            rotateToggle.AssignBinding(BasisSettingsDefaults.PlayspaceMoverRotate);

            var rotateInputDropdown = PanelDropdown.CreateNewEntry(moverParent);
            rotateInputDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.playspaceMover.rotateInput"));
            rotateInputDropdown.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.playspaceMover.rotateInput.tooltip"));
            rotateInputDropdown.AssignLocalizedEntries(
                new List<string>(PlayspaceMoverInputValues),
                new List<string>(PlayspaceMoverInputKeys));
            rotateInputDropdown.AssignBinding(BasisSettingsDefaults.PlayspaceMoverRotateInput);

            var scaleToggle = PanelToggle.CreateNewEntry(moverParent);
            scaleToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.playspaceMover.scale"));
            scaleToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.playspaceMover.scale.tooltip"));
            scaleToggle.AssignBinding(BasisSettingsDefaults.PlayspaceMoverScale);

            var verticalToggle = PanelToggle.CreateNewEntry(moverParent);
            verticalToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.playspaceMover.vertical"));
            verticalToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.playspaceMover.vertical.tooltip"));
            verticalToggle.AssignBinding(BasisSettingsDefaults.PlayspaceMoverVertical);

            var flipToggle = PanelToggle.CreateNewEntry(moverParent);
            flipToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.playspaceMover.flip"));
            flipToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.playspaceMover.flip.tooltip"));
            flipToggle.AssignBinding(BasisSettingsDefaults.PlayspaceMoverFlip);

            var flipAxisDropdown = PanelDropdown.CreateNewEntry(moverParent);
            flipAxisDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.playspaceMover.flipAxis"));
            flipAxisDropdown.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.playspaceMover.flipAxis.tooltip"));
            flipAxisDropdown.AssignLocalizedEntries(
                new List<string> { BasisLocalPlayspaceMover.AxisRoll, BasisLocalPlayspaceMover.AxisPitch, BasisLocalPlayspaceMover.AxisYaw },
                new List<string> { "settings.bodyTracking.playspaceMover.flipAxis.roll", "settings.bodyTracking.playspaceMover.flipAxis.pitch", "settings.bodyTracking.playspaceMover.flipAxis.yaw" });
            flipAxisDropdown.AssignBinding(BasisSettingsDefaults.PlayspaceMoverFlipAxis);

            var flipAngleSlider = PanelSlider.CreateEntryAndBind(
                moverParent,
                PanelSlider.SliderSettings.Degrees(BasisLocalization.Get("settings.bodyTracking.playspaceMover.flipAngle"), 0f, 360f, true, 0),
                BasisSettingsDefaults.PlayspaceMoverFlipAngle);
            flipAngleSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.playspaceMover.flipAngle.tooltip"));

            var resetButton = PanelButton.CreateNew(moverParent);
            resetButton.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.playspaceMover.reset"));
            resetButton.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.playspaceMover.reset.tooltip"));
            resetButton.OnClicked += BasisLocalPlayspaceMover.ResetOffset;

            var gizmoToggle = PanelToggle.CreateNewEntry(moverParent);
            gizmoToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.playspaceMover.gizmos"));
            gizmoToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.playspaceMover.gizmos.tooltip"));
            gizmoToggle.AssignBinding(BasisSettingsDefaults.PlayspaceGizmos);

            var gizmoLayers = new List<PanelToggle>
            {
                PlayspaceGizmoLayerToggle(moverParent, "settings.bodyTracking.playspaceMover.gizmos.boundary", BasisSettingsDefaults.PlayspaceGizmoBoundary),
                PlayspaceGizmoLayerToggle(moverParent, "settings.bodyTracking.playspaceMover.gizmos.origin", BasisSettingsDefaults.PlayspaceGizmoOrigin),
                PlayspaceGizmoLayerToggle(moverParent, "settings.bodyTracking.playspaceMover.gizmos.offset", BasisSettingsDefaults.PlayspaceGizmoOffset),
                PlayspaceGizmoLayerToggle(moverParent, "settings.bodyTracking.playspaceMover.gizmos.hands", BasisSettingsDefaults.PlayspaceGizmoHands),
                PlayspaceGizmoLayerToggle(moverParent, "settings.bodyTracking.playspaceMover.gizmos.readouts", BasisSettingsDefaults.PlayspaceGizmoReadouts),
            };

            SetPlayspaceGizmoLayersVisible(gizmoLayers, gizmoToggle.Value);
            gizmoToggle.OnValueChanged += value =>
            {
                SetPlayspaceGizmoLayersVisible(gizmoLayers, value);
                RebuildLayoutChain(moverParent, tabDesc);
            };
        });

        // ------------------
        // Tracking & Input
        // ------------------
        CreateCollapsibleSection(tabDesc, tabDesc,
            BasisLocalization.Get("settings.bodyTracking.section.tracking.title"),
            BasisLocalization.Get("settings.bodyTracking.section.tracking.description"), true, trackingParent =>
        {
            var fbtEnabledToggle = PanelToggle.CreateNewEntry(trackingParent);
            fbtEnabledToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.fbt"));
            fbtEnabledToggle.AssignBinding(BasisSettingsDefaults.EnableFBT);
            fbtEnabledToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.fbt.tooltip"));

            var trackerVisualsDropdown = PanelDropdown.CreateNewEntry(trackingParent);
            trackerVisualsDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.trackerVisuals.title"));
            trackerVisualsDropdown.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.trackerVisuals.tooltip"));
            trackerVisualsDropdown.AssignLocalizedEntries(
                new List<string>
                {
                    BasisSettingsDefaults.TrackerVisuals_Off,
                    BasisSettingsDefaults.TrackerVisuals_Markers,
                    BasisSettingsDefaults.TrackerVisuals_DeviceModels
                },
                new List<string>
                {
                    "settings.bodyTracking.trackerVisuals.off",
                    "settings.bodyTracking.trackerVisuals.markers",
                    "settings.bodyTracking.trackerVisuals.deviceModels"
                });
            trackerVisualsDropdown.AssignBinding(BasisSettingsDefaults.TrackerVisuals);

            var oscEnabledToggle = PanelToggle.CreateNewEntry(trackingParent);
            oscEnabledToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.osc"));
            oscEnabledToggle.AssignBinding(BasisSettingsDefaults.EnableOSC);
            oscEnabledToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.osc.tooltip"));

            var faceTrackingToggle = PanelToggle.CreateNewEntry(trackingParent);
            faceTrackingToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.faceTracking.title"));
            faceTrackingToggle.AssignBinding(BasisSettingsDefaults.EnableFaceTracking);
            faceTrackingToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.faceTracking.title.tooltip"));

            var eyeTrackingToggle = PanelToggle.CreateNewEntry(trackingParent);
            eyeTrackingToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.eyeTracking.title"));
            eyeTrackingToggle.AssignBinding(BasisSettingsDefaults.EnableEyeTracking);
            eyeTrackingToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.eyeTracking.title.tooltip"));

            var footIKToggle = PanelToggle.CreateNewEntry(trackingParent);
            footIKToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.footIk"));
            footIKToggle.AssignBinding(BasisSettingsDefaults.FootIKEnabled);
            footIKToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.footIk.tooltip"));

           // var neuralPoleToggle = PanelToggle.CreateNewEntry(trackingParent);
          //  neuralPoleToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.neuralPole"));
           // neuralPoleToggle.AssignBinding(BasisSettingsDefaults.FBIKNeuralPole);
          //  neuralPoleToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.neuralPole.tooltip"));

            var disableAnimInFBTToggle = PanelToggle.CreateNewEntry(trackingParent);
            disableAnimInFBTToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.disableAnimFbt"));
            disableAnimInFBTToggle.AssignBinding(BasisSettingsDefaults.DisableAnimationsInFBT);
            disableAnimInFBTToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.disableAnimFbt.tooltip"));

            var jobLocomotionToggle = PanelToggle.CreateNewEntry(trackingParent);
            jobLocomotionToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.jobLocomotion"));
            jobLocomotionToggle.AssignBinding(BasisSettingsDefaults.FBIKJobLocomotion);
            jobLocomotionToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.jobLocomotion.tooltip"));

            var butterflyKneesToggle = PanelToggle.CreateNewEntry(trackingParent);
            butterflyKneesToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.butterflyKnees"));
            butterflyKneesToggle.AssignBinding(BasisSettingsDefaults.FBIKButterflyKnees);
            butterflyKneesToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.butterflyKnees.tooltip"));

            var kneeFollowsFootToggle = PanelToggle.CreateNewEntry(trackingParent);
            kneeFollowsFootToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.kneeFollowsFoot"));
            kneeFollowsFootToggle.AssignBinding(BasisSettingsDefaults.FBIKKneeFollowsFoot);
            kneeFollowsFootToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.kneeFollowsFoot.tooltip"));
        });

        // ------------------
        // Advanced IK toggle
        // ------------------
        var advancedToggle = PanelSectionToggle.CreateNewEntry(tabDesc.ContentParent);
        advancedToggle.SetTitle(BasisLocalization.Get("settings.bodyTracking.advanced"));
        advancedToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.advanced.tooltip"));
        advancedToggle.BindToToggle(BasisSettingsDefaults.FBIKAdvancedVisible);

        var colliderGroup = PanelElementDescriptor.CreateNew(
            PanelElementDescriptor.ElementStyles.Group,
            tabDesc.ContentParent);

        colliderGroup.SetTitle(BasisLocalization.Get("settings.bodyTracking.colliders.title"));
        colliderGroup.SetIcon(AddressableAssets.Sprites.Settings);
        colliderGroup.SetBackgroundVisible(false);
        advancedToggle.RegisterContentContainer(colliderGroup);

        var colliderParent = colliderGroup.ContentParent;

        // ============== Body Collision ==============
        CreateCollapsibleSection(tabDesc, colliderGroup,
            BasisLocalization.Get("settings.bodyTracking.section.collision.title"),
            BasisLocalization.Get("settings.bodyTracking.section.collision.description"), false, collisionParent =>
        {
            var collisionsToggle = PanelToggle.CreateNewEntry(collisionParent);
            collisionsToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.collisionsEnabled"));
            collisionsToggle.AssignBinding(BasisSettingsDefaults.FBIKCollisionsEnabled);
            collisionsToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.collisionsEnabled.tooltip"));

            var protectElbowToggle = PanelToggle.CreateNewEntry(collisionParent);
            protectElbowToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.protectElbow.title"));
            protectElbowToggle.AssignBinding(BasisSettingsDefaults.FBIKProtectElbow);
            protectElbowToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.protectElbow.title.tooltip"));

            var collideTrackedElbowToggle = PanelToggle.CreateNewEntry(collisionParent);
            collideTrackedElbowToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.collideTrackedElbow.title"));
            collideTrackedElbowToggle.AssignBinding(BasisSettingsDefaults.FBIKCollideTrackedElbow);
            collideTrackedElbowToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.collideTrackedElbow.title.tooltip"));

            var wristAxialBoundToggle = PanelToggle.CreateNewEntry(collisionParent);
            wristAxialBoundToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.wristAxialBound.title"));
            wristAxialBoundToggle.AssignBinding(BasisSettingsDefaults.FBIKWristAxialBound);
            wristAxialBoundToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.wristAxialBound.title.tooltip"));

            // Elbow drag (no elbow tracker only). How heavy this should feel is subjective and cannot be
            // settled offline, so the corner frequency is exposed rather than baked. The slider's minimum is
            // a real value (1 Hz = heaviest), not a "0 means default" sentinel.
            var elbowDragToggle = PanelToggle.CreateNewEntry(collisionParent);
            elbowDragToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.elbowDrag.title"));
            elbowDragToggle.AssignBinding(BasisSettingsDefaults.FBIKElbowDrag);
            elbowDragToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.elbowDrag.title.tooltip"));

            var elbowDragSlider = PanelSlider.CreateAndBind(
                collisionParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.elbowDragHz.title"), 0.5f, 8f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKElbowDragHz);
            if (elbowDragSlider != null)
            {
                elbowDragSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.elbowDragHz.title.tooltip"));
                elbowDragSlider.Descriptor.SetActive(elbowDragToggle.Value);
                elbowDragToggle.OnValueChanged += value =>
                {
                    elbowDragSlider.Descriptor.SetActive(value);
                    RebuildLayoutChain(collisionParent, tabDesc);
                };
            }

            var chestRadiusSlider = PanelSlider.CreateAndBind(
                collisionParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.chestRadius.title"), 0.01f, 0.5f, false, 3, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKChestRadius);
            if (chestRadiusSlider != null)
            {
                chestRadiusSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.chestRadius.title.tooltip"));
            }

            var collisionSkinSlider = PanelSlider.CreateAndBind(
                collisionParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.collisionSkin.title"), 0f, 0.1f, false, 3, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKCollisionSkin);
            if (collisionSkinSlider != null)
            {
                collisionSkinSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.collisionSkin.title.tooltip"));
            }

            var handRadiusSlider = PanelSlider.CreateAndBind(
                collisionParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.handRadius.title"), 0f, 0.2f, false, 3, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKHandRadius);
            if (handRadiusSlider != null)
            {
                handRadiusSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.handRadius.title.tooltip"));
            }

            var handSkinSlider = PanelSlider.CreateAndBind(
                collisionParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.handSkin.title"), 0f, 0.1f, false, 3, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKHandSkin);
            if (handSkinSlider != null)
            {
                handSkinSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.handSkin.title.tooltip"));
            }
        });

        // ============== Shoulders ==============
        CreateCollapsibleSection(tabDesc, colliderGroup,
            BasisLocalization.Get("settings.bodyTracking.section.shoulders.title"),
            BasisLocalization.Get("settings.bodyTracking.section.shoulders.description"), false, shoulderParent =>
        {
            var shoulderSolveToggle = PanelToggle.CreateNewEntry(shoulderParent);
            shoulderSolveToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.shoulderSolve.title"));
            shoulderSolveToggle.AssignBinding(BasisSettingsDefaults.FBIKShoulderSolveEnabled);
            shoulderSolveToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.shoulderSolve.title.tooltip"));

            var shoulderShrugToggle = PanelToggle.CreateNewEntry(shoulderParent);
            shoulderShrugToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.shoulderShrug.title"));
            shoulderShrugToggle.AssignBinding(BasisSettingsDefaults.FBIKShoulderShrug);
            shoulderShrugToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.shoulderShrug.title.tooltip"));

            var shoulderRetractionToggle = PanelToggle.CreateNewEntry(shoulderParent);
            shoulderRetractionToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.shoulderRetraction.title"));
            shoulderRetractionToggle.AssignBinding(BasisSettingsDefaults.FBIKShoulderRetraction);
            shoulderRetractionToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.shoulderRetraction.title.tooltip"));

            var shoulderElevSlider = PanelSlider.CreateAndBind(
                shoulderParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.shoulderElevation.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKShoulderElevation);
            if (shoulderElevSlider != null)
            {
                shoulderElevSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.shoulderElevation.title.tooltip"));
            }

            var shoulderProtSlider = PanelSlider.CreateAndBind(
                shoulderParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.shoulderProtraction.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKShoulderProtraction);
            if (shoulderProtSlider != null)
            {
                shoulderProtSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.shoulderProtraction.title.tooltip"));
            }

            var shoulderCoupleSlider = PanelSlider.CreateAndBind(
                shoulderParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.shoulderCoupleRatio.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKShoulderCoupleRatio);
            shoulderCoupleSlider?.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.shoulderCoupleRatio.title.tooltip"));

            var shoulderMaxSlider = PanelSlider.CreateAndBind(
                shoulderParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.shoulderMaxDeg.title"), 0f, 60f, false, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKShoulderMaxDeg);
            shoulderMaxSlider?.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.shoulderMaxDeg.title.tooltip"));

            var shoulderSlideStartSlider = PanelSlider.CreateAndBind(
                shoulderParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.shoulderSlideStart.title"), 0f, 90f, false, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKShoulderSlideStartDeg);
            shoulderSlideStartSlider?.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.shoulderSlideStart.title.tooltip"));

            var shoulderSlideMaxSlider = PanelSlider.CreateAndBind(
                shoulderParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.shoulderSlideMax.title"), 0f, 45f, false, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKShoulderSlideMaxDeg);
            shoulderSlideMaxSlider?.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.shoulderSlideMax.title.tooltip"));

            var shoulderSlideFractionSlider = PanelSlider.CreateAndBind(
                shoulderParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.shoulderSlideFraction.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKShoulderSlideFraction);
            shoulderSlideFractionSlider?.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.shoulderSlideFraction.title.tooltip"));
        });

        // ============== Arm Twist ==============
        CreateCollapsibleSection(tabDesc, colliderGroup,
            BasisLocalization.Get("settings.bodyTracking.section.armTwist.title"),
            BasisLocalization.Get("settings.bodyTracking.section.armTwist.description"), false, twistParent =>
        {
            var lowerArmTwist = PanelSlider.CreateAndBind(
                twistParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.lowerArmTwist.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKLowerArmTwistFraction);
            if (lowerArmTwist != null)
            {
                lowerArmTwist.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.lowerArmTwist.title.tooltip"));
            }

            var upperArmTwist = PanelSlider.CreateAndBind(
                twistParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.upperArmTwist.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKUpperArmTwistFraction);
            if (upperArmTwist != null)
            {
                upperArmTwist.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.upperArmTwist.title.tooltip"));
            }
        });

        // ============== Body Fit ==============
        CreateCollapsibleSection(tabDesc, colliderGroup,
            BasisLocalization.Get("settings.bodyTracking.section.bodyFit.title"),
            BasisLocalization.Get("settings.bodyTracking.section.bodyFit.description"), false, fitParent =>
        {
            AddAnatomyToggle(fitParent, BasisSettingsDefaults.FBIKBodyFit,
                "settings.bodyTracking.bodyFit.enabled.title",
                "settings.bodyTracking.bodyFit.enabled.description");

            var maxDeviation = PanelSlider.CreateAndBind(
                fitParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.bodyFit.maxDeviation.title"), 0f, Basis.IK.BasisBodyFitCore.MaxDeviationCeiling, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKBodyFitMaxDeviation);
            if (maxDeviation != null)
            {
                maxDeviation.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.bodyFit.maxDeviation.tooltip"));
            }

            var armHeightRatioToggle = PanelToggle.CreateNewEntry(fitParent);
            armHeightRatioToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.armHeightRatio.enabled.title"));
            armHeightRatioToggle.AssignBinding(BasisSettingsDefaults.FBIKArmHeightRatioEnabled);
            armHeightRatioToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.armHeightRatio.enabled.title.tooltip"));

            var armHeightRatioSlider = PanelSlider.CreateAndBind(
                fitParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.armHeightRatio.title"), 0.6f, 1.5f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKArmHeightRatio);
            if (armHeightRatioSlider != null)
            {
                armHeightRatioSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.armHeightRatio.title.tooltip"));
                armHeightRatioSlider.Descriptor.SetActive(armHeightRatioToggle.Value);
                armHeightRatioToggle.OnValueChanged += value =>
                {
                    armHeightRatioSlider.Descriptor.SetActive(value);
                    RebuildLayoutChain(fitParent, tabDesc);
                };
            }
        });

        // ============== Anatomy ==============
        CreateCollapsibleSection(tabDesc, colliderGroup,
            BasisLocalization.Get("settings.bodyTracking.section.anatomy.title"),
            BasisLocalization.Get("settings.bodyTracking.section.anatomy.description"), false, anatomyParent =>
        {
            AddAnatomyToggle(anatomyParent, BasisSettingsDefaults.FBIKAnatDifferentialStiffness,
                "settings.bodyTracking.anat.diffStiffness.title",
                "settings.bodyTracking.anat.diffStiffness.description");
            AddAnatomyToggle(anatomyParent, BasisSettingsDefaults.FBIKAnatShoulderSlide,
                "settings.bodyTracking.anat.shoulderSlide.title",
                "settings.bodyTracking.anat.shoulderSlide.description");
            AddAnatomyToggle(anatomyParent, BasisSettingsDefaults.FBIKAnatCervicalLordosis,
                "settings.bodyTracking.anat.cervicalLordosis.title",
                "settings.bodyTracking.anat.cervicalLordosis.description");
            AddAnatomyToggle(anatomyParent, BasisSettingsDefaults.FBIKAnatPelvicTwistRouting,
                "settings.bodyTracking.anat.pelvicTwistRouting.title",
                "settings.bodyTracking.anat.pelvicTwistRouting.description");
            AddAnatomyToggle(anatomyParent, BasisSettingsDefaults.FBIKSpineAnatomicalRom,
                "settings.bodyTracking.anat.spineRom.title",
                "settings.bodyTracking.anat.spineRom.description");
            AddAnatomyToggle(anatomyParent, BasisSettingsDefaults.FBIKChestIKTarget,
                "settings.bodyTracking.anat.chestIkTarget.title",
                "settings.bodyTracking.anat.chestIkTarget.description");
            AddAnatomyToggle(anatomyParent, BasisSettingsDefaults.FBIKLegSwivelSmoothing,
                "settings.bodyTracking.anat.legSwivelSmoothing.title",
                "settings.bodyTracking.anat.legSwivelSmoothing.description");
            AddAnatomyToggle(anatomyParent, BasisSettingsDefaults.FBIKKneeFootPoleHold,
                "settings.bodyTracking.kneeFootPoleHold.title",
                "settings.bodyTracking.kneeFootPoleHold.description");
            AddAnatomyToggle(anatomyParent, BasisSettingsDefaults.FBIKKneeFootPoleConditioning,
                "settings.bodyTracking.kneeFootPoleConditioning.title",
                "settings.bodyTracking.kneeFootPoleConditioning.description");
            AddAnatomyToggle(anatomyParent, BasisSettingsDefaults.FBIKTrackerBendNormal,
                "settings.bodyTracking.anat.trackerBendNormal.title",
                "settings.bodyTracking.anat.trackerBendNormal.description");
        });

        // ============== Avatar constraints ==============
        // The avatar-authored constraint solve runs after the IK, so it gets the last word on any bone
        // it drives. Turning it off is the quickest way to tell an IK problem apart from a constraint
        // on the same bone -- and on an avatar whose constraints fight the solver, a usable workaround.
        CreateCollapsibleSection(tabDesc, colliderGroup,
            BasisLocalization.Get("settings.bodyTracking.section.constraints.title"),
            BasisLocalization.Get("settings.bodyTracking.section.constraints.description"), false, constraintParent =>
        {
            AddAnatomyToggle(constraintParent, BasisSettingsDefaults.AvatarConstraintsEnabled,
                "settings.bodyTracking.constraints.enabled.title",
                "settings.bodyTracking.constraints.enabled.description");
        });

        // ============== Desktop head carry ==============
        // Desktop only: in VR the headset already rides the lever arm this reproduces.
        CreateCollapsibleSection(tabDesc, colliderGroup,
            BasisLocalization.Get("settings.bodyTracking.section.headSwing.title"),
            BasisLocalization.Get("settings.bodyTracking.section.headSwing.description"), false, swingParent =>
        {
            AddAnatomyToggle(swingParent, BasisSettingsDefaults.DesktopHeadSwingEnabled,
                "settings.bodyTracking.headSwing.enabled.title",
                "settings.bodyTracking.headSwing.enabled.description");

            var strength = PanelSlider.CreateAndBind(
                swingParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.headSwing.strength"), 0f, 2f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.DesktopHeadSwingStrength);
            strength?.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.headSwing.strength.tooltip"));

            var backward = PanelSlider.CreateAndBind(
                swingParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.headSwing.backward"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.DesktopHeadSwingBackward);
            backward?.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.headSwing.backward.tooltip"));
        });

        // ============== Spine: Reach Limits ==============
        CreateCollapsibleSection(tabDesc, colliderGroup,
            BasisLocalization.Get("settings.bodyTracking.section.spineReach.title"),
            BasisLocalization.Get("settings.bodyTracking.section.spineReach.description"), false, reachParent =>
        {
            var maxBendSlider = PanelSlider.CreateAndBind(
                reachParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.maxBendDeg.title"), 0f, 180f, false, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKMaxBendDeg);
            if (maxBendSlider != null)
            {
                maxBendSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.maxBendDeg.title.tooltip"));
            }

            var maxChestDeltaSlider = PanelSlider.CreateAndBind(
                reachParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.maxChestDelta.title"), 0f, 180f, false, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKMaxChestDelta);
            if (maxChestDeltaSlider != null)
            {
                maxChestDeltaSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.maxChestDelta.title.tooltip"));
            }

            var butterflyMaxOpenSlider = PanelSlider.CreateAndBind(
                reachParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.butterflyKneeMaxOpen.title"), 0f, 90f, false, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKButterflyKneeMaxOpenDeg);
            if (butterflyMaxOpenSlider != null)
            {
                butterflyMaxOpenSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.butterflyKneeMaxOpen.title.tooltip"));
            }

            var kneeFootFollowSlider = PanelSlider.CreateAndBind(
                reachParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.kneeFootFollow.title"), 0.1f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKKneeFootFollowUpright);
            if (kneeFootFollowSlider != null)
            {
                kneeFootFollowSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.kneeFootFollow.title.tooltip"));
            }
        });

        // ============== Spine: Bend Distribution ==============
        CreateCollapsibleSection(tabDesc, colliderGroup,
            BasisLocalization.Get("settings.bodyTracking.section.spineBend.title"),
            BasisLocalization.Get("settings.bodyTracking.section.spineBend.description"), false, bendParent =>
        {
            var spineBendPitch = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.spineBendPitch.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKSpineBendPitch);
            if (spineBendPitch != null)
            {
                spineBendPitch.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.spineBendPitch.title.tooltip"));
            }

            var spineBendYaw = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.spineBendYaw.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKSpineBendYaw);
            if (spineBendYaw != null)
            {
                spineBendYaw.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.spineBendYaw.title.tooltip"));
            }

            var spineBendRoll = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.spineBendRoll.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKSpineBendRoll);
            if (spineBendRoll != null)
            {
                spineBendRoll.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.spineBendRoll.title.tooltip"));
            }

            var upperChestBendPitch = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.upperChestBendPitch.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKUpperChestBendPitch);
            if (upperChestBendPitch != null)
            {
                upperChestBendPitch.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.upperChestBendPitch.title.tooltip"));
            }

            var upperChestBendYaw = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.upperChestBendYaw.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKUpperChestBendYaw);
            if (upperChestBendYaw != null)
            {
                upperChestBendYaw.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.upperChestBendYaw.title.tooltip"));
            }

            var upperChestBendRoll = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.upperChestBendRoll.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKUpperChestBendRoll);
            if (upperChestBendRoll != null)
            {
                upperChestBendRoll.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.upperChestBendRoll.title.tooltip"));
            }

            var chestBendPitch = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.chestBendPitch.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKChestBendPitch);
            chestBendPitch?.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.chestBendPitch.title.tooltip"));

            var chestBendYaw = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.chestBendYaw.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKChestBendYaw);
            chestBendYaw?.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.chestBendYaw.title.tooltip"));

            var chestBendRoll = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.chestBendRoll.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKChestBendRoll);
            chestBendRoll?.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.chestBendRoll.title.tooltip"));

            var spineSquishBoost = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.spineSquishBoost.title"), 0f, 2f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKSpineSquishBoost);
            if (spineSquishBoost != null)
            {
                spineSquishBoost.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.spineSquishBoost.title.tooltip"));
            }

            var spineGazeFollow = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.spineGazeFollow.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKSpineGazeFollow);
            if (spineGazeFollow != null)
            {
                spineGazeFollow.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.spineGazeFollow.title.tooltip"));
            }

            var neckGazeFollow = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.neckGazeFollow.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKNeckGazeFollow);
            if (neckGazeFollow != null)
            {
                neckGazeFollow.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.neckGazeFollow.title.tooltip"));
            }

            var vspineGazeSwingRemoval = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.vspineGazeSwingRemoval.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.VSpineGazeSwingRemoval);
            if (vspineGazeSwingRemoval != null)
            {
                vspineGazeSwingRemoval.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.vspineGazeSwingRemoval.title.tooltip"));
            }

            var neckExtensionDamp = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.neckExtensionDamp.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKNeckExtensionDamp);
            if (neckExtensionDamp != null)
            {
                neckExtensionDamp.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.neckExtensionDamp.title.tooltip"));
            }

            var neckFlexionDamp = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.neckFlexionDamp.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKNeckFlexionDamp);
            if (neckFlexionDamp != null)
            {
                neckFlexionDamp.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.neckFlexionDamp.title.tooltip"));
            }

            var spineMaxFwd = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.spineMaxForward.title"), 0f, 90f, false, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKSpineMaxForwardDeg);
            if (spineMaxFwd != null)
            {
                spineMaxFwd.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.spineMaxForward.title.tooltip"));
            }

            var spineMaxBack = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.spineMaxBackward.title"), 0f, 90f, false, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKSpineMaxBackwardDeg);
            if (spineMaxBack != null)
            {
                spineMaxBack.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.spineMaxBackward.title.tooltip"));
            }

            var spineMaxLat = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.spineMaxLateral.title"), 0f, 90f, false, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKSpineMaxLateralDeg);
            if (spineMaxLat != null)
            {
                spineMaxLat.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.spineMaxLateral.title.tooltip"));
            }

            var neckMaxCone = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.neckMaxCone.title"), 0f, 90f, false, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKNeckMaxConeDeg);
            if (neckMaxCone != null)
            {
                neckMaxCone.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.neckMaxCone.title.tooltip"));
            }

            var neckGazeFollowMax = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.neckGazeFollowMax.title"), 0f, 60f, false, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKNeckGazeFollowMaxDeg);
            neckGazeFollowMax?.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.neckGazeFollowMax.title.tooltip"));

            // var thoracicStiffen = PanelSlider.CreateAndBind(
            //     bendParent,
            //     PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.thoracicBendStiffen.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
            //     BasisSettingsDefaults.FBIKThoracicBendStiffen);
            // thoracicStiffen?.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.thoracicBendStiffen.title.tooltip"));

            var neckYawShare = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.neckYawShare.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKNeckYawShare);
            neckYawShare?.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.neckYawShare.title.tooltip"));

            var spineStretchMax = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.spineStretchMax.title"), 0f, 0.1f, false, 3, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKSpineStretchMax);
            spineStretchMax?.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.spineStretchMax.title.tooltip"));

            var tautBand = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.spineTautBand.title"), 0.001f, 0.1f, false, 3, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKSpineTautBandFrac);
            tautBand?.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.spineTautBand.title.tooltip"));

            var bendTwistCoupling = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.bendTwistCoupling.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKBendTwistCoupling);
            bendTwistCoupling?.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.bendTwistCoupling.title.tooltip"));
        });

        // ============== Spine: Dynamics ==============
        CreateCollapsibleSection(tabDesc, colliderGroup,
            BasisLocalization.Get("settings.bodyTracking.section.spineDynamics.title"),
            BasisLocalization.Get("settings.bodyTracking.section.spineDynamics.description"), false, dynamicsParent =>
        {
            var hipHingeStart = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.hipHingeStart.title"), 0f, 90f, false, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKHipHingeStartDeg);
            if (hipHingeStart != null)
            {
                hipHingeStart.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.hipHingeStart.title.tooltip"));
            }

            var hipHingeMax = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.hipHingeMaxAdd.title"), 0f, 60f, false, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKHipHingeMaxAddDeg);
            if (hipHingeMax != null)
            {
                hipHingeMax.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.hipHingeMaxAdd.title.tooltip"));
            }

            var moveBodyBack = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.moveBodyBackWhenCrouching.title"), 0f, 2f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKMoveBodyBackWhenCrouching);
            if (moveBodyBack != null)
            {
                moveBodyBack.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.moveBodyBackWhenCrouching.title.tooltip"));
            }

            var trunkCounterbalance = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.trunkCounterbalance.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKTrunkCounterbalance);
            if (trunkCounterbalance != null)
            {
                trunkCounterbalance.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.trunkCounterbalance.title.tooltip"));
            }

            var elbowSwingToggle = PanelToggle.CreateNewEntry(dynamicsParent);
            elbowSwingToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.elbowSwingSmoothing.title"));
            elbowSwingToggle.AssignBinding(BasisSettingsDefaults.FBIKElbowSwingEnabled);
            elbowSwingToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.elbowSwingSmoothing.title.tooltip"));

            var swingSmooth = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.swingSmoothRate.title"), 0f, 3600f, false, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKSwingSmoothRate);
            if (swingSmooth != null)
            {
                swingSmooth.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.swingSmoothRate.title.tooltip"));
            }

            var chestSpringHz = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.chestSpringHz.title"), 0f, 30f, false, 1, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKChestSpringHz);
            if (chestSpringHz != null)
            {
                chestSpringHz.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.chestSpringHz.title.tooltip"));
            }

            var chestSpringDamping = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.chestSpringDamping.title"), 0f, 2f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKChestSpringDamping);
            if (chestSpringDamping != null)
            {
                chestSpringDamping.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.chestSpringDamping.title.tooltip"));
            }

            var spineCcdRelax = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.spineCcdRelax.title"), 0.1f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKSpineCCDRelax);
            if (spineCcdRelax != null)
            {
                spineCcdRelax.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.spineCcdRelax.title.tooltip"));
            }

            var spineTwistKeep = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.spineTwistKeep.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKSpineTwistKeep);
            if (spineTwistKeep != null)
            {
                spineTwistKeep.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.spineTwistKeep.title.tooltip"));
            }

            var spineNeckTwistKeep = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.spineNeckTwistKeep.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKSpineNeckTwistKeep);
            if (spineNeckTwistKeep != null)
            {
                spineNeckTwistKeep.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.spineNeckTwistKeep.title.tooltip"));
            }

            var chestArmSwingFactor = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.chestArmSwingFactor.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKChestArmSwingFactor);
            if (chestArmSwingFactor != null)
            {
                chestArmSwingFactor.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.chestArmSwingFactor.title.tooltip"));
            }

            var chestArmSwingMaxDeg = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.chestArmSwingMax.title"), 0f, 30f, false, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKChestArmSwingMaxDeg);
            if (chestArmSwingMaxDeg != null)
            {
                chestArmSwingMaxDeg.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.chestArmSwingMax.title.tooltip"));
            }

            var lordosisPitchGain = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.lordosisPitchGain.title"), 0f, 30f, false, 1, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKLordosisPitchGainDeg);
            if (lordosisPitchGain != null)
            {
                lordosisPitchGain.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.lordosisPitchGain.title.tooltip"));
            }

            var lordosisBase = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.lordosisBase.title"), 0f, 15f, false, 1, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKLordosisBaseDeg);
            if (lordosisBase != null)
            {
                lordosisBase.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.lordosisBase.title.tooltip"));
            }

            var lordosisNeckShare = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.lordosisNeckShare.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKLordosisNeckShare);
            if (lordosisNeckShare != null)
            {
                lordosisNeckShare.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.lordosisNeckShare.title.tooltip"));
            }

            var lordosisMaxHeadPitch = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.lordosisMaxHeadPitch.title"), 0f, 90f, false, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKLordosisMaxHeadPitchDeg);
            if (lordosisMaxHeadPitch != null)
            {
                lordosisMaxHeadPitch.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.lordosisMaxHeadPitch.title.tooltip"));
            }

            var lordosisExtremeStart = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.lordosisExtremeStart.title"), 0f, 90f, false, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKLordosisExtremeStartDeg);
            if (lordosisExtremeStart != null)
            {
                lordosisExtremeStart.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.lordosisExtremeStart.title.tooltip"));
            }

            var lordosisExtremeFull = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.lordosisExtremeFull.title"), 0f, 90f, false, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKLordosisExtremeFullDeg);
            if (lordosisExtremeFull != null)
            {
                lordosisExtremeFull.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.lordosisExtremeFull.title.tooltip"));
            }

            var lordosisExtremeRollFwd = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.lordosisExtremeRollForward.title"), 0f, 30f, false, 1, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKLordosisExtremeRollForwardMaxDeg);
            if (lordosisExtremeRollFwd != null)
            {
                lordosisExtremeRollFwd.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.lordosisExtremeRollForward.title.tooltip"));
            }

            var lordosisExtremeRollBack = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.lordosisExtremeRollBackward.title"), 0f, 30f, false, 1, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKLordosisExtremeRollBackwardMaxDeg);
            if (lordosisExtremeRollBack != null)
            {
                lordosisExtremeRollBack.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.lordosisExtremeRollBackward.title.tooltip"));
            }

            var lordosisExtremeHipsHoriz = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.lordosisExtremeHipsHoriz.title"), 0f, 0.1f, false, 3, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKLordosisExtremeHipsHorizontalMax);
            if (lordosisExtremeHipsHoriz != null)
            {
                lordosisExtremeHipsHoriz.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.lordosisExtremeHipsHoriz.title.tooltip"));
            }

            var lordosisExtremeChestHoriz = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.lordosisExtremeChestHoriz.title"), 0f, 0.1f, false, 3, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKLordosisExtremeChestHorizontalMax);
            if (lordosisExtremeChestHoriz != null)
            {
                lordosisExtremeChestHoriz.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.lordosisExtremeChestHoriz.title.tooltip"));
            }

            var lordosisExtremeHipsHorizLookUp = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.lordosisExtremeHipsHorizLookUp.title"), 0f, 0.1f, false, 3, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKLordosisExtremeHipsHorizontalLookUp);
            if (lordosisExtremeHipsHorizLookUp != null)
            {
                lordosisExtremeHipsHorizLookUp.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.lordosisExtremeHipsHorizLookUp.title.tooltip"));
            }

            var lordosisExtremeChestHorizLookUp = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.lordosisExtremeChestHorizLookUp.title"), 0f, 0.1f, false, 3, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKLordosisExtremeChestHorizontalLookUp);
            if (lordosisExtremeChestHorizLookUp != null)
            {
                lordosisExtremeChestHorizLookUp.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.lordosisExtremeChestHorizLookUp.title.tooltip"));
            }

            var lordosisExtremeHipsDown = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.lordosisExtremeHipsDown.title"), 0f, 0.1f, false, 3, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKLordosisExtremeHipsDownMax);
            if (lordosisExtremeHipsDown != null)
            {
                lordosisExtremeHipsDown.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.lordosisExtremeHipsDown.title.tooltip"));
            }

            var lordosisExtremeChestDown = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.lordosisExtremeChestDown.title"), 0f, 0.1f, false, 3, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKLordosisExtremeChestDownMax);
            if (lordosisExtremeChestDown != null)
            {
                lordosisExtremeChestDown.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.lordosisExtremeChestDown.title.tooltip"));
            }

            var lordosisExtremeHipsDownLookUp = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.lordosisExtremeHipsDownLookUp.title"), 0f, 0.01f, false, 4, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKLordosisExtremeHipsDownLookUp);
            if (lordosisExtremeHipsDownLookUp != null)
            {
                lordosisExtremeHipsDownLookUp.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.lordosisExtremeHipsDownLookUp.title.tooltip"));
            }

            var lordosisExtremeChestDownLookUp = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.lordosisExtremeChestDownLookUp.title"), 0f, 0.01f, false, 4, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKLordosisExtremeChestDownLookUp);
            if (lordosisExtremeChestDownLookUp != null)
            {
                lordosisExtremeChestDownLookUp.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.lordosisExtremeChestDownLookUp.title.tooltip"));
            }

            var trunkCounterbalanceMax = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.trunkCounterbalanceMax.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKTrunkCounterbalanceMaxFrac);
            trunkCounterbalanceMax?.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.trunkCounterbalanceMax.title.tooltip"));

            var chestFollowShare = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.chestFollowChestShare.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKChestFollowChestShare);
            chestFollowShare?.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.chestFollowChestShare.title.tooltip"));

            var chestIkWeight = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.chestIkWeight.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKChestIkWeight);
            chestIkWeight?.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.chestIkWeight.title.tooltip"));

            var chestIkIterations = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.chestIkIterations.title"), 1f, 24f, false, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKChestIkIterations);
            chestIkIterations?.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.chestIkIterations.title.tooltip"));

            var chestIkSweeps = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.chestIkSweeps.title"), 1f, 6f, false, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKChestIkHeadRestoreSweeps);
            chestIkSweeps?.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.chestIkSweeps.title.tooltip"));

            var chestPosPullMax = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.chestPosPullMax.title"), 0f, 60f, false, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKChestPosPullMaxDeg);
            chestPosPullMax?.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.chestPosPullMax.title.tooltip"));

            var chestPullMaxDist = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.chestPullMaxDist.title"), 0.05f, 1.5f, false, 2, ValueDisplayMode.Meters),
                BasisSettingsDefaults.FBIKChestPullMaxDist);
            chestPullMaxDist?.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.chestPullMaxDist.title.tooltip"));

            var trackedKneeMinCutoff = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.trackedKneeMinCutoff.title"), 0.1f, 10f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKTrackedKneeSwivelMinCutoffHz);
            trackedKneeMinCutoff?.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.trackedKneeMinCutoff.title.tooltip"));

            var trackedKneeBeta = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.trackedKneeBeta.title"), 0f, 2f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKTrackedKneeSwivelBeta);
            trackedKneeBeta?.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.trackedKneeBeta.title.tooltip"));

            var trackedKneeDerivCutoff = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.trackedKneeDerivCutoff.title"), 0.1f, 10f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKTrackedKneeSwivelDerivCutoffHz);
            trackedKneeDerivCutoff?.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.trackedKneeDerivCutoff.title.tooltip"));
        });

        // ============== Virtual Spine (no torso tracker) ==============
        CreateCollapsibleSection(tabDesc, colliderGroup,
            BasisLocalization.Get("settings.bodyTracking.section.virtualSpine.title"),
            BasisLocalization.Get("settings.bodyTracking.section.virtualSpine.description"), false, vspineParent =>
        {
            var vspineChestPitch = PanelSlider.CreateAndBind(
                vspineParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.vspineChestPitchFrac.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.VSpineChestPitchFrac);
            if (vspineChestPitch != null)
            {
                vspineChestPitch.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.vspineChestPitchFrac.title.tooltip"));
            }

            var vspineChestRoll = PanelSlider.CreateAndBind(
                vspineParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.vspineChestRollFrac.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.VSpineChestRollFrac);
            if (vspineChestRoll != null)
            {
                vspineChestRoll.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.vspineChestRollFrac.title.tooltip"));
            }

            var vspineSpinePitch = PanelSlider.CreateAndBind(
                vspineParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.vspineSpinePitchFrac.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.VSpineSpinePitchFrac);
            if (vspineSpinePitch != null)
            {
                vspineSpinePitch.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.vspineSpinePitchFrac.title.tooltip"));
            }

            var vspineSpineRoll = PanelSlider.CreateAndBind(
                vspineParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.vspineSpineRollFrac.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.VSpineSpineRollFrac);
            if (vspineSpineRoll != null)
            {
                vspineSpineRoll.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.vspineSpineRollFrac.title.tooltip"));
            }

            var vspineNeckRotSpeed = PanelSlider.CreateAndBind(
                vspineParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.vspineNeckRotationSpeed.title"), 0f, 100f, false, 1, ValueDisplayMode.Raw),
                BasisSettingsDefaults.VSpineNeckRotationSpeed);
            if (vspineNeckRotSpeed != null)
            {
                vspineNeckRotSpeed.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.vspineNeckRotationSpeed.title.tooltip"));
            }

            var vspineChestRotSpeed = PanelSlider.CreateAndBind(
                vspineParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.vspineChestRotationSpeed.title"), 0f, 100f, false, 1, ValueDisplayMode.Raw),
                BasisSettingsDefaults.VSpineChestRotationSpeed);
            if (vspineChestRotSpeed != null)
            {
                vspineChestRotSpeed.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.vspineChestRotationSpeed.title.tooltip"));
            }

            var vspineSpineRotSpeed = PanelSlider.CreateAndBind(
                vspineParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.vspineSpineRotationSpeed.title"), 0f, 100f, false, 1, ValueDisplayMode.Raw),
                BasisSettingsDefaults.VSpineSpineRotationSpeed);
            if (vspineSpineRotSpeed != null)
            {
                vspineSpineRotSpeed.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.vspineSpineRotationSpeed.title.tooltip"));
            }

            var vspineHipsRotSpeed = PanelSlider.CreateAndBind(
                vspineParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.vspineHipsRotationSpeed.title"), 0f, 100f, false, 1, ValueDisplayMode.Raw),
                BasisSettingsDefaults.VSpineHipsRotationSpeed);
            if (vspineHipsRotSpeed != null)
            {
                vspineHipsRotSpeed.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.vspineHipsRotationSpeed.title.tooltip"));
            }

            var vspineHipsFwd = PanelSlider.CreateAndBind(
                vspineParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.vspineHipsForwardBias.title"), -0.1f, 0.1f, false, 3, ValueDisplayMode.Raw),
                BasisSettingsDefaults.VSpineHipsForwardBias);
            if (vspineHipsFwd != null)
            {
                vspineHipsFwd.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.vspineHipsForwardBias.title.tooltip"));
            }

            var vspineTorsoYawDeadzone = PanelSlider.CreateAndBind(
                vspineParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.vspineTorsoYawDeadzone.title"), 0f, 90f, false, 1, ValueDisplayMode.Raw),
                BasisSettingsDefaults.VSpineTorsoYawDeadzoneDeg);
            if (vspineTorsoYawDeadzone != null)
            {
                vspineTorsoYawDeadzone.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.vspineTorsoYawDeadzone.title.tooltip"));
            }

            var vspineTorsoYawDeadzoneVR = PanelSlider.CreateAndBind(
                vspineParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.vspineTorsoYawDeadzoneVR.title"), 0f, 90f, false, 1, ValueDisplayMode.Raw),
                BasisSettingsDefaults.VSpineTorsoYawDeadzoneVRDeg);
            vspineTorsoYawDeadzoneVR?.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.vspineTorsoYawDeadzoneVR.title.tooltip"));

            var vspineTorsoYawBlend = PanelSlider.CreateAndBind(
                vspineParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.vspineTorsoYawBlend.title"), 1f, 60f, false, 1, ValueDisplayMode.Raw),
                BasisSettingsDefaults.VSpineTorsoYawBlendSpeed);
            if (vspineTorsoYawBlend != null)
            {
                vspineTorsoYawBlend.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.vspineTorsoYawBlend.title.tooltip"));
            }

            var vspineTorsoYawPlayInVR = PanelToggle.CreateNewEntry(vspineParent);
            vspineTorsoYawPlayInVR.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.vspineTorsoYawPlayInVR.title"));
            vspineTorsoYawPlayInVR.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.vspineTorsoYawPlayInVR.title.tooltip"));
            vspineTorsoYawPlayInVR.AssignBinding(BasisSettingsDefaults.VSpineTorsoYawPlayInVR);

            var vspineNodPivotEstimate = PanelToggle.CreateNewEntry(vspineParent);
            vspineNodPivotEstimate.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.vspineNodPivotEstimate.title"));
            vspineNodPivotEstimate.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.vspineNodPivotEstimate.title.tooltip"));
            vspineNodPivotEstimate.AssignBinding(BasisSettingsDefaults.VSpineNodPivotEstimate);

            var vspinePostureModel = PanelToggle.CreateNewEntry(vspineParent);
            vspinePostureModel.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.vspinePostureModel.title"));
            vspinePostureModel.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.vspinePostureModel.title.tooltip"));
            vspinePostureModel.AssignBinding(BasisSettingsDefaults.VSpinePostureModel);

            var vspineHipsCompression = PanelSlider.CreateAndBind(
                vspineParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.vspineHipsCompression.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.VSpineHipsCompressionStrength);
            vspineHipsCompression?.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.vspineHipsCompression.title.tooltip"));

            var vspineHipsMaxDrop = PanelSlider.CreateAndBind(
                vspineParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.vspineHipsMaxDrop.title"), 0.05f, 1f, false, 2, ValueDisplayMode.Meters),
                BasisSettingsDefaults.VSpineHipsMaxDropMeters);
            vspineHipsMaxDrop?.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.vspineHipsMaxDrop.title.tooltip"));

            void SyncVSpineCompressionVisibility()
            {
                bool legacyLaw = !BasisSettingsDefaults.VSpinePostureModel.RawValue;
                vspineHipsCompression?.Descriptor.SetActive(legacyLaw);
                vspineHipsMaxDrop?.Descriptor.SetActive(legacyLaw);
            }

            SyncVSpineCompressionVisibility();
            vspinePostureModel.OnValueChanged += _ =>
            {
                SyncVSpineCompressionVisibility();
                RebuildLayoutChain(vspineParent, tabDesc);
            };
        });

        // ============== Smoothing (One Euro) ==============
        CreateCollapsibleSection(tabDesc, colliderGroup,
            BasisLocalization.Get("settings.bodyTracking.section.smoothing.title"),
            BasisLocalization.Get("settings.bodyTracking.section.smoothing.description"), false, smoothingParent =>
        {
            var smoothingStrength = PanelSlider.CreateAndBind(
                smoothingParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.smoothingStrength.title"), 1f, 100f, false, 1, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKSmoothingStrength);
            if (smoothingStrength != null)
            {
                smoothingStrength.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.smoothingStrength.title.tooltip"));
            }

            for (int Index = 0; Index < BasisSettingsDefaults.FBIKSmoothingGroups.Length; Index++)
            {
                AddSmoothingGroup(tabDesc, smoothingParent, BasisSettingsDefaults.FBIKSmoothingGroups[Index]);
            }

            var posHz = PanelSlider.CreateAndBind(
                smoothingParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.posSmoothingHz.title"), 0.01f, 60f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKPositionSmoothingHz);
            if (posHz != null)
            {
                posHz.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.posSmoothingHz.title.tooltip"));
            }

            var rotHz = PanelSlider.CreateAndBind(
                smoothingParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.rotSmoothingHz.title"), 0.01f, 60f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKRotationSmoothingHz);
            if (rotHz != null)
            {
                rotHz.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.rotSmoothingHz.title.tooltip"));
            }

            var minCutoff = PanelSlider.CreateAndBind(
                smoothingParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.minCutoff.title"), 0.1f, 10f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKMinCutoff);
            if (minCutoff != null)
            {
                minCutoff.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.minCutoff.title.tooltip"));
            }

            var beta = PanelSlider.CreateAndBind(
                smoothingParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.beta.title"), 0f, 10f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKBeta);
            if (beta != null)
            {
                beta.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.beta.title.tooltip"));
            }

            var derivativeCutoff = PanelSlider.CreateAndBind(
                smoothingParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.derivativeCutoff.title"), 0.1f, 10f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKDerivativeCutoff);
            if (derivativeCutoff != null)
            {
                derivativeCutoff.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.derivativeCutoff.title.tooltip"));
            }
        });

        // ============== IK Solve Gizmos ==============
        CreateCollapsibleSection(tabDesc, colliderGroup,
            BasisLocalization.Get("settings.bodyTracking.section.solveGizmos.title"),
            BasisLocalization.Get("settings.bodyTracking.section.solveGizmos.description"), false, gizmoParent =>
        {
            var gated = new List<PanelElementDescriptor>();

            var solveGizmoToggle = PanelToggle.CreateNewEntry(gizmoParent);
            solveGizmoToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.ikGizmos.enabled"));
            solveGizmoToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.ikGizmos.enabled.tooltip"));
            solveGizmoToggle.AssignBinding(Basis.Scripts.Debugging.BasisIKSolveGizmoStages.Enabled);

            // One row per registered solve stage. BasisIKSolveGizmoStages.All is the only place a
            // stage is declared, so a new pass added to the solver appears here, persists, and
            // reaches the Burst recorder's mask without another edit in this file.
            var stages = Basis.Scripts.Debugging.BasisIKSolveGizmoStages.All;
            for (int i = 0; i < stages.Length; i++)
            {
                var stageToggle = PanelToggle.CreateNewEntry(gizmoParent);
                stageToggle.Descriptor.SetTitle(BasisLocalization.Get(stages[i].TitleKey));
                stageToggle.Descriptor.SetTooltip(BasisLocalization.Get(stages[i].TooltipKey));
                stageToggle.AssignBinding(stages[i].Binding);
                gated.Add(stageToggle.Descriptor);
            }

            // Diagnostics: why nothing is on screen. Gated with the stages so they hide with them.
            var solveGizmoDiagnose = PanelToggle.CreateNewEntry(gizmoParent);
            solveGizmoDiagnose.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.ikGizmos.diagnose"));
            solveGizmoDiagnose.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.ikGizmos.diagnose.tooltip"));
            solveGizmoDiagnose.AssignBinding(Basis.Scripts.Debugging.BasisIKSolveGizmoStages.Diagnose);
            gated.Add(solveGizmoDiagnose.Descriptor);

            var solveGizmoSelfTest = PanelToggle.CreateNewEntry(gizmoParent);
            solveGizmoSelfTest.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.ikGizmos.selfTest"));
            solveGizmoSelfTest.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.ikGizmos.selfTest.tooltip"));
            solveGizmoSelfTest.AssignBinding(Basis.Scripts.Debugging.BasisIKSolveGizmoStages.DiagnoseSelfTest);
            gated.Add(solveGizmoSelfTest.Descriptor);

            var solveGizmoLabels = PanelToggle.CreateNewEntry(gizmoParent);
            solveGizmoLabels.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.ikGizmos.labels"));
            solveGizmoLabels.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.ikGizmos.labels.tooltip"));
            solveGizmoLabels.AssignBinding(Basis.Scripts.Debugging.BasisIKSolveGizmoStages.Labels);
            gated.Add(solveGizmoLabels.Descriptor);

            var solveGizmoScale = PanelSlider.CreateAndBind(
                gizmoParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.ikGizmos.scale"),
                    Basis.Scripts.Debugging.BasisIKSolveGizmoStages.ScaleMin,
                    Basis.Scripts.Debugging.BasisIKSolveGizmoStages.ScaleMax, false, 2, ValueDisplayMode.Raw),
                Basis.Scripts.Debugging.BasisIKSolveGizmoStages.Scale);
            if (solveGizmoScale != null)
            {
                solveGizmoScale.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.ikGizmos.scale.tooltip"));
                gated.Add(solveGizmoScale.Descriptor);
            }

            void ApplySolveGizmoVisibility(bool visible)
            {
                for (int i = 0; i < gated.Count; i++)
                {
                    gated[i].SetActive(visible);
                }
            }

            ApplySolveGizmoVisibility(solveGizmoToggle.Value);
            solveGizmoToggle.OnValueChanged += value =>
            {
                ApplySolveGizmoVisibility(value);
                RebuildLayoutChain(gizmoParent, tabDesc);
            };
        });

        colliderGroup.gameObject.SetActive(BasisSettingsDefaults.FBIKAdvancedVisible.RawValue);
        advancedToggle.OnExpandedChanged += visible =>
        {
            colliderGroup.gameObject.SetActive(visible);
            RebuildLayout(colliderGroup.GetComponentInParent<PanelElementDescriptor>());
            RebuildLayout(tabDesc);
        };

        // ------------------
        // Debug Section
        // ------------------
        BuildDebugSection(tabDesc);

        // The tab's own localization key, which is what the header Reset looks this page up by —
        // a display name would never match and the page would silently lose its reset.
        SettingsProvider.RegisterPageReset("settings.tab.bodytracking", ResetIkDefaults);

        RebuildLayout(tabDesc);
        return tabPage;
    }

    public static void SetAvatarScaleSliderValueWithoutNotify(float value)
    {
        if (_avatarScaleSlider == null)
        {
            return;
        }

        _avatarScaleSlider.SetValueWithoutNotify(value);
    }

    // ------------------
    // Debug Info
    // ------------------
    // One card per category. Each card's description holds all of its metrics as
    // "Label: value" lines, so the panel collapses from ~27 group cards to 6.
    private static readonly List<(string title, string[] labels, PanelElementDescriptor descriptor)> _debugCategories = new();
    private static PanelElementDescriptor _sizeSummaryCard;

    private static void BuildDebugSection(PanelElementDescriptor tabDesc)
    {
        var debugToggle = PanelSectionToggle.CreateNewEntry(tabDesc.ContentParent);
        debugToggle.SetTitle(BasisLocalization.Get("settings.bodyTracking.debugInfo"));
        debugToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.debugInfo.tooltip"));

        var debugGroup = PanelElementDescriptor.CreateNew(
            PanelElementDescriptor.ElementStyles.Group,
            tabDesc.ContentParent);

        debugGroup.SetTitle(BasisLocalization.Get("settings.bodyTracking.heightDebug.title"));
        debugGroup.SetIcon(AddressableAssets.Sprites.Settings);
        debugGroup.SetBackgroundVisible(false);
        debugToggle.RegisterContentContainer(debugGroup);

        var debugParent = debugGroup.ContentParent;

        _debugCategories.Clear();

        // Plain-language answer to "how well does this avatar match me" — the one readout here that a
        // player rather than a developer is meant to read, so it goes first.
        _sizeSummaryCard = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, debugParent);
        _sizeSummaryCard.SetTitle(BasisLocalization.Get("calibration.report.size.title"));
        _sizeSummaryCard.SetDescription("");

        AddDebugCategory(debugParent, BasisLocalization.Get("settings.bodyTracking.debug.playerMetrics"),
            "Player Eye Height", "Player Arm Span", "Player Hip Height", "Player Eye To Hip Drop",
            "Player Ape Index", "Player Leg Fraction");

        AddDebugCategory(debugParent, BasisLocalization.Get("settings.bodyTracking.debug.avatarMetrics"),
            "Avatar Eye Height", "Avatar Arm Span", "Avatar Arm Span (fitted)", "Avatar Hip Height",
            "Avatar Leg Span", "Avatar Spine Span", "Avatar Shoulder Width", "Avatar Arm Length",
            "Avatar Ape Index", "Avatar Leg Fraction");

        AddDebugCategory(debugParent, BasisLocalization.Get("settings.bodyTracking.debug.scaledHeights"),
            "Scaled Player Height", "Scaled Avatar Height");

        AddDebugCategory(debugParent, BasisLocalization.Get("settings.bodyTracking.debug.unscaledHeights"),
            "Unscaled Player Height", "Unscaled Avatar Height");

        AddDebugCategory(debugParent, BasisLocalization.Get("settings.bodyTracking.debug.ratiosScaling"),
            "Player to Avatar Ratio", "Avatar to Player Ratio", "Device Scale", "Applied Up Scale", "Scaled to Match Value");

        AddDebugCategory(debugParent, BasisLocalization.Get("settings.bodyTracking.debug.calibrationState"),
            "Height Mode", "Seated Mode", "Seated Height Delta", "Height Grounding Offset");

        AddDebugCategory(debugParent, BasisLocalization.Get("settings.bodyTracking.debug.bodyFit"),
            "Body Fit", "Body Fit Max Deviation", "Arm Fit", "Body Fit Arm Scale", "Body Fit Arm Length",
            "Body Fit Player Arm Length", "Body Fit Arm Ratio (raw)",
            "Hand Device To Wrist L", "Hand Device To Wrist R",
            "Leg & Spine Fit", "Body Fit Leg Scale", "Body Fit Torso Scale", "Body Fit Hip Height",
            "Body Fit Head Height Shift");

        var refreshButton = PanelButton.CreateNew(debugParent);
        refreshButton.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.refreshDebug"));
        refreshButton.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.refreshDebug.tooltip"));
        refreshButton.OnClicked += RefreshDebugData;

        RefreshDebugData();

        debugGroup.gameObject.SetActive(false);
        debugToggle.SetExpandedWithoutNotify(false);
        debugToggle.OnExpandedChanged += visible =>
        {
            debugGroup.gameObject.SetActive(visible);
            if (visible)
            {
                RefreshDebugData();
            }
            RebuildLayout(tabDesc);
        };
    }

    private static void AddDebugCategory(RectTransform parent, string title, params string[] labels)
    {
        var card = PanelElementDescriptor.CreateNew(
            PanelElementDescriptor.ElementStyles.Group,
            parent);
        card.SetTitle(title);
        card.SetDescription("");
        _debugCategories.Add((title, labels, card));
    }

    private static void RefreshDebugData()
    {
        if (_sizeSummaryCard != null)
        {
            try
            {
                _sizeSummaryCard.SetDescription(BasisBodyFitSummary.Build());
            }
            catch (Exception e)
            {
                // A readout must never be able to break the panel it lives in.
                BasisDebug.LogError($"Body fit summary failed: {e}", BasisDebug.LogTag.Avatar);
                _sizeSummaryCard.SetDescription("--");
            }
        }

        var sb = new System.Text.StringBuilder();
        foreach (var (title, labels, descriptor) in _debugCategories)
        {
            sb.Clear();
            for (int i = 0; i < labels.Length; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(labels[i]).Append(": ").Append(GetDebugMetric(labels[i]));
            }
            descriptor.SetDescription(sb.ToString());
        }
    }

    private static string GetDebugMetric(string label) => label switch
    {
        "Player Eye Height" => $"{BasisHeightDriver.PlayerEyeHeight:F4} m",
        "Player Arm Span" => $"{BasisHeightDriver.PlayerArmSpan:F4} m",
        "Avatar Eye Height" => $"{BasisHeightDriver.AvatarEyeHeight:F4} m",
        "Avatar Arm Span" => $"{BasisHeightDriver.AvatarArmSpan:F4} m",
        "Avatar Arm Span (fitted)" => Mathf.Approximately(BasisHeightDriver.EffectiveAvatarArmSpan, BasisHeightDriver.AvatarArmSpan)
            ? $"{BasisHeightDriver.AvatarArmSpan:F4} m (unchanged)"
            : $"{BasisHeightDriver.EffectiveAvatarArmSpan:F4} m (authored {BasisHeightDriver.AvatarArmSpan:F4})",
        "Scaled Player Height" => $"{BasisHeightDriver.SelectedScaledPlayerHeight:F4} m",
        "Scaled Avatar Height" => $"{BasisHeightDriver.SelectedScaledAvatarHeight:F4} m",
        "Unscaled Player Height" => $"{BasisHeightDriver.SelectedUnScaledPlayerHeight:F4} m",
        "Unscaled Avatar Height" => $"{BasisHeightDriver.SelectedUnScaledAvatarHeight:F4} m",
        "Player to Avatar Ratio" => $"{BasisHeightDriver.PlayerToAvatarRatioScaled:F4}",
        "Avatar to Player Ratio" => $"{BasisHeightDriver.AvatarToPlayerRatioScaled:F4}",
        "Device Scale" => $"{BasisHeightDriver.DeviceScale:F4}",
        "Applied Up Scale" => $"{BasisHeightDriver.AppliedUpScale:F4}",
        "Scaled to Match Value" => $"{BasisHeightDriver.ScaledToMatchValue:F4}",
        "Height Mode" => BasisHeightDriver.TryGetArmToHeightBlend(out float armToHeightBlend)
            ? $"ArmToHeightBlend {armToHeightBlend:P0}"
            : $"{SMModuleCalibration.HeightMode}",
        "Seated Mode" => SMModuleSitStand.IsSteatedMode ? "Seated" : "Standing",
        "Seated Height Delta" => $"{SMModuleSitStand.MissingHeightDelta:F4} m",
        "Height Grounding Offset" => $"{BasisHeightDriver.HeightModeGroundingOffset:F4} m",

        "Player Hip Height" => BasisHeightDriver.PlayerHipHeight > 0f
            ? $"{BasisHeightDriver.PlayerHipHeight:F4} m"
            : "-- (no hips tracker)",
        "Player Eye To Hip Drop" => BasisHeightDriver.PlayerEyeToHipDrop > 0f
            ? $"{BasisHeightDriver.PlayerEyeToHipDrop:F4} m"
            : "-- (no hips tracker)",
        "Player Ape Index" => Ratio(BasisHeightDriver.PlayerArmSpan, BasisHeightDriver.PlayerEyeHeight),
        "Player Leg Fraction" => Ratio(BasisHeightDriver.PlayerHipHeight, BasisHeightDriver.PlayerEyeHeight),

        "Avatar Hip Height" => $"{BasisHeightDriver.AvatarHipHeight:F4} m",
        "Avatar Leg Span" => $"{BasisHeightDriver.AvatarLegSpan:F4} m",
        "Avatar Spine Span" => $"{BasisHeightDriver.AvatarSpineSpan:F4} m",
        "Avatar Shoulder Width" => $"{BasisHeightDriver.AvatarShoulderWidth:F4} m",
        "Avatar Arm Length" => $"{AvatarArmLength():F4} m",
        "Avatar Ape Index" => Ratio(BasisHeightDriver.AvatarArmSpan, BasisHeightDriver.AvatarEyeHeight),
        "Avatar Leg Fraction" => Ratio(BasisHeightDriver.AvatarHipHeight, BasisHeightDriver.AvatarEyeHeight),

        "Body Fit" => BodyFitStatus(),
        "Body Fit Max Deviation" => $"{BasisSettingsDefaults.FBIKBodyFitMaxDeviation.RawValue:P1}",
        "Arm Fit" => FitReason(BasisLocalRigDriver.AppliedBodyFit.ArmStatus),
        "Leg & Spine Fit" => FitReason(BasisLocalRigDriver.AppliedBodyFit.BodyStatus),
        "Body Fit Arm Scale" => ScaleMetric(BasisLocalRigDriver.AppliedBodyFit.ArmScale, BasisLocalRigDriver.AppliedBodyFit.HasArmFit),
        "Body Fit Leg Scale" => ScaleMetric(BasisLocalRigDriver.AppliedBodyFit.LegScale, BasisLocalRigDriver.AppliedBodyFit.HasBodyFit),
        "Body Fit Torso Scale" => ScaleMetric(BasisLocalRigDriver.AppliedBodyFit.TorsoScale, BasisLocalRigDriver.AppliedBodyFit.HasBodyFit),
        "Body Fit Arm Length" => BasisLocalRigDriver.AppliedBodyFit.HasArmFit
            ? $"{AvatarArmLength():F4} -> {AvatarArmLength() * BasisLocalRigDriver.AppliedBodyFit.ArmScale:F4} m"
            : "unchanged",
        "Body Fit Player Arm Length" => $"{PlayerArmLength():F4} m",
        "Body Fit Arm Ratio (raw)" => RawArmRatio(),
        "Hand Device To Wrist L" => HandDeviceToWristGap(BasisBoneTrackedRole.LeftHand),
        "Hand Device To Wrist R" => HandDeviceToWristGap(BasisBoneTrackedRole.RightHand),
        "Body Fit Hip Height" => BasisLocalRigDriver.AppliedBodyFit.HasBodyFit
            ? $"{BasisHeightDriver.AvatarHipHeight:F4} -> {FittedHipHeight():F4} m (target {BasisHeightDriver.PlayerHipHeight * SafeSpaceRatio():F4})"
            : "unchanged",
        "Body Fit Head Height Shift" => BasisLocalRigDriver.AppliedBodyFit.HasBodyFit
            ? $"{HeadHeightShift() * 1000f:F3} mm"
            : "0.000 mm",

        _ => "--"
    };

    private static string Ratio(float numerator, float denominator) =>
        denominator > 0f && numerator > 0f ? $"{numerator / denominator:F4}" : "--";

    private static float AvatarArmLength() =>
        Mathf.Max(0f, (BasisHeightDriver.AvatarArmSpan - BasisHeightDriver.AvatarShoulderWidth) * 0.5f);

    private static float SafeSpaceRatio() =>
        BasisHeightDriver.PlayerEyeHeight > 0f ? BasisHeightDriver.AvatarEyeHeight / BasisHeightDriver.PlayerEyeHeight : 1f;

    private static float PlayerArmLength() =>
        Mathf.Max(0f, (BasisHeightDriver.PlayerArmSpan * SafeSpaceRatio() - BasisHeightDriver.AvatarShoulderWidth) * 0.5f);

    private static string RawArmRatio()
    {
        float avatarArm = AvatarArmLength();
        if (avatarArm <= 0f)
        {
            return "--";
        }

        float raw = PlayerArmLength() / avatarArm;
        float deviation = BasisSettingsDefaults.FBIKBodyFitMaxDeviation.RawValue;
        bool clamped = raw > 1f + deviation || raw < 1f - deviation;
        return $"{raw:F4} ({(raw - 1f):+0.0%;-0.0%;0.0%}){(clamped ? " - clamped" : string.Empty)}";
    }

    private static string HandDeviceToWristGap(BasisBoneTrackedRole role)
    {
        BasisDeviceManagement manager = BasisDeviceManagement.Instance;
        if (manager == null || !manager.FindDevice(out BasisInput input, role) || input == null)
        {
            return "-- (no device)";
        }

        var mapping = BasisLocalPlayer.Instance?.LocalRigDriver?.basisTransformMapping;
        Transform wrist = role == BasisBoneTrackedRole.LeftHand ? mapping?.leftHand : mapping?.rightHand;
        if (wrist == null)
        {
            return "-- (no wrist bone)";
        }

        return $"{Vector3.Distance(input.ScaledDeviceCoord.position, wrist.position):F4} m";
    }

    private static float FittedHipHeight() =>
        BasisHeightDriver.AvatarHipHeight + (BasisLocalRigDriver.AppliedBodyFit.LegScale - 1f) * BasisHeightDriver.AvatarLegSpan;

    private static float HeadHeightShift()
    {
        var fit = BasisLocalRigDriver.AppliedBodyFit;
        return (fit.LegScale - 1f) * BasisHeightDriver.AvatarLegSpan
             + (fit.TorsoScale - 1f) * BasisHeightDriver.AvatarSpineSpan;
    }

    private static string ScaleMetric(float scale, bool active) =>
        active ? $"{scale:F4} ({(scale - 1f):+0.0%;-0.0%;0.0%})" : "1.0000 (off)";

    private static string FitReason(Basis.IK.BasisBodyFitStatus status) =>
        status == Basis.IK.BasisBodyFitStatus.Fitted
            ? "fitted"
            : $"not fitted - {Basis.IK.BasisBodyFitCore.Describe(status)}";

    private static string BodyFitStatus()
    {
        if (!BasisSettingsDefaults.FBIKBodyFit.RawValue)
        {
            return "off";
        }
        var fit = BasisLocalRigDriver.AppliedBodyFit;
        if (!fit.HasArmFit && !fit.HasBodyFit)
        {
            return "on, nothing fitted";
        }
        if (fit.HasArmFit && fit.HasBodyFit)
        {
            return "on, arms + legs + spine fitted";
        }
        return fit.HasArmFit ? "on, arms fitted only" : "on, legs + spine fitted only";
    }

    private static void ResetIkDefaults()
    {
        // Main IK / calibration controls
        BasisSettingsDefaults.SitStand.ResetToDefault();
        BasisSettingsDefaults.TrackerVisuals.ResetToDefault();
        BasisSettingsDefaults.IKMode.ResetToDefault();
        BasisSettingsDefaults.EnableArmToHeightBlend.ResetToDefault();
        BasisSettingsDefaults.ArmToHeightBlend.ResetToDefault();
        BasisSettingsDefaults.ContinuousBodyMeasurement.ResetToDefault();
        BasisSettingsDefaults.IKLockMode.ResetToDefault();
        BasisSettingsDefaults.CalibrationMirror.ResetToDefault();
        BasisSettingsDefaults.CustomScale.ResetToDefault();
        BasisSettingsDefaults.SelectedScale.ResetToDefault();

        // Per-avatar size nudge lives on this page but is stored under a hashed avatar key rather than
        // a binding, so it has to be cleared (and its slider re-read) by hand.
        BasisPerAvatarScale.ClearForCurrentAvatar();
        if (_perAvatarSizeSlider != null)
        {
            _perAvatarSizeSlider.SetValueWithoutNotify(BasisPerAvatarScale.Current);
        }

        // Playspace Mover
        BasisSettingsDefaults.EnablePlayspaceMover.ResetToDefault();
        BasisSettingsDefaults.PlayspaceMoverInput.ResetToDefault();
        BasisSettingsDefaults.PlayspaceMoverRotateInput.ResetToDefault();
        BasisSettingsDefaults.PlayspaceMoverHand.ResetToDefault();
        BasisSettingsDefaults.PlayspaceMoverRotate.ResetToDefault();
        BasisSettingsDefaults.PlayspaceMoverScale.ResetToDefault();
        BasisSettingsDefaults.PlayspaceMoverVertical.ResetToDefault();
        BasisSettingsDefaults.PlayspaceMoverFlip.ResetToDefault();
        BasisSettingsDefaults.PlayspaceMoverFlipAngle.ResetToDefault();
        BasisSettingsDefaults.PlayspaceMoverFlipAxis.ResetToDefault();
        BasisSettingsDefaults.PlayspaceGizmos.ResetToDefault();
        BasisSettingsDefaults.PlayspaceGizmoBoundary.ResetToDefault();
        BasisSettingsDefaults.PlayspaceGizmoOrigin.ResetToDefault();
        BasisSettingsDefaults.PlayspaceGizmoOffset.ResetToDefault();
        BasisSettingsDefaults.PlayspaceGizmoHands.ResetToDefault();
        BasisSettingsDefaults.PlayspaceGizmoReadouts.ResetToDefault();

        // Global One Euro / smoothing parameters
        BasisSettingsDefaults.FBIKSmoothingStrength.ResetToDefault();
        BasisSettingsDefaults.FBIKMinCutoff.ResetToDefault();
        BasisSettingsDefaults.FBIKBeta.ResetToDefault();
        BasisSettingsDefaults.FBIKDerivativeCutoff.ResetToDefault();
        BasisSettingsDefaults.FBIKPositionSmoothingHz.ResetToDefault();
        BasisSettingsDefaults.FBIKRotationSmoothingHz.ResetToDefault();

        for (int Index = 0; Index < BasisSettingsDefaults.FBIKSmoothingGroups.Length; Index++)
        {
            BasisSettingsDefaults.SmoothingGroupBindings group = BasisSettingsDefaults.FBIKSmoothingGroups[Index];
            group.Preset.ResetToDefault();
            group.Custom.ResetToDefault();
            group.MinCutoff.ResetToDefault();
            group.Beta.ResetToDefault();
            group.Strength.ResetToDefault();
            group.PositionHz.ResetToDefault();
            group.RotationHz.ResetToDefault();
        }

        // Bone selection UI state (optional, but usually desired)
        BasisSettingsDefaults.SelectedBone.ResetToDefault();

        // If you have master toggles / global helpers:
        // This binding is set by SyncMasterEuroFromChildren(), but reset it anyway.
        BasisSettingsDefaults.FBIKEuroAll.ResetToDefault();

        // IK Collider & Tuning
        BasisSettingsDefaults.FBIKAdvancedVisible.ResetToDefault();
        Basis.Scripts.Debugging.BasisIKSolveGizmoStages.ResetToDefaults();
        BasisSettingsDefaults.FBIKCollisionsEnabled.ResetToDefault();
        BasisSettingsDefaults.FootIKEnabled.ResetToDefault();
        BasisSettingsDefaults.DisableAnimationsInFBT.ResetToDefault();
        BasisSettingsDefaults.FBIKJobLocomotion.ResetToDefault();
        BasisSettingsDefaults.FBIKProtectElbow.ResetToDefault();
        BasisSettingsDefaults.FBIKCollideTrackedElbow.ResetToDefault();
        BasisSettingsDefaults.FBIKWristAxialBound.ResetToDefault();
        BasisSettingsDefaults.FBIKElbowDrag.ResetToDefault();
        BasisSettingsDefaults.FBIKElbowDragHz.ResetToDefault();
        BasisSettingsDefaults.FBIKChestRadius.ResetToDefault();
        BasisSettingsDefaults.FBIKCollisionSkin.ResetToDefault();
        BasisSettingsDefaults.FBIKHandRadius.ResetToDefault();
        BasisSettingsDefaults.FBIKHandSkin.ResetToDefault();
        BasisSettingsDefaults.FBIKShoulderSolveEnabled.ResetToDefault();
        BasisSettingsDefaults.FBIKShoulderShrug.ResetToDefault();
        BasisSettingsDefaults.FBIKShoulderRetraction.ResetToDefault();
        BasisSettingsDefaults.FBIKShoulderElevation.ResetToDefault();
        BasisSettingsDefaults.FBIKShoulderProtraction.ResetToDefault();
        BasisSettingsDefaults.FBIKShoulderCoupleRatio.ResetToDefault();
        BasisSettingsDefaults.FBIKShoulderMaxDeg.ResetToDefault();
        BasisSettingsDefaults.FBIKShoulderSlideStartDeg.ResetToDefault();
        BasisSettingsDefaults.FBIKShoulderSlideMaxDeg.ResetToDefault();
        BasisSettingsDefaults.FBIKShoulderSlideFraction.ResetToDefault();
        BasisSettingsDefaults.FBIKThoracicBendStiffen.ResetToDefault();
        BasisSettingsDefaults.FBIKSpineTautBandFrac.ResetToDefault();
        BasisSettingsDefaults.FBIKBendTwistCoupling.ResetToDefault();
        BasisSettingsDefaults.FBIKNeckGazeFollowMaxDeg.ResetToDefault();
        BasisSettingsDefaults.FBIKTrunkCounterbalanceMaxFrac.ResetToDefault();
        BasisSettingsDefaults.FBIKChestIkWeight.ResetToDefault();
        BasisSettingsDefaults.FBIKChestIkIterations.ResetToDefault();
        BasisSettingsDefaults.FBIKChestIkHeadRestoreSweeps.ResetToDefault();
        BasisSettingsDefaults.FBIKChestPosPullMaxDeg.ResetToDefault();
        BasisSettingsDefaults.FBIKChestPullMaxDist.ResetToDefault();
        BasisSettingsDefaults.FBIKChestFollowChestShare.ResetToDefault();
        BasisSettingsDefaults.FBIKTrackedKneeSwivelMinCutoffHz.ResetToDefault();
        BasisSettingsDefaults.FBIKTrackedKneeSwivelBeta.ResetToDefault();
        BasisSettingsDefaults.FBIKTrackedKneeSwivelDerivCutoffHz.ResetToDefault();
        BasisSettingsDefaults.FBIKMaxBendDeg.ResetToDefault();
        BasisSettingsDefaults.FBIKMaxChestDelta.ResetToDefault();
        BasisSettingsDefaults.FBIKButterflyKnees.ResetToDefault();
        BasisSettingsDefaults.FBIKButterflyKneeMaxOpenDeg.ResetToDefault();
        BasisSettingsDefaults.FBIKKneeFollowsFoot.ResetToDefault();
        BasisSettingsDefaults.FBIKKneeFootFollowUpright.ResetToDefault();
        BasisSettingsDefaults.FBIKKneeFootPoleHold.ResetToDefault();
        BasisSettingsDefaults.FBIKKneeFootPoleConditioning.ResetToDefault();
        BasisSettingsDefaults.FBIKArmHeightRatioEnabled.ResetToDefault();
        BasisSettingsDefaults.FBIKArmHeightRatio.ResetToDefault();
       // BasisSettingsDefaults.FBIKNeuralPole.ResetToDefault();
        BasisSettingsDefaults.FBIKSpineBendPitch.ResetToDefault();
        BasisSettingsDefaults.FBIKSpineBendYaw.ResetToDefault();
        BasisSettingsDefaults.FBIKSpineBendRoll.ResetToDefault();
        BasisSettingsDefaults.FBIKUpperChestBendPitch.ResetToDefault();
        BasisSettingsDefaults.FBIKUpperChestBendYaw.ResetToDefault();
        BasisSettingsDefaults.FBIKUpperChestBendRoll.ResetToDefault();
        BasisSettingsDefaults.FBIKChestBendPitch.ResetToDefault();
        BasisSettingsDefaults.FBIKChestBendYaw.ResetToDefault();
        BasisSettingsDefaults.FBIKChestBendRoll.ResetToDefault();
        BasisSettingsDefaults.FBIKNeckYawShare.ResetToDefault();
        BasisSettingsDefaults.FBIKSpineStretchMax.ResetToDefault();
        BasisSettingsDefaults.FBIKHipHingeStartDeg.ResetToDefault();
        BasisSettingsDefaults.FBIKHipHingeMaxAddDeg.ResetToDefault();
        BasisSettingsDefaults.FBIKMoveBodyBackWhenCrouching.ResetToDefault();
        BasisSettingsDefaults.FBIKTrunkCounterbalance.ResetToDefault();
        BasisSettingsDefaults.FBIKSwingSmoothRate.ResetToDefault();
        BasisSettingsDefaults.FBIKElbowSwingEnabled.ResetToDefault();
        BasisSettingsDefaults.FBIKChestSpringHz.ResetToDefault();
        BasisSettingsDefaults.FBIKChestSpringDamping.ResetToDefault();
        BasisSettingsDefaults.FBIKLordosisPitchGainDeg.ResetToDefault();
        BasisSettingsDefaults.FBIKLordosisBaseDeg.ResetToDefault();
        BasisSettingsDefaults.FBIKLordosisNeckShare.ResetToDefault();
        BasisSettingsDefaults.FBIKLordosisMaxHeadPitchDeg.ResetToDefault();
        BasisSettingsDefaults.FBIKLordosisExtremeStartDeg.ResetToDefault();
        BasisSettingsDefaults.FBIKLordosisExtremeFullDeg.ResetToDefault();
        BasisSettingsDefaults.FBIKLordosisExtremeRollForwardMaxDeg.ResetToDefault();
        BasisSettingsDefaults.FBIKLordosisExtremeRollBackwardMaxDeg.ResetToDefault();
        BasisSettingsDefaults.FBIKLordosisExtremeHipsHorizontalMax.ResetToDefault();
        BasisSettingsDefaults.FBIKLordosisExtremeChestHorizontalMax.ResetToDefault();
        BasisSettingsDefaults.FBIKLordosisExtremeHipsHorizontalLookUp.ResetToDefault();
        BasisSettingsDefaults.FBIKLordosisExtremeChestHorizontalLookUp.ResetToDefault();
        BasisSettingsDefaults.FBIKLordosisExtremeHipsDownMax.ResetToDefault();
        BasisSettingsDefaults.FBIKLordosisExtremeChestDownMax.ResetToDefault();
        BasisSettingsDefaults.FBIKLordosisExtremeHipsDownLookUp.ResetToDefault();
        BasisSettingsDefaults.FBIKLordosisExtremeChestDownLookUp.ResetToDefault();
        BasisSettingsDefaults.VSpineChestPitchFrac.ResetToDefault();
        BasisSettingsDefaults.VSpineChestRollFrac.ResetToDefault();
        BasisSettingsDefaults.VSpineSpinePitchFrac.ResetToDefault();
        BasisSettingsDefaults.VSpineSpineRollFrac.ResetToDefault();
        BasisSettingsDefaults.VSpineNeckRotationSpeed.ResetToDefault();
        BasisSettingsDefaults.VSpineChestRotationSpeed.ResetToDefault();
        BasisSettingsDefaults.VSpineSpineRotationSpeed.ResetToDefault();
        BasisSettingsDefaults.VSpineHipsRotationSpeed.ResetToDefault();
        BasisSettingsDefaults.VSpineHipsForwardBias.ResetToDefault();
        BasisSettingsDefaults.VSpineTorsoYawDeadzoneDeg.ResetToDefault();
        BasisSettingsDefaults.VSpineTorsoYawBlendSpeed.ResetToDefault();
        BasisSettingsDefaults.VSpineTorsoYawPlayInVR.ResetToDefault();
        BasisSettingsDefaults.VSpineTorsoYawDeadzoneVRDeg.ResetToDefault();
        BasisSettingsDefaults.VSpinePostureModel.ResetToDefault();
        BasisSettingsDefaults.VSpineHipsCompressionStrength.ResetToDefault();
        BasisSettingsDefaults.VSpineHipsMaxDropMeters.ResetToDefault();
        BasisSettingsDefaults.FBIKSpineMaxForwardDeg.ResetToDefault();
        BasisSettingsDefaults.FBIKSpineMaxBackwardDeg.ResetToDefault();
        BasisSettingsDefaults.FBIKSpineMaxLateralDeg.ResetToDefault();
        BasisSettingsDefaults.FBIKSpineSquishBoost.ResetToDefault();
        BasisSettingsDefaults.FBIKSpineGazeFollow.ResetToDefault();
        BasisSettingsDefaults.FBIKNeckGazeFollow.ResetToDefault();
        BasisSettingsDefaults.FBIKNeckExtensionDamp.ResetToDefault();
        BasisSettingsDefaults.FBIKNeckFlexionDamp.ResetToDefault();
        BasisSettingsDefaults.VSpineNodPivotEstimate.ResetToDefault();
        BasisSettingsDefaults.VSpineGazeSwingRemoval.ResetToDefault();
        BasisSettingsDefaults.FBIKSpineCCDRelax.ResetToDefault();
        BasisSettingsDefaults.FBIKSpineTwistKeep.ResetToDefault();
        BasisSettingsDefaults.FBIKSpineNeckTwistKeep.ResetToDefault();
        BasisSettingsDefaults.FBIKNeckMaxConeDeg.ResetToDefault();
        BasisSettingsDefaults.FBIKChestArmSwingFactor.ResetToDefault();
        BasisSettingsDefaults.FBIKChestArmSwingMaxDeg.ResetToDefault();
        BasisSettingsDefaults.FBIKLowerArmTwistFraction.ResetToDefault();
        BasisSettingsDefaults.FBIKUpperArmTwistFraction.ResetToDefault();
        BasisSettingsDefaults.FBIKAnatDifferentialStiffness.ResetToDefault();
        BasisSettingsDefaults.FBIKAnatShoulderSlide.ResetToDefault();
        BasisSettingsDefaults.FBIKAnatCervicalLordosis.ResetToDefault();
        BasisSettingsDefaults.FBIKAnatPelvicTwistRouting.ResetToDefault();
        BasisSettingsDefaults.FBIKSpineAnatomicalRom.ResetToDefault();
        BasisSettingsDefaults.FBIKChestIKTarget.ResetToDefault();
        BasisSettingsDefaults.FBIKLegSwivelSmoothing.ResetToDefault();
        BasisSettingsDefaults.FBIKTrackerBendNormal.ResetToDefault();
        BasisSettingsDefaults.FBIKBodyFit.ResetToDefault();
        BasisSettingsDefaults.FBIKBodyFitMaxDeviation.ResetToDefault();
        BasisSettingsDefaults.DesktopHeadSwingEnabled.ResetToDefault();
        BasisSettingsDefaults.DesktopHeadSwingStrength.ResetToDefault();
        BasisSettingsDefaults.DesktopHeadSwingBackward.ResetToDefault();

        // Per-bone toggles and calibration sphere scale
        foreach (var b in _bones)
        {
            b.UseCalibration?.ResetToDefault();
            b.SmoothPos.ResetToDefault();
            b.SmoothRot.ResetToDefault();
            b.EuroPos.ResetToDefault();
            b.EuroRot.ResetToDefault();
            b.CalibSphereScale?.ResetToDefault();
        }

        // Refresh the editor bindings + derived master state
        RebindBoneEditor();
        SyncMasterEuroFromChildren();
    }

    private static void AddFBIKTogglesCompact(RectTransform parent)
    {
        var blocks = new (string name,
            BasisSettingsBinding<bool> useCalibration,
            BasisSettingsBinding<bool> smoothPos,
            BasisSettingsBinding<bool> smoothRot,
            BasisSettingsBinding<bool> euroPos,
            BasisSettingsBinding<bool> euroRot,
            BasisSettingsBinding<float> calibSphereScale)[]
        {
            ("Hips", BasisSettingsDefaults.FBIKHipsUseCalibration, BasisSettingsDefaults.FBIKHipsSmoothPos, BasisSettingsDefaults.FBIKHipsSmoothRot, BasisSettingsDefaults.FBIKHipsEuroPos, BasisSettingsDefaults.FBIKHipsEuroRot, BasisSettingsDefaults.CalibSphereScaleHips),
            ("Head", BasisSettingsDefaults.FBIKHeadUseCalibration, BasisSettingsDefaults.FBIKHeadSmoothPos, BasisSettingsDefaults.FBIKHeadSmoothRot, BasisSettingsDefaults.FBIKHeadEuroPos, BasisSettingsDefaults.FBIKHeadEuroRot, null),
            ("Left Foot", BasisSettingsDefaults.FBIKLeftFootUseCalibration, BasisSettingsDefaults.FBIKLeftFootSmoothPos, BasisSettingsDefaults.FBIKLeftFootSmoothRot, BasisSettingsDefaults.FBIKLeftFootEuroPos, BasisSettingsDefaults.FBIKLeftFootEuroRot, BasisSettingsDefaults.CalibSphereScaleLeftFoot),
            ("Right Foot", BasisSettingsDefaults.FBIKRightFootUseCalibration, BasisSettingsDefaults.FBIKRightFootSmoothPos, BasisSettingsDefaults.FBIKRightFootSmoothRot, BasisSettingsDefaults.FBIKRightFootEuroPos, BasisSettingsDefaults.FBIKRightFootEuroRot, BasisSettingsDefaults.CalibSphereScaleRightFoot),
            ("Chest", BasisSettingsDefaults.FBIKChestUseCalibration, BasisSettingsDefaults.FBIKChestSmoothPos, BasisSettingsDefaults.FBIKChestSmoothRot, BasisSettingsDefaults.FBIKChestEuroPos, BasisSettingsDefaults.FBIKChestEuroRot, BasisSettingsDefaults.CalibSphereScaleChest),
            ("Left Lower Leg", BasisSettingsDefaults.FBIKLeftLowerLegUseCalibration, BasisSettingsDefaults.FBIKLeftLowerLegSmoothPos, BasisSettingsDefaults.FBIKLeftLowerLegSmoothRot, BasisSettingsDefaults.FBIKLeftLowerLegEuroPos, BasisSettingsDefaults.FBIKLeftLowerLegEuroRot, BasisSettingsDefaults.CalibSphereScaleLeftLowerLeg),
            ("Right Lower Leg", BasisSettingsDefaults.FBIKRightLowerLegUseCalibration, BasisSettingsDefaults.FBIKRightLowerLegSmoothPos, BasisSettingsDefaults.FBIKRightLowerLegSmoothRot, BasisSettingsDefaults.FBIKRightLowerLegEuroPos, BasisSettingsDefaults.FBIKRightLowerLegEuroRot, BasisSettingsDefaults.CalibSphereScaleRightLowerLeg),
            ("Left Hand", BasisSettingsDefaults.FBIKLeftHandUseCalibration, BasisSettingsDefaults.FBIKLeftHandSmoothPos, BasisSettingsDefaults.FBIKLeftHandSmoothRot, BasisSettingsDefaults.FBIKLeftHandEuroPos, BasisSettingsDefaults.FBIKLeftHandEuroRot, BasisSettingsDefaults.CalibSphereScaleLeftHand),
            ("Right Hand", BasisSettingsDefaults.FBIKRightHandUseCalibration, BasisSettingsDefaults.FBIKRightHandSmoothPos, BasisSettingsDefaults.FBIKRightHandSmoothRot, BasisSettingsDefaults.FBIKRightHandEuroPos, BasisSettingsDefaults.FBIKRightHandEuroRot, BasisSettingsDefaults.CalibSphereScaleRightHand),
            ("Left Lower Arm", BasisSettingsDefaults.FBIKLeftLowerArmUseCalibration, BasisSettingsDefaults.FBIKLeftLowerArmSmoothPos, BasisSettingsDefaults.FBIKLeftLowerArmSmoothRot, BasisSettingsDefaults.FBIKLeftLowerArmEuroPos, BasisSettingsDefaults.FBIKLeftLowerArmEuroRot, BasisSettingsDefaults.CalibSphereScaleLeftLowerArm),
            ("Right Lower Arm", BasisSettingsDefaults.FBIKRightLowerArmUseCalibration, BasisSettingsDefaults.FBIKRightLowerArmSmoothPos, BasisSettingsDefaults.FBIKRightLowerArmSmoothRot, BasisSettingsDefaults.FBIKRightLowerArmEuroPos, BasisSettingsDefaults.FBIKRightLowerArmEuroRot, BasisSettingsDefaults.CalibSphereScaleRightLowerArm),
            ("Left Toe", BasisSettingsDefaults.FBIKLeftToeUseCalibration, BasisSettingsDefaults.FBIKLeftToeSmoothPos, BasisSettingsDefaults.FBIKLeftToeSmoothRot, BasisSettingsDefaults.FBIKLeftToeEuroPos, BasisSettingsDefaults.FBIKLeftToeEuroRot, BasisSettingsDefaults.CalibSphereScaleLeftToes),
            ("Right Toe", BasisSettingsDefaults.FBIKRightToeUseCalibration, BasisSettingsDefaults.FBIKRightToeSmoothPos, BasisSettingsDefaults.FBIKRightToeSmoothRot, BasisSettingsDefaults.FBIKRightToeEuroPos, BasisSettingsDefaults.FBIKRightToeEuroRot, BasisSettingsDefaults.CalibSphereScaleRightToes),
            ("Left Shoulder", BasisSettingsDefaults.FBIKLeftShoulderUseCalibration, BasisSettingsDefaults.FBIKLeftShoulderSmoothPos, BasisSettingsDefaults.FBIKLeftShoulderSmoothRot, BasisSettingsDefaults.FBIKLeftShoulderEuroPos, BasisSettingsDefaults.FBIKLeftShoulderEuroRot, BasisSettingsDefaults.CalibSphereScaleLeftShoulder),
            ("Right Shoulder", BasisSettingsDefaults.FBIKRightShoulderUseCalibration, BasisSettingsDefaults.FBIKRightShoulderSmoothPos, BasisSettingsDefaults.FBIKRightShoulderSmoothRot, BasisSettingsDefaults.FBIKRightShoulderEuroPos, BasisSettingsDefaults.FBIKRightShoulderEuroRot, BasisSettingsDefaults.CalibSphereScaleRightShoulder),
        };

        _bones.Clear();
        foreach (var b in blocks)
        {
            _bones.Add(new BoneBindings
            {
                Name = b.name,
                UseCalibration = b.useCalibration,
                SmoothPos = b.smoothPos,
                SmoothRot = b.smoothRot,
                EuroPos = b.euroPos,
                EuroRot = b.euroRot,
                CalibSphereScale = b.calibSphereScale
            });
        }

        var boneSelectGroup = PanelElementDescriptor.CreateNew(
            PanelElementDescriptor.ElementStyles.Group,
            parent);
        boneSelectGroup.SetTitle(BasisLocalization.Get("settings.ik.title.perBoneSettings"));
        boneSelectGroup.SetDescription(BasisLocalization.Get("settings.ik.title.perBoneSettings.description"));

        var boneNames = _bones.Select(b => b.Name).ToList();
        _boneDropdown = PanelDropdown.CreateNewEntry(boneSelectGroup.ContentParent);
        _boneDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.ik.title.bone"));
        _boneDropdown.AssignEntries(boneNames);
        _boneDropdown.AssignBinding(BasisSettingsDefaults.SelectedBone);
        _boneDropdown.Descriptor.SetTooltip(BasisLocalization.Get("settings.ik.title.bone.tooltip"));
        _boneDropdown.OnValueChanged += _ => RebindBoneEditor();

        _boneEuroEditorGroup = PanelElementDescriptor.CreateNew(
            PanelElementDescriptor.ElementStyles.Group,
            parent);
        _boneEuroEditorGroup.SetTitle(BasisLocalization.Get("settings.ik.title.calibrationSmoothing"));
        _boneEuroEditorGroup.SetDescription(BasisLocalization.Get("settings.ik.title.calibrationSmoothing.description"));

        _uiUseCalibration = PanelToggle.CreateNewEntry(_boneEuroEditorGroup.ContentParent);
        _uiUseCalibration.Descriptor.SetTitle(BasisLocalization.Get("settings.ik.title.useForCalibration"));
        _uiUseCalibration.Descriptor.SetTooltip(BasisLocalization.Get("settings.ik.title.useForCalibration.tooltip"));

        _uiSmoothPos = PanelToggle.CreateNewEntry(_boneEuroEditorGroup.ContentParent);
        _uiSmoothPos.Descriptor.SetTitle(BasisLocalization.Get("settings.ik.title.smoothPosition"));
        _uiSmoothPos.Descriptor.SetTooltip(BasisLocalization.Get("settings.ik.title.smoothPosition.tooltip"));

        _uiSmoothRot = PanelToggle.CreateNewEntry(_boneEuroEditorGroup.ContentParent);
        _uiSmoothRot.Descriptor.SetTitle(BasisLocalization.Get("settings.ik.title.smoothRotation"));
        _uiSmoothRot.Descriptor.SetTooltip(BasisLocalization.Get("settings.ik.title.smoothRotation.tooltip"));

        _uiEuroPos = PanelToggle.CreateNewEntry(_boneEuroEditorGroup.ContentParent);
        _uiEuroPos.Descriptor.SetTitle(BasisLocalization.Get("settings.ik.title.euroFilteringPosition"));
        _uiEuroPos.Descriptor.SetTooltip(BasisLocalization.Get("settings.ik.title.euroFilteringPosition.tooltip"));

        _uiEuroRot = PanelToggle.CreateNewEntry(_boneEuroEditorGroup.ContentParent);
        _uiEuroRot.Descriptor.SetTitle(BasisLocalization.Get("settings.ik.title.euroFilteringRotation"));
        _uiEuroRot.Descriptor.SetTooltip(BasisLocalization.Get("settings.ik.title.euroFilteringRotation.tooltip"));

        _uiCalibSphereScale = PanelSlider.CreateAndBind(
            _boneEuroEditorGroup.ContentParent,
            PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.calibSphereScale"), 0.1f, 5f, false, 2, ValueDisplayMode.Raw),
            BasisSettingsDefaults.CalibSphereScaleHips);

        if (_uiCalibSphereScale != null)
        {
            _uiCalibSphereScale.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.calibSphereScale.tooltip"));
        }

        RebindBoneEditor();
    }

    private static void RebindBoneEditor()
    {
        if (_boneDropdown == null || _bones.Count == 0)
            return;

        int index = Mathf.Clamp(_boneDropdown.DropdownComponent.value, 0, _bones.Count - 1);
        var bone = _bones[index];

        if (_uiUseCalibration != null && bone.UseCalibration != null)
        {
            _uiUseCalibration.AssignBinding(bone.UseCalibration);
        }

        _uiSmoothPos.AssignBinding(bone.SmoothPos);
        _uiSmoothRot.AssignBinding(bone.SmoothRot);
        _uiEuroPos.AssignBinding(bone.EuroPos);
        _uiEuroRot.AssignBinding(bone.EuroRot);

        bool hasCalibSphere = bone.CalibSphereScale != null;
        if (_uiCalibSphereScale != null)
        {
            _uiCalibSphereScale.gameObject.SetActive(hasCalibSphere);
            if (hasCalibSphere)
            {
                _uiCalibSphereScale.AssignBinding(bone.CalibSphereScale);
            }
        }

        RebuildLayout(_boneEuroEditorGroup);

        SyncMasterEuroFromChildren();
    }

    private static void SyncMasterEuroFromChildren()
    {
        if (_bones.Count == 0)
            return;

        bool allOn = _bones.All(b => b.EuroPos.RawValue && b.EuroRot.RawValue);
        BasisSettingsDefaults.FBIKEuroAll.SetValue(allOn);
    }

    private static void AddAnatomyToggle(RectTransform parent, BasisSettingsBinding<bool> binding, string titleKey, string descriptionKey)
    {
        var toggle = PanelToggle.CreateNewEntry(parent);
        toggle.Descriptor.SetTitle(BasisLocalization.Get(titleKey));
        // Explanatory text on hover (tooltip) instead of inline, to keep the page compact.
        toggle.Descriptor.SetTooltip(BasisLocalization.Get(descriptionKey));
        toggle.AssignBinding(binding);
    }

    private static void RebuildLayout(PanelElementDescriptor descriptor)
    {
        RectTransform content = descriptor != null ? descriptor.ContentParent : null;
        if (content != null)
        {
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(content);
        }
    }

    private static PanelToggle PlayspaceGizmoLayerToggle(RectTransform parent, string localizationKey, BasisSettingsBinding<bool> binding)
    {
        var toggle = PanelToggle.CreateNewEntry(parent);
        toggle.Descriptor.SetTitle(BasisLocalization.Get(localizationKey));
        toggle.Descriptor.SetTooltip(BasisLocalization.Get(localizationKey + ".tooltip"));
        toggle.AssignBinding(binding);
        return toggle;
    }

    private static void SetPlayspaceGizmoLayersVisible(List<PanelToggle> toggles, bool visible)
    {
        for (int Index = 0; Index < toggles.Count; Index++)
        {
            toggles[Index].Descriptor.SetActive(visible);
        }
    }

    private static void RebuildLayoutChain(RectTransform from, PanelElementDescriptor tabDesc)
    {
        PanelElementDescriptor.RebuildLayoutChain(from, tabDesc != null ? tabDesc.ContentParent : null);
    }

    private static void AddSmoothingGroup(PanelElementDescriptor tabDesc, RectTransform parent, BasisSettingsDefaults.SmoothingGroupBindings group)
    {
        string groupName = BasisLocalization.Get(group.NameKey);

        var presetDropdown = PanelDropdown.CreateNewEntry(parent);
        presetDropdown.Descriptor.SetTitle(groupName);
        presetDropdown.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.smoothing.preset.tooltip"));
        presetDropdown.AssignLocalizedEntries(
            new List<string>(BasisSmoothingProfiles.PresetOrder),
            new List<string>(BasisSmoothingProfiles.PresetLocalizationKeys));
        presetDropdown.AssignBinding(group.Preset);

        // Folded into the dropdown above as the "Custom" preset entry.
        // var customToggle = PanelToggle.CreateNewEntry(parent);
        // customToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.smoothing.custom"));
        // customToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.smoothing.custom.tooltip"));
        // customToggle.AssignBinding(group.Custom);

        // Every group's sliders carry the same five titles, so the group name goes on the row itself —
        // with two groups tuned by hand the flat list is otherwise ten rows of "Beta" and "Min Cutoff".
        var sliders = new List<PanelSlider>(5);
        AddSmoothingGroupSlider(sliders, parent, groupName, "settings.bodyTracking.minCutoff.title", 0.1f, 10f, group.MinCutoff);
        AddSmoothingGroupSlider(sliders, parent, groupName, "settings.bodyTracking.beta.title", 0f, 10f, group.Beta);
        AddSmoothingGroupSlider(sliders, parent, groupName, "settings.bodyTracking.smoothingStrength.title", 1f, 100f, group.Strength);
        AddSmoothingGroupSlider(sliders, parent, groupName, "settings.bodyTracking.posSmoothingHz.title", 0.01f, 60f, group.PositionHz);
        AddSmoothingGroupSlider(sliders, parent, groupName, "settings.bodyTracking.rotSmoothingHz.title", 0.01f, 60f, group.RotationHz);

        void ApplyCustomVisibility(bool visible)
        {
            for (int Index = 0; Index < sliders.Count; Index++)
            {
                sliders[Index].Descriptor.SetActive(visible);
            }
        }

        ApplyCustomVisibility(BasisSmoothingProfiles.IsCustom(presetDropdown.Value));
        presetDropdown.OnValueChanged += value =>
        {
            ApplyCustomVisibility(BasisSmoothingProfiles.IsCustom(value));
            RebuildLayoutChain(parent, tabDesc);
        };
    }

    private static void AddSmoothingGroupSlider(List<PanelSlider> sliders, RectTransform parent, string groupName, string titleKey, float min, float max, BasisSettingsBinding<float> binding)
    {
        string title = $"{groupName}: {BasisLocalization.Get(titleKey)}";
        var slider = PanelSlider.CreateAndBind(
            parent,
            PanelSlider.SliderSettings.Advanced(title, min, max, false, 2, ValueDisplayMode.Raw),
            binding);
        if (slider != null)
        {
            sliders.Add(slider);
        }
    }

    private static void CreateCollapsibleSection(PanelElementDescriptor tabDesc, PanelElementDescriptor parentGroup, string title, string description, bool defaultOpen, Action<RectTransform> addContent)
    {
        var parent = parentGroup.ContentParent;

        var sectionToggle = PanelSectionToggle.CreateNewEntry(parent);
        // Section blurb on hover (tooltip) instead of inline, to keep the page compact.
        sectionToggle.Descriptor.SetTooltip(description);

        var sectionGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(sectionToggle, parent, title);
        sectionGroup.SetIcon(AddressableAssets.Sprites.Settings);

        // Add content while the group is still active so child component Awake/Start runs and
        // their text initializes. SetActive(false) before attach would orphan their lifecycle.
        addContent(sectionGroup.ContentParent);

        PanelSectionToggleHelpers.FinalizeCollapsibleGroup(sectionToggle, sectionGroup, defaultOpen, _ =>
        {
            // Rebuild the containers that actually carry the VerticalLayoutGroup, innermost first, so the
            // outer pass sees the corrected inner height. ForceRebuild() on a descriptor root only recurses
            // when that rect has an ILayoutController, which the tab page and group roots do not.
            RebuildLayout(parentGroup);
            RebuildLayout(tabDesc);
        });
    }

}
