namespace BattlePhaze.SettingsManager.EditorDefine
{
    using System.Collections.Generic;
    using System;
    using System.Linq;
    using UnityEditor;
    using UnityEditor.Build;

    public static class SettingsManagerDefines
    {
        public static void AddDefineIfNecessary(string define, BuildTargetGroup buildTargetGroup)
        {
            NamedBuildTarget NamedBuildTarget = NamedBuildTarget.FromBuildTargetGroup(buildTargetGroup);
            PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget, out string[] Defines);
            if (Defines.Contains(define) == false)
            {
                List<string> DefinesList = new List<string>
                {
                    define
                };
                DefinesList.AddRange(Defines);
                PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget, DefinesList.ToArray());
            }
        }

        public static bool CheckDefineExistence(string define, BuildTargetGroup buildTargetGroup)
        {
            NamedBuildTarget NamedBuildTarget = NamedBuildTarget.FromBuildTargetGroup(buildTargetGroup);
            var defines = PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget);
            return !string.IsNullOrEmpty(defines) && defines.Contains(define);
        }

        public static void RemoveDefineIfNecessary(string define, BuildTargetGroup buildTargetGroup)
        {
            NamedBuildTarget NamedBuildTarget = NamedBuildTarget.FromBuildTargetGroup(buildTargetGroup);
            PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget, out string[] Defines);
            if (Defines.Contains(define))
            {
                List<string> DefinesList = new List<string>();
                DefinesList.AddRange(Defines);
                DefinesList.Remove(define);
                PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget, DefinesList.ToArray());
            }
        }

        public static void DefineWarmup(SettingsManagerEnums.SupportedRenderPipelines renderPipeline)
        {
            var buildTargetGroup = EditorUserBuildSettings.selectedBuildTargetGroup;
            switch (renderPipeline)
            {
                case SettingsManagerEnums.SupportedRenderPipelines.HighDefinitionRenderPipeline:
                    RemoveDefineIfNecessary("SETTINGS_MANAGER_LEGACY", buildTargetGroup);
                    RemoveDefineIfNecessary("SETTINGS_MANAGER_UNIVERSAL", buildTargetGroup);
                    AddDefineIfNecessary("SETTINGS_MANAGER_HD", buildTargetGroup);
                    break;
                case SettingsManagerEnums.SupportedRenderPipelines.BuiltIn:
                    RemoveDefineIfNecessary("SETTINGS_MANAGER_HD", buildTargetGroup);
                    RemoveDefineIfNecessary("SETTINGS_MANAGER_UNIVERSAL", buildTargetGroup);
                    AddDefineIfNecessary("SETTINGS_MANAGER_LEGACY", buildTargetGroup);
                    break;
                case SettingsManagerEnums.SupportedRenderPipelines.UniversalRenderPipeline:
                    RemoveDefineIfNecessary("SETTINGS_MANAGER_HD", buildTargetGroup);
                    RemoveDefineIfNecessary("SETTINGS_MANAGER_LEGACY", buildTargetGroup);
                    AddDefineIfNecessary("SETTINGS_MANAGER_UNIVERSAL", buildTargetGroup);
                    break;
            }
        }
    }
}