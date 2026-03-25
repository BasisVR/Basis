using UnityEngine;
using Basis.BasisUI;

public class SMModuleDebugOptions : BasisSettingsBase
{
    public static bool UseGizmos = false;

    // --- Canonical setting key (from defaults) ---
    private static string K_DEBUG_VISUALS => BasisSettingsDefaults.DebugVisuals.BindingKey; // "debug visuals"

    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        // Match only against the canonical binding key
        if (matchedSettingName != K_DEBUG_VISUALS)
            return;

        if (bool.TryParse(optionValue, out bool selected))
        {
#if UNITY_SERVER
            selected = false;
#endif

            if (UseGizmos != selected)
            {
                UseGizmos = selected;
                BasisDebug.Log($"Gizmo State is {UseGizmos} {selected}");

                if (UseGizmos)
                {
                    BasisGizmoManager.TryCreateParent();
                }

                BasisGizmoManager.OnUseGizmosChanged?.Invoke(UseGizmos);

                if (!UseGizmos)
                {
                    BasisGizmoManager.DestroyParent();

                    foreach (BasisGizmos gizmo in BasisGizmoManager.Gizmos.Values)
                    {
                        if (gizmo != null)
                        {
                            GameObject.Destroy(gizmo.gameObject);
                        }
                    }

                    foreach (BasisLineGizmos lineGizmo in BasisGizmoManager.GizmosLine.Values)
                    {
                        if (lineGizmo != null)
                        {
                            GameObject.Destroy(lineGizmo.gameObject);
                        }
                    }

                    BasisGizmoManager.Gizmos.Clear();
                    BasisGizmoManager.GizmosLine.Clear();
                }
            }
        }
    }

    public override void ChangedSettings()
    {
    }
}
