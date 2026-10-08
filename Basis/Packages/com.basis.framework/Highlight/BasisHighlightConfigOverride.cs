using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using Basis.BasisUI;
using Basis.Scripts.Settings;
using UnityEngine;

namespace Basis.Scripts.BasisSdk.Highlight
{
    /// <summary>
    /// Tints every pickup collider highlight mesh with the user-chosen colour
    /// (<see cref="BasisSettingsDefaults.HighlightColor"/>) via a property block,
    /// so the shared highlight material asset is never modified. An unset colour leaves
    /// the material's own colour. Highlight renderers register on creation and the
    /// colour is re-applied on every settings change.
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisHighlightConfigOverride
    {
        private static Color? _current;

        [RuntimeInitializeOnLoadMethod]
        private static void Initialize()
        {
            BasisSettingsSystem.OnSettingsFinishedChanges += Apply;
        }

        public static void Apply()
        {
            Apply(ParseColor(BasisSettingsDefaults.HighlightColor.RawValue));
        }

        public static void PreviewColor(Color? color)
        {
            Apply(color);
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

        private static void Apply(Color? color)
        {
            _current = color;
            List<BasisHighlightPass> live = BasisHighlightPass.Live;
            for (int i = 0; i < live.Count; i++)
            {
                ApplyTo(live[i], color);
            }
        }

        internal static void ApplyCurrentTo(BasisHighlightPass pass) => ApplyTo(pass, _current);

        private static void ApplyTo(BasisHighlightPass pass, Color? color)
        {
            if (pass == null) return;
            if (color.HasValue) pass.SetOutlineColor(color.Value);
            else pass.ResetOutlineColor();
        }
    }
}
