using Basis.Scripts.Networking.Receivers;
using SteamAudio;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Bridges Unity's audio callback to the remote voice pipeline.
    /// For each audio frame, mixes network voice via <see cref="BasisAudioReceiver"/>,
    /// runs viseme analysis, and exposes a tap for any listeners via <see cref="AudioData"/>.
    /// </summary>
    public class BasisRemoteAudioDriver : MonoBehaviour
    {
        /// <summary>
        /// Viseme (lip-sync) analysis driver processing audio samples each frame.
        /// </summary>
        [SerializeReference]
        public BasisAudioAndVisemeDriver BasisAudioAndVisemeDriver = new BasisAudioAndVisemeDriver();

        /// <summary>
        /// Remote audio receiver that decodes and mixes network voice.
        /// </summary>
        [SerializeReference]
        public BasisAudioReceiver BasisAudioReceiver = new BasisAudioReceiver();

        /// <summary>
        /// Optional callback invoked after audio is processed:
        /// <c>float[] samples</c> (interleaved per channel), <c>int channels</c>.
        /// </summary>
        public Action<float[], int> AudioData;

        /// <summary>
        /// True once <see cref="Initalize(BasisAudioAndVisemeDriver)"/> has been called.
        /// </summary>
        public bool Initalized = false;

        /// <summary>
        /// Unity audio callback. Mixes network voice, runs viseme processing,
        /// and notifies <see cref="AudioData"/> listeners.
        /// </summary>
        /// <param name="data">Interleaved PCM buffer provided by Unity.</param>
        /// <param name="channels">Number of channels in <paramref name="data"/>.</param>
        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (Initalized)
            {
                int length = data.Length;
                BasisAudioReceiver.OnAudioFilterRead(data, channels, length);
                BasisAudioAndVisemeDriver.ProcessAudioSamples(data, channels, length);
                AudioData?.Invoke(data, channels);
            }
        }
        public void OnDestroy()
        {
            if (BasisAudioAndVisemeDriver != null)
            {
                BasisAudioAndVisemeDriver.OnDestroy();
                Drivers.Remove(BasisAudioAndVisemeDriver);
            }
        }
        /// <summary>
        /// Resets this driver for object pooling without destroying the GameObject.
        /// Performs the same cleanup as OnDestroy but keeps the component alive.
        /// </summary>
        public void ResetForPool()
        {
            Initalized = false;
            if (BasisAudioAndVisemeDriver != null)
            {
                BasisAudioAndVisemeDriver.OnDestroy();
                Drivers.Remove(BasisAudioAndVisemeDriver);
            }
            BasisAudioAndVisemeDriver = null;
            BasisAudioReceiver = null;
            AudioData = null;
        }
        /// <summary>
        /// Initializes the driver with a viseme processor and marks it ready.
        /// </summary>
        /// <param name="basisVisemeDriver">The viseme (lip-sync) driver to use.</param>
        public void Initalize(BasisAudioAndVisemeDriver basisVisemeDriver)
        {
            BasisAudioAndVisemeDriver = basisVisemeDriver;
            if (Drivers.Contains(BasisAudioAndVisemeDriver) == false)
            {
                Drivers.Add(BasisAudioAndVisemeDriver);
            }
            Initalized = true;
        }
        public static void Simulate(float DeltaTime)
        {
            int count = Drivers.Count;
            for (int Index = 0; Index < count; Index++)
            {
                BasisAudioAndVisemeDriver VisemeDriver = Drivers[Index];
                VisemeDriver.Simulate(DeltaTime);
            }

            // Process all pending OpenLipSync contexts in a single batched
            // background task. This replaces per-context Task.Run() which
            // caused thread pool saturation with many players.
            BasisOpenLipSyncContext.ProcessAllPending();
        }
        public static void Apply()
        {
            int count = Drivers.Count;
            for (int Index = 0; Index < count; Index++)
            {
                BasisAudioAndVisemeDriver VisemeDriver = Drivers[Index];
                VisemeDriver.Apply();
            }
        }
        public static List<BasisAudioAndVisemeDriver> Drivers = new List<BasisAudioAndVisemeDriver>();
    }
}
