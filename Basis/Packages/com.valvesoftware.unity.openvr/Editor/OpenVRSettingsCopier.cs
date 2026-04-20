using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.Callbacks;
using System.IO;
using System;
using System.Linq;
using System.Text;

#if UNITY_XR_MANAGEMENT
using UnityEngine.XR;
using UnityEngine.Experimental.XR;
using UnityEngine.XR.Management;
using UnityEditor.XR.Management;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
#endif

namespace Unity.XR.OpenVR.Editor
{
    public class OpenVRSettingsCopier
    {
        private const string defaultAssetPath = "Assets/XR/Settings/Open VR Settings.asset";

        private static void CreatePath(string path)
        {
            string[] split = defaultAssetPath.Split('/');
            for (int splitIndex = 1; splitIndex < split.Length; splitIndex++)
            {
                string splitPath = string.Join("/", split, 0, splitIndex);
                if (AssetDatabase.IsValidFolder(splitPath) == false)
                {
                    AssetDatabase.CreateFolder(string.Join("/", split, 0, splitIndex - 1), split[splitIndex-1]);
                    Debug.Log("Created: " + splitPath);
                }
            }
        }

        [PostProcessBuildAttribute(1)]
        public static void OnPostprocessBuild(BuildTarget target, string pathToBuiltProject)
        {
            //make sure we're on a reasonable target
            if (target != BuildTarget.StandaloneLinux64 && target != BuildTarget.StandaloneWindows && target != BuildTarget.StandaloneWindows64)
                return;

#if UNITY_XR_MANAGEMENT
            //make sure we have the xr settings
            XRGeneralSettings generalSettings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget));
            if (generalSettings == null)
                return;

            //make sure our loader is checked
            bool hasLoader = generalSettings.Manager.loaders.Any(loader => loader is OpenVRLoader);
            if (hasLoader == false)
                return;
#endif

            OpenVRSettings settings = OpenVRSettings.GetSettings();

            bool saved = settings.InitializeActionManifestFileRelativeFilePath();

            string settingsAssetPath = AssetDatabase.GetAssetPath(settings);
            if (string.IsNullOrEmpty(settingsAssetPath))
            {
                CreatePath(defaultAssetPath);
                UnityEditor.AssetDatabase.CreateAsset(settings, defaultAssetPath);
                settingsAssetPath = AssetDatabase.GetAssetPath(settings);
            }


            FileInfo buildPath = new FileInfo(pathToBuiltProject);
            string buildName = buildPath.Name.Replace(buildPath.Extension, "");
            DirectoryInfo buildDirectory = buildPath.Directory;

            string dataDirectory = Path.Combine(buildDirectory.FullName, buildName + "_Data");
            if (Directory.Exists(dataDirectory) == false)
            {
                string vsDebugDataDirectory = Path.Combine(buildDirectory.FullName, "build/bin/" + buildName + "_Data");
                if (Directory.Exists(vsDebugDataDirectory) == false)
                {
                    Debug.LogError("<b>[OpenVR]</b> Could not find data directory at: " + dataDirectory + ". Also checked vs debug at: " + vsDebugDataDirectory);
                }
                else
                {
                    dataDirectory = vsDebugDataDirectory;
                }
            }

            string streamingAssets = Path.Combine(dataDirectory, "StreamingAssets");
            if (Directory.Exists(streamingAssets) == false)
                Directory.CreateDirectory(streamingAssets);

            string streamingSteamVR = Path.Combine(streamingAssets, "SteamVR");
            if (Directory.Exists(streamingSteamVR) == false)
                Directory.CreateDirectory(streamingSteamVR);

            Debug.Log("settingsAssetPath: " + settingsAssetPath);

            FileInfo newSettingsPath = new FileInfo(Path.Combine(streamingSteamVR, "OpenVRSettings.asset"));

            if (newSettingsPath.Exists)
            {
                newSettingsPath.IsReadOnly = false;
                newSettingsPath.Delete();
            }

            //File.Copy(currentSettingsPath.FullName, newSettingsPath.FullName);
            File.WriteAllText(newSettingsPath.FullName, CreateSettingText());
            Debug.Log("Wrote openvr settings to build directory: " + newSettingsPath.FullName);
            //Debug.Log("Copied openvr settings to build directory: " + newSettingsPath.FullName);
        }


        private static string CreateSettingText()
        {
            OpenVRSettings settings = OpenVRSettings.GetSettings();
            StringBuilder text = new StringBuilder();
            text.AppendLine("StereoRenderingMode: " + (int)settings.StereoRenderingMode);
            text.AppendLine("InitializationType: " + (int)settings.InitializationType);
            text.AppendLine("EditorAppKey: " + settings.EditorAppKey);
            text.AppendLine("ActionManifestFileRelativeFilePath: " + settings.ActionManifestFileRelativeFilePath);
            text.AppendLine("MirrorView: " + (int)settings.MirrorView);
            return text.ToString();
        }
    }
}