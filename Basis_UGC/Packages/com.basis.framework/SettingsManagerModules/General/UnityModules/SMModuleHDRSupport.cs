using UnityEngine;
namespace BattlePhaze.SettingsManager.Intergrations
{
    public class SMModuleHDRSupport : SettingsManagerOption
    {
        public Camera Camera;
        public override void ReceiveOption(SettingsMenuInput Option, SettingsManager Manager = null)
        {
            if (NameReturn(0, Option))
            {
                if (Camera == null)
                {
                    Camera = Camera.main;
                    if (Camera == null)
                    {
                        Camera = FindFirstObjectByType<Camera>();
                        if (Camera == null)
                        {
                            return;
                        }
                    }
                }
                switch (Option.SelectedValue)
                {
                    case "true":
                        Camera.allowHDR = true;
                        break;
                    case "false":
                        Camera.allowHDR = false;
                        break;
                }
            }
        }
    }
}