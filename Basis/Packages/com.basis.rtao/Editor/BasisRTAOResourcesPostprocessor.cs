using Basis.Rendering.RTAO;
using UnityEditor;
using UnityEngine;

namespace Basis.Rendering.RTAO.Editor
{
    public sealed class BasisRTAOResourcesPostprocessor : AssetPostprocessor
    {
        public const string PackagedResourcesPath = "Packages/com.basis.rtao/BasisRTAOResources.asset";

        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            Repair(false);
        }

        public static void CreateOrRepair()
        {
            BasisRTAOResources resources = AssetDatabase.LoadAssetAtPath<BasisRTAOResources>(PackagedResourcesPath);
            if (resources == null)
            {
                resources = ScriptableObject.CreateInstance<BasisRTAOResources>();
                AssetDatabase.CreateAsset(resources, PackagedResourcesPath);
            }

            resources.PopulateFromPackage();
            EditorUtility.SetDirty(resources);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (!resources.HasEveryReference())
                Debug.LogError($"[BasisRTAO] resource references are still incomplete: {resources.DescribeMissing(BasisRTAOBackend.Hardware)} {resources.DescribeMissing(BasisRTAOBackend.ScreenSpace)}");
            else
                Debug.Log("[BasisRTAO] resources are complete.");
        }

        [MenuItem("Basis/Rendering/RTAO/Repair Resources")]
        public static void RepairFromMenu()
        {
            if (Repair(true))
                Debug.Log("[BasisRTAO] resource references repaired.");
            else
                Debug.Log("[BasisRTAO] resource references were already complete.");
        }

        public static bool Repair(bool force)
        {
            BasisRTAOResources resources = AssetDatabase.LoadAssetAtPath<BasisRTAOResources>(PackagedResourcesPath);
            if (resources == null)
                return false;
            if (!force && resources.HasEveryReference())
                return false;

            resources.PopulateFromPackage();
            if (!resources.HasEveryReference())
                return false;

            EditorUtility.SetDirty(resources);
            AssetDatabase.SaveAssetIfDirty(resources);
            return true;
        }
    }
}
