using BattlePhaze.SettingsManager;
using UnityEngine;
public class SMModuleDebugOptions : SettingsManagerOption
{
    public override void ReceiveOption(SettingsMenuInput Option, SettingsManager Manager)
    {
        if (NameReturn(0, Option))
        {
            bool Selected = CheckIsOn(Option.SelectedValue);
            if (BasisGizmoManager.UseGizmos != Selected)
            {
                BasisGizmoManager.UseGizmos = Selected;
                BasisDebug.Log("Gizmo State is " + BasisGizmoManager.UseGizmos + " " + Option.SelectedValue);
                if(BasisGizmoManager.UseGizmos)
                {
                    BasisGizmoManager.TryCreateParent();
                }
                BasisGizmoManager.OnUseGizmosChanged?.Invoke(BasisGizmoManager.UseGizmos);
                if (BasisGizmoManager.UseGizmos == false)
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
