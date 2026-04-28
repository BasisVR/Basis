using Basis;
using Basis.BasisUI;
using Basis.Scripts.Avatar;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Settings panel for the opt-in client-side avatar performance limits. Every limit
/// is a (toggle, slider) pair — the toggle flips <c>UsePerfLimit*</c> on, the slider
/// sets the corresponding <c>MaxPerf*</c> threshold. All values live in
/// <see cref="BasisSettingsDefaults"/> and are applied at runtime by
/// <c>SMModuleAvatarPerformanceLimits</c>, which re-evaluates every currently-loaded
/// remote avatar when any value changes.
/// </summary>
public static class SettingsProviderPerformanceLimits
{
    /// <summary>
    /// The root RectTransform of the currently-built tab. Captured once at tab
    /// creation and read by the toggle callbacks so flipping a limit on/off can
    /// trigger a layout rebuild — hiding/showing a slider with <c>SetActive</c>
    /// drops it out of the layout pass, but LayoutGroups don't re-flow siblings
    /// until somebody calls <c>LayoutRebuilder.ForceRebuildLayoutImmediate</c>.
    /// </summary>
    private static RectTransform _layoutRoot;

    public static PanelTabPage PerformanceLimitsTab(PanelTabGroup tabGroup)
    {
        PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
        PanelElementDescriptor descriptor = tab.Descriptor;
        descriptor.SetIcon(AddressableAssets.Sprites.Settings);
        descriptor.SetTitle(BasisLocalization.Get("settings.tab.performancelimits"));

        RectTransform container = descriptor.ContentParent;
        BuildPerformanceLimitsContent(container);

        // Use the same tabKey we registered with AddLazyTab — it drives both the
        // reset button label and the "navigate back to this tab" hop after the
        // reset. Hardcoded English would show that literal string in JP/NL.
        SettingsProvider.AddResetPageButton(container, "settings.tab.performancelimits", ResetPerformanceLimitDefaults);

        descriptor.ForceRebuild();
        return tab;
    }

    /// <summary>
    /// Builds every performance-limit group + control into <paramref name="container"/>
    /// without adding a reset button. Used by the standalone tab and by the merged
    /// Graphics tab so both share one source of truth.
    /// </summary>
    public static void BuildPerformanceLimitsContent(RectTransform container)
    {
        _layoutRoot = container;

        // Session-only bypass. Lives at the top so it's the first thing users see
        // when they want to quickly check what an avatar actually looks like
        // unfiltered. Not wired to BasisSettingsDefaults because it deliberately
        // doesn't persist — flipping it writes straight to the runtime flag on
        // BasisAvatarPerformanceLimits, which fires OnBypassChanged and pulls
        // every affected avatar through the same debounced reconcile as a normal
        // limit change.
        PanelElementDescriptor bypassGroup =
            PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
        bypassGroup.SetTitle(BasisLocalization.Get("settings.perf.sessionBypass.title"));
        bypassGroup.SetDescription(BasisLocalization.Get("settings.perf.sessionBypass.description"));

        PanelToggle bypassToggle = PanelToggle.CreateNewEntry(bypassGroup.ContentParent);
        bypassToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.perf.sessionBypass.toggle"));
        bypassToggle.SetValueWithoutNotify(BasisAvatarPerformanceLimits.BypassAllLimits);
        bypassToggle.OnValueChanged += on =>
        {
            BasisAvatarPerformanceLimits.BypassAllLimits = on;
        };

        // Header explanation. Trim limits (animators, lights, particles, trails, lines,
        // cloth, unity colliders) destroy excess components on remote avatars at load
        // time and ship on by default. Hard-block limits (triangles, bounds, texture
        // memory, material slots, bones) fall back to the loading mesh when tripped;
        // only texture memory is on by default at 512 MB.
        PanelElementDescriptor intro =
            PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
        intro.SetTitle(BasisLocalization.Get("settings.perf.intro.title"));
        intro.SetDescription(BasisLocalization.Get("settings.perf.intro.description"));

        // ---------------- Geometry ----------------
        PanelElementDescriptor geometry =
            PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
        geometry.SetTitle(BasisLocalization.Get("settings.perf.group.geometry"));
        geometry.SetDescription(BasisLocalization.Get("settings.perf.group.geometry.description"));

        AddLimitPair(geometry.ContentParent,
            BasisLocalization.Get("settings.perf.triangles.toggle"),
            BasisLocalization.Get("settings.perf.triangles.slider"),
            BasisSettingsDefaults.UsePerfLimitTriangles,
            BasisSettingsDefaults.MaxPerfTriangles,
            1000, 2_000_000, true, displayMode: ValueDisplayMode.Compact);

        // Minimum starts at 10 m — anything smaller rejects a normal humanoid on
        // principle, and the bounds check is there to catch 50 m-tall monster
        // bundles rather than tune small avatars.
        AddLimitPair(geometry.ContentParent,
            BasisLocalization.Get("settings.perf.boundsSize.toggle"),
            BasisLocalization.Get("settings.perf.boundsSize.slider"),
            BasisSettingsDefaults.UsePerfLimitBoundsSize,
            BasisSettingsDefaults.MaxPerfBoundsSize,
            10f, 50f, false, decimals: 1);

        // Hard-block cap. Intended as a guard rail for pathological bundles rather
        // than a daily driver — label reflects that so users don't set it low.
        // "per avatar" is explicit because bones is the one limit where users
        // reasonably ask whether it sums across the room or counts per-player.
        AddLimitPair(geometry.ContentParent,
            BasisLocalization.Get("settings.perf.bones.toggle"),
            BasisLocalization.Get("settings.perf.bones.slider"),
            BasisSettingsDefaults.UsePerfLimitBones,
            BasisSettingsDefaults.MaxPerfBones,
            16, 16384, true, displayMode: ValueDisplayMode.Compact);

        // ---------------- Meshes & Materials ----------------
        PanelElementDescriptor meshes =
            PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
        meshes.SetTitle(BasisLocalization.Get("settings.perf.group.meshesMaterials"));

        AddLimitPair(meshes.ContentParent,
            BasisLocalization.Get("settings.perf.skinnedMeshes.toggle"),
            BasisLocalization.Get("settings.perf.skinnedMeshes.slider"),
            BasisSettingsDefaults.UsePerfLimitSkinnedMeshes,
            BasisSettingsDefaults.MaxPerfSkinnedMeshes,
            1, 64, true);

        AddLimitPair(meshes.ContentParent,
            BasisLocalization.Get("settings.perf.basicMeshes.toggle"),
            BasisLocalization.Get("settings.perf.basicMeshes.slider"),
            BasisSettingsDefaults.UsePerfLimitBasicMeshes,
            BasisSettingsDefaults.MaxPerfBasicMeshes,
            1, 128, true);

        AddLimitPair(meshes.ContentParent,
            BasisLocalization.Get("settings.perf.materialSlots.toggle"),
            BasisLocalization.Get("settings.perf.materialSlots.slider"),
            BasisSettingsDefaults.UsePerfLimitMaterialSlots,
            BasisSettingsDefaults.MaxPerfMaterialSlots,
            1, 256, true);

        AddLimitPair(meshes.ContentParent,
            BasisLocalization.Get("settings.perf.textureMemory.toggle"),
            BasisLocalization.Get("settings.perf.textureMemory.slider"),
            BasisSettingsDefaults.UsePerfLimitTextureMemory,
            BasisSettingsDefaults.MaxPerfTextureMemoryMB,
            8, 4096, true);

        // ---------------- Physics ----------------
        PanelElementDescriptor physics =
            PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
        physics.SetTitle(BasisLocalization.Get("settings.perf.group.physics"));

        AddLimitPair(physics.ContentParent,
            BasisLocalization.Get("settings.perf.jiggleBones.toggle"),
            BasisLocalization.Get("settings.perf.jiggleBones.slider"),
            BasisSettingsDefaults.UsePerfLimitJiggleBones,
            BasisSettingsDefaults.MaxPerfJiggleBones,
            0, 128, true);

        // JiggleColliderExample is not in AvatarContentPoliceSelector.asset, so the
        // content police strips every instance at load time before this limit could
        // ever run. Surface nothing for it in the UI.
        // AddLimitPair(physics.ContentParent,
        //     "Limit Jiggle Colliders", "Max Jiggle Colliders",
        //     BasisSettingsDefaults.UsePerfLimitJiggleColliders,
        //     BasisSettingsDefaults.MaxPerfJiggleColliders,
        //     0, 64, true);

        AddLimitPair(physics.ContentParent,
            BasisLocalization.Get("settings.perf.colliders.toggle"),
            BasisLocalization.Get("settings.perf.colliders.slider"),
            BasisSettingsDefaults.UsePerfLimitColliders,
            BasisSettingsDefaults.MaxPerfColliders,
            0, 128, true);

        AddLimitPair(physics.ContentParent,
            BasisLocalization.Get("settings.perf.cloth.toggle"),
            BasisLocalization.Get("settings.perf.cloth.slider"),
            BasisSettingsDefaults.UsePerfLimitCloth,
            BasisSettingsDefaults.MaxPerfCloth,
            0, 16, true);

        // ---------------- Effects ----------------
        PanelElementDescriptor effects =
            PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
        effects.SetTitle(BasisLocalization.Get("settings.perf.group.effects"));

        // UnityEngine.Light is not in AvatarContentPoliceSelector.asset, so the
        // content police strips every Light on a downloaded remote avatar before
        // this limit could ever run. Addressable (in-build) avatars are curated,
        // so lights there are effectively impossible too. Surface nothing for it.
        // AddLimitPair(effects.ContentParent,
        //     "Limit Lights", "Max Lights",
        //     BasisSettingsDefaults.UsePerfLimitLights,
        //     BasisSettingsDefaults.MaxPerfLights,
        //     0, 32, true);

        AddLimitPair(effects.ContentParent,
            BasisLocalization.Get("settings.perf.particleSystems.toggle"),
            BasisLocalization.Get("settings.perf.particleSystems.slider"),
            BasisSettingsDefaults.UsePerfLimitParticleSystems,
            BasisSettingsDefaults.MaxPerfParticleSystems,
            0, 128, true);

        AddLimitPair(effects.ContentParent,
            BasisLocalization.Get("settings.perf.trailRenderers.toggle"),
            BasisLocalization.Get("settings.perf.trailRenderers.slider"),
            BasisSettingsDefaults.UsePerfLimitTrailRenderers,
            BasisSettingsDefaults.MaxPerfTrailRenderers,
            0, 64, true);

        AddLimitPair(effects.ContentParent,
            BasisLocalization.Get("settings.perf.lineRenderers.toggle"),
            BasisLocalization.Get("settings.perf.lineRenderers.slider"),
            BasisSettingsDefaults.UsePerfLimitLineRenderers,
            BasisSettingsDefaults.MaxPerfLineRenderers,
            0, 64, true);

        // ---------------- Runtime ----------------
        PanelElementDescriptor runtime =
            PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
        runtime.SetTitle(BasisLocalization.Get("settings.perf.group.runtime"));
        runtime.SetDescription(BasisLocalization.Get("settings.perf.group.runtime.description"));

        AddLimitPair(runtime.ContentParent,
            BasisLocalization.Get("settings.perf.animators.toggle"),
            BasisLocalization.Get("settings.perf.animators.slider"),
            BasisSettingsDefaults.UsePerfLimitAnimators,
            BasisSettingsDefaults.MaxPerfAnimators,
            1, 32, true);

        // ---------------- Content Tags ----------------
        // Sits at the bottom of the same tab so users see content-safety filters
        // alongside perf filters — same mental model ("block this avatar before it
        // loads"), different criteria (creator-declared category vs. measured cost).
        SettingsProviderContentTags.BuildContentTagsContent(container);
    }

    /// <summary>
    /// Adds a toggle and slider for a single limit. The toggle controls whether the
    /// limit is enforced; the slider sets the threshold. When the toggle is off the
    /// slider's entire GameObject is hidden so the settings panel only shows the
    /// knob for an active limit — matches how VRChat / NeosVR hide inactive options.
    /// <paramref name="displayMode"/> lets large-number sliders (triangles, bones)
    /// opt into the compact "1k / 32.5k / 2M" readout instead of raw integers.
    /// </summary>
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
        ValueDisplayMode displayMode = ValueDisplayMode.Raw)
    {
        PanelToggle toggle = PanelToggle.CreateNewEntry(parent);
        toggle.Descriptor.SetTitle(toggleTitle);
        toggle.AssignBinding(useBinding);

        PanelSlider slider = PanelSlider.CreateEntryAndBind(
            parent,
            PanelSlider.SliderSettings.Advanced(sliderTitle, sliderMin, sliderMax, wholeNumbers, decimals, displayMode),
            maxBinding);

        // Seed visibility from the saved toggle state, then keep it in sync whenever
        // the binding flips — including programmatic changes like the reset button.
        // SetActive on the slider GameObject removes it from the layout pass entirely
        // so the container re-flows and only active limits take up vertical space;
        // LayoutRebuilder.ForceRebuildLayoutImmediate is what actually closes / reopens
        // the gap. PanelToggle.OnValueChanged only fires from user-driven SetValue,
        // so we hook BasisSettingsBinding.OnChanged here to also catch resets.
        if (slider != null)
        {
            void Sync(bool on)
            {
                if (slider == null) return;
                slider.gameObject.SetActive(on);
                if (_layoutRoot != null)
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(_layoutRoot);
                }
            }

            Sync(useBinding.RawValue);
            useBinding.OnChanged += Sync;
        }
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
    }
}
