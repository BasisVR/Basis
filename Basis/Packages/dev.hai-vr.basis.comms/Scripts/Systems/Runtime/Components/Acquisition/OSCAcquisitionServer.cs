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
    public class OSCAcquisitionServer : MonoBehaviour
    {
        public static OSCAcquisitionServer SceneInstance => HVRCommsUtil.GetOrCreateSceneInstance(ref _sceneInstance);
        private static OSCAcquisitionServer _sceneInstance;

        private HVROsc _client;
        private OsushiQuery _osushi;
        private const int OurFakeServerPort = 9000;
        private const int ExternalProgramReceiverPort = 9001;
        // Access levels for OSC query nodes. These are arbitrary values that the server uses to indicate the meaning of nodes to clients.
        private const int SubscriptionAccess = 2;
        private const int PublishAccess = 3;

        private bool _settingSubscribed;
        private bool _running;
        private string _lastWakeUp;
        private readonly object _queryLock = new object();
        private OsushiNode _oscQueryRoot;
        private readonly Dictionary<EntityId, SubscriptionSnapshot> _subscriptionSnapshots = new Dictionary<EntityId, SubscriptionSnapshot>();
        private readonly Dictionary<string, int> _exactSubscriptionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _prefixSubscriptionCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        public static void Simulate()
        {
            if (_sceneInstance == null)
            {
                return;
            }

            _sceneInstance.SimulateInstance();
        }

        private sealed class SubscriptionSnapshot
        {
            public HashSet<string> ExactAddresses { get; } = new HashSet<string>(StringComparer.Ordinal);
            public HashSet<string> PrefixAddresses { get; } = new HashSet<string>(StringComparer.Ordinal);
        }

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
                BasisDebug.LogWarning($"Failed to start OSC client ({e.Message})", BasisDebug.LogTag.LocalNetwork);
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

        private void SimulateInstance()
        {
            if (_client == null) return;

            // Hybrid design: background thread receives/decodes UDP, main thread routes callbacks.
            var messages = _client.DrainReceivedMessages();
            BasisOscService.SubmitRawMessages(messages);
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
                    BasisDebug.LogWarning($"Failed to close client ({e.Message})", BasisDebug.LogTag.LocalNetwork);
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
                    BasisDebug.LogWarning($"Failed to close osushi service ({e.Message})", BasisDebug.LogTag.LocalNetwork);
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
                BasisDebug.LogWarning($"Failed to send wake up message ({e.Message})", BasisDebug.LogTag.LocalNetwork);
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
                leaf.ACCESS = PublishAccess;
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
                    BasisDebug.LogWarning($"Failed to publish OSC value ({e.Message})", BasisDebug.LogTag.LocalNetwork);
                }
            }
        }

        public void UpdateSubscriptions(EntityId ownerId, IEnumerable<string> subscribedAddresses, IEnumerable<string> subscribedPrefixes)
        {
            lock (_queryLock)
            {
                EnsureOscQueryRoot();

                SubscriptionSnapshot next = CreateSnapshot(subscribedAddresses, subscribedPrefixes);
                _subscriptionSnapshots.TryGetValue(ownerId, out SubscriptionSnapshot previous);

                UpdateSubscriptionCounts(previous?.ExactAddresses, next.ExactAddresses, _exactSubscriptionCounts, EnsureExactSubscriptionNode, RemoveExactSubscriptionNode);
                UpdateSubscriptionCounts(previous?.PrefixAddresses, next.PrefixAddresses, _prefixSubscriptionCounts, EnsurePrefixSubscriptionNode, RemovePrefixSubscriptionNode);

                if (next.ExactAddresses.Count == 0 && next.PrefixAddresses.Count == 0)
                {
                    _subscriptionSnapshots.Remove(ownerId);
                }
                else
                {
                    _subscriptionSnapshots[ownerId] = next;
                }
            }
        }

        public void ClearSubscriptions(EntityId ownerId)
        {
            lock (_queryLock)
            {
                if (!_subscriptionSnapshots.TryGetValue(ownerId, out SubscriptionSnapshot previous))
                {
                    return;
                }

                UpdateSubscriptionCounts(previous.ExactAddresses, null, _exactSubscriptionCounts, EnsureExactSubscriptionNode, RemoveExactSubscriptionNode);
                UpdateSubscriptionCounts(previous.PrefixAddresses, null, _prefixSubscriptionCounts, EnsurePrefixSubscriptionNode, RemovePrefixSubscriptionNode);
                _subscriptionSnapshots.Remove(ownerId);
            }
        }

        internal void CopyDebugSubscriptionCounts(List<KeyValuePair<string, int>> exactCounts, List<KeyValuePair<string, int>> prefixCounts)
        {
            lock (_queryLock)
            {
                exactCounts.Clear();
                prefixCounts.Clear();

                foreach (KeyValuePair<string, int> count in _exactSubscriptionCounts)
                {
                    exactCounts.Add(count);
                }

                foreach (KeyValuePair<string, int> count in _prefixSubscriptionCounts)
                {
                    prefixCounts.Add(count);
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

            int segmentCount = segments.Length;
            for (int i = 0; i < segmentCount; i++)
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

        private static string NormalizeSubscriptionPath(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return null;
            }

            string normalized = address.Trim();
            if (!normalized.StartsWith("/", StringComparison.Ordinal))
            {
                normalized = "/" + normalized;
            }

            while (normalized.Length > 1 && normalized.EndsWith("/", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(0, normalized.Length - 1);
            }

            return normalized;
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

        private SubscriptionSnapshot CreateSnapshot(IEnumerable<string> subscribedAddresses, IEnumerable<string> subscribedPrefixes)
        {
            SubscriptionSnapshot snapshot = new SubscriptionSnapshot();
            AddNormalizedPaths(snapshot.ExactAddresses, subscribedAddresses);
            AddNormalizedPaths(snapshot.PrefixAddresses, subscribedPrefixes);
            return snapshot;
        }

        private static void AddNormalizedPaths(HashSet<string> target, IEnumerable<string> source)
        {
            if (source == null)
            {
                return;
            }

            foreach (string rawPath in source)
            {
                string normalizedPath = NormalizeSubscriptionPath(rawPath);
                if (normalizedPath != null)
                {
                    target.Add(normalizedPath);
                }
            }
        }

        private void UpdateSubscriptionCounts(
            HashSet<string> previousPaths,
            HashSet<string> nextPaths,
            Dictionary<string, int> counts,
            Action<string> onAdded,
            Action<string> onRemoved)
        {
            if (previousPaths != null)
            {
                foreach (string path in previousPaths)
                {
                    if (nextPaths != null && nextPaths.Contains(path))
                    {
                        continue;
                    }

                    if (counts.TryGetValue(path, out int currentCount))
                    {
                        currentCount--;
                        if (currentCount <= 0)
                        {
                            counts.Remove(path);
                            onRemoved(path);
                        }
                        else
                        {
                            counts[path] = currentCount;
                        }
                    }
                }
            }

            if (nextPaths == null)
            {
                return;
            }

            foreach (string path in nextPaths)
            {
                if (previousPaths != null && previousPaths.Contains(path))
                {
                    continue;
                }

                if (counts.TryGetValue(path, out int currentCount))
                {
                    counts[path] = currentCount + 1;
                    continue;
                }

                counts[path] = 1;
                onAdded(path);
            }
        }

        private void EnsureExactSubscriptionNode(string fullPath)
        {
            OsushiNode node = EnsureQueryNode(fullPath);
            if (node.ACCESS == 0 && node.TYPE == null && node.VALUE == null)
            {
                node.ACCESS = SubscriptionAccess;
            }
        }

        private void EnsurePrefixSubscriptionNode(string fullPath)
        {
            _ = EnsureQueryNode(fullPath);
        }

        private void RemoveExactSubscriptionNode(string fullPath)
        {
            OsushiNode node = ResolveQueryNode(fullPath);
            if (node == null || node == _oscQueryRoot || node.FULL_PATH != fullPath)
            {
                return;
            }

            if (node.ACCESS == SubscriptionAccess && node.TYPE == null && node.VALUE == null)
            {
                node.ACCESS = 0;
            }

            if (!_prefixSubscriptionCounts.ContainsKey(fullPath))
            {
                PruneQueryNode(fullPath);
            }
        }

        private void RemovePrefixSubscriptionNode(string fullPath)
        {
            if (_exactSubscriptionCounts.ContainsKey(fullPath))
            {
                return;
            }

            PruneQueryNode(fullPath);
        }

        private void PruneQueryNode(string fullPath)
        {
            string normalizedPath = NormalizeSubscriptionPath(fullPath);
            if (string.IsNullOrEmpty(normalizedPath) || normalizedPath == "/")
            {
                return;
            }

            string[] segments = normalizedPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            PruneQueryNode(_oscQueryRoot, segments, 0);
        }

        private static bool PruneQueryNode(OsushiNode current, string[] segments, int index)
        {
            if (current == null || current.CONTENTS == null || index >= segments.Length)
            {
                return false;
            }

            string segment = segments[index];
            if (!current.CONTENTS.TryGetValue(segment, out OsushiNode child))
            {
                return false;
            }

            if (index == segments.Length - 1)
            {
                if (CanRemoveNode(child))
                {
                    current.CONTENTS.Remove(segment);
                    return current.CONTENTS.Count == 0;
                }

                return false;
            }

            bool childEmpty = PruneQueryNode(child, segments, index + 1);
            if (childEmpty && CanRemoveNode(child))
            {
                current.CONTENTS.Remove(segment);
            }

            return current.CONTENTS.Count == 0;
        }

        private static bool CanRemoveNode(OsushiNode node)
        {
            bool hasChildren = node.CONTENTS != null && node.CONTENTS.Count > 0;
            bool hasValue = node.VALUE != null;
            bool hasType = !string.IsNullOrEmpty(node.TYPE);
            return !hasChildren && !hasValue && !hasType && node.ACCESS == 0;
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

            int valueCount = values.Length;
            for (int i = 0; i < valueCount; i++)
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

            int valueCount = values.Length;
            for (int i = 0; i < valueCount; i++)
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

            int valueCount = values.Length;
            object[] result = new object[valueCount];
            for (int i = 0; i < valueCount; i++)
            {
                result[i] = values[i]?.ToOscArgument();
            }

            return result;
        }
    }
}
