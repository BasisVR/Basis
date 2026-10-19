#if UNITY_WEBGL && !UNITY_EDITOR
using Basis.Scripts.Platform;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    public partial class BasisRemoteAudioDriver
    {
        [System.NonSerialized] public int WebVoiceId, WebUnderruns, WebExtraTarget;
        [System.NonSerialized] public bool WebActive;
        [System.NonSerialized] public AudioSource WebSource;

        private void OnEnable()
        {
            WebSource = GetComponent<AudioSource>();
            BasisWebVoiceOutput.Register(this);
        }

        private void OnDisable()
        {
            BasisWebVoiceOutput.Unregister(this);
        }

        public void WebFilterRead(float[] data, int channels)
        {
            OnAudioFilterRead(data, channels);
        }
    }
}
#endif
