using Basis;
using Basis.BasisUI;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public static class SettingsProviderIK
{
    private static PanelDropdown dropdownIKMode;
    private static PanelDropdown dropdownIKLockMode;
    private static PanelDropdown dropdownSeatedMode;

    public const string SeatedMode_Seated = "Seated Mode";
    public const string SeatedMode_Standing = "Standing Mode";

    private static readonly List<PanelToggle> _euroToggleUIs = new();
    private static readonly List<PanelToggle> _trackerLerpToggleUIs = new();

    private static PanelDropdown _boneDropdown;

    private static PanelToggle _uiUseCalibration;
    private static PanelToggle _uiSmoothPos;
    private static PanelToggle _uiSmoothRot;
    private static PanelToggle _uiEuroPos;
    private static PanelToggle _uiEuroRot;
    private static PanelSlider _uiCalibSphereScale;
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

        // --- Group: "Body Tracking" (replaces tab.Group(...)) ---
        var ikGroup = PanelElementDescriptor.CreateNew(
            PanelElementDescriptor.ElementStyles.Group,
            tabDesc.ContentParent);

        ikGroup.SetTitle(BasisLocalization.Get("settings.tab.bodytracking"));
        ikGroup.SetDescription(BasisLocalization.Get("settings.bodyTracking.description"));
        ikGroup.SetIcon(AddressableAssets.Sprites.Settings);

        var ikParent = ikGroup.ContentParent;

        // --- Seated Mode dropdown ---
        dropdownSeatedMode = PanelDropdown.CreateNewEntry(ikParent);
        dropdownSeatedMode.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.seatedMode"));
        dropdownSeatedMode.Descriptor.SetDescription(
            "Select the reference pose used for body scaling"
        );
        dropdownSeatedMode.AssignEntries(new List<string> { SeatedMode_Standing, SeatedMode_Seated });
        dropdownSeatedMode.AssignBinding(BasisSettingsDefaults.SitStand);

        // --- IK mode dropdown ---
        dropdownIKMode = PanelDropdown.CreateNewEntry(ikParent);
        dropdownIKMode.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.ikMode"));
        dropdownIKMode.AssignEntries(new List<string> { "Eye Height", "Arm Distance" });
        dropdownIKMode.AssignBinding(BasisSettingsDefaults.IKMode);
        dropdownIKMode.Descriptor.SetDescription(
            "Determines how body scale is calculated."
        );

        // --- IK Lock Mode dropdown ---
        dropdownIKLockMode = PanelDropdown.CreateNewEntry(ikParent);
        dropdownIKLockMode.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.spineLockMode"));
        dropdownIKLockMode.AssignEntries(new List<string> { "Lock Hips", "Lock Head", "Lock Both" });
        dropdownIKLockMode.AssignBinding(BasisSettingsDefaults.IKLockMode);
        dropdownIKLockMode.Descriptor.SetDescription(
            "Lock Hips: Hips are the anchor, Lock Head: Head is the anchor."
        //"Controls how the spine IK chain resolves the relationship between head and hips.\n\n"// +
        //  "Lock Hips: Hips are the anchor. Prevents spine curvature from leg movement. Best for full-body tracking.\n" +
        //  "Lock Head: Head is the anchor. Hips are derived below head. Best for HMD-only or 3-point tracking.\n" +
        //  "Lock Both: Both head and hips are independent. Spine stretches to connect them."
        );

        // --- Custom scale toggle ---
        var customScaleToggle = PanelToggle.CreateNewEntry(ikParent);
        customScaleToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.customScale"));
        customScaleToggle.AssignBinding(BasisSettingsDefaults.CustomScale);
        customScaleToggle.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.customScale.description"));

        // --- Avatar scale slider ---
        var avatarScaleSlider = PanelSlider.CreateAndBind(
            ikParent,
            PanelSlider.SliderSettings.Advanced("Avatar Height Scale", 0.1f, 5f, false, 2, ValueDisplayMode.Meters),
            BasisSettingsDefaults.SelectedScale);

        if (avatarScaleSlider != null)
        {
            avatarScaleSlider.Descriptor.SetDescription(
                "Manually adjusts avatar height when Custom Scale is enabled. " +
                "This affects perceived size only and does not change tracking accuracy."
            );

            avatarScaleSlider.gameObject.SetActive(BasisSettingsDefaults.CustomScale.RawValue);
            customScaleToggle.OnValueChanged += visible =>
            {
                avatarScaleSlider.gameObject.SetActive(visible);
                tabDesc.ForceRebuild();
                ikGroup.ForceRebuild();
            };
        }

        dropdownIKMode.OnValueChanged += _ => EvaluateInteractables();
        dropdownSeatedMode.OnValueChanged += _ => EvaluateInteractables();
        EvaluateInteractables();

        _trackerLerpToggleUIs.Clear();
        _euroToggleUIs.Clear();

        CreateCollapsibleSection(tabDesc, ikGroup,
            BasisLocalization.Get("settings.bodyTracking.section.perBone.title"),
            BasisLocalization.Get("settings.bodyTracking.section.perBone.description"), false,
            AddFBIKTogglesCompact);

        SyncMasterEuroFromChildren();

        // ------------------
        // Advanced IK toggle
        // ------------------
        var advancedToggle = PanelToggle.CreateNewEntry(tabDesc.ContentParent);
        advancedToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.advanced"));
        advancedToggle.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.advanced.description"));
        advancedToggle.AssignBinding(BasisSettingsDefaults.FBIKAdvancedVisible);

        var colliderGroup = PanelElementDescriptor.CreateNew(
            PanelElementDescriptor.ElementStyles.Group,
            tabDesc.ContentParent);

        colliderGroup.SetTitle(BasisLocalization.Get("settings.bodyTracking.colliders.title"));
        colliderGroup.SetDescription(BasisLocalization.Get("settings.bodyTracking.colliders.description"));
        colliderGroup.SetIcon(AddressableAssets.Sprites.Settings);

        var colliderParent = colliderGroup.ContentParent;

        // ============== Tracking & Input ==============
        CreateCollapsibleSection(tabDesc, colliderGroup,
            BasisLocalization.Get("settings.bodyTracking.section.tracking.title"),
            BasisLocalization.Get("settings.bodyTracking.section.tracking.description"), true, trackingParent =>
        {
            var fbtEnabledToggle = PanelToggle.CreateNewEntry(trackingParent);
            fbtEnabledToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.fbt"));
            fbtEnabledToggle.AssignBinding(BasisSettingsDefaults.EnableFBT);
            fbtEnabledToggle.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.fbt.description"));

            var oscEnabledToggle = PanelToggle.CreateNewEntry(trackingParent);
            oscEnabledToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.osc"));
            oscEnabledToggle.AssignBinding(BasisSettingsDefaults.EnableOSC);
            oscEnabledToggle.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.osc.description"));

            var faceTrackingToggle = PanelToggle.CreateNewEntry(trackingParent);
            faceTrackingToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.faceTracking.title"));
            faceTrackingToggle.AssignBinding(BasisSettingsDefaults.EnableFaceTracking);
            faceTrackingToggle.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.faceTracking.description"));

            var eyeTrackingToggle = PanelToggle.CreateNewEntry(trackingParent);
            eyeTrackingToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.eyeTracking.title"));
            eyeTrackingToggle.AssignBinding(BasisSettingsDefaults.EnableEyeTracking);
            eyeTrackingToggle.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.eyeTracking.description"));

            var footIKToggle = PanelToggle.CreateNewEntry(trackingParent);
            footIKToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.footIk"));
            footIKToggle.AssignBinding(BasisSettingsDefaults.FootIKEnabled);
            footIKToggle.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.footIk.description"));

            var disableAnimInFBTToggle = PanelToggle.CreateNewEntry(trackingParent);
            disableAnimInFBTToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.disableAnimFbt"));
            disableAnimInFBTToggle.AssignBinding(BasisSettingsDefaults.DisableAnimationsInFBT);
            disableAnimInFBTToggle.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.disableAnimFbt.description"));
        });

        // ============== Body Collision ==============
        CreateCollapsibleSection(tabDesc, colliderGroup,
            BasisLocalization.Get("settings.bodyTracking.section.collision.title"),
            BasisLocalization.Get("settings.bodyTracking.section.collision.description"), false, collisionParent =>
        {
            var collisionsToggle = PanelToggle.CreateNewEntry(collisionParent);
            collisionsToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.collisionsEnabled"));
            collisionsToggle.AssignBinding(BasisSettingsDefaults.FBIKCollisionsEnabled);
            collisionsToggle.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.collisionsEnabled.description"));

            var protectElbowToggle = PanelToggle.CreateNewEntry(collisionParent);
            protectElbowToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.protectElbow.title"));
            protectElbowToggle.AssignBinding(BasisSettingsDefaults.FBIKProtectElbow);
            protectElbowToggle.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.protectElbow.description"));

            var handCapsuleToggle = PanelToggle.CreateNewEntry(collisionParent);
            handCapsuleToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.handCapsule.title"));
            handCapsuleToggle.AssignBinding(BasisSettingsDefaults.FBIKUseHandCapsule);
            handCapsuleToggle.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.handCapsule.description"));

            var chestRadiusSlider = PanelSlider.CreateAndBind(
                collisionParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.chestRadius.title"), 0.01f, 0.5f, false, 3, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKChestRadius);
            if (chestRadiusSlider != null)
                chestRadiusSlider.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.chestRadius.description"));

            var collisionSkinSlider = PanelSlider.CreateAndBind(
                collisionParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.collisionSkin.title"), 0f, 0.1f, false, 3, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKCollisionSkin);
            if (collisionSkinSlider != null)
                collisionSkinSlider.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.collisionSkin.description"));

            var handRadiusSlider = PanelSlider.CreateAndBind(
                collisionParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.handRadius.title"), 0f, 0.2f, false, 3, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKHandRadius);
            if (handRadiusSlider != null)
                handRadiusSlider.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.handRadius.description"));

            var handSkinSlider = PanelSlider.CreateAndBind(
                collisionParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.handSkin.title"), 0f, 0.1f, false, 3, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKHandSkin);
            if (handSkinSlider != null)
                handSkinSlider.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.handSkin.description"));
        });

        // ============== Shoulders ==============
        CreateCollapsibleSection(tabDesc, colliderGroup,
            BasisLocalization.Get("settings.bodyTracking.section.shoulders.title"),
            BasisLocalization.Get("settings.bodyTracking.section.shoulders.description"), false, shoulderParent =>
        {
            var shoulderSolveToggle = PanelToggle.CreateNewEntry(shoulderParent);
            shoulderSolveToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.shoulderSolve.title"));
            shoulderSolveToggle.AssignBinding(BasisSettingsDefaults.FBIKShoulderSolveEnabled);
            shoulderSolveToggle.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.shoulderSolve.description"));

            var shoulderElevSlider = PanelSlider.CreateAndBind(
                shoulderParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.shoulderElevation.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKShoulderElevation);
            if (shoulderElevSlider != null)
                shoulderElevSlider.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.shoulderElevation.description"));

            var shoulderProtSlider = PanelSlider.CreateAndBind(
                shoulderParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.shoulderProtraction.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKShoulderProtraction);
            if (shoulderProtSlider != null)
                shoulderProtSlider.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.shoulderProtraction.description"));
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
                lowerArmTwist.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.lowerArmTwist.description"));

            var upperArmTwist = PanelSlider.CreateAndBind(
                twistParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.upperArmTwist.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKUpperArmTwistFraction);
            if (upperArmTwist != null)
                upperArmTwist.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.upperArmTwist.description"));
        });

        // ============== Anatomy (Experimental) ==============
        CreateCollapsibleSection(tabDesc, colliderGroup,
            BasisLocalization.Get("settings.bodyTracking.section.anatomy.title"),
            BasisLocalization.Get("settings.bodyTracking.section.anatomy.description"), false, anatomyParent =>
        {
            AddExperimentalToggle(anatomyParent, BasisSettingsDefaults.FBIKAnatDifferentialStiffness,
                "settings.bodyTracking.anat.diffStiffness.title",
                "settings.bodyTracking.anat.diffStiffness.description");
            AddExperimentalToggle(anatomyParent, BasisSettingsDefaults.FBIKAnatShoulderSlide,
                "settings.bodyTracking.anat.shoulderSlide.title",
                "settings.bodyTracking.anat.shoulderSlide.description");
            AddExperimentalToggle(anatomyParent, BasisSettingsDefaults.FBIKAnatCervicalLordosis,
                "settings.bodyTracking.anat.cervicalLordosis.title",
                "settings.bodyTracking.anat.cervicalLordosis.description");
            AddExperimentalToggle(anatomyParent, BasisSettingsDefaults.FBIKAnatPelvicTwistRouting,
                "settings.bodyTracking.anat.pelvicTwistRouting.title",
                "settings.bodyTracking.anat.pelvicTwistRouting.description");
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
                maxBendSlider.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.maxBendDeg.description"));

            var struggleStartSlider = PanelSlider.CreateAndBind(
                reachParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.struggleStart.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKStruggleStart);
            if (struggleStartSlider != null)
                struggleStartSlider.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.struggleStart.description"));

            var struggleEndSlider = PanelSlider.CreateAndBind(
                reachParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.struggleEnd.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKStruggleEnd);
            if (struggleEndSlider != null)
                struggleEndSlider.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.struggleEnd.description"));

            var maxChestDeltaSlider = PanelSlider.CreateAndBind(
                reachParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.maxChestDelta.title"), 0f, 180f, false, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKMaxChestDelta);
            if (maxChestDeltaSlider != null)
                maxChestDeltaSlider.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.maxChestDelta.description"));

            var maxHipDeltaSlider = PanelSlider.CreateAndBind(
                reachParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.maxHipDelta.title"), 0f, 180f, false, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKMaxHipDelta);
            if (maxHipDeltaSlider != null)
                maxHipDeltaSlider.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.maxHipDelta.description"));
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
                spineBendPitch.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.spineBendPitch.description"));

            var spineBendYaw = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.spineBendYaw.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKSpineBendYaw);
            if (spineBendYaw != null)
                spineBendYaw.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.spineBendYaw.description"));

            var spineBendRoll = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.spineBendRoll.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKSpineBendRoll);
            if (spineBendRoll != null)
                spineBendRoll.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.spineBendRoll.description"));

            var upperChestBendPitch = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.upperChestBendPitch.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKUpperChestBendPitch);
            if (upperChestBendPitch != null)
                upperChestBendPitch.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.upperChestBendPitch.description"));

            var upperChestBendYaw = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.upperChestBendYaw.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKUpperChestBendYaw);
            if (upperChestBendYaw != null)
                upperChestBendYaw.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.upperChestBendYaw.description"));

            var upperChestBendRoll = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.upperChestBendRoll.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKUpperChestBendRoll);
            if (upperChestBendRoll != null)
                upperChestBendRoll.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.upperChestBendRoll.description"));

            var spineSquishBoost = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.spineSquishBoost.title"), 0f, 2f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKSpineSquishBoost);
            if (spineSquishBoost != null)
                spineSquishBoost.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.spineSquishBoost.description"));

            var spineMaxFwd = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.spineMaxForward.title"), 0f, 90f, false, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKSpineMaxForwardDeg);
            if (spineMaxFwd != null)
                spineMaxFwd.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.spineMaxForward.description"));

            var spineMaxBack = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.spineMaxBackward.title"), 0f, 90f, false, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKSpineMaxBackwardDeg);
            if (spineMaxBack != null)
                spineMaxBack.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.spineMaxBackward.description"));

            var spineMaxLat = PanelSlider.CreateAndBind(
                bendParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.spineMaxLateral.title"), 0f, 90f, false, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKSpineMaxLateralDeg);
            if (spineMaxLat != null)
                spineMaxLat.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.spineMaxLateral.description"));
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
                hipHingeStart.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.hipHingeStart.description"));

            var hipHingeMax = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.hipHingeMaxAdd.title"), 0f, 60f, false, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKHipHingeMaxAddDeg);
            if (hipHingeMax != null)
                hipHingeMax.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.hipHingeMaxAdd.description"));

            var chestSpringHz = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.chestSpringHz.title"), 0f, 30f, false, 1, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKChestSpringHz);
            if (chestSpringHz != null)
                chestSpringHz.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.chestSpringHz.description"));

            var chestSpringDamping = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.chestSpringDamping.title"), 0f, 2f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKChestSpringDamping);
            if (chestSpringDamping != null)
                chestSpringDamping.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.chestSpringDamping.description"));

            var chestArmSwingFactor = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.chestArmSwingFactor.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKChestArmSwingFactor);
            if (chestArmSwingFactor != null)
                chestArmSwingFactor.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.chestArmSwingFactor.description"));

            var chestArmSwingMaxDeg = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.chestArmSwingMax.title"), 0f, 30f, false, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKChestArmSwingMaxDeg);
            if (chestArmSwingMaxDeg != null)
                chestArmSwingMaxDeg.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.chestArmSwingMax.description"));

            var chestSpringHeadVelGain = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.chestSpringHeadVelGain.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKChestSpringHeadVelGain);
            if (chestSpringHeadVelGain != null)
                chestSpringHeadVelGain.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.chestSpringHeadVelGain.description"));

            var lordosisPitchGain = PanelSlider.CreateAndBind(
                dynamicsParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.lordosisPitchGain.title"), 0f, 30f, false, 1, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKLordosisPitchGainDeg);
            if (lordosisPitchGain != null)
                lordosisPitchGain.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.lordosisPitchGain.description"));
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
                vspineChestPitch.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.vspineChestPitchFrac.description"));

            var vspineChestRoll = PanelSlider.CreateAndBind(
                vspineParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.vspineChestRollFrac.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.VSpineChestRollFrac);
            if (vspineChestRoll != null)
                vspineChestRoll.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.vspineChestRollFrac.description"));

            var vspineSpinePitch = PanelSlider.CreateAndBind(
                vspineParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.vspineSpinePitchFrac.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.VSpineSpinePitchFrac);
            if (vspineSpinePitch != null)
                vspineSpinePitch.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.vspineSpinePitchFrac.description"));

            var vspineSpineRoll = PanelSlider.CreateAndBind(
                vspineParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.vspineSpineRollFrac.title"), 0f, 1f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.VSpineSpineRollFrac);
            if (vspineSpineRoll != null)
                vspineSpineRoll.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.vspineSpineRollFrac.description"));

            var vspineChestFwd = PanelSlider.CreateAndBind(
                vspineParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.vspineChestForwardBias.title"), -0.1f, 0.1f, false, 3, ValueDisplayMode.Raw),
                BasisSettingsDefaults.VSpineChestForwardBias);
            if (vspineChestFwd != null)
                vspineChestFwd.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.vspineChestForwardBias.description"));

            var vspineHipsDeadband = PanelSlider.CreateAndBind(
                vspineParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.vspineHipsYawDeadband.title"), 0f, 20f, false, 1, ValueDisplayMode.Raw),
                BasisSettingsDefaults.VSpineHipsYawDeadbandDeg);
            if (vspineHipsDeadband != null)
                vspineHipsDeadband.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.vspineHipsYawDeadband.description"));

            var vspineNeckRotSpeed = PanelSlider.CreateAndBind(
                vspineParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.vspineNeckRotationSpeed.title"), 0f, 100f, false, 1, ValueDisplayMode.Raw),
                BasisSettingsDefaults.VSpineNeckRotationSpeed);
            if (vspineNeckRotSpeed != null)
                vspineNeckRotSpeed.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.vspineNeckRotationSpeed.description"));

            var vspineChestRotSpeed = PanelSlider.CreateAndBind(
                vspineParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.vspineChestRotationSpeed.title"), 0f, 100f, false, 1, ValueDisplayMode.Raw),
                BasisSettingsDefaults.VSpineChestRotationSpeed);
            if (vspineChestRotSpeed != null)
                vspineChestRotSpeed.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.vspineChestRotationSpeed.description"));

            var vspineSpineRotSpeed = PanelSlider.CreateAndBind(
                vspineParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.vspineSpineRotationSpeed.title"), 0f, 100f, false, 1, ValueDisplayMode.Raw),
                BasisSettingsDefaults.VSpineSpineRotationSpeed);
            if (vspineSpineRotSpeed != null)
                vspineSpineRotSpeed.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.vspineSpineRotationSpeed.description"));

            var vspineHipsRotSpeed = PanelSlider.CreateAndBind(
                vspineParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.vspineHipsRotationSpeed.title"), 0f, 100f, false, 1, ValueDisplayMode.Raw),
                BasisSettingsDefaults.VSpineHipsRotationSpeed);
            if (vspineHipsRotSpeed != null)
                vspineHipsRotSpeed.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.vspineHipsRotationSpeed.description"));

            var vspineHipsFwd = PanelSlider.CreateAndBind(
                vspineParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.vspineHipsForwardBias.title"), -0.1f, 0.1f, false, 3, ValueDisplayMode.Raw),
                BasisSettingsDefaults.VSpineHipsForwardBias);
            if (vspineHipsFwd != null)
                vspineHipsFwd.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.vspineHipsForwardBias.description"));
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
                smoothingStrength.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.smoothingStrength.description"));

            var posHz = PanelSlider.CreateAndBind(
                smoothingParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.posSmoothingHz.title"), 0.01f, 60f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKPositionSmoothingHz);
            if (posHz != null)
                posHz.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.posSmoothingHz.description"));

            var rotHz = PanelSlider.CreateAndBind(
                smoothingParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.rotSmoothingHz.title"), 0.01f, 60f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKRotationSmoothingHz);
            if (rotHz != null)
                rotHz.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.rotSmoothingHz.description"));

            var minCutoff = PanelSlider.CreateAndBind(
                smoothingParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.minCutoff.title"), 0.1f, 10f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKMinCutoff);
            if (minCutoff != null)
                minCutoff.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.minCutoff.description"));

            var beta = PanelSlider.CreateAndBind(
                smoothingParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.beta.title"), 0f, 10f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKBeta);
            if (beta != null)
                beta.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.beta.description"));

            var derivativeCutoff = PanelSlider.CreateAndBind(
                smoothingParent,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.derivativeCutoff.title"), 0.1f, 10f, false, 2, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FBIKDerivativeCutoff);
            if (derivativeCutoff != null)
                derivativeCutoff.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.derivativeCutoff.description"));
        });

        // ONE RESET BUTTON FOR THIS PAGE
        SettingsProvider.AddResetPageButton(tabDesc.ContentParent, "Body Tracking", ResetIkDefaults);


        colliderGroup.gameObject.SetActive(BasisSettingsDefaults.FBIKAdvancedVisible.RawValue);
        advancedToggle.OnValueChanged += visible =>
        {
            colliderGroup.gameObject.SetActive(visible);
            tabDesc.ForceRebuild();
            colliderGroup.GetComponentInParent<PanelElementDescriptor>()?.ForceRebuild();
        };

        // ------------------
        // Debug Section
        // ------------------
        BuildDebugSection(tabDesc);

        tabDesc.ForceRebuild();
        return tabPage;
    }

    // ------------------
    // Debug Info
    // ------------------
    // One card per category. Each card's description holds all of its metrics as
    // "Label: value" lines, so the panel collapses from ~27 group cards to 6.
    private static readonly List<(string title, string[] labels, PanelElementDescriptor descriptor)> _debugCategories = new();

    private static void BuildDebugSection(PanelElementDescriptor tabDesc)
    {
        var debugToggle = PanelToggle.CreateNewEntry(tabDesc.ContentParent);
        debugToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.debugInfo"));
        debugToggle.Descriptor.SetDescription(BasisLocalization.Get("settings.bodyTracking.debugInfo.description"));

        var debugGroup = PanelElementDescriptor.CreateNew(
            PanelElementDescriptor.ElementStyles.Group,
            tabDesc.ContentParent);

        debugGroup.SetTitle(BasisLocalization.Get("settings.bodyTracking.heightDebug.title"));
        debugGroup.SetDescription(BasisLocalization.Get("settings.bodyTracking.heightDebug.description"));
        debugGroup.SetIcon(AddressableAssets.Sprites.Settings);

        var debugParent = debugGroup.ContentParent;

        _debugCategories.Clear();

        AddDebugCategory(debugParent, BasisLocalization.Get("settings.bodyTracking.debug.playerMetrics"),
            "Player Eye Height", "Player Arm Span", "Additional Player Height");

        AddDebugCategory(debugParent, BasisLocalization.Get("settings.bodyTracking.debug.avatarMetrics"),
            "Avatar Eye Height", "Avatar Arm Span");

        AddDebugCategory(debugParent, BasisLocalization.Get("settings.bodyTracking.debug.scaledHeights"),
            "Scaled Player Height", "Scaled Avatar Height");

        AddDebugCategory(debugParent, BasisLocalization.Get("settings.bodyTracking.debug.unscaledHeights"),
            "Unscaled Player Height", "Unscaled Avatar Height");

        AddDebugCategory(debugParent, BasisLocalization.Get("settings.bodyTracking.debug.ratiosScaling"),
            "Player to Avatar Ratio", "Avatar to Player Ratio", "Device Scale", "Applied Up Scale", "Scaled to Match Value");

        AddDebugCategory(debugParent, BasisLocalization.Get("settings.bodyTracking.debug.calibrationState"),
            "Height Mode", "Seated Mode", "Seated Height Delta", "Pitch Calibration Enabled", "Has Pitch Calibrated Height", "Pitch Calibrated Eye Height");

        var refreshButton = PanelButton.CreateNew(debugParent);
        refreshButton.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.refreshDebug"));
        refreshButton.OnClicked += RefreshDebugData;

        RefreshDebugData();

        debugGroup.gameObject.SetActive(false);
        debugToggle.SetValueWithoutNotify(false);
        debugToggle.OnValueChanged += visible =>
        {
            debugGroup.gameObject.SetActive(visible);
            if (visible)
            {
                RefreshDebugData();
            }
            tabDesc.ForceRebuild();
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
        "Additional Player Height" => $"{BasisHeightDriver.AdditionalPlayerHeight:F4} m",
        "Avatar Eye Height" => $"{BasisHeightDriver.AvatarEyeHeight:F4} m",
        "Avatar Arm Span" => $"{BasisHeightDriver.AvatarArmSpan:F4} m",
        "Scaled Player Height" => $"{BasisHeightDriver.SelectedScaledPlayerHeight:F4} m",
        "Scaled Avatar Height" => $"{BasisHeightDriver.SelectedScaledAvatarHeight:F4} m",
        "Unscaled Player Height" => $"{BasisHeightDriver.SelectedUnScaledPlayerHeight:F4} m",
        "Unscaled Avatar Height" => $"{BasisHeightDriver.SelectedUnScaledAvatarHeight:F4} m",
        "Player to Avatar Ratio" => $"{BasisHeightDriver.PlayerToAvatarRatioScaled:F4}",
        "Avatar to Player Ratio" => $"{BasisHeightDriver.AvatarToPlayerRatioScaled:F4}",
        "Device Scale" => $"{BasisHeightDriver.DeviceScale:F4}",
        "Applied Up Scale" => $"{BasisHeightDriver.AppliedUpScale:F4}",
        "Scaled to Match Value" => $"{BasisHeightDriver.ScaledToMatchValue:F4}",
        "Height Mode" => $"{SMModuleCalibration.HeightMode}",
        "Seated Mode" => SMModuleSitStand.IsSteatedMode ? "Seated" : "Standing",
        "Seated Height Delta" => $"{SMModuleSitStand.MissingHeightDelta:F4} m",
        "Pitch Calibration Enabled" => SMModuleCalibration.PitchCalibrationEnabled ? "Yes" : "No",
        "Has Pitch Calibrated Height" => BasisHeightDriver.HasPitchCalibratedHeight ? "Yes" : "No",
        "Pitch Calibrated Eye Height" => $"{BasisHeightDriver.PitchCalibratedEyeHeight:F4} m",
        _ => "--"
    };

    private static void ResetIkDefaults()
    {
        // Main IK / calibration controls
        BasisSettingsDefaults.SitStand.ResetToDefault();
        BasisSettingsDefaults.IKMode.ResetToDefault();
        BasisSettingsDefaults.IKLockMode.ResetToDefault();
        BasisSettingsDefaults.CustomScale.ResetToDefault();
        BasisSettingsDefaults.SelectedScale.ResetToDefault();

        // Global One Euro / smoothing parameters
        BasisSettingsDefaults.FBIKSmoothingStrength.ResetToDefault();
        BasisSettingsDefaults.FBIKMinCutoff.ResetToDefault();
        BasisSettingsDefaults.FBIKBeta.ResetToDefault();
        BasisSettingsDefaults.FBIKDerivativeCutoff.ResetToDefault();
        BasisSettingsDefaults.FBIKPositionSmoothingHz.ResetToDefault();
        BasisSettingsDefaults.FBIKRotationSmoothingHz.ResetToDefault();

        // Bone selection UI state (optional, but usually desired)
        BasisSettingsDefaults.SelectedBone.ResetToDefault();

        // If you have master toggles / global helpers:
        // This binding is set by SyncMasterEuroFromChildren(), but reset it anyway.
        BasisSettingsDefaults.FBIKEuroAll.ResetToDefault();

        // IK Collider & Tuning
        BasisSettingsDefaults.FBIKAdvancedVisible.ResetToDefault();
        BasisSettingsDefaults.FBIKCollisionsEnabled.ResetToDefault();
        BasisSettingsDefaults.FootIKEnabled.ResetToDefault();
        BasisSettingsDefaults.DisableAnimationsInFBT.ResetToDefault();
        BasisSettingsDefaults.FBIKProtectElbow.ResetToDefault();
        BasisSettingsDefaults.FBIKUseHandCapsule.ResetToDefault();
        BasisSettingsDefaults.FBIKChestRadius.ResetToDefault();
        BasisSettingsDefaults.FBIKCollisionSkin.ResetToDefault();
        BasisSettingsDefaults.FBIKHandRadius.ResetToDefault();
        BasisSettingsDefaults.FBIKHandSkin.ResetToDefault();
        BasisSettingsDefaults.FBIKShoulderSolveEnabled.ResetToDefault();
        BasisSettingsDefaults.FBIKShoulderElevation.ResetToDefault();
        BasisSettingsDefaults.FBIKShoulderProtraction.ResetToDefault();
        BasisSettingsDefaults.FBIKMaxBendDeg.ResetToDefault();
        BasisSettingsDefaults.FBIKStruggleStart.ResetToDefault();
        BasisSettingsDefaults.FBIKStruggleEnd.ResetToDefault();
        BasisSettingsDefaults.FBIKMaxChestDelta.ResetToDefault();
        BasisSettingsDefaults.FBIKMaxHipDelta.ResetToDefault();
        BasisSettingsDefaults.FBIKSpineBendPitch.ResetToDefault();
        BasisSettingsDefaults.FBIKSpineBendYaw.ResetToDefault();
        BasisSettingsDefaults.FBIKSpineBendRoll.ResetToDefault();
        BasisSettingsDefaults.FBIKUpperChestBendPitch.ResetToDefault();
        BasisSettingsDefaults.FBIKUpperChestBendYaw.ResetToDefault();
        BasisSettingsDefaults.FBIKUpperChestBendRoll.ResetToDefault();
        BasisSettingsDefaults.FBIKHipHingeStartDeg.ResetToDefault();
        BasisSettingsDefaults.FBIKHipHingeMaxAddDeg.ResetToDefault();
        BasisSettingsDefaults.FBIKChestSpringHz.ResetToDefault();
        BasisSettingsDefaults.FBIKChestSpringDamping.ResetToDefault();
        BasisSettingsDefaults.FBIKChestSpringHeadVelGain.ResetToDefault();
        BasisSettingsDefaults.FBIKLordosisPitchGainDeg.ResetToDefault();
        BasisSettingsDefaults.VSpineChestPitchFrac.ResetToDefault();
        BasisSettingsDefaults.VSpineChestRollFrac.ResetToDefault();
        BasisSettingsDefaults.VSpineSpinePitchFrac.ResetToDefault();
        BasisSettingsDefaults.VSpineSpineRollFrac.ResetToDefault();
        BasisSettingsDefaults.VSpineChestForwardBias.ResetToDefault();
        BasisSettingsDefaults.VSpineHipsYawDeadbandDeg.ResetToDefault();
        BasisSettingsDefaults.VSpineNeckRotationSpeed.ResetToDefault();
        BasisSettingsDefaults.VSpineChestRotationSpeed.ResetToDefault();
        BasisSettingsDefaults.VSpineSpineRotationSpeed.ResetToDefault();
        BasisSettingsDefaults.VSpineHipsRotationSpeed.ResetToDefault();
        BasisSettingsDefaults.VSpineHipsForwardBias.ResetToDefault();
        BasisSettingsDefaults.FBIKSpineMaxForwardDeg.ResetToDefault();
        BasisSettingsDefaults.FBIKSpineMaxBackwardDeg.ResetToDefault();
        BasisSettingsDefaults.FBIKSpineMaxLateralDeg.ResetToDefault();
        BasisSettingsDefaults.FBIKSpineSquishBoost.ResetToDefault();
        BasisSettingsDefaults.FBIKChestArmSwingFactor.ResetToDefault();
        BasisSettingsDefaults.FBIKChestArmSwingMaxDeg.ResetToDefault();
        BasisSettingsDefaults.FBIKLowerArmTwistFraction.ResetToDefault();
        BasisSettingsDefaults.FBIKUpperArmTwistFraction.ResetToDefault();
        BasisSettingsDefaults.FBIKAnatDifferentialStiffness.ResetToDefault();
        BasisSettingsDefaults.FBIKAnatShoulderSlide.ResetToDefault();
        BasisSettingsDefaults.FBIKAnatCervicalLordosis.ResetToDefault();
        BasisSettingsDefaults.FBIKAnatPelvicTwistRouting.ResetToDefault();

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

        // Refresh the editor bindings + derived master state + interactables
        RebindBoneEditor();
        EvaluateInteractables();
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
        boneSelectGroup.SetTitle("Per-Bone Settings");
        boneSelectGroup.SetDescription(
            "Pick a bone to inspect or tune. The toggles and sliders below apply only " +
            "to the bone you select here — switch bones to see each one's settings."
        );

        var boneNames = _bones.Select(b => b.Name).ToList();
        _boneDropdown = PanelDropdown.CreateNewEntry(boneSelectGroup.ContentParent);
        _boneDropdown.Descriptor.SetTitle("Bone");
        _boneDropdown.AssignEntries(boneNames);
        _boneDropdown.AssignBinding(BasisSettingsDefaults.SelectedBone);
        _boneDropdown.Descriptor.SetDescription("Select which bone’s smoothing and filtering settings are shown below.");
        _boneDropdown.OnValueChanged += _ => RebindBoneEditor();

        _boneEuroEditorGroup = PanelElementDescriptor.CreateNew(
            PanelElementDescriptor.ElementStyles.Group,
            parent);
        _boneEuroEditorGroup.SetTitle("Calibration & Smoothing");
        _boneEuroEditorGroup.SetDescription(
            "Controls for the selected bone. Use For Calibration decides whether trackers " +
            "can be assigned to this role during full-body calibration; the smoothing and " +
            "Euro filter toggles below shape how the bone reacts to incoming motion."
        );

        _uiUseCalibration = PanelToggle.CreateNewEntry(_boneEuroEditorGroup.ContentParent);
        _uiUseCalibration.Descriptor.SetTitle("Use For Calibration");
        _uiUseCalibration.Descriptor.SetDescription(
            "When enabled, this role participates in full-body tracker calibration. " +
            "Disable to keep trackers from being assigned to it during the constellation pass."
        );

        _uiSmoothPos = PanelToggle.CreateNewEntry(_boneEuroEditorGroup.ContentParent);
        _uiSmoothPos.Descriptor.SetTitle("Smooth Position");
        _uiSmoothPos.Descriptor.SetDescription("Blends this bone’s position over time to reduce jitter.");

        _uiSmoothRot = PanelToggle.CreateNewEntry(_boneEuroEditorGroup.ContentParent);
        _uiSmoothRot.Descriptor.SetTitle("Smooth Rotation");
        _uiSmoothRot.Descriptor.SetDescription("Blends this bone’s rotation over time to reduce wobble.");

        _uiEuroPos = PanelToggle.CreateNewEntry(_boneEuroEditorGroup.ContentParent);
        _uiEuroPos.Descriptor.SetTitle("Euro Filtering (Position)");
        _uiEuroPos.Descriptor.SetDescription("Steady at rest with minimal lag during motion. ");

        _uiEuroRot = PanelToggle.CreateNewEntry(_boneEuroEditorGroup.ContentParent);
        _uiEuroRot.Descriptor.SetTitle("Euro Filtering (Rotation)");
        _uiEuroRot.Descriptor.SetDescription("Reduces micro-wobble while remaining responsive.");

        _uiCalibSphereScale = PanelSlider.CreateAndBind(
            _boneEuroEditorGroup.ContentParent,
            PanelSlider.SliderSettings.Advanced("Calibration Sphere Scale", 0.1f, 5f, false, 2, ValueDisplayMode.Raw),
            BasisSettingsDefaults.CalibSphereScaleHips);

        if (_uiCalibSphereScale != null)
        {
            _uiCalibSphereScale.Descriptor.SetDescription(
                "Adjusts the calibration sphere size for this bone. " +
                "Larger spheres make it easier for trackers to attach during calibration. " +
                "1.0 = default size."
            );
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

        _boneEuroEditorGroup.ForceRebuild();

        SyncMasterEuroFromChildren();
    }

    private static void SyncMasterEuroFromChildren()
    {
        if (_bones.Count == 0)
            return;

        bool allOn = _bones.All(b => b.EuroPos.RawValue && b.EuroRot.RawValue);
        BasisSettingsDefaults.FBIKEuroAll.SetValue(allOn);
    }

    private static void AddExperimentalToggle(RectTransform parent, BasisSettingsBinding<bool> binding, string titleKey, string descriptionKey)
    {
        var toggle = PanelToggle.CreateNewEntry(parent);
        toggle.Descriptor.SetTitle(BasisLocalization.Get(titleKey));
        toggle.Descriptor.SetDescription(BasisLocalization.Get(descriptionKey));
        toggle.AssignBinding(binding);
    }

    private static void CreateCollapsibleSection(PanelElementDescriptor tabDesc, PanelElementDescriptor parentGroup, string title, string description, bool defaultOpen, Action<RectTransform> addContent)
    {
        var parent = parentGroup.ContentParent;

        var sectionToggle = PanelToggle.CreateNewEntry(parent);
        sectionToggle.Descriptor.SetTitle(title);
        sectionToggle.Descriptor.SetDescription(description);

        var sectionGroup = PanelElementDescriptor.CreateNew(
            PanelElementDescriptor.ElementStyles.Group,
            parent);
        sectionGroup.SetTitle(title);
        sectionGroup.SetDescription(description);
        sectionGroup.SetIcon(AddressableAssets.Sprites.Settings);

        // Add content while the group is still active so child component Awake/Start runs and
        // their text initializes. SetActive(false) before attach would orphan their lifecycle.
        addContent(sectionGroup.ContentParent);

        sectionGroup.gameObject.SetActive(defaultOpen);
        sectionToggle.SetValueWithoutNotify(defaultOpen);

        sectionToggle.OnValueChanged += visible =>
        {
            sectionGroup.gameObject.SetActive(visible);
            tabDesc.ForceRebuild();
            parentGroup.ForceRebuild();
        };
    }

    private static void EvaluateInteractables()
    {
        if (dropdownSeatedMode == null || dropdownIKMode == null)
            return;

        bool isSeated = GetCurrentText(dropdownSeatedMode) == SeatedMode_Seated;
        SetDropdownInteractable(dropdownIKMode, !isSeated);
    }

    private static string GetCurrentText(PanelDropdown dd)
        => dd.DropdownComponent.options[dd.DropdownComponent.value].text;

    private static void SetDropdownInteractable(PanelDropdown dd, bool interactable)
        => dd.DropdownComponent.interactable = interactable;
}
