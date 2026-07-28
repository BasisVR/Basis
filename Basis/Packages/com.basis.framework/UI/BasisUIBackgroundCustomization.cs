using Basis.BasisUI;
using Basis.Scripts.Settings;
using UnityEngine;

namespace Basis.Scripts.UI
{
    public static class BasisUIBackgroundCustomization
    {
        private const string BackgroundShaderName = "Basis/UI/Background";

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

        private static Material _runtimeMaterial;
        private static Color _defaultCursorGlowColor = Color.white;

        [RuntimeInitializeOnLoadMethod]
        private static void Initialize()
        {
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
            if (current == _runtimeMaterial)
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
                Apply();
            }

            background.material = _runtimeMaterial;
        }

        public static void Apply()
        {
            if (_runtimeMaterial == null)
            {
                return;
            }

            _runtimeMaterial.SetFloat(AccentAmountID, BasisSettingsDefaults.MenuBGAccentAmount.RawValue);
            _runtimeMaterial.SetFloat(AccentFeatherID, BasisSettingsDefaults.MenuBGAccentFeather.RawValue);
            _runtimeMaterial.SetFloat(AccentSoftnessID, BasisSettingsDefaults.MenuBGAccentSoftness.RawValue);
            _runtimeMaterial.SetFloat(BrandGradientID, BasisSettingsDefaults.MenuBGBrandGradient.RawValue);
            _runtimeMaterial.SetFloat(GradientCycleID, BasisSettingsDefaults.MenuBGGradientCycle.RawValue);
            _runtimeMaterial.SetFloat(AnimationSpeedID, BasisSettingsDefaults.MenuBGAnimationSpeed.RawValue);
            _runtimeMaterial.SetFloat(SheenID, BasisSettingsDefaults.MenuBGSheen.RawValue);
            _runtimeMaterial.SetFloat(CursorGlowID, BasisSettingsDefaults.MenuBGCursorGlow.RawValue);
            _runtimeMaterial.SetFloat(CursorGlowRadiusID, BasisSettingsDefaults.MenuBGCursorGlowRadius.RawValue);
            _runtimeMaterial.SetFloat(VignetteID, BasisSettingsDefaults.MenuBGVignette.RawValue);
            _runtimeMaterial.SetFloat(ExposureID, BasisSettingsDefaults.MenuBGExposure.RawValue);
            _runtimeMaterial.SetFloat(GrainID, BasisSettingsDefaults.MenuBGGrain.RawValue);
            _runtimeMaterial.SetFloat(GrainScaleID, BasisSettingsDefaults.MenuBGGrainScale.RawValue);

            Color? glow = ParseColor(BasisSettingsDefaults.MenuBGCursorGlowColor.RawValue);
            _runtimeMaterial.SetColor(CursorGlowColorID, glow ?? _defaultCursorGlowColor);
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
            if (_runtimeMaterial == null)
            {
                return;
            }
            _runtimeMaterial.SetColor(CursorGlowColorID, color ?? _defaultCursorGlowColor);
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
            if (_runtimeMaterial == null)
            {
                return;
            }
            _runtimeMaterial.SetFloat(propertyID, value);
        }
    }
}
