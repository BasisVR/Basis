using Basis.Scripts.Device_Management;
using UnityEngine;

namespace Basis.Integration.AudioLink
{
    /// <summary>
    /// Re-publishes AudioLink globals after a Basis mode switch so consumers that resolve <c>_AudioTexture</c> by global lookup keep working across Desktop/VR transitions.
    /// </summary>
    /// <remarks>Workaround for <see href="https://github.com/llealloo/audiolink/issues/365"/>.</remarks>
    internal static class BasisAudioLinkRepublisher
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Subscribe()
        {
            BasisDeviceManagement.OnBootModeChanged -= OnBootModeChanged;
            BasisDeviceManagement.OnBootModeChanged += OnBootModeChanged;
        }

        private static void OnBootModeChanged(string mode)
        {
            global::AudioLink.AudioLink audioLink = Object.FindFirstObjectByType<global::AudioLink.AudioLink>();
            if (audioLink == null || !audioLink.AudioLinkEnabled)
            {
                return;
            }

            audioLink.AudioLinkEnabled = false;
            audioLink.AudioLinkEnabled = true;
        }
    }
}
