using System.Collections.Generic;
using UnityEditor;
using UnityEditor.XR.Management;
using UnityEngine.XR.Management;

namespace Basis.Setup.Modules
{
    public sealed class BasisXRSetup : BasisSetupModuleBase
    {
        private const string PerBuildTargetPath = "Assets/XR/XRGeneralSettingsPerBuildTarget.asset";

        public override string Key => "xr.management";
        public override string DisplayName => "XR Loaders (OpenXR)";
        public override string Category => "XR";
        public override int Version => 1;

        public override IEnumerable<string> OwnedPaths => new[]
        {
            PerBuildTargetPath,
            "Assets/XR/Loaders/OpenXRLoader.asset",
            "Assets/XR/Settings/OpenXR Editor Settings.asset",
            "Assets/XR/Settings/OpenXR Package Settings.asset",
        };

        protected override void Activate()
        {
            XRGeneralSettingsPerBuildTarget perBuildTarget =
                AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(PerBuildTargetPath);
            if (perBuildTarget != null)
            {
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.settingsKey, perBuildTarget, true);
            }
        }
    }
}
