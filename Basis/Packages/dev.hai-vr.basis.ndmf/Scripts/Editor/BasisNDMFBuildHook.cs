#if BASISNDMF_NDMF_IS_INSTALLED
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

namespace HVR.Basis.NDMF
{
    [InitializeOnLoad]
    internal class BasisNDMFBuildHook
    {
        static BasisNDMFBuildHook()
        {
            BasisAssetBundlePipeline.OnBeforeBuildPrefab += (prefab, _) => BasisAvatarPrefabProcessor(prefab);
            // Test In Editor keeps the clone inactive until final build-time conversion is complete.
            // NDMF is a structural processor, so it belongs in the explicit inactive preparation stage.
            BasisAvatarSDKInspector.OnBeforeTestInEditorPrepareInactive += prefab => BasisAvatarPrefabProcessor(prefab);
        }

        private static GameObject BasisAvatarPrefabProcessor(GameObject copy)
        {
            AvatarProcessor.ProcessAvatar(copy, BasisFrameworkPlatform.Instance);
            return copy;
        }
    }
}
#endif
