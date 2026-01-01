using UnityEngine;
using System.Runtime.InteropServices;

namespace Basis.Scripts.Platform
{
    /// <summary>
    /// Configures iOS audio session to output through the main speaker instead of the earpiece.
    /// - Defaults to speaker when no external audio (headphones/AirPods) is connected
    /// - Respects silent mode switch
    /// - Automatically uses external audio devices when connected
    /// </summary>
    public static class BasisIOSAudioSession
    {
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void BasisConfigureAudioSessionForSpeaker();

        [DllImport("__Internal")]
        private static extern void BasisReapplyAudioSession();
#endif

        /// <summary>
        /// Called automatically before scene load to configure iOS audio session.
        /// Sets PlayAndRecord category with DefaultToSpeaker option.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
#if UNITY_IOS && !UNITY_EDITOR
            try
            {
                BasisConfigureAudioSessionForSpeaker();
                Debug.Log("[BasisIOSAudioSession] Audio session configured");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[BasisIOSAudioSession] Failed to configure audio session: {e.Message}");
            }
#endif
        }

        /// <summary>
        /// Re-apply audio session settings. Call this if audio unexpectedly
        /// routes to the wrong output (e.g., after UI sounds or other audio events).
        /// </summary>
        public static void ReapplySettings()
        {
#if UNITY_IOS && !UNITY_EDITOR
            try
            {
                BasisReapplyAudioSession();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[BasisIOSAudioSession] Failed to reapply audio session: {e.Message}");
            }
#endif
        }
    }
}
