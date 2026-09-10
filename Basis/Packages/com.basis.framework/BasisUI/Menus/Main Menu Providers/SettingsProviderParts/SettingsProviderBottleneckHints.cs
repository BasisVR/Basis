using System.Collections.Generic;
using Basis.Scripts.Drivers;
using UnityEngine;

namespace Basis.BasisUI
{
    public static class SettingsProviderBottleneckHints
    {
        private struct Hint
        {
            public PanelElementDescriptor Element;
            public BasisPanelTint.Handle Tint;
            public BasisFrameCostSide Side;
            public float Strength;
        }

        private static readonly List<Hint> Lit = new();
        private static readonly List<PanelComponent> Walk = new();
        private static readonly List<PanelElementDescriptor> Order = new();
        private static readonly Dictionary<PanelElementDescriptor, BasisFrameCostSide> Marked = new();
        private static readonly HashSet<PanelElementDescriptor> Headers = new();

        private static Dictionary<string, BasisFrameCostSide> _table;
        private static PanelElementDescriptor _page;
        private static BasisFrameCostSide _shown = BasisFrameCostSide.None;

        public static BasisFrameCostSide SideFor(BasisFrameBottleneckKind kind)
        {
            switch (kind)
            {
                case BasisFrameBottleneckKind.Cpu: return BasisFrameCostSide.Cpu;
                case BasisFrameBottleneckKind.Gpu: return BasisFrameCostSide.Gpu;
                default: return BasisFrameCostSide.None;
            }
        }

        public static void Mark(PanelElementDescriptor element, BasisFrameCostSide side)
        {
            if (element == null || side == BasisFrameCostSide.None)
            {
                return;
            }

            if (Marked.TryGetValue(element, out BasisFrameCostSide already))
            {
                Marked[element] = already | side;
                return;
            }

            Marked[element] = side;
            Order.Add(element);
        }

        public static void Mark(PanelComponent component, BasisFrameCostSide side)
        {
            Mark(component != null ? component.Descriptor : null, side);
        }

        public static void Bind(PanelElementDescriptor page)
        {
            Lit.Clear();
            _page = null;
            _shown = BasisFrameCostSide.None;

            if (page == null)
            {
                Forget();
                return;
            }

            _page = page;

            CollectBoundControls(page.ContentParent);
            CollectSections(page.ContentParent);

            for (int index = 0; index < Order.Count; index++)
            {
                PanelElementDescriptor element = Order[index];
                if (element == null)
                {
                    continue;
                }

                Lit.Add(new Hint
                {
                    Element = element,
                    Tint = BasisPanelTint.Capture(element),
                    Side = Marked[element],
                    Strength = Headers.Contains(element) ? 0f : BasisPanelTint.Strength
                });
            }

            Forget();

            Show(SettingsProviderFrameBottleneck.Verdict, false);
        }

        public static void Release(PanelElementDescriptor page)
        {
            if (page != null && !ReferenceEquals(_page, page))
            {
                return;
            }

            Lit.Clear();
            Forget();
            _page = null;
            _shown = BasisFrameCostSide.None;
        }

        private static void Forget()
        {
            Marked.Clear();
            Order.Clear();
            Headers.Clear();
        }

        public static void Show(BasisFrameBottleneckKind kind)
        {
            Show(kind, true);
        }

        private static void Show(BasisFrameBottleneckKind kind, bool animate)
        {
            BasisFrameCostSide side = SideFor(kind);
            if (side == _shown)
            {
                return;
            }
            _shown = side;

            for (int index = 0; index < Lit.Count; index++)
            {
                Hint hint = Lit[index];
                if (hint.Element == null)
                {
                    continue;
                }

                bool tween = animate && hint.Element.gameObject.activeInHierarchy;
                if (side != BasisFrameCostSide.None && (hint.Side & side) != 0)
                {
                    BasisPanelTint.Apply(hint.Tint, BasisPanelTint.Hint, hint.Strength, tween);
                }
                else
                {
                    BasisPanelTint.Clear(hint.Tint, tween);
                }
            }
        }

        private static void CollectBoundControls(RectTransform root)
        {
            if (root == null)
            {
                return;
            }

            _table ??= BuildTable();

            root.GetComponentsInChildren(true, Walk);
            for (int index = 0; index < Walk.Count; index++)
            {
                PanelComponent component = Walk[index];
                string key = component != null ? component.BoundSettingKey : null;
                if (string.IsNullOrEmpty(key) || !_table.TryGetValue(key, out BasisFrameCostSide side))
                {
                    continue;
                }

                Mark(component.Descriptor, side);
            }
            Walk.Clear();
        }

        private static void CollectSections(RectTransform root)
        {
            int rows = Order.Count;
            for (int index = 0; index < rows; index++)
            {
                PanelElementDescriptor element = Order[index];
                if (element == null)
                {
                    continue;
                }

                BasisFrameCostSide side = Marked[element];
                for (Transform step = element.transform; step != null; step = step.parent)
                {
                    if (step.TryGetComponent(out PanelSectionContentMarker marker) && marker.Owner != null)
                    {
                        PanelElementDescriptor header = marker.Owner.Descriptor;
                        if (header != null && !Marked.ContainsKey(header))
                        {
                            Headers.Add(header);
                        }

                        Mark(header, side);
                    }

                    if (ReferenceEquals(step, root))
                    {
                        break;
                    }
                }
            }
        }

        private static Dictionary<string, BasisFrameCostSide> BuildTable()
        {
            Dictionary<string, BasisFrameCostSide> table = new();

            Add(table, BasisFrameCostSide.Cpu,
                BasisSettingsDefaults.PoseLOD,
                BasisSettingsDefaults.LocalHeadBlendShapes,
                BasisSettingsDefaults.UsePerfLimitJiggleBones,
                BasisSettingsDefaults.MaxPerfJiggleBones,
                BasisSettingsDefaults.UsePerfLimitJiggleColliders,
                BasisSettingsDefaults.MaxPerfJiggleColliders,
                BasisSettingsDefaults.UsePerfLimitCloth,
                BasisSettingsDefaults.MaxPerfCloth,
                BasisSettingsDefaults.UsePerfLimitColliders,
                BasisSettingsDefaults.MaxPerfColliders,
                BasisSettingsDefaults.UsePerfLimitAnimators,
                BasisSettingsDefaults.MaxPerfAnimators,
                BasisSettingsDefaults.UsePerfLimitBones,
                BasisSettingsDefaults.MaxPerfBones,
                BasisSettingsDefaults.UsePerfLimitCilboxBehaviours,
                BasisSettingsDefaults.MaxPerfCilboxBehaviours,
                BasisSettingsDefaults.UseJiggleCollisionFrustumCull,
                BasisSettingsDefaults.UseJiggleCollisionDistanceCull,
                BasisSettingsDefaults.UseJiggleColliderDistanceLod,
                BasisSettingsDefaults.JiggleCollisionCullDistance,
                BasisSettingsDefaults.JiggleCullFrustumExpansion,
                BasisSettingsDefaults.JiggleCullNearKeepRadius,
                BasisSettingsDefaults.JiggleBroadPhaseCellSize,
                BasisSettingsDefaults.JiggleColliderLodNearDistance,
                BasisSettingsDefaults.JiggleColliderLodMidDistance,
                BasisSettingsDefaults.JiggleColliderLodFarDistance);

            Add(table, BasisFrameCostSide.Gpu,
                BasisSettingsDefaults.Antialiasing,
                BasisSettingsDefaults.RenderResolution,
                BasisSettingsDefaults.HDRSupport,
                BasisSettingsDefaults.FoveatedRendering,
                BasisSettingsDefaults.AvatarMeshLOD,
                BasisSettingsDefaults.GlobalMeshLOD,
                BasisSettingsDefaults.UseAvatarShadowLod,
                BasisSettingsDefaults.UseBloomOverride,
                BasisSettingsDefaults.BloomIntensity,
                BasisSettingsDefaults.UseVolumetricFogOverride,
                BasisSettingsDefaults.VolumetricFogDensity,
                BasisSettingsDefaults.VolumetricFogBakedAPV,
                BasisSettingsDefaults.VolumetricFogResolution,
                BasisSettingsDefaults.VolumetricFogMaxSteps,
                BasisSettingsDefaults.VolumetricFogBlurIterations,
                BasisSettingsDefaults.VolumetricFogTemporal,
                BasisSettingsDefaults.VolumetricFogFroxels,
                BasisSettingsDefaults.VolumetricFogAnalyticDepth,
                BasisSettingsDefaults.VolumetricFogScaleSteps,
                BasisSettingsDefaults.VolumetricFogSunTrims,
                BasisSettingsDefaults.VolumetricFogFroxelGrid,
                BasisSettingsDefaults.UseMotionBlurOverride,
                BasisSettingsDefaults.MotionBlurIntensity,
                BasisSettingsDefaults.MotionBlurClamp,
                BasisSettingsDefaults.MotionBlurQuality,
                BasisSettingsDefaults.MotionBlurMode,
                BasisSettingsDefaults.DevVariableRateShading,
                BasisSettingsDefaults.VrsFovealInnerRadius,
                BasisSettingsDefaults.VrsFovealOuterRadius,
                BasisSettingsDefaults.UsePerfLimitTriangles,
                BasisSettingsDefaults.MaxPerfTriangles,
                BasisSettingsDefaults.UsePerfLimitTextureMemory,
                BasisSettingsDefaults.MaxPerfTextureMemoryMB,
                BasisSettingsDefaults.UsePerfLimitLights,
                BasisSettingsDefaults.MaxPerfLights);

            Add(table, BasisFrameCostSide.Both,
                BasisSettingsDefaults.AvatarRange,
                BasisSettingsDefaults.UseMaxVisibleAvatars,
                BasisSettingsDefaults.MaxVisibleAvatars,
                BasisSettingsDefaults.QualityLevel,
                BasisSettingsDefaults.ShadowQuality,
                BasisSettingsDefaults.FieldOfView,
                BasisSettingsDefaults.UseAvatarSkinLod,
                BasisSettingsDefaults.UseAvatarVisibilityCull,
                BasisSettingsDefaults.UseGpuOcclusionCulling,
                BasisSettingsDefaults.UseMirrorQualityOverride,
                BasisSettingsDefaults.MirrorQuality,
                BasisSettingsDefaults.UseCameraClipOverride,
                BasisSettingsDefaults.CameraClipNear,
                BasisSettingsDefaults.CameraClipFar,
                BasisSettingsDefaults.NPEnabled,
                BasisSettingsDefaults.NPMenuOnly,
                BasisSettingsDefaults.NPHoverMenuOnly,
                BasisSettingsDefaults.UsePerfLimitSkinnedMeshes,
                BasisSettingsDefaults.MaxPerfSkinnedMeshes,
                BasisSettingsDefaults.UsePerfLimitBasicMeshes,
                BasisSettingsDefaults.MaxPerfBasicMeshes,
                BasisSettingsDefaults.UsePerfLimitMaterialSlots,
                BasisSettingsDefaults.MaxPerfMaterialSlots,
                BasisSettingsDefaults.UsePerfLimitParticleSystems,
                BasisSettingsDefaults.MaxPerfParticleSystems,
                BasisSettingsDefaults.UsePerfLimitTrailRenderers,
                BasisSettingsDefaults.MaxPerfTrailRenderers,
                BasisSettingsDefaults.UsePerfLimitLineRenderers,
                BasisSettingsDefaults.MaxPerfLineRenderers,
                BasisSettingsDefaults.UsePerfLimitBoundsSize,
                BasisSettingsDefaults.MaxPerfBoundsSize);

            return table;
        }

        private static void Add(Dictionary<string, BasisFrameCostSide> table, BasisFrameCostSide side,
            params IBasisSettingsBinding[] bindings)
        {
            for (int index = 0; index < bindings.Length; index++)
            {
                string key = bindings[index]?.BindingKey;
                if (!string.IsNullOrEmpty(key))
                {
                    table[key] = side;
                }
            }
        }
    }
}
