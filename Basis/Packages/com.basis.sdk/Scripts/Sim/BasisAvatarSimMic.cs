#if !BASIS_FRAMEWORK_EXISTS
using Basis.Scripts.Drivers;
using UnityEngine;

namespace Basis.Scripts.BasisSdk.Players
{
    // Mic capture for the editor preview. Routes raw PCM samples from the OS
    // mic to a BasisAudioAndVisemeDriver via OnAudioFilterRead. AudioSource
    // volume stays at 0 so the mic doesn't echo to speakers.
    [RequireComponent(typeof(AudioSource))]
    public sealed class BasisAvatarSimMic : MonoBehaviour
    {
        public BasisAudioAndVisemeDriver Driver;
        public string DeviceName;
        public bool IsCapturing { get; private set; }

        private AudioSource _source;
        private AudioClip _clip;
        private const int ClipLengthSeconds = 1;

        public bool StartCapture(string device)
        {
            if (IsCapturing) StopCapture();
            if (Microphone.devices.Length == 0) return false;

            DeviceName = string.IsNullOrEmpty(device) ? Microphone.devices[0] : device;

            Microphone.GetDeviceCaps(DeviceName, out int minFreq, out int maxFreq);
            int sampleRate = maxFreq > 0 ? maxFreq : AudioSettings.outputSampleRate;

            _clip = Microphone.Start(DeviceName, true, ClipLengthSeconds, sampleRate);
            if (_clip == null) return false;

            _source = GetComponent<AudioSource>();
            _source.clip = _clip;
            _source.loop = true;
            _source.volume = 0f;
            _source.Play();
            IsCapturing = true;
            return true;
        }

        public void StopCapture()
        {
            if (!IsCapturing) return;
            if (!string.IsNullOrEmpty(DeviceName)) Microphone.End(DeviceName);
            if (_source != null) _source.Stop();
            _clip = null;
            IsCapturing = false;
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            Driver?.ProcessAudioSamples(data, channels, data.Length);
        }

        private void OnDisable() => StopCapture();
    }
}
#endif
