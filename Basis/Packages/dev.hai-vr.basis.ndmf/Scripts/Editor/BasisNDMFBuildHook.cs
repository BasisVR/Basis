#if BASISNDMF_NDMF_IS_INSTALLED
using Basis.Scripts.BasisSdk;
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
            // OnBeforeBuildPrefab fires for every prefab bundle the SDK builds, props included, and
            // an NDMF build is not a no-op on a non-avatar: BuildContext stamps an NDMFAvatarRoot on
            // the root, and SyncPlatformConfigPass hands the root to
            // BasisFrameworkPlatform.InitFromCommonAvatarInfo, which adds a BasisAvatar when there
            // isn't one. That welded an avatar into every prop bundle built with NDMF installed, and
            // the library — reading the component census — then filed those props under Avatars.
            // Only an avatar has anything here for NDMF to process.
            if (copy == null || copy.GetComponent<BasisAvatar>() == null) return copy;

            AvatarProcessor.ProcessAvatar(copy, BasisFrameworkPlatform.Instance);
            return copy;
        }
    }
}
#endif
