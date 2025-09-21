using UnityEngine;
public class SMModuleDebugOptions : BasisSettingsBase
{
    public static bool UseGizmos = false;
    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        if (bool.TryParse(optionValue.ToLower(), out bool Selected))
        {
            if (UseGizmos != Selected)
            {
                UseGizmos = Selected;
                BasisDebug.Log($"Gizmo State is {UseGizmos} {Selected}");
                if (UseGizmos)
                {
                    BasisGizmoManager.TryCreateParent();
                }
                BasisGizmoManager.OnUseGizmosChanged?.Invoke(UseGizmos);
                if (UseGizmos == false)
                {
                    BasisGizmoManager.DestroyParent();
                    foreach (BasisGizmos BasisGizmos in BasisGizmoManager.Gizmos.Values)
                    {
                        if (BasisGizmos != null)
                        {
                            GameObject.Destroy(BasisGizmos.gameObject);
                        }
                    }
                    foreach (BasisLineGizmos BasisLineGizmos in BasisGizmoManager.GizmosLine.Values)
                    {
                        if (BasisLineGizmos != null)
                        {
                            GameObject.Destroy(BasisLineGizmos.gameObject);
                        }
                    }
                    BasisGizmoManager.Gizmos.Clear();
                    BasisGizmoManager.GizmosLine.Clear();
                }
            }
        }
    }
}
