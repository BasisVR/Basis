using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using Basis.BasisUI;
using Basis.Scripts.Settings;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.Scripts.UI
{

    [AutoStaticsCleanup]
    public static partial class BasisUIBackgroundCustomization
    {
        private const string BackgroundShaderName = "Basis/UI/Background";
        private const string LowTierKeyword = "_BASISBG_LOW";
        private const string VeryLowTierKeyword = "_BASISBG_VERYLOW";

        private static readonly int AccentAmountID = Shader.PropertyToID("_BlendFactor");
        private static readonly int AccentFeatherID = Shader.PropertyToID("_AccentFeather");
        private static readonly int AccentSoftnessID = Shader.PropertyToID("_AccentSoftness");
        private static readonly int BrandGradientID = Shader.PropertyToID("_GradientStrength");
        private static readonly int GradientCycleID = Shader.PropertyToID("_GradientCycle");
        private static readonly int AnimationSpeedID = Shader.PropertyToID("_TimeScale");
        private static readonly int SheenID = Shader.PropertyToID("_SheenStrength");
        private static readonly int CursorGlowColorID = Shader.PropertyToID("_CursorGlowColor");
        private static readonly int CursorGlowID = Shader.PropertyToID("_CursorGlow");
        private static readonly int CursorGlowRadiusID = Shader.PropertyToID("_CursorGlowRadius");
        private static readonly int VignetteID = Shader.PropertyToID("_Vignette");
        private static readonly int ExposureID = Shader.PropertyToID("_Exposure");
        private static readonly int GrainID = Shader.PropertyToID("_Grain");
        private static readonly int GrainScaleID = Shader.PropertyToID("_GrainScale");
        private static readonly int SrcBlendID = Shader.PropertyToID("_BgSrcBlend");
        private static readonly int DstBlendID = Shader.PropertyToID("_BgDstBlend");

        private static readonly List<BasisImageBackground> _backgrounds = new List<BasisImageBackground>();

        private static Material _runtimeMaterial;
        private static Color _defaultCursorGlowColor = Color.white;

        public static BasisMenuBackgroundTier ActiveTier { get; private set; } = BasisMenuBackgroundTier.Full;

        [RuntimeInitializeOnLoadMethod]
        private static void Initialize()
        {
            BasisSettingsSystem.OnSettingsFinishedChanges -= Apply;
            BasisSettingsSystem.OnSettingsFinishedChanges += Apply;
        }

        public static void Register(BasisImageBackground background)
        {
            if (background == null || !Application.isPlaying)
            {
                return;
            }

            Material current = background.material;
            if (current == null || current.shader == null || current.shader.name != BackgroundShaderName)
            {
                return;
            }

            if (_runtimeMaterial == null)
            {
                _defaultCursorGlowColor = current.GetColor(CursorGlowColorID);
                _runtimeMaterial = new Material(current)
                {
                    name = current.name + " (Runtime)",
                    hideFlags = HideFlags.HideAndDontSave,
                };
            }

            background.material = _runtimeMaterial;

            if (!_backgrounds.Contains(background))
            {
                _backgrounds.Add(background);
            }

            Apply();
        }

        private static Material ResolveRendered(int index)
        {
            BasisImageBackground background = _backgrounds[index];
            if (background == null)
            {
                _backgrounds.RemoveAt(index);
                return null;
            }

            Material rendered = background.materialForRendering;
            return rendered == _runtimeMaterial ? null : rendered;
        }

        private static void SetFloatEverywhere(int propertyID, float value)
        {
            if (_runtimeMaterial == null)
            {
                return;
            }

            _runtimeMaterial.SetFloat(propertyID, value);
            for (int i = _backgrounds.Count - 1; i >= 0; i--)
            {
                Material rendered = ResolveRendered(i);
                if (rendered != null)
                {
                    rendered.SetFloat(propertyID, value);
                }
            }
        }

        private static void SetColorEverywhere(int propertyID, Color value)
        {
            if (_runtimeMaterial == null)
            {
                return;
            }

            _runtimeMaterial.SetColor(propertyID, value);
            for (int i = _backgrounds.Count - 1; i >= 0; i--)
            {
                Material rendered = ResolveRendered(i);
                if (rendered != null)
                {
                    rendered.SetColor(propertyID, value);
                }
            }
        }

        public static void Apply()
        {
            if (_runtimeMaterial == null)
            {
                return;
            }

            ApplySettingsTo(_runtimeMaterial);
            for (int i = _backgrounds.Count - 1; i >= 0; i--)
            {
                Material rendered = ResolveRendered(i);
                if (rendered != null)
                {
                    ApplySettingsTo(rendered);
                }
            }
        }

        private static void ApplySettingsTo(Material material)
        {
            material.SetFloat(AccentAmountID, BasisSettingsDefaults.MenuBGAccentAmount.RawValue);
            material.SetFloat(AccentFeatherID, BasisSettingsDefaults.MenuBGAccentFeather.RawValue);
            material.SetFloat(AccentSoftnessID, BasisSettingsDefaults.MenuBGAccentSoftness.RawValue);
            material.SetFloat(BrandGradientID, BasisSettingsDefaults.MenuBGBrandGradient.RawValue);
            material.SetFloat(GradientCycleID, BasisSettingsDefaults.MenuBGGradientCycle.RawValue);
            material.SetFloat(AnimationSpeedID, BasisSettingsDefaults.MenuBGAnimationSpeed.RawValue);
            material.SetFloat(SheenID, BasisSettingsDefaults.MenuBGSheen.RawValue);
            material.SetFloat(CursorGlowID, BasisSettingsDefaults.MenuBGCursorGlow.RawValue);
            material.SetFloat(CursorGlowRadiusID, BasisSettingsDefaults.MenuBGCursorGlowRadius.RawValue);
            material.SetFloat(VignetteID, BasisSettingsDefaults.MenuBGVignette.RawValue);
            material.SetFloat(ExposureID, BasisSettingsDefaults.MenuBGExposure.RawValue);
            material.SetFloat(GrainID, BasisSettingsDefaults.MenuBGGrain.RawValue);
            material.SetFloat(GrainScaleID, BasisSettingsDefaults.MenuBGGrainScale.RawValue);

            Color? glow = ParseColor(BasisSettingsDefaults.MenuBGCursorGlowColor.RawValue);
            material.SetColor(CursorGlowColorID, glow ?? _defaultCursorGlowColor);

            ApplyQualityTier(material);
        }

        public static BasisMenuBackgroundTier ResolveTier(string qualityLevel)
        {
            if (string.IsNullOrEmpty(qualityLevel))
            {
                return BasisMenuBackgroundTier.Full;
            }
            if (string.Equals(qualityLevel, "Very Low", StringComparison.OrdinalIgnoreCase))
            {
                return BasisMenuBackgroundTier.VeryLow;
            }
            if (string.Equals(qualityLevel, "Low", StringComparison.OrdinalIgnoreCase))
            {
                return BasisMenuBackgroundTier.Low;
            }
            return BasisMenuBackgroundTier.Full;
        }

        private static void ApplyQualityTier(Material material)
        {
            BasisMenuBackgroundTier tier = ResolveTier(BasisSettingsDefaults.QualityLevel.RawValue);
            ActiveTier = tier;

            material.DisableKeyword(LowTierKeyword);
            material.DisableKeyword(VeryLowTierKeyword);

            switch (tier)
            {
                case BasisMenuBackgroundTier.VeryLow:
                    material.EnableKeyword(VeryLowTierKeyword);
                    break;
                case BasisMenuBackgroundTier.Low:
                    material.EnableKeyword(LowTierKeyword);
                    break;
            }

            bool opaque = tier != BasisMenuBackgroundTier.Full;
            material.SetFloat(SrcBlendID, (float)(opaque ? BlendMode.One : BlendMode.SrcAlpha));
            material.SetFloat(DstBlendID, (float)(opaque ? BlendMode.Zero : BlendMode.OneMinusSrcAlpha));
        }

        public static void PreviewAccentAmount(float value) => Preview(AccentAmountID, value);

        public static void PreviewAccentFeather(float value) => Preview(AccentFeatherID, value);

        public static void PreviewAccentSoftness(float value) => Preview(AccentSoftnessID, value);

        public static void PreviewBrandGradient(float value) => Preview(BrandGradientID, value);

        public static void PreviewGradientCycle(float value) => Preview(GradientCycleID, value);

        public static void PreviewAnimationSpeed(float value) => Preview(AnimationSpeedID, value);

        public static void PreviewSheen(float value) => Preview(SheenID, value);

        public static void PreviewCursorGlow(float value) => Preview(CursorGlowID, value);

        public static void PreviewCursorGlowRadius(float value) => Preview(CursorGlowRadiusID, value);

        public static void PreviewVignette(float value) => Preview(VignetteID, value);

        public static void PreviewExposure(float value) => Preview(ExposureID, value);

        public static void PreviewGrain(float value) => Preview(GrainID, value);

        public static void PreviewGrainScale(float value) => Preview(GrainScaleID, value);

        public static void PreviewCursorGlowColor(Color? color)
        {
            SetColorEverywhere(CursorGlowColorID, color ?? _defaultCursorGlowColor);
        }

        public static readonly Color DefaultCursorGlowSwatch = new Color(0.62f, 0.216f, 0.341f, 1f);

        public static Color? ParseColor(string hex)
        {
            if (string.IsNullOrEmpty(hex))
            {
                return null;
            }
            if (!hex.StartsWith("#"))
            {
                hex = "#" + hex;
            }
            return ColorUtility.TryParseHtmlString(hex, out Color color) ? color : (Color?)null;
        }

        private static void Preview(int propertyID, float value)
        {
            SetFloatEverywhere(propertyID, value);
        }
    }
}
