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
        private readonly object _queryLock = new object();
        private OsushiNode _oscQueryRoot;

        public event AddressUpdated OnAddressUpdated;
        public delegate void AddressUpdated(string address, float value);

        private void OnEnable()
        {
            if (!_settingSubscribed)
            {
                BasisSettingsDefaults.EnableOSC.OnChanged += OnEnableOSCChanged;
                _settingSubscribed = true;
            }

            if (!BasisSettingsDefaults.EnableOSC.RawValue)
            {
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

                lock (_queryLock)
                {
                    EnsureOscQueryRoot();
                    _osushi = new OsushiQuery(GetOscQueryResponse);
                }

                _osushi.Start();
                _running = true;

                if (_lastWakeUp != null)
                {
                    SendWakeUpMessage(_lastWakeUp);
                }
            }
            catch (Exception e)
            {
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
                try
                {
                    BasisOscService.Publish(OscMessage.FromRaw(message));
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Failed to convert inbound OSC message {message.path} ({e.Message})");
                    continue;
                }

                if (message.arguments.Length > 0)
                {
                    var arg = message.arguments[0];
                    if (arg is float floatValue)
                    {
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
                Debug.LogWarning($"Failed to send wake up message ({e.Message}");
            }
        }

        public void PublishValue(string address, OscData value)
        {
            PublishValues(address, value == null ? Array.Empty<OscData>() : new[] { value });
        }

        public void PublishValues(string address, OscData[] values)
        {
            string normalizedAddress = NormalizeQueryAddress(address);
            if (normalizedAddress == null)
            {
                return;
            }

            lock (_queryLock)
            {
                EnsureOscQueryRoot();
                OsushiNode leaf = EnsureQueryNode(normalizedAddress);
                leaf.ACCESS = 3;
                leaf.TYPE = BuildTypeTag(values);
                leaf.VALUE = BuildQueryValues(values);
            }

            if (_client != null)
            {
                try
                {
                    _client.SendOscMultivalue(normalizedAddress, BuildOscArguments(values));
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Failed to publish OSC value ({e.Message}");
                }
            }
        }

        private string GetOscQueryResponse(string rawUrl)
        {
            lock (_queryLock)
            {
                EnsureOscQueryRoot();
                OsushiNode payload = ResolveQueryNode(rawUrl) ?? _oscQueryRoot;
                return JsonConvert.SerializeObject(payload, Formatting.Indented);
            }
        }

        private void EnsureOscQueryRoot()
        {
            if (_oscQueryRoot == null)
            {
                _oscQueryRoot = OsushiUtil.CreateFaceTrackingNodes();
            }
        }

        private OsushiNode ResolveQueryNode(string rawUrl)
        {
            string requestPath = NormalizeRequestPath(rawUrl);
            if (string.IsNullOrEmpty(requestPath) || requestPath == "/")
            {
                return _oscQueryRoot;
            }

            string[] segments = requestPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            OsushiNode current = _oscQueryRoot;

            for (int i = 0; i < segments.Length; i++)
            {
                if (current.CONTENTS == null || !current.CONTENTS.TryGetValue(segments[i], out current))
                {
                    return _oscQueryRoot;
                }
            }

            return current;
        }

        private static string NormalizeRequestPath(string rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl))
            {
                return "/";
            }

            int queryIndex = rawUrl.IndexOf('?');
            string path = queryIndex >= 0 ? rawUrl.Substring(0, queryIndex) : rawUrl;
            return path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path;
        }

        private static string NormalizeQueryAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return null;
            }

            string trimmed = address.Trim();
            if (trimmed.StartsWith("/", StringComparison.Ordinal))
            {
                return trimmed;
            }

            return "/avatar/parameters/" + trimmed;
        }

        private OsushiNode EnsureQueryNode(string fullPath)
        {
            string[] segments = fullPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            OsushiNode current = _oscQueryRoot;
            string currentPath = string.Empty;

            foreach (string segment in segments)
            {
                current.CONTENTS ??= new Dictionary<string, OsushiNode>();
                currentPath += "/" + segment;
                if (!current.CONTENTS.TryGetValue(segment, out OsushiNode next))
                {
                    next = new OsushiNode
                    {
                        FULL_PATH = currentPath,
                        ACCESS = 0,
                    };
                    current.CONTENTS[segment] = next;
                }

                current = next;
            }

            current.FULL_PATH = fullPath;
            return current;
        }

        private static string BuildTypeTag(OscData[] values)
        {
            if (values == null || values.Length == 0)
            {
                return ",";
            }

            System.Text.StringBuilder builder = new System.Text.StringBuilder(",");
            AppendTypeTags(builder, values);
            return builder.ToString();
        }

        private static void AppendTypeTags(System.Text.StringBuilder builder, OscData[] values)
        {
            if (values == null)
            {
                return;
            }

            for (int i = 0; i < values.Length; i++)
            {
                OscData value = values[i];
                if (value == null)
                {
                    builder.Append('N');
                    continue;
                }

                if (value.Kind == OscDataKind.Array)
                {
                    builder.Append('[');
                    AppendTypeTags(builder, value.Elements);
                    builder.Append(']');
                    continue;
                }

                builder.Append(value.GetTypeTagChar());
            }
        }

        private static List<object> BuildQueryValues(OscData[] values)
        {
            List<object> result = new List<object>();
            if (values == null)
            {
                return result;
            }

            for (int i = 0; i < values.Length; i++)
            {
                result.Add(values[i]?.ToQueryValue());
            }

            return result;
        }

        private static object[] BuildOscArguments(OscData[] values)
        {
            if (values == null || values.Length == 0)
            {
                return Array.Empty<object>();
            }

            object[] result = new object[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                result[i] = values[i]?.ToOscArgument();
            }

            return result;
        }
    }
}
