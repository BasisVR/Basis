using System.IO;
using Unity.XR.OpenVR;
using UnityEditor;

namespace Basis.Setup.Modules
{
    public sealed class BasisOpenVRSettingsSetup : BasisSetupModuleBase
    {
        private const string TargetPath = "Assets/XR/Settings/OpenVRSettings.asset";
        private const string SettingsGuid = "eb72c1b2c1c33b14291190a772f5e3df";
        private const string ConfigKey = "Unity.XR.OpenVR.Settings";
        private const string BasisAppKey = "application.generated.unity.basisunity.exe";

        public override string Key => "openvr.settings";
        public override string DisplayName => "OpenVR Settings";
        public override string Category => "XR";
        public override int Version => 1;

        protected override bool Exists()
        {
            return File.Exists(BasisSetupIO.ToFullPath(TargetPath));
        }

        protected override bool Build(BasisSetupMode mode)
        {
            OpenVRSettings settings = OpenVRSettings.GetSettings();
            if (settings == null)
            {
                return false;
            }

            settings.EditorAppKey = BasisAppKey;
            settings.StereoRenderingMode = OpenVRSettings.StereoRenderingModes.SinglePassInstanced;
            settings.InitializationType = OpenVRSettings.InitializationTypes.Scene;
            settings.MirrorView = OpenVRSettings.MirrorViewModes.Right;
            settings.InitializeActionManifestFileRelativeFilePath();

            string assetPath = AssetDatabase.GetAssetPath(settings);
            if (string.IsNullOrEmpty(assetPath))
            {
                BasisSetupIO.EnsureAssetFolder("Assets/XR/Settings");
                AssetDatabase.CreateAsset(settings, TargetPath);
                BasisSetupIO.ForceGuid(TargetPath, SettingsGuid);
            }
            else
            {
                EditorUtility.SetDirty(settings);
            }

            EditorBuildSettings.AddConfigObject(ConfigKey, settings, true);
            return true;
        }
    }
}
