using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

#if UNITY_XR_MANAGEMENT
using UnityEngine.XR;
using UnityEngine.Experimental.XR;
using UnityEngine.XR.Management;
using UnityEditor.XR.Management;

namespace Unity.XR.OpenVR
{
    public class OpenVRBuildProcessor : IPreprocessBuildWithReport
    {
        private readonly string[] runtimePluginNames = new string[]
        {
        "XRSDKOpenVR",
        "openvr_api",
        "vc14",
        };

        public int callbackOrder { get; set; }

        public bool ShouldIncludeRuntimePluginsInBuild(string path)
        {
            XRGeneralSettings generalSettings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget));
            if (generalSettings == null)
                return false; 
            
            foreach (var loader in generalSettings.Manager.loaders)
            {
                if (loader is OpenVRLoader)
                    return true;
            }
            return false;
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            var allPlugins = PluginImporter.GetAllImporters();
            foreach (var plugin in allPlugins)
            {
                if (plugin.isNativePlugin)
                {
                    foreach (var pluginName in runtimePluginNames)
                    {
                        if (plugin.assetPath.Contains(pluginName))
                        {
                            plugin.SetIncludeInBuildDelegate(ShouldIncludeRuntimePluginsInBuild);
                            break;
                        }
                    }
                }
            }
        }
    }
}
#endif
