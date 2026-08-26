using Basis.IK;
using Basis.Scripts.Settings;
using UnityEngine;
namespace Basis.Scripts.Debugging
{
    public sealed class BasisIKSolveGizmoStageInfo
    {
        public readonly BasisIKGizmoStage Stage;
        public readonly string TitleKey, TooltipKey;
        public readonly Color Color;
        public readonly BasisSettingsBinding<bool> Binding;
        public BasisIKSolveGizmoStageInfo(BasisIKGizmoStage stage, string id, string titleKey, string tooltipKey, Color color, bool defaultOn)
        {
            Stage = stage;
            TitleKey = titleKey;
            TooltipKey = tooltipKey;
            Color = color;
            Binding = new BasisSettingsBinding<bool>("gizmoiksolve_" + id, new BasisPlatformDefault<bool>(defaultOn));
        }
    }
    public static class BasisIKSolveGizmoStages
    {
        public const int DrawCapacity = 12288;
        public const int LabelCapacity = 128;
        public static readonly BasisSettingsBinding<bool> Enabled = new("gizmoiksolve_enabled", new BasisPlatformDefault<bool>(false));
        public static readonly BasisSettingsBinding<bool> Labels = new("gizmoiksolve_labels", new BasisPlatformDefault<bool>(false));
        public static readonly BasisSettingsBinding<float> Scale = new("gizmoiksolve_scale", new BasisPlatformDefault<float>(1f));

        /// <summary>Logs one line a second naming which link of the gizmo chain is empty.</summary>
        public static readonly BasisSettingsBinding<bool> Diagnose = new("gizmoiksolve_diagnose", new BasisPlatformDefault<bool>(false));

        /// <summary>Draws a magenta line in front of the camera through BasisGizmoManager alone,
        /// with none of the IK gizmo path in the way. Missing means the break is below this layer.</summary>
        public static readonly BasisSettingsBinding<bool> DiagnoseSelfTest = new("gizmoiksolve_diagnoseselftest", new BasisPlatformDefault<bool>(false));
        public const float ScaleMin = 0.25f;
        public const float ScaleMax = 4f;
        public static readonly BasisIKSolveGizmoStageInfo[] All =
        {
            new(BasisIKGizmoStage.Targets, "targets", "settings.bodyTracking.ikGizmos.targets", "settings.bodyTracking.ikGizmos.targets.tooltip", new Color(1f, 0.84f, 0f, 1f), true),

            new(BasisIKGizmoStage.Spine, "spine", "settings.bodyTracking.ikGizmos.spine", "settings.bodyTracking.ikGizmos.spine.tooltip", new Color(0.2f, 1f, 0.35f, 1f), true),

            new(BasisIKGizmoStage.Shoulders, "shoulders", "settings.bodyTracking.ikGizmos.shoulders", "settings.bodyTracking.ikGizmos.shoulders.tooltip", new Color(1f, 0.35f, 1f, 1f), false),

            new(BasisIKGizmoStage.Legs, "legs", "settings.bodyTracking.ikGizmos.legs", "settings.bodyTracking.ikGizmos.legs.tooltip", new Color(1f, 0.55f, 0.15f, 1f), false),

            new(BasisIKGizmoStage.Arms, "arms", "settings.bodyTracking.ikGizmos.arms", "settings.bodyTracking.ikGizmos.arms.tooltip", new Color(0.2f, 0.9f, 1f, 1f), false),

            new(BasisIKGizmoStage.Toes, "toes", "settings.bodyTracking.ikGizmos.toes", "settings.bodyTracking.ikGizmos.toes.tooltip", new Color(0.55f, 0.75f, 1f, 1f), false),

            new(BasisIKGizmoStage.Overrides, "overrides", "settings.bodyTracking.ikGizmos.overrides", "settings.bodyTracking.ikGizmos.overrides.tooltip", new Color(1f, 0.25f, 0.25f, 1f), false),

            new(BasisIKGizmoStage.Skeleton, "skeleton", "settings.bodyTracking.ikGizmos.skeleton", "settings.bodyTracking.ikGizmos.skeleton.tooltip", new Color(0.7f, 0.7f, 0.7f, 1f), false),

            new(BasisIKGizmoStage.Scratch, "scratch", "settings.bodyTracking.ikGizmos.scratch", "settings.bodyTracking.ikGizmos.scratch.tooltip", new Color(1f, 1f, 1f, 1f), true),

            new(BasisIKGizmoStage.Frames, "frames", "settings.bodyTracking.ikGizmos.frames", "settings.bodyTracking.ikGizmos.frames.tooltip", new Color(0.9f, 0.9f, 0.35f, 1f), false),

            new(BasisIKGizmoStage.Limits, "limits", "settings.bodyTracking.ikGizmos.limits", "settings.bodyTracking.ikGizmos.limits.tooltip", new Color(0.45f, 0.85f, 0.45f, 1f), false),

            new(BasisIKGizmoStage.Reach, "reach", "settings.bodyTracking.ikGizmos.reach", "settings.bodyTracking.ikGizmos.reach.tooltip", new Color(0.6f, 0.9f, 0.6f, 1f), false),

            new(BasisIKGizmoStage.Numbers, "numbers", "settings.bodyTracking.ikGizmos.numbers", "settings.bodyTracking.ikGizmos.numbers.tooltip", new Color(0.85f, 0.85f, 0.85f, 1f), false),
        };
        public static void LoadAll()
        {
            Enabled.ReloadAndNotify();
            Labels.ReloadAndNotify();
            Diagnose.ReloadAndNotify();
            DiagnoseSelfTest.ReloadAndNotify();
            Scale.ReloadAndNotify();
            for (int i = 0; i < All.Length; i++)
            {
                All[i].Binding.ReloadAndNotify();
            }
        }
        public static void ResetToDefaults()
        {
            Enabled.ResetToDefault();
            Labels.ResetToDefault();
            Diagnose.ResetToDefault();
            DiagnoseSelfTest.ResetToDefault();
            Scale.ResetToDefault();
            for (int i = 0; i < All.Length; i++)
            {
                All[i].Binding.ResetToDefault();
            }
        }
        public static int Mask()
        {
            if (!Enabled.RawValue)
            {
                return 0;
            }
            int mask = 0;
            for (int i = 0; i < All.Length; i++)
            {
                if (All[i].Binding.RawValue)
                {
                    mask |= (int)All[i].Stage;
                }
            }
            return mask;
        }
        public static bool Active => Mask() != 0;
        public static uint PackedColor(int index)
        {
            Color32 c = All[index].Color;
            return BasisIKGizmoPalette.Rgba(c.r, c.g, c.b, c.a);
        }
        public static Color ColorForStageIndex(int stageIndex)
        {
            for (int i = 0; i < All.Length; i++)
            {
                if (BasisIKGizmoRecorder.StageIndex(All[i].Stage) == stageIndex)
                {
                    return All[i].Color;
                }
            }
            return Color.white;
        }
    }
}
