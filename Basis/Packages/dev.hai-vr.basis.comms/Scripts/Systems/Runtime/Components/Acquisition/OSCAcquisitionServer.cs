using System;
using System.Collections.Generic;
using Basis.BasisUI;
using HVR.Basis.Comms.OSC;
using HVR.Osushi;
using Newtonsoft.Json;
using UnityEngine;

namespace HVR.Basis.Comms
{
    [AddComponentMenu("HVR.Basis/Comms/Internal/OSC Acquisition Server")]
    internal class OSCAcquisitionServer : MonoBehaviour
    {
        public static OSCAcquisitionServer SceneInstance => HVRCommsUtil.GetOrCreateSceneInstance(ref _sceneInstance);
        private static OSCAcquisitionServer _sceneInstance;

        private HVROsc _client;
        private OsushiQuery _osushi;
        private const int OurFakeServerPort = 9000;
        private const int ExternalProgramReceiverPort = 9001;
        private bool _settingSubscribed;
        private bool _running;
        private string _lastWakeUp;

        public event AddressUpdated OnAddressUpdated;
        public delegate void AddressUpdated(string address, float value);

        internal Dictionary<string, int> _debugUpdates = new();

        private void OnEnable()
        {
            if (!_settingSubscribed)
            {
                BasisSettingsDefaults.EnableOSC.OnChanged += OnEnableOSCChanged;
                _settingSubscribed = true;
            }

            if (!BasisSettingsDefaults.EnableOSC.RawValue)
            {
                // OSC is turned off in settings — stay idle until the user enables it.
                return;
            }

            StartClient();
        }

        private void StartClient()
        {
            if (_running) return;

            try
            {
                _client = new HVROsc(OurFakeServerPort);
                _client.Start();
                _client.SetReceiverOscPort(ExternalProgramReceiverPort);

                var root = OsushiUtil.CreateFaceTrackingNodes();
                var rootText = JsonConvert.SerializeObject(root, Formatting.Indented);
                var avtrText = JsonConvert.SerializeObject(root.CONTENTS["avatar"], Formatting.Indented);
                _osushi = new OsushiQuery(rootText, avtrText);
                _osushi.Start();
                _running = true;

                if (_lastWakeUp != null)
                {
                    // Re-announce avatar presence after a toggle-off/toggle-on cycle.
                    SendWakeUpMessage(_lastWakeUp);
                }
            }
            catch (Exception e)
            {
                // Prevent avatar loading failure (i.e. there are two OSC clients opened on this device)
                Debug.LogWarning($"Failed to start OSC client ({e.Message}");
                StopClient();
            }
        }

        private void OnEnableOSCChanged(bool value)
        {
            if (!isActiveAndEnabled) return;

            if (value)
            {
                StartClient();
            }
            else
            {
                StopClient();
            }
        }

        private void Update()
        {
            if (_client == null) return;

            var messages = _client.PullMessages();
            foreach (var message in messages)
            {
                if (message.arguments.Length > 0)
                {
                    var arg = message.arguments[0];
                    if (arg is float floatValue)
                    {
                        if (_debugUpdates.TryGetValue(message.path, out var count))
                        {
                            _debugUpdates[message.path] = count + 1;
                        }
                        else
                        {
                            _debugUpdates.Add(message.path, 1);
                        }

                        var messagePath = message.path;
                        if (messagePath.StartsWith("/avatar/parameters/"))
                        {
                            messagePath = messagePath.Substring(19);
                        }
                        OnAddressUpdated?.Invoke(messagePath, floatValue);
                    }
                }
            }
        }

        private void OnDisable()
        {
            StopClient();
        }

        private void OnDestroy()
        {
            if (_settingSubscribed)
            {
                BasisSettingsDefaults.EnableOSC.OnChanged -= OnEnableOSCChanged;
                _settingSubscribed = false;
            }
        }

        private void StopClient()
        {
            _running = false;

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

            if (_osushi != null)
            {
                try
                {
                    _osushi.Stop();
                }
                catch (Exception e)
                {
                    // Prevent avatar loading failure
                    Debug.LogWarning($"Failed to close osushi service ({e.Message}");
                }
                _osushi = null;
            }
        }

        public void SendWakeUpMessage(string wakeUp)
        {
            _lastWakeUp = wakeUp;

            if (_client == null) return;

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
