using System.IO;
using UnityEditor;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.XR.Management;

namespace Basis.Setup.Modules
{
    public sealed class BasisXRSetup : BasisSetupModuleBase
    {
        private const string TargetPath = "Assets/XR/XRGeneralSettingsPerBuildTarget.asset";
        private const string OpenXRLoader = "UnityEngine.XR.OpenXR.OpenXRLoader";
        private const string OpenVRLoader = "Unity.XR.OpenVR.OpenVRLoader";

        public override string Key => "xr.management";
        public override string DisplayName => "XR Loaders (OpenXR / OpenVR)";
        public override string Category => "XR";
        public override int Version => 1;

        protected override bool Exists()
        {
            return File.Exists(BasisSetupIO.ToFullPath(TargetPath));
        }

        protected override bool Build(BasisSetupMode mode)
        {
            BasisSetupIO.EnsureAssetFolder("Assets/XR");

            XRGeneralSettingsPerBuildTarget perBuildTarget =
                AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(TargetPath);
            bool created = perBuildTarget == null;
            if (created)
            {
                perBuildTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(perBuildTarget, TargetPath);
            }

            EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perBuildTarget, true);

            ConfigureTarget(perBuildTarget, BuildTargetGroup.Standalone, true);
            ConfigureTarget(perBuildTarget, BuildTargetGroup.Android, false);

            EditorUtility.SetDirty(perBuildTarget);
            return created;
        }

        private static void ConfigureTarget(XRGeneralSettingsPerBuildTarget perBuildTarget,
            BuildTargetGroup group, bool includeOpenVR)
        {
            if (perBuildTarget.SettingsForBuildTarget(group) == null)
            {
                perBuildTarget.CreateDefaultManagerSettingsForBuildTarget(group);
            }

            XRGeneralSettings general = perBuildTarget.SettingsForBuildTarget(group);
            if (general == null || general.Manager == null)
            {
                return;
            }

            TryAssign(general.Manager, OpenXRLoader, group);
            if (includeOpenVR)
            {
                TryAssign(general.Manager, OpenVRLoader, group);
            }
        }

        private static void TryAssign(XRManagerSettings manager, string loaderTypeName, BuildTargetGroup group)
        {
            try
            {
                XRPackageMetadataStore.AssignLoader(manager, loaderTypeName, group);
            }
            catch
            {
            }
        }
    }
}
