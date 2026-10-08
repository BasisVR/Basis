using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Basis.Setup.Modules
{
    public sealed class BasisQualitySetup : BasisSetupModuleBase
    {
        private const string Folder = "Assets/Basis/Settings/Quality Settiings";
        private const string DesktopPath = Folder + "/Modified - Desktop.asset";

        public override string Key => "quality.pipelines";
        public override string DisplayName => "Quality Pipeline Assets (per platform)";
        public override string Category => "Quality";
        public override int Version => 1;
        public override int Order => 20;

        public override IEnumerable<string> OwnedPaths => new[] { Folder };

        protected override void Activate()
        {
            UniversalRenderPipelineAsset desktop = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(DesktopPath);
            if (desktop != null && GraphicsSettings.defaultRenderPipeline == null)
            {
                GraphicsSettings.defaultRenderPipeline = desktop;
            }

            RegenerateQualityLevels();
        }

        private static void RegenerateQualityLevels()
        {
            Type guard = FindType("BasisQualitySettingsGuard");
            if (guard == null)
            {
                return;
            }

            MethodInfo method = guard.GetMethod("Regenerate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?? guard.GetMethod("ForceRegenerate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            try
            {
                method?.Invoke(null, null);
            }
            catch
            {
            }
        }

        private static Type FindType(string simpleName)
        {
            foreach (Assembly assembly in UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch
                {
                    continue;
                }

                for (int i = 0; i < types.Length; i++)
                {
                    if (types[i] != null && types[i].Name == simpleName)
                    {
                        return types[i];
                    }
                }
            }

            return null;
        }
    }
}
