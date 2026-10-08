using Unity.Scripting.LifecycleManagement;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Settings;
using UnityEngine;

namespace Basis.Scripts.UI
{
    /// <summary>
    /// Applies the user-configurable line width and colours to every input device's
    /// UI pointer line and interaction (grab) line. Width drives both lines; the UI
    /// line colour comes from <see cref="BasisSettingsDefaults.RaycastLineColor"/> and
    /// the interaction line colour from <see cref="BasisSettingsDefaults.PickupLineColor"/>.
    /// An unset colour keeps the built-in appearance. Re-applied on every settings change.
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisRaycastLineCustomization
    {
        private static readonly int BaseColorID = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorID = Shader.PropertyToID("_Color");
        private static MaterialPropertyBlock _block;

        [NoAutoStaticsCleanup] private static readonly Gradient DefaultUiGradient = BuildDefaultUiGradient();

        public static float Width => BasisSettingsDefaults.RaycastLineWidth.RawValue;

        [RuntimeInitializeOnLoadMethod]
        private static void Initialize()
        {
            BasisSettingsSystem.OnSettingsFinishedChanges += ApplyToAllDevices;
        }

        public static void ApplyToAllDevices()
        {
            float width = Width;
            BasisPlayerInteract.interactLineWidth = width;

            BasisDeviceManagement instance = BasisDeviceManagement.Instance;
            if (instance == null)
            {
                return;
            }

            Color? uiColor = ParseColor(BasisSettingsDefaults.RaycastLineColor.RawValue);
            Color? lineColor = ParseColor(BasisSettingsDefaults.PickupLineColor.RawValue);

            int count = instance.AllInputDevices.Count;
            for (int Index = 0; Index < count; Index++)
            {
                BasisInput input = instance.AllInputDevices[Index];
                if (input == null)
                {
                    continue;
                }

                ApplyUiLine(GetUiLine(input), width, uiColor);
                ApplyInteractionLine(input.InteractionLineRenderer, width, lineColor);
            }
        }

        public static void StyleUiLine(LineRenderer lineRenderer)
        {
            ApplyUiLine(lineRenderer, Width, ParseColor(BasisSettingsDefaults.RaycastLineColor.RawValue));
        }

        public static void StyleInteractionLine(LineRenderer lineRenderer)
        {
            ApplyInteractionLine(lineRenderer, Width, ParseColor(BasisSettingsDefaults.PickupLineColor.RawValue));
        }

        public static void PreviewWidth(float width)
        {
            BasisPlayerInteract.interactLineWidth = width;
            ForEachDevice(input =>
            {
                LineRenderer ui = GetUiLine(input);
                if (ui != null)
                {
                    ui.widthMultiplier = width;
                }
                if (input.InteractionLineRenderer != null)
                {
                    input.InteractionLineRenderer.widthMultiplier = width;
                }
            });
        }

        public static void PreviewUiLineColor(Color? color)
        {
            ForEachDevice(input => ApplyUiLine(GetUiLine(input), Width, color));
        }

        public static void PreviewInteractionLineColor(Color? color)
        {
            ForEachDevice(input => ApplyInteractionLine(input.InteractionLineRenderer, Width, color));
        }

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

        private static void ForEachDevice(System.Action<BasisInput> action)
        {
            BasisDeviceManagement instance = BasisDeviceManagement.Instance;
            if (instance == null)
            {
                return;
            }
            int count = instance.AllInputDevices.Count;
            for (int Index = 0; Index < count; Index++)
            {
                BasisInput input = instance.AllInputDevices[Index];
                if (input != null)
                {
                    action(input);
                }
            }
        }

        private static LineRenderer GetUiLine(BasisInput input)
        {
            return input.BasisUIRaycast != null ? input.BasisUIRaycast.LineRenderer : null;
        }

        private static void ApplyUiLine(LineRenderer lineRenderer, float width, Color? color)
        {
            if (lineRenderer == null)
            {
                return;
            }
            lineRenderer.widthMultiplier = width;
            lineRenderer.colorGradient = color.HasValue ? SolidGradient(color.Value) : DefaultUiGradient;
        }

        private static void ApplyInteractionLine(LineRenderer lineRenderer, float width, Color? color)
        {
            if (lineRenderer == null)
            {
                return;
            }
            lineRenderer.widthMultiplier = width;

            if (!color.HasValue)
            {
                lineRenderer.SetPropertyBlock(null);
                return;
            }

            if (_block == null)
            {
                _block = new MaterialPropertyBlock();
            }
            lineRenderer.GetPropertyBlock(_block);
            _block.SetColor(BaseColorID, color.Value);
            _block.SetColor(ColorID, color.Value);
            lineRenderer.SetPropertyBlock(_block);
        }

        private static Gradient SolidGradient(Color color)
        {
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(color, 0f), new GradientColorKey(color, 1f) },
                new[] { new GradientAlphaKey(color.a, 0f), new GradientAlphaKey(color.a, 1f) });
            return gradient;
        }

        private static Gradient BuildDefaultUiGradient()
        {
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(0.3019608f, 0.09411766f, 0.2980392f), 0f),
                    new GradientColorKey(new Color(0.1058824f, 0.1411765f, 0.3137255f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, 1f),
                });
            return gradient;
        }
    }
}
