using Basis;
using Basis.BasisUI;
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

    private static PanelToggle _uiSmoothPos;
    private static PanelToggle _uiSmoothRot;
    private static PanelToggle _uiEuroPos;
    private static PanelToggle _uiEuroRot;
    private static PanelSlider _uiCalibSphereScale;
    private static PanelElementDescriptor _boneEditorGroup;
    private static PanelElementDescriptor _boneEuroEditorGroup;

    private struct BoneBindings
    {
        public string Name;
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
        tabDesc.SetTitle("Body Tracking");
        tabDesc.SetIcon(AddressableAssets.Sprites.Settings);

        // --- Group: "Body Tracking" (replaces tab.Group(...)) ---
        var ikGroup = PanelElementDescriptor.CreateNew(
            PanelElementDescriptor.ElementStyles.Group,
            tabDesc.ContentParent);

        ikGroup.SetTitle("Body Tracking");
        ikGroup.SetDescription("Fine-tuning for avatar scaling, calibration, and IK smoothing");
        ikGroup.SetIcon(AddressableAssets.Sprites.Settings);

        var ikParent = ikGroup.ContentParent;

        // --- Seated Mode dropdown ---
        dropdownSeatedMode = PanelDropdown.CreateNewEntry(ikParent);
        dropdownSeatedMode.Descriptor.SetTitle("Seated / Standing Mode");
        dropdownSeatedMode.Descriptor.SetDescription(
            "Select the reference pose used for body scaling"
        );
        dropdownSeatedMode.AssignEntries(new List<string> { SeatedMode_Standing, SeatedMode_Seated });
        dropdownSeatedMode.AssignBinding(BasisSettingsDefaults.SitStand);

        // --- IK mode dropdown ---
        dropdownIKMode = PanelDropdown.CreateNewEntry(ikParent);
        dropdownIKMode.Descriptor.SetTitle("Full Body IK Mode");
        dropdownIKMode.AssignEntries(new List<string> { "Eye Height", "Arm Distance" });
        dropdownIKMode.AssignBinding(BasisSettingsDefaults.IKMode);
        dropdownIKMode.Descriptor.SetDescription(
            "Determines how body scale is calculated."
        );

        // --- IK Lock Mode dropdown ---
        dropdownIKLockMode = PanelDropdown.CreateNewEntry(ikParent);
        dropdownIKLockMode.Descriptor.SetTitle("Spine Lock Mode");
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
        customScaleToggle.Descriptor.SetTitle("Custom Scale");
        customScaleToggle.AssignBinding(BasisSettingsDefaults.CustomScale);
        customScaleToggle.Descriptor.SetDescription("Enables manual override of automatic body scaling.");

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
        }

        dropdownIKMode.OnValueChanged += _ => EvaluateInteractables();
        dropdownSeatedMode.OnValueChanged += _ => EvaluateInteractables();
        EvaluateInteractables();

        // ------------------
        // One Euro (Global)
        // ------------------
        var smoothingStrength = PanelSlider.CreateAndBind(
            ikParent,
            PanelSlider.SliderSettings.Advanced("Smoothing Strength", 1f, 100f, false, 1, ValueDisplayMode.Raw),
            BasisSettingsDefaults.FBIKSmoothingStrength);

        if (smoothingStrength != null)
        {
            smoothingStrength.Descriptor.SetDescription(
                "Global multiplier for all smoothing filters.\n\n" +
                "1x = default. Higher values greatly increase smoothing but add latency.\n" +
                "WARNING: Values above 10x may cause noticeable input delay."
            );
        }

        var minCutoff = PanelSlider.CreateAndBind(
            ikParent,
            PanelSlider.SliderSettings.Advanced("Min Cutoff", 0.1f, 10f, false, 2, ValueDisplayMode.Raw),
            BasisSettingsDefaults.FBIKMinCutoff);

        if (minCutoff != null)
        {
            minCutoff.Descriptor.SetDescription(
                "Controls smoothing strength when movement is very small.\n\n" +
                "Higher values make the avatar steadier when still, but slower to start moving."
            );
        }

        var beta = PanelSlider.CreateAndBind(
            ikParent,
            PanelSlider.SliderSettings.Advanced("Beta", 0f, 10f, false, 2, ValueDisplayMode.Raw),
            BasisSettingsDefaults.FBIKBeta);

        if (beta != null)
        {
            beta.Descriptor.SetDescription(
                "Controls how aggressively smoothing is reduced during fast motion.\n\n" +
                "Higher values reduce lag during quick movement, but may reintroduce jitter."
            );
        }

        var derivativeCutoff = PanelSlider.CreateAndBind(
            ikParent,
            PanelSlider.SliderSettings.Advanced("Derivative Cutoff", 0.1f, 10f, false, 2, ValueDisplayMode.Raw),
            BasisSettingsDefaults.FBIKDerivativeCutoff);

        if (derivativeCutoff != null)
        {
            derivativeCutoff.Descriptor.SetDescription(
                "Controls how much motion speed affects smoothing behavior.\n\n" +
                "Lower values are steadier; higher values feel more responsive but noisier."
            );
        }

        var posHz = PanelSlider.CreateAndBind(
            ikParent,
            PanelSlider.SliderSettings.Advanced("Position Smoothing (Hz)", 0.01f, 60f, false, 2, ValueDisplayMode.Raw),
            BasisSettingsDefaults.FBIKPositionSmoothingHz);

        if (posHz != null)
        {
            posHz.Descriptor.SetDescription(
                "Global position smoothing frequency.\n\n" +
                "Lower Hz increases smoothing and latency. Higher Hz feels more immediate but may jitter."
            );
        }

        var rotHz = PanelSlider.CreateAndBind(
            ikParent,
            PanelSlider.SliderSettings.Advanced("Rotation Smoothing (Hz)", 0.01f, 60f, false, 2, ValueDisplayMode.Raw),
            BasisSettingsDefaults.FBIKRotationSmoothingHz);

        if (rotHz != null)
        {
            rotHz.Descriptor.SetDescription(
                "Global rotation smoothing frequency.\n\n" +
                "Lower Hz reduces micro-wobble but adds delay. Higher Hz feels snappier but may shimmer."
            );
        }

        _trackerLerpToggleUIs.Clear();
        _euroToggleUIs.Clear();

        AddFBIKTogglesCompact(ikParent);

        SyncMasterEuroFromChildren();

        // ------------------
        // Advanced IK toggle
        // ------------------
        var advancedToggle = PanelToggle.CreateNewEntry(tabDesc.ContentParent);
        advancedToggle.Descriptor.SetTitle("Advanced IK Settings");
        advancedToggle.Descriptor.SetDescription("Show advanced collider, shoulder, and spine tuning parameters.");
        advancedToggle.AssignBinding(BasisSettingsDefaults.FBIKAdvancedVisible);

        var colliderGroup = PanelElementDescriptor.CreateNew(
            PanelElementDescriptor.ElementStyles.Group,
            tabDesc.ContentParent);

        colliderGroup.SetTitle("IK Colliders & Tuning");
        colliderGroup.SetDescription("Controls for IK collision detection and spine/shoulder tuning parameters");
        colliderGroup.SetIcon(AddressableAssets.Sprites.Settings);

        var colliderParent = colliderGroup.ContentParent;

        // --- Collider toggles ---
        var collisionsToggle = PanelToggle.CreateNewEntry(colliderParent);
        collisionsToggle.Descriptor.SetTitle("Collisions Enabled");
        collisionsToggle.AssignBinding(BasisSettingsDefaults.FBIKCollisionsEnabled);
        collisionsToggle.Descriptor.SetDescription("Enables virtual capsule collision between elbows and chest to prevent arm clipping through the body.");

        var protectElbowToggle = PanelToggle.CreateNewEntry(colliderParent);
        protectElbowToggle.Descriptor.SetTitle("Protect Elbow");
        protectElbowToggle.AssignBinding(BasisSettingsDefaults.FBIKProtectElbow);
        protectElbowToggle.Descriptor.SetDescription("Pushes elbows outward when they penetrate the chest capsule.");

        var handCapsuleToggle = PanelToggle.CreateNewEntry(colliderParent);
        handCapsuleToggle.Descriptor.SetTitle("Use Hand Capsule");
        handCapsuleToggle.AssignBinding(BasisSettingsDefaults.FBIKUseHandCapsule);
        handCapsuleToggle.Descriptor.SetDescription("Enables the hand collision capsule for IK solving.");

        // --- Collider size sliders ---
        var chestRadiusSlider = PanelSlider.CreateAndBind(
            colliderParent,
            PanelSlider.SliderSettings.Advanced("Chest Radius", 0.01f, 0.5f, false, 3, ValueDisplayMode.Raw),
            BasisSettingsDefaults.FBIKChestRadius);
        if (chestRadiusSlider != null)
            chestRadiusSlider.Descriptor.SetDescription("Radius of the virtual chest capsule used for elbow collision. Default: 0.18");

        var collisionSkinSlider = PanelSlider.CreateAndBind(
            colliderParent,
            PanelSlider.SliderSettings.Advanced("Collision Skin", 0f, 0.1f, false, 3, ValueDisplayMode.Raw),
            BasisSettingsDefaults.FBIKCollisionSkin);
        if (collisionSkinSlider != null)
            collisionSkinSlider.Descriptor.SetDescription("Extra buffer padding around the chest capsule. Default: 0.02");

        var handRadiusSlider = PanelSlider.CreateAndBind(
            colliderParent,
            PanelSlider.SliderSettings.Advanced("Hand Radius", 0f, 0.2f, false, 3, ValueDisplayMode.Raw),
            BasisSettingsDefaults.FBIKHandRadius);
        if (handRadiusSlider != null)
            handRadiusSlider.Descriptor.SetDescription("Radius of the virtual hand target capsule. Default: 0.05");

        var handSkinSlider = PanelSlider.CreateAndBind(
            colliderParent,
            PanelSlider.SliderSettings.Advanced("Hand Skin", 0f, 0.1f, false, 3, ValueDisplayMode.Raw),
            BasisSettingsDefaults.FBIKHandSkin);
        if (handSkinSlider != null)
            handSkinSlider.Descriptor.SetDescription("Extra buffer around hand capsule. Default: 0.01");

        // --- Shoulder ---
        var shoulderSolveToggle = PanelToggle.CreateNewEntry(colliderParent);
        shoulderSolveToggle.Descriptor.SetTitle("Shoulder Pre-Solve");
        shoulderSolveToggle.AssignBinding(BasisSettingsDefaults.FBIKShoulderSolveEnabled);
        shoulderSolveToggle.Descriptor.SetDescription("Raises and protracts shoulders based on hand target position before arm IK runs.");

        var shoulderElevSlider = PanelSlider.CreateAndBind(
            colliderParent,
            PanelSlider.SliderSettings.Advanced("Shoulder Elevation", 0f, 1f, false, 2, ValueDisplayMode.Raw),
            BasisSettingsDefaults.FBIKShoulderElevation);
        if (shoulderElevSlider != null)
            shoulderElevSlider.Descriptor.SetDescription("How much the shoulder raises when the hand is above it. Default: 0.4");

        var shoulderProtSlider = PanelSlider.CreateAndBind(
            colliderParent,
            PanelSlider.SliderSettings.Advanced("Shoulder Protraction", 0f, 1f, false, 2, ValueDisplayMode.Raw),
            BasisSettingsDefaults.FBIKShoulderProtraction);
        if (shoulderProtSlider != null)
            shoulderProtSlider.Descriptor.SetDescription("How much the shoulder moves forward when the hand reaches out. Default: 0.3");

        // --- Spine tuning ---
        var maxBendSlider = PanelSlider.CreateAndBind(
            colliderParent,
            PanelSlider.SliderSettings.Advanced("Max Spine Bend (deg)", 0f, 180f, false, 0, ValueDisplayMode.Raw),
            BasisSettingsDefaults.FBIKMaxBendDeg);
        if (maxBendSlider != null)
            maxBendSlider.Descriptor.SetDescription("Maximum bend angle for the spine chain. Default: 90");

        var struggleStartSlider = PanelSlider.CreateAndBind(
            colliderParent,
            PanelSlider.SliderSettings.Advanced("Struggle Start", 0f, 1f, false, 2, ValueDisplayMode.Raw),
            BasisSettingsDefaults.FBIKStruggleStart);
        if (struggleStartSlider != null)
            struggleStartSlider.Descriptor.SetDescription("Arm extension ratio where struggle blending begins. Default: 0.9");

        var struggleEndSlider = PanelSlider.CreateAndBind(
            colliderParent,
            PanelSlider.SliderSettings.Advanced("Struggle End", 0f, 1f, false, 2, ValueDisplayMode.Raw),
            BasisSettingsDefaults.FBIKStruggleEnd);
        if (struggleEndSlider != null)
            struggleEndSlider.Descriptor.SetDescription("Arm extension ratio where struggle is fully applied. Default: 1.0");

        var maxChestDeltaSlider = PanelSlider.CreateAndBind(
            colliderParent,
            PanelSlider.SliderSettings.Advanced("Max Chest Delta (deg)", 0f, 180f, false, 0, ValueDisplayMode.Raw),
            BasisSettingsDefaults.FBIKMaxChestDelta);
        if (maxChestDeltaSlider != null)
            maxChestDeltaSlider.Descriptor.SetDescription("Maximum rotation delta allowed for the chest bone per frame. Default: 90");

        var maxHipDeltaSlider = PanelSlider.CreateAndBind(
            colliderParent,
            PanelSlider.SliderSettings.Advanced("Max Hip Delta (deg)", 0f, 180f, false, 0, ValueDisplayMode.Raw),
            BasisSettingsDefaults.FBIKMaxHipDelta);
        if (maxHipDeltaSlider != null)
            maxHipDeltaSlider.Descriptor.SetDescription("Maximum rotation delta allowed for the hip bone per frame. Default: 90");

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
    private static readonly List<(string label, PanelElementDescriptor descriptor)> _debugEntries = new();

    private static void BuildDebugSection(PanelElementDescriptor tabDesc)
    {
        var debugToggle = PanelToggle.CreateNewEntry(tabDesc.ContentParent);
        debugToggle.Descriptor.SetTitle("Debug Info");
        debugToggle.Descriptor.SetDescription("Show live height, scaling, and calibration data.");

        var debugGroup = PanelElementDescriptor.CreateNew(
            PanelElementDescriptor.ElementStyles.Group,
            tabDesc.ContentParent);

        debugGroup.SetTitle("Height & Calibration Debug");
        debugGroup.SetDescription("Current values from the height driver and calibration system.");
        debugGroup.SetIcon(AddressableAssets.Sprites.Settings);

        var debugParent = debugGroup.ContentParent;

        _debugEntries.Clear();

        // Player Metrics
        AddDebugHeader(debugParent, "Player Metrics");
        AddDebugEntry(debugParent, "Player Eye Height");
        AddDebugEntry(debugParent, "Player Arm Span");
        AddDebugEntry(debugParent, "Additional Player Height");

        // Avatar Metrics
        AddDebugHeader(debugParent, "Avatar Metrics");
        AddDebugEntry(debugParent, "Avatar Eye Height");
        AddDebugEntry(debugParent, "Avatar Arm Span");

        // Scaled Heights
        AddDebugHeader(debugParent, "Scaled Heights");
        AddDebugEntry(debugParent, "Scaled Player Height");
        AddDebugEntry(debugParent, "Scaled Avatar Height");

        // Unscaled Heights
        AddDebugHeader(debugParent, "Unscaled Heights");
        AddDebugEntry(debugParent, "Unscaled Player Height");
        AddDebugEntry(debugParent, "Unscaled Avatar Height");

        // Ratios & Scaling
        AddDebugHeader(debugParent, "Ratios & Scaling");
        AddDebugEntry(debugParent, "Player to Avatar Ratio");
        AddDebugEntry(debugParent, "Avatar to Player Ratio");
        AddDebugEntry(debugParent, "Device Scale");
        AddDebugEntry(debugParent, "Applied Up Scale");
        AddDebugEntry(debugParent, "Scaled to Match Value");

        // Calibration State
        AddDebugHeader(debugParent, "Calibration State");
        AddDebugEntry(debugParent, "Height Mode");
        AddDebugEntry(debugParent, "Seated Mode");
        AddDebugEntry(debugParent, "Seated Height Delta");
        AddDebugEntry(debugParent, "Pitch Calibration Enabled");
        AddDebugEntry(debugParent, "Has Pitch Calibrated Height");
        AddDebugEntry(debugParent, "Pitch Calibrated Eye Height");

        // Refresh button
        var refreshButton = PanelButton.CreateNew(debugParent);
        refreshButton.Descriptor.SetTitle("Refresh Debug Data");
        refreshButton.OnClicked += RefreshDebugData;

        RefreshDebugData();

        // Start hidden, toggle controls visibility
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

    private static void AddDebugHeader(RectTransform parent, string title)
    {
        var header = PanelElementDescriptor.CreateNew(
            PanelElementDescriptor.ElementStyles.Group,
            parent);
        header.SetTitle(title);
        header.SetDescription("");
    }

    private static void AddDebugEntry(RectTransform parent, string label)
    {
        var entry = PanelElementDescriptor.CreateNew(
            PanelElementDescriptor.ElementStyles.Group,
            parent);
        entry.SetTitle(label);
        entry.SetDescription("--");
        _debugEntries.Add((label, entry));
    }

    private static void RefreshDebugData()
    {
        foreach (var (label, descriptor) in _debugEntries)
        {
            string value = label switch
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

            descriptor.SetDescription(value);
        }
    }

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

        // Per-bone toggles and calibration sphere scale
        foreach (var b in _bones)
        {
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
            BasisSettingsBinding<bool> smoothPos,
            BasisSettingsBinding<bool> smoothRot,
            BasisSettingsBinding<bool> euroPos,
            BasisSettingsBinding<bool> euroRot,
            BasisSettingsBinding<float> calibSphereScale)[]
        {
            ("Hips", BasisSettingsDefaults.FBIKHipsSmoothPos, BasisSettingsDefaults.FBIKHipsSmoothRot, BasisSettingsDefaults.FBIKHipsEuroPos, BasisSettingsDefaults.FBIKHipsEuroRot, BasisSettingsDefaults.CalibSphereScaleHips),
            ("Head", BasisSettingsDefaults.FBIKHeadSmoothPos, BasisSettingsDefaults.FBIKHeadSmoothRot, BasisSettingsDefaults.FBIKHeadEuroPos, BasisSettingsDefaults.FBIKHeadEuroRot, null),
            ("Left Foot", BasisSettingsDefaults.FBIKLeftFootSmoothPos, BasisSettingsDefaults.FBIKLeftFootSmoothRot, BasisSettingsDefaults.FBIKLeftFootEuroPos, BasisSettingsDefaults.FBIKLeftFootEuroRot, BasisSettingsDefaults.CalibSphereScaleLeftFoot),
            ("Right Foot", BasisSettingsDefaults.FBIKRightFootSmoothPos, BasisSettingsDefaults.FBIKRightFootSmoothRot, BasisSettingsDefaults.FBIKRightFootEuroPos, BasisSettingsDefaults.FBIKRightFootEuroRot, BasisSettingsDefaults.CalibSphereScaleRightFoot),
            ("Chest", BasisSettingsDefaults.FBIKChestSmoothPos, BasisSettingsDefaults.FBIKChestSmoothRot, BasisSettingsDefaults.FBIKChestEuroPos, BasisSettingsDefaults.FBIKChestEuroRot, BasisSettingsDefaults.CalibSphereScaleChest),
            ("Left Lower Leg", BasisSettingsDefaults.FBIKLeftLowerLegSmoothPos, BasisSettingsDefaults.FBIKLeftLowerLegSmoothRot, BasisSettingsDefaults.FBIKLeftLowerLegEuroPos, BasisSettingsDefaults.FBIKLeftLowerLegEuroRot, BasisSettingsDefaults.CalibSphereScaleLeftLowerLeg),
            ("Right Lower Leg", BasisSettingsDefaults.FBIKRightLowerLegSmoothPos, BasisSettingsDefaults.FBIKRightLowerLegSmoothRot, BasisSettingsDefaults.FBIKRightLowerLegEuroPos, BasisSettingsDefaults.FBIKRightLowerLegEuroRot, BasisSettingsDefaults.CalibSphereScaleRightLowerLeg),
            ("Left Hand", BasisSettingsDefaults.FBIKLeftHandSmoothPos, BasisSettingsDefaults.FBIKLeftHandSmoothRot, BasisSettingsDefaults.FBIKLeftHandEuroPos, BasisSettingsDefaults.FBIKLeftHandEuroRot, BasisSettingsDefaults.CalibSphereScaleLeftHand),
            ("Right Hand", BasisSettingsDefaults.FBIKRightHandSmoothPos, BasisSettingsDefaults.FBIKRightHandSmoothRot, BasisSettingsDefaults.FBIKRightHandEuroPos, BasisSettingsDefaults.FBIKRightHandEuroRot, BasisSettingsDefaults.CalibSphereScaleRightHand),
            ("Left Lower Arm", BasisSettingsDefaults.FBIKLeftLowerArmSmoothPos, BasisSettingsDefaults.FBIKLeftLowerArmSmoothRot, BasisSettingsDefaults.FBIKLeftLowerArmEuroPos, BasisSettingsDefaults.FBIKLeftLowerArmEuroRot, BasisSettingsDefaults.CalibSphereScaleLeftLowerArm),
            ("Right Lower Arm", BasisSettingsDefaults.FBIKRightLowerArmSmoothPos, BasisSettingsDefaults.FBIKRightLowerArmSmoothRot, BasisSettingsDefaults.FBIKRightLowerArmEuroPos, BasisSettingsDefaults.FBIKRightLowerArmEuroRot, BasisSettingsDefaults.CalibSphereScaleRightLowerArm),
            ("Left Toe", BasisSettingsDefaults.FBIKLeftToeSmoothPos, BasisSettingsDefaults.FBIKLeftToeSmoothRot, BasisSettingsDefaults.FBIKLeftToeEuroPos, BasisSettingsDefaults.FBIKLeftToeEuroRot, BasisSettingsDefaults.CalibSphereScaleLeftToes),
            ("Right Toe", BasisSettingsDefaults.FBIKRightToeSmoothPos, BasisSettingsDefaults.FBIKRightToeSmoothRot, BasisSettingsDefaults.FBIKRightToeEuroPos, BasisSettingsDefaults.FBIKRightToeEuroRot, BasisSettingsDefaults.CalibSphereScaleRightToes),
            ("Left Shoulder", BasisSettingsDefaults.FBIKLeftShoulderSmoothPos, BasisSettingsDefaults.FBIKLeftShoulderSmoothRot, BasisSettingsDefaults.FBIKLeftShoulderEuroPos, BasisSettingsDefaults.FBIKLeftShoulderEuroRot, BasisSettingsDefaults.CalibSphereScaleLeftShoulder),
            ("Right Shoulder", BasisSettingsDefaults.FBIKRightShoulderSmoothPos, BasisSettingsDefaults.FBIKRightShoulderSmoothRot, BasisSettingsDefaults.FBIKRightShoulderEuroPos, BasisSettingsDefaults.FBIKRightShoulderEuroRot, BasisSettingsDefaults.CalibSphereScaleRightShoulder),
        };

        _bones.Clear();
        foreach (var b in blocks)
        {
            _bones.Add(new BoneBindings
            {
                Name = b.name,
                SmoothPos = b.smoothPos,
                SmoothRot = b.smoothRot,
                EuroPos = b.euroPos,
                EuroRot = b.euroRot,
                CalibSphereScale = b.calibSphereScale
            });
        }

        var boneNames = _bones.Select(b => b.Name).ToList();
        _boneDropdown = PanelDropdown.CreateNewEntry(parent);
        _boneDropdown.Descriptor.SetTitle("Bone");
        _boneDropdown.AssignEntries(boneNames);
        _boneDropdown.AssignBinding(BasisSettingsDefaults.SelectedBone);
        _boneDropdown.Descriptor.SetDescription("Select which bone’s smoothing and filtering settings are shown below.");
        _boneDropdown.OnValueChanged += _ => RebindBoneEditor();

        _boneEditorGroup = PanelElementDescriptor.CreateNew(
            PanelElementDescriptor.ElementStyles.Group,
            parent);

        _boneEditorGroup.SetTitle("Bone Smoothing");
        _boneEditorGroup.SetDescription("Reduces jitter but always adds a small amount of delay.");

        _boneEuroEditorGroup = PanelElementDescriptor.CreateNew(
            PanelElementDescriptor.ElementStyles.Group,
            parent);

        _boneEuroEditorGroup.SetTitle("Bone Filtering (One Euro)");
        _boneEuroEditorGroup.SetDescription(
            "Adaptive smoothing that changes based on motion speed. " +
            "Stable when still, responsive during fast movement."
        );

        _uiSmoothPos = PanelToggle.CreateNewEntry(_boneEditorGroup.ContentParent);
        _uiSmoothPos.Descriptor.SetTitle("Smooth Position");
        _uiSmoothPos.Descriptor.SetDescription("Blends this bone’s position over time to reduce jitter.");

        _uiSmoothRot = PanelToggle.CreateNewEntry(_boneEditorGroup.ContentParent);
        _uiSmoothRot.Descriptor.SetTitle("Smooth Rotation");
        _uiSmoothRot.Descriptor.SetDescription("Blends this bone’s rotation over time to reduce wobble.");

        _uiEuroPos = PanelToggle.CreateNewEntry(_boneEuroEditorGroup.ContentParent);
        _uiEuroPos.Descriptor.SetTitle("Euro Filtering (Position)");
        _uiEuroPos.Descriptor.SetDescription("Steady at rest with minimal lag during motion. ");

        _uiEuroRot = PanelToggle.CreateNewEntry(_boneEuroEditorGroup.ContentParent);
        _uiEuroRot.Descriptor.SetTitle("Euro Filtering (Rotation)");
        _uiEuroRot.Descriptor.SetDescription("Reduces micro-wobble while remaining responsive.");

        _uiCalibSphereScale = PanelSlider.CreateAndBind(
            _boneEditorGroup.ContentParent,
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

        _boneEditorGroup.ForceRebuild();
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
