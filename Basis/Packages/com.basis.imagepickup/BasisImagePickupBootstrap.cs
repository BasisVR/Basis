using UnityEngine;

namespace Basis.ImagePickup
{
    /// <summary>
    /// Arms the image pickup service at startup, plus the desktop file-drop hook on Windows players.
    /// </summary>
    public static class BasisImagePickupBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize()
        {
            BasisImagePickupManager.Initialize();
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            BasisDesktopImageDropHook.Install();
#endif
        }
    }
}
