using BattlePhaze.SettingsManager.EditorDefine;
using System.Linq;
using UnityEditor;
namespace BattlePhaze.SettingsManager
{
    public class SettingsManagerEditorDefineManagement
    {
        public static void SetDefines(SettingsManagerEditor editor, SettingsManager manager)
        {
            if (editor.PreCachedPipeline != manager.ManagerSettings.CurrentPipeline && manager != null)
            {
                SettingsManagerDefines.DefineWarmup(manager.ManagerSettings.CurrentPipeline);
            }
            string[] defineNames =
            {
        "SETTINGS_MANAGER_LEGACY",
        "SETTINGS_MANAGER_HD",
        "SETTINGS_MANAGER_UNIVERSAL"
    };
            BuildTargetGroup buildTargetGroup = EditorUserBuildSettings.selectedBuildTargetGroup;
            bool hasAnyDefine = defineNames.Any(defineName => SettingsManagerDefines.CheckDefineExistence(defineName, buildTargetGroup));
            if (!hasAnyDefine)
            {
                SettingsManagerDefines.AddDefineIfNecessary("SETTINGS_MANAGER_LEGACY", buildTargetGroup);
            }
            if ((int)manager.ManagerSettings.CurrentPipeline != manager.ManagerSettings.BuildValues)
            {
                manager.ManagerSettings.BuildValues = (int)manager.ManagerSettings.CurrentPipeline;
                foreach (string defineName in defineNames)
                {
                    SettingsManagerDefines.RemoveDefineIfNecessary(defineName, buildTargetGroup);
                }
                switch (manager.ManagerSettings.CurrentPipeline)
                {
                    case SettingsManagerEnums.SupportedRenderPipelines.HighDefinitionRenderPipeline:
                        SettingsManagerDefines.AddDefineIfNecessary("SETTINGS_MANAGER_HD", buildTargetGroup);
                        break;
                    case SettingsManagerEnums.SupportedRenderPipelines.BuiltIn:
                        SettingsManagerDefines.AddDefineIfNecessary("SETTINGS_MANAGER_LEGACY", buildTargetGroup);
                        break;
                    case SettingsManagerEnums.SupportedRenderPipelines.UniversalRenderPipeline:
                        SettingsManagerDefines.AddDefineIfNecessary("SETTINGS_MANAGER_UNIVERSAL", buildTargetGroup);
                        break;
                }
            }
        }
    }
}