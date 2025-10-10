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
            BasisAvatarSDKInspector.OnBeforeTestInEditor += prefab => BasisAvatarPrefabProcessor(prefab);
        }

        private static GameObject BasisAvatarPrefabProcessor(GameObject copy)
        {
            AvatarProcessor.ProcessAvatar(copy, BasisFrameworkPlatform.Instance);
            return copy;
        }
    }
}
#endif
