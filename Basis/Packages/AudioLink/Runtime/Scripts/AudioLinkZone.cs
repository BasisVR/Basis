#if UDONSHARP
using VRC.SDKBase;
using UdonSharp;
#endif
using UnityEngine;
using UnityEngine.Serialization;

namespace AudioLink
{
    [RequireComponent(typeof(Collider))]
#if UDONSHARP
    public class AudioLinkZone : UdonSharpBehaviour
#else
    public class AudioLinkZone : MonoBehaviour
#endif
    {
        [Header("Targets")]
        public AudioLink audioLink;
        [FormerlySerializedAs("audioSource")]
        public AudioSource targetAudioSource;

        [Header("Settings")]
        public bool disableSource = true;
        public bool enableTarget = true;

#if UDONSHARP
        public override void OnPlayerTriggerEnter(VRCPlayerApi player)
        {
            if (!player.IsValid() || !player.isLocal) return;
#else
        private void OnTriggerEnter(Collider player)
        {
            if (!player.gameObject.CompareTag("Player")) return;
#endif
            if (disableSource && audioLink.audioSource != null) audioLink.audioSource.gameObject.SetActive(false);

            audioLink.audioSource = targetAudioSource;

            if (enableTarget && audioLink.audioSource != null) audioLink.audioSource.gameObject.SetActive(true);
        }
    }
}
