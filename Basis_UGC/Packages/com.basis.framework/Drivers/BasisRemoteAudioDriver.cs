using Basis.Scripts.Networking.Receivers;
using System;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    public class BasisRemoteAudioDriver : MonoBehaviour
    {
        public BasisAudioAndVisemeDriver BasisAudioAndVisemeDriver;
        public BasisAudioReceiver BasisAudioReceiver;
        public Action<float[],int> AudioData;
        public void OnAudioFilterRead(float[] data, int channels)
        {
            //2048  BasisDebug.Log("data" + data.Length);
            int length = data.Length;
            BasisAudioReceiver.OnAudioFilterRead(data, channels, length);
            BasisAudioAndVisemeDriver.ProcessAudioSamples(data, channels, length);
            AudioData?.Invoke(data, channels);
        }
        public void Initalize(BasisAudioAndVisemeDriver basisVisemeDriver)
        {
            if (basisVisemeDriver != null)
            {
                BasisAudioAndVisemeDriver = basisVisemeDriver;
            }
            else
            {
                this.enabled = false;
            }
        }
    }
}
