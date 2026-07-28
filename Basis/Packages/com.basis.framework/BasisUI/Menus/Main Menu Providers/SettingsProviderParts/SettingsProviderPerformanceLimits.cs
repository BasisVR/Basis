using Basis;
using Basis.BasisUI;
using Basis.Scripts.Avatar;
using UnityEngine;
using UnityEngine.UI;
public static class SettingsProviderPerformanceLimits
{
    private static RectTransform _layoutRoot;

    public static PanelTabPage PerformanceLimitsTab(PanelTabGroup tabGroup)
    {
        PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
        PanelElementDescriptor descriptor = tab.Descriptor;
        descriptor.SetIcon(AddressableAssets.Sprites.Settings);
        descriptor.SetTitle(BasisLocalization.Get("settings.tab.performancelimits"));

        RectTransform container = descriptor.ContentParent;
        BuildPerformanceLimitsContent(container);

        SettingsProvider.AddResetPageButton(container, "settings.tab.performancelimits", ResetPerformanceLimitDefaults);

        descriptor.ForceRebuild();
        return tab;
    }
    public static void BuildPerformanceLimitsContent(RectTransform container, bool wrapInSection = false)
    {
        _layoutRoot = container;
        RectTransform contentParent = container;

        PanelSectionToggle performanceToggle = null;
        PanelElementDescriptor performanceGroup = null;
        if (wrapInSection)
        {
            performanceToggle = PanelSectionToggle.CreateNewEntry(container);
            performanceGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                performanceToggle,
                container,
                BasisLocalization.Get("settings.perf.intro.title"),
                false);
            contentParent = performanceGroup.ContentParent;
        }

        PanelElementDescriptor bypassGroup =
            PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, contentParent);
        bypassGroup.SetTitle(BasisLocalization.Get("settings.perf.sessionBypass.title"));

        PanelToggle bypassToggle = PanelToggle.CreateNewEntry(bypassGroup.ContentParent);
        bypassToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.perf.sessionBypass.toggle"));
        bypassToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.perf.sessionBypass.toggle.tooltip"));
        bypassToggle.SetValueWithoutNotify(BasisAvatarPerformanceLimits.BypassAllLimits);
        bypassToggle.OnValueChanged += on =>
        {
            BasisAvatarPerformanceLimits.BypassAllLimits = on;
        };

        PanelSectionToggle geometryToggle = PanelSectionToggle.CreateNewEntry(contentParent);
        PanelElementDescriptor geometry = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
            geometryToggle,
            contentParent,
            BasisLocalization.Get("settings.perf.group.geometry"));

        AddLimitPair(geometry.ContentParent,
            BasisLocalization.Get("settings.perf.triangles.toggle"),
            BasisLocalization.Get("settings.perf.triangles.slider"),
            BasisSettingsDefaults.UsePerfLimitTriangles,
            BasisSettingsDefaults.MaxPerfTriangles,
            1000, 2_000_000, true, displayMode: ValueDisplayMode.Compact,
            toggleTooltip: BasisLocalization.Get("settings.perf.triangles.toggle.tooltip"),
            sliderTooltip: BasisLocalization.Get("settings.perf.triangles.slider.tooltip"));

        AddLimitPair(geometry.ContentParent,
            BasisLocalization.Get("settings.perf.boundsSize.toggle"),
            BasisLocalization.Get("settings.perf.boundsSize.slider"),
            BasisSettingsDefaults.UsePerfLimitBoundsSize,
            BasisSettingsDefaults.MaxPerfBoundsSize,
            10f, 50f, false, decimals: 1,
            toggleTooltip: BasisLocalization.Get("settings.perf.boundsSize.toggle.tooltip"),
            sliderTooltip: BasisLocalization.Get("settings.perf.boundsSize.slider.tooltip"));

        AddLimitPair(geometry.ContentParent,
            BasisLocalization.Get("settings.perf.bones.toggle"),
            BasisLocalization.Get("settings.perf.bones.slider"),
            BasisSettingsDefaults.UsePerfLimitBones,
            BasisSettingsDefaults.MaxPerfBones,
            16, 16384, true, displayMode: ValueDisplayMode.Compact,
            toggleTooltip: BasisLocalization.Get("settings.perf.bones.toggle.tooltip"),
            sliderTooltip: BasisLocalization.Get("settings.perf.bones.slider.tooltip"));

        PanelSectionToggleHelpers.FinalizeCollapsibleGroup(geometryToggle, geometry, false, _ => ForceLayout());

        PanelSectionToggle meshesToggle = PanelSectionToggle.CreateNewEntry(contentParent);
        PanelElementDescriptor meshes = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
            meshesToggle,
            contentParent,
            BasisLocalization.Get("settings.perf.group.meshesMaterials"));

        AddLimitPair(meshes.ContentParent,
            BasisLocalization.Get("settings.perf.skinnedMeshes.toggle"),
            BasisLocalization.Get("settings.perf.skinnedMeshes.slider"),
            BasisSettingsDefaults.UsePerfLimitSkinnedMeshes,
            BasisSettingsDefaults.MaxPerfSkinnedMeshes,
            1, 64, true,
            toggleTooltip: BasisLocalization.Get("settings.perf.skinnedMeshes.toggle.tooltip"),
            sliderTooltip: BasisLocalization.Get("settings.perf.skinnedMeshes.slider.tooltip"));

        AddLimitPair(meshes.ContentParent,
            BasisLocalization.Get("settings.perf.basicMeshes.toggle"),
            BasisLocalization.Get("settings.perf.basicMeshes.slider"),
            BasisSettingsDefaults.UsePerfLimitBasicMeshes,
            BasisSettingsDefaults.MaxPerfBasicMeshes,
            1, 128, true,
            toggleTooltip: BasisLocalization.Get("settings.perf.basicMeshes.toggle.tooltip"),
            sliderTooltip: BasisLocalization.Get("settings.perf.basicMeshes.slider.tooltip"));

        AddLimitPair(meshes.ContentParent,
            BasisLocalization.Get("settings.perf.materialSlots.toggle"),
            BasisLocalization.Get("settings.perf.materialSlots.slider"),
            BasisSettingsDefaults.UsePerfLimitMaterialSlots,
            BasisSettingsDefaults.MaxPerfMaterialSlots,
            1, 256, true,
            toggleTooltip: BasisLocalization.Get("settings.perf.materialSlots.toggle.tooltip"),
            sliderTooltip: BasisLocalization.Get("settings.perf.materialSlots.slider.tooltip"));

        AddLimitPair(meshes.ContentParent,
            BasisLocalization.Get("settings.perf.textureMemory.toggle"),
            BasisLocalization.Get("settings.perf.textureMemory.slider"),
            BasisSettingsDefaults.UsePerfLimitTextureMemory,
            BasisSettingsDefaults.MaxPerfTextureMemoryMB,
            8, 4096, true,
            toggleTooltip: BasisLocalization.Get("settings.perf.textureMemory.toggle.tooltip"),
            sliderTooltip: BasisLocalization.Get("settings.perf.textureMemory.slider.tooltip"));

        PanelSectionToggleHelpers.FinalizeCollapsibleGroup(meshesToggle, meshes, false, _ => ForceLayout());

        PanelSectionToggle physicsToggle = PanelSectionToggle.CreateNewEntry(contentParent);
        PanelElementDescriptor physics = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
            physicsToggle,
            contentParent,
            BasisLocalization.Get("settings.perf.group.physics"));

        AddLimitPair(physics.ContentParent,
            BasisLocalization.Get("settings.perf.jiggleBones.toggle"),
            BasisLocalization.Get("settings.perf.jiggleBones.slider"),
            BasisSettingsDefaults.UsePerfLimitJiggleBones,
            BasisSettingsDefaults.MaxPerfJiggleBones,
            0, 128, true,
            toggleTooltip: BasisLocalization.Get("settings.perf.jiggleBones.toggle.tooltip"),
            sliderTooltip: BasisLocalization.Get("settings.perf.jiggleBones.slider.tooltip"));

        AddLimitPair(physics.ContentParent,
            BasisLocalization.Get("settings.perf.colliders.toggle"),
            BasisLocalization.Get("settings.perf.colliders.slider"),
            BasisSettingsDefaults.UsePerfLimitColliders,
            BasisSettingsDefaults.MaxPerfColliders,
            0, 128, true,
            toggleTooltip: BasisLocalization.Get("settings.perf.colliders.toggle.tooltip"),
            sliderTooltip: BasisLocalization.Get("settings.perf.colliders.slider.tooltip"));

        AddLimitPair(physics.ContentParent,
            BasisLocalization.Get("settings.perf.cloth.toggle"),
            BasisLocalization.Get("settings.perf.cloth.slider"),
            BasisSettingsDefaults.UsePerfLimitCloth,
            BasisSettingsDefaults.MaxPerfCloth,
            0, 16, true,
            toggleTooltip: BasisLocalization.Get("settings.perf.cloth.toggle.tooltip"),
            sliderTooltip: BasisLocalization.Get("settings.perf.cloth.slider.tooltip"));

        PanelToggle jiggleFrustumToggle = PanelToggle.CreateNewEntry(physics.ContentParent);
        jiggleFrustumToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.perf.jiggleFrustumCull.toggle"));
        jiggleFrustumToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.perf.jiggleFrustumCull.toggle.tooltip"));
        jiggleFrustumToggle.AssignBinding(BasisSettingsDefaults.UseJiggleCollisionFrustumCull);

        AddLimitPair(physics.ContentParent,
            BasisLocalization.Get("settings.perf.jiggleDistanceCull.toggle"),
            BasisLocalization.Get("settings.perf.jiggleDistanceCull.slider"),
            BasisSettingsDefaults.UseJiggleCollisionDistanceCull,
            BasisSettingsDefaults.JiggleCollisionCullDistance,
            2f, 100f, false, decimals: 0,
            toggleTooltip: BasisLocalization.Get("settings.perf.jiggleDistanceCull.toggle.tooltip"),
            sliderTooltip: BasisLocalization.Get("settings.perf.jiggleDistanceCull.slider.tooltip"));

        PanelSlider jiggleCullExpansion = PanelSlider.CreateEntryAndBind(physics.ContentParent,
            PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.perf.jiggleCullExpansion"), 1f, 2f, false, 2, ValueDisplayMode.Raw),
            BasisSettingsDefaults.JiggleCullFrustumExpansion);
        if (jiggleCullExpansion != null) jiggleCullExpansion.Descriptor.SetTooltip(BasisLocalization.Get("settings.perf.jiggleCullExpansion.tooltip"));

        PanelSlider jiggleCullNearKeep = PanelSlider.CreateEntryAndBind(physics.ContentParent,
            PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.perf.jiggleCullNearKeep"), 0f, 15f, false, 1, ValueDisplayMode.Meters),
            BasisSettingsDefaults.JiggleCullNearKeepRadius);
        if (jiggleCullNearKeep != null) jiggleCullNearKeep.Descriptor.SetTooltip(BasisLocalization.Get("settings.perf.jiggleCullNearKeep.tooltip"));

        PanelSlider jiggleCellSize = PanelSlider.CreateEntryAndBind(physics.ContentParent,
            PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.perf.jiggleBroadPhaseCellSize"), 0.25f, 2f, false, 2, ValueDisplayMode.Meters),
            BasisSettingsDefaults.JiggleBroadPhaseCellSize);
        if (jiggleCellSize != null)
        {
            jiggleCellSize.Descriptor.SetTooltip(BasisLocalization.Get("settings.perf.jiggleBroadPhaseCellSize.tooltip"));

            void SyncCellSizeRestartNotice(float _)
            {
                jiggleCellSize.Descriptor.SetTitle(SettingsProvider.JiggleBroadPhaseCellSizeNeedsRestart
                    ? BasisLocalization.Get("settings.perf.jiggleBroadPhaseCellSize.restart")
                    : BasisLocalization.Get("settings.perf.jiggleBroadPhaseCellSize"));
            }

            SyncCellSizeRestartNotice(0f);
            BasisSettingsDefaults.JiggleBroadPhaseCellSize.OnChanged += SyncCellSizeRestartNotice;
            jiggleCellSize.OnInstanceReleased += () => BasisSettingsDefaults.JiggleBroadPhaseCellSize.OnChanged -= SyncCellSizeRestartNotice;
        }

        AddJiggleColliderLodControls(physics.ContentParent);

        PanelSectionToggleHelpers.FinalizeCollapsibleGroup(physicsToggle, physics, false, _ => ForceLayout());

        PanelSectionToggle effectsToggle = PanelSectionToggle.CreateNewEntry(contentParent);
        PanelElementDescriptor effects = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
            effectsToggle,
            contentParent,
            BasisLocalization.Get("settings.perf.group.effects"));

        AddLimitPair(effects.ContentParent,
            BasisLocalization.Get("settings.perf.particleSystems.toggle"),
            BasisLocalization.Get("settings.perf.particleSystems.slider"),
            BasisSettingsDefaults.UsePerfLimitParticleSystems,
            BasisSettingsDefaults.MaxPerfParticleSystems,
            0, 128, true,
            toggleTooltip: BasisLocalization.Get("settings.perf.particleSystems.toggle.tooltip"),
            sliderTooltip: BasisLocalization.Get("settings.perf.particleSystems.slider.tooltip"));

        AddLimitPair(effects.ContentParent,
            BasisLocalization.Get("settings.perf.trailRenderers.toggle"),
            BasisLocalization.Get("settings.perf.trailRenderers.slider"),
            BasisSettingsDefaults.UsePerfLimitTrailRenderers,
            BasisSettingsDefaults.MaxPerfTrailRenderers,
            0, 64, true,
            toggleTooltip: BasisLocalization.Get("settings.perf.trailRenderers.toggle.tooltip"),
            sliderTooltip: BasisLocalization.Get("settings.perf.trailRenderers.slider.tooltip"));

        AddLimitPair(effects.ContentParent,
            BasisLocalization.Get("settings.perf.lineRenderers.toggle"),
            BasisLocalization.Get("settings.perf.lineRenderers.slider"),
            BasisSettingsDefaults.UsePerfLimitLineRenderers,
            BasisSettingsDefaults.MaxPerfLineRenderers,
            0, 64, true,
            toggleTooltip: BasisLocalization.Get("settings.perf.lineRenderers.toggle.tooltip"),
            sliderTooltip: BasisLocalization.Get("settings.perf.lineRenderers.slider.tooltip"));

        PanelSectionToggleHelpers.FinalizeCollapsibleGroup(effectsToggle, effects, false, _ => ForceLayout());

        PanelSectionToggle runtimeToggle = PanelSectionToggle.CreateNewEntry(contentParent);
        PanelElementDescriptor runtime = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
            runtimeToggle,
            contentParent,
            BasisLocalization.Get("settings.perf.group.runtime"));

        AddLimitPair(runtime.ContentParent,
            BasisLocalization.Get("settings.perf.animators.toggle"),
            BasisLocalization.Get("settings.perf.animators.slider"),
            BasisSettingsDefaults.UsePerfLimitAnimators,
            BasisSettingsDefaults.MaxPerfAnimators,
            1, 32, true,
            toggleTooltip: BasisLocalization.Get("settings.perf.animators.toggle.tooltip"),
            sliderTooltip: BasisLocalization.Get("settings.perf.animators.slider.tooltip"));

        AddLimitPair(runtime.ContentParent,
            BasisLocalization.Get("settings.perf.cilboxBehaviours.toggle"),
            BasisLocalization.Get("settings.perf.cilboxBehaviours.slider"),
            BasisSettingsDefaults.UsePerfLimitCilboxBehaviours,
            BasisSettingsDefaults.MaxPerfCilboxBehaviours,
            0, 64, true,
            toggleTooltip: BasisLocalization.Get("settings.perf.cilboxBehaviours.toggle.tooltip"),
            sliderTooltip: BasisLocalization.Get("settings.perf.cilboxBehaviours.slider.tooltip"));

        PanelSectionToggleHelpers.FinalizeCollapsibleGroup(runtimeToggle, runtime, false, _ => ForceLayout());

        SettingsProviderContentTags.BuildContentTagsContent(container);

        if (performanceToggle != null)
        {
            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(performanceToggle, performanceGroup, false, _ => ForceLayout());
        }
    }

    private static void ForceLayout()
    {
        if (_layoutRoot != null)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(_layoutRoot);
        }
    }

    private static void AddLimitPair(
        Component parent,
        string toggleTitle,
        string sliderTitle,
        BasisSettingsBinding<bool> useBinding,
        BasisSettingsBinding<float> maxBinding,
        float sliderMin,
        float sliderMax,
        bool wholeNumbers,
        int decimals = 0,
        ValueDisplayMode displayMode = ValueDisplayMode.Raw,
        string toggleTooltip = null,
        string sliderTooltip = null)
    {
        PanelToggle toggle = PanelToggle.CreateNewEntry(parent);
        toggle.Descriptor.SetTitle(toggleTitle);
        toggle.Descriptor.SetTooltip(toggleTooltip);
        toggle.AssignBinding(useBinding);

        PanelSlider slider = PanelSlider.CreateEntryAndBind(parent, PanelSlider.SliderSettings.Advanced(sliderTitle, sliderMin, sliderMax, wholeNumbers, decimals, displayMode), maxBinding);

        if (slider != null)
        {
            slider.Descriptor.SetTooltip(sliderTooltip);

            slider.gameObject.SetActive(useBinding.RawValue);

            void Sync(bool on)
            {
                if (slider == null) return;
                slider.gameObject.SetActive(on);
                ForceLayout();
            }

            useBinding.OnChanged += Sync;
            slider.OnInstanceReleased += () => useBinding.OnChanged -= Sync;
        }
    }

    // Master toggle + three distance thresholds for distance-based jiggle collider reduction.
    // The sliders only show while the feature is on, matching AddLimitPair's reveal behaviour.
    private static void AddJiggleColliderLodControls(Component parent)
    {
        PanelToggle toggle = PanelToggle.CreateNewEntry(parent);
        toggle.Descriptor.SetTitle(BasisLocalization.Get("settings.perf.jiggleColliderLod.toggle"));
        toggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.perf.jiggleColliderLod.toggle.tooltip"));
        toggle.AssignBinding(BasisSettingsDefaults.UseJiggleColliderDistanceLod);

        PanelSlider near = PanelSlider.CreateEntryAndBind(parent,
            PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.perf.jiggleColliderLod.near"), 5f, 100f, false, 0, ValueDisplayMode.Meters),
            BasisSettingsDefaults.JiggleColliderLodNearDistance);
        PanelSlider mid = PanelSlider.CreateEntryAndBind(parent,
            PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.perf.jiggleColliderLod.mid"), 10f, 150f, false, 0, ValueDisplayMode.Meters),
            BasisSettingsDefaults.JiggleColliderLodMidDistance);
        PanelSlider far = PanelSlider.CreateEntryAndBind(parent,
            PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.perf.jiggleColliderLod.far"), 20f, 250f, false, 0, ValueDisplayMode.Meters),
            BasisSettingsDefaults.JiggleColliderLodFarDistance);

        if (near != null) near.Descriptor.SetTooltip(BasisLocalization.Get("settings.perf.jiggleColliderLod.near.tooltip"));
        if (mid != null) mid.Descriptor.SetTooltip(BasisLocalization.Get("settings.perf.jiggleColliderLod.mid.tooltip"));
        if (far != null) far.Descriptor.SetTooltip(BasisLocalization.Get("settings.perf.jiggleColliderLod.far.tooltip"));

        void Sync(bool on)
        {
            if (near != null) near.gameObject.SetActive(on);
            if (mid != null) mid.gameObject.SetActive(on);
            if (far != null) far.gameObject.SetActive(on);
            ForceLayout();
        }

        Sync(BasisSettingsDefaults.UseJiggleColliderDistanceLod.RawValue);
        BasisSettingsDefaults.UseJiggleColliderDistanceLod.OnChanged += Sync;
        toggle.OnInstanceReleased += () => BasisSettingsDefaults.UseJiggleColliderDistanceLod.OnChanged -= Sync;
    }

    public static void ResetPerformanceLimitDefaults()
    {
        BasisSettingsDefaults.UsePerfLimitTriangles.ResetToDefault();
        BasisSettingsDefaults.MaxPerfTriangles.ResetToDefault();
        BasisSettingsDefaults.UsePerfLimitBoundsSize.ResetToDefault();
        BasisSettingsDefaults.MaxPerfBoundsSize.ResetToDefault();
        BasisSettingsDefaults.UsePerfLimitTextureMemory.ResetToDefault();
        BasisSettingsDefaults.MaxPerfTextureMemoryMB.ResetToDefault();
        BasisSettingsDefaults.UsePerfLimitSkinnedMeshes.ResetToDefault();
        BasisSettingsDefaults.MaxPerfSkinnedMeshes.ResetToDefault();
        BasisSettingsDefaults.UsePerfLimitBasicMeshes.ResetToDefault();
        BasisSettingsDefaults.MaxPerfBasicMeshes.ResetToDefault();
        BasisSettingsDefaults.UsePerfLimitMaterialSlots.ResetToDefault();
        BasisSettingsDefaults.MaxPerfMaterialSlots.ResetToDefault();
        BasisSettingsDefaults.UsePerfLimitJiggleBones.ResetToDefault();
        BasisSettingsDefaults.MaxPerfJiggleBones.ResetToDefault();
        BasisSettingsDefaults.UsePerfLimitJiggleColliders.ResetToDefault();
        BasisSettingsDefaults.MaxPerfJiggleColliders.ResetToDefault();
        BasisSettingsDefaults.UseJiggleCollisionFrustumCull.ResetToDefault();
        BasisSettingsDefaults.UseJiggleCollisionDistanceCull.ResetToDefault();
        BasisSettingsDefaults.JiggleCollisionCullDistance.ResetToDefault();
        BasisSettingsDefaults.JiggleCullFrustumExpansion.ResetToDefault();
        BasisSettingsDefaults.JiggleCullNearKeepRadius.ResetToDefault();
        BasisSettingsDefaults.JiggleBroadPhaseCellSize.ResetToDefault();
        BasisSettingsDefaults.UseJiggleColliderDistanceLod.ResetToDefault();
        BasisSettingsDefaults.JiggleColliderLodNearDistance.ResetToDefault();
        BasisSettingsDefaults.JiggleColliderLodMidDistance.ResetToDefault();
        BasisSettingsDefaults.JiggleColliderLodFarDistance.ResetToDefault();
        BasisSettingsDefaults.UsePerfLimitAnimators.ResetToDefault();
        BasisSettingsDefaults.MaxPerfAnimators.ResetToDefault();
        BasisSettingsDefaults.UsePerfLimitBones.ResetToDefault();
        BasisSettingsDefaults.MaxPerfBones.ResetToDefault();
        BasisSettingsDefaults.UsePerfLimitLights.ResetToDefault();
        BasisSettingsDefaults.MaxPerfLights.ResetToDefault();
        BasisSettingsDefaults.UsePerfLimitParticleSystems.ResetToDefault();
        BasisSettingsDefaults.MaxPerfParticleSystems.ResetToDefault();
        BasisSettingsDefaults.UsePerfLimitTrailRenderers.ResetToDefault();
        BasisSettingsDefaults.MaxPerfTrailRenderers.ResetToDefault();
        BasisSettingsDefaults.UsePerfLimitLineRenderers.ResetToDefault();
        BasisSettingsDefaults.MaxPerfLineRenderers.ResetToDefault();
        BasisSettingsDefaults.UsePerfLimitCloth.ResetToDefault();
        BasisSettingsDefaults.MaxPerfCloth.ResetToDefault();
        BasisSettingsDefaults.UsePerfLimitColliders.ResetToDefault();
        BasisSettingsDefaults.MaxPerfColliders.ResetToDefault();
        BasisSettingsDefaults.UsePerfLimitCilboxBehaviours.ResetToDefault();
        BasisSettingsDefaults.MaxPerfCilboxBehaviours.ResetToDefault();
    }
}
