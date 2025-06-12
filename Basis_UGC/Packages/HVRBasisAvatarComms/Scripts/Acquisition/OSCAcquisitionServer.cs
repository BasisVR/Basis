using System;
using HVR.Basis.Comms.OSC;
using UnityEngine;

namespace HVR.Basis.Comms
{
    [AddComponentMenu("HVR.Basis/Comms/Internal/OSC Acquisition Server")]
    internal class OSCAcquisitionServer : MonoBehaviour
    {
        public static OSCAcquisitionServer SceneInstance => CommsUtil.GetOrCreateSceneInstance(ref _sceneInstance);
        private static OSCAcquisitionServer _sceneInstance;

        private HVROsc _client;
        private const int OurFakeServerPort = 9000;
        private const int ExternalProgramReceiverPort = 9001;
        
        public event AddressUpdated OnAddressUpdated;
        public delegate void AddressUpdated(string address, float value);

        private void OnEnable()
        {
            try
            {
                _client = new HVROsc(OurFakeServerPort);
                _client.Start();
                _client.SetReceiverOscPort(ExternalProgramReceiverPort);
            }
            catch (Exception e)
            {
                // Prevent avatar loading failure (i.e. there are two OSC clients opened on this device)
                Debug.LogWarning($"Failed to start OSC client ({e.Message}");
                enabled = false;
            }
        }

        private void Update()
        {
            var messages = _client.PullMessages();
            foreach (var message in messages)
            {
                if (message.arguments.Length > 0)
                {
                    var arg = message.arguments[0];
                    if (arg is float floatValue)
                    {
                        OnAddressUpdated?.Invoke(message.path, floatValue);
                    }
                }
            }
        }

        private void OnDisable()
        {
            if (_client != null)
            {
                try
                {
                    _client.Finish();
                }
                catch (Exception e)
                {
                    // Prevent avatar loading failure (i.e. there are two OSC clients opened on this device)
                    Debug.LogWarning($"Failed to close client ({e.Message}");
                }
                _client = null;
            }
        }

        public void SendWakeUpMessage(string wakeUp)
        {
            try
            {
                _client.SendOsc("/avatar/change", wakeUp);
            }
            catch (Exception e)
            {
                // Prevent avatar loading failure (i.e. there are two OSC clients opened on this device)
                Debug.LogWarning($"Failed to send wake up message ({e.Message}");
            }
        }
    }
}