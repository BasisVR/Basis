using Basis.Scripts.Networking.Receivers;
using System;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    public class BasisRemoteAudioDriver : MonoBehaviour
    {
        public BasisAudioAndVisemeDriver BasisAudioAndVisemeDriver = null;
        public BasisAudioReceiver BasisAudioReceiver = null;
        public Action<float[], int> AudioData;
        public bool Initalized = false;
        public void OnAudioFilterRead(float[] data, int channels)
        {
            if (Initalized)
            {
                int length = data.Length;
                BasisAudioReceiver.OnAudioFilterRead(data, channels, length);
                BasisAudioAndVisemeDriver.ProcessAudioSamples(data, channels, length);
                AudioData?.Invoke(data, channels);
            }
        }
        public void Initalize(BasisAudioAndVisemeDriver basisVisemeDriver)
        {
            if (basisVisemeDriver != null)
            {
                BasisAudioAndVisemeDriver = basisVisemeDriver;
                Initalized = true;
            }
            else
            {
                this.enabled = false;
            }
        }
    }
}
