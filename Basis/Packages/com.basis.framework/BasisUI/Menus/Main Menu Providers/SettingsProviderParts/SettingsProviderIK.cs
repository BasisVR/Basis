using Basis;
using Basis.BasisUI;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public static class SettingsProviderIK
{
    private static PanelDropdown dropdownIKMode;
    private static PanelDropdown dropdownSeatedMode;

    public const string SeatedMode_Seated = "Seated mode";
    public const string SeatedMode_Standing = "Standing Mode";

    private static readonly List<PanelToggle> _euroToggleUIs = new();
    private static readonly List<PanelToggle> _trackerLerpToggleUIs = new();

    private static PanelDropdown _boneDropdown;

    // ------------------
    // IK & Input
    // ------------------
    public static PanelTabPage IKTab(PanelTabGroup tabGroup)
    {
        // --- Tab (replaces BasisTabBuilder) ---
        var tabPage = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
        var tabDesc = tabPage.Descriptor;
        tabDesc.SetTitle("IK Tab");
        tabDesc.SetIcon(AddressableAssets.Sprites.Settings);

        // --- Group: "Calibration & IK" (replaces tab.Group(...)) ---
        var ikGroup = PanelElementDescriptor.CreateNew(
            PanelElementDescriptor.ElementStyles.Group,
            tabDesc.ContentParent);

        ikGroup.SetTitle("Calibration & IK");
        ikGroup.SetDescription("Fine-tuning for avatar scaling, calibration, and IK smoothing");
        ikGroup.SetIcon(AddressableAssets.Sprites.Settings);

        var ikParent = ikGroup.ContentParent;

        // --- Seated Mode dropdown ---
        dropdownSeatedMode = PanelDropdown.CreateNewEntry(ikParent);
        dropdownSeatedMode.Descriptor.SetTitle(BasisSettingsDefaults.SitStand.BindingKey);
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

        // This now takes a RectTransform parent instead of a BasisGroupBuilder.
        AddFBIKTogglesCompact(ikParent);

        SyncMasterEuroFromChildren();

        tabDesc.ForceRebuild();
        return tabPage;
    }

    private static PanelToggle _uiSmoothPos;
    private static PanelToggle _uiSmoothRot;
    private static PanelToggle _uiEuroPos;
    private static PanelToggle _uiEuroRot;
    private static PanelElementDescriptor _boneEditorGroup;
    private static PanelElementDescriptor _boneEuroEditorGroup;

    private struct BoneBindings
    {
        public string Name;
        public BasisSettingsBinding<bool> SmoothPos;
        public BasisSettingsBinding<bool> SmoothRot;
        public BasisSettingsBinding<bool> EuroPos;
        public BasisSettingsBinding<bool> EuroRot;
    }

    private static readonly List<BoneBindings> _bones = new();

    private static void AddFBIKTogglesCompact(RectTransform parent)
    {
        var blocks = new (string name,
            BasisSettingsBinding<bool> smoothPos,
            BasisSettingsBinding<bool> smoothRot,
            BasisSettingsBinding<bool> euroPos,
            BasisSettingsBinding<bool> euroRot)[]
        {
            ("Hips", BasisSettingsDefaults.FBIKHipsSmoothPos, BasisSettingsDefaults.FBIKHipsSmoothRot, BasisSettingsDefaults.FBIKHipsEuroPos, BasisSettingsDefaults.FBIKHipsEuroRot),
            ("Head", BasisSettingsDefaults.FBIKHeadSmoothPos, BasisSettingsDefaults.FBIKHeadSmoothRot, BasisSettingsDefaults.FBIKHeadEuroPos, BasisSettingsDefaults.FBIKHeadEuroRot),
            ("Left Foot", BasisSettingsDefaults.FBIKLeftFootSmoothPos, BasisSettingsDefaults.FBIKLeftFootSmoothRot, BasisSettingsDefaults.FBIKLeftFootEuroPos, BasisSettingsDefaults.FBIKLeftFootEuroRot),
            ("Right Foot", BasisSettingsDefaults.FBIKRightFootSmoothPos, BasisSettingsDefaults.FBIKRightFootSmoothRot, BasisSettingsDefaults.FBIKRightFootEuroPos, BasisSettingsDefaults.FBIKRightFootEuroRot),
            ("Chest", BasisSettingsDefaults.FBIKChestSmoothPos, BasisSettingsDefaults.FBIKChestSmoothRot, BasisSettingsDefaults.FBIKChestEuroPos, BasisSettingsDefaults.FBIKChestEuroRot),
            ("Left Lower Leg", BasisSettingsDefaults.FBIKLeftLowerLegSmoothPos, BasisSettingsDefaults.FBIKLeftLowerLegSmoothRot, BasisSettingsDefaults.FBIKLeftLowerLegEuroPos, BasisSettingsDefaults.FBIKLeftLowerLegEuroRot),
            ("Right Lower Leg", BasisSettingsDefaults.FBIKRightLowerLegSmoothPos, BasisSettingsDefaults.FBIKRightLowerLegSmoothRot, BasisSettingsDefaults.FBIKRightLowerLegEuroPos, BasisSettingsDefaults.FBIKRightLowerLegEuroRot),
            ("Left Hand", BasisSettingsDefaults.FBIKLeftHandSmoothPos, BasisSettingsDefaults.FBIKLeftHandSmoothRot, BasisSettingsDefaults.FBIKLeftHandEuroPos, BasisSettingsDefaults.FBIKLeftHandEuroRot),
            ("Right Hand", BasisSettingsDefaults.FBIKRightHandSmoothPos, BasisSettingsDefaults.FBIKRightHandSmoothRot, BasisSettingsDefaults.FBIKRightHandEuroPos, BasisSettingsDefaults.FBIKRightHandEuroRot),
            ("Left Lower Arm", BasisSettingsDefaults.FBIKLeftLowerArmSmoothPos, BasisSettingsDefaults.FBIKLeftLowerArmSmoothRot, BasisSettingsDefaults.FBIKLeftLowerArmEuroPos, BasisSettingsDefaults.FBIKLeftLowerArmEuroRot),
            ("Right Lower Arm", BasisSettingsDefaults.FBIKRightLowerArmSmoothPos, BasisSettingsDefaults.FBIKRightLowerArmSmoothRot, BasisSettingsDefaults.FBIKRightLowerArmEuroPos, BasisSettingsDefaults.FBIKRightLowerArmEuroRot),
            ("Left Toe", BasisSettingsDefaults.FBIKLeftToeSmoothPos, BasisSettingsDefaults.FBIKLeftToeSmoothRot, BasisSettingsDefaults.FBIKLeftToeEuroPos, BasisSettingsDefaults.FBIKLeftToeEuroRot),
            ("Right Toe", BasisSettingsDefaults.FBIKRightToeSmoothPos, BasisSettingsDefaults.FBIKRightToeSmoothRot, BasisSettingsDefaults.FBIKRightToeEuroPos, BasisSettingsDefaults.FBIKRightToeEuroRot),
            ("Left Shoulder", BasisSettingsDefaults.FBIKLeftShoulderSmoothPos, BasisSettingsDefaults.FBIKLeftShoulderSmoothRot, BasisSettingsDefaults.FBIKLeftShoulderEuroPos, BasisSettingsDefaults.FBIKLeftShoulderEuroRot),
            ("Right Shoulder", BasisSettingsDefaults.FBIKRightShoulderSmoothPos, BasisSettingsDefaults.FBIKRightShoulderSmoothRot, BasisSettingsDefaults.FBIKRightShoulderEuroPos, BasisSettingsDefaults.FBIKRightShoulderEuroRot),
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
                EuroRot = b.euroRot
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
        SyncMasterEuroFromChildren();
    }
    private static void SyncMasterEuroFromChildren()
    {
        if (_bones.Count == 0)
        {
            return;
        }

        bool allOn = _bones.All(b => b.EuroPos.RawValue && b.EuroRot.RawValue);
        BasisSettingsDefaults.FBIKEuroAll.SetValue(allOn);
    }
    private static void EvaluateInteractables()
    {
        if (dropdownSeatedMode == null || dropdownIKMode == null)
        {
            return;
        }

        bool isSeated = GetCurrentText(dropdownSeatedMode) == SeatedMode_Seated;
        SetDropdownInteractable(dropdownIKMode, !isSeated);
    }
    private static string GetCurrentText(PanelDropdown dd)  => dd.DropdownComponent.options[dd.DropdownComponent.value].text;
    private static void SetDropdownInteractable(PanelDropdown dd, bool interactable)  => dd.DropdownComponent.interactable = interactable;
}
