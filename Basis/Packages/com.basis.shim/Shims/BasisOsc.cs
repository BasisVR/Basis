using System;
using System.Collections.Generic;
using System.Text;
using Cilbox;
using Basis.Scripts.BasisSdk;
using HVR.Basis.Comms;
using HVR.Basis.Comms.OSC;
using UnityEngine;

namespace Basis.Shims
{
    [DisallowMultipleComponent]
    public class BasisOsc : CilboxShim
    {
        public delegate void OscMessageEvent(OscMessage message, OscData[] arguments);
        public delegate void OscValueEvent(OscData value);
        private const string AvatarParametersPrefix = "/avatar/parameters";
        private const string AvatarPublicPrefix = "/avatar/public";
        private const string PropPublishPrefix = "/prop";
        private const string ScenePublishPrefix = "/scene";

        private enum OscScope
        {
            None,
            AvatarLocal,
            AvatarRemote,
            Prop,
            Scene
        }

        public sealed class InspectorState
        {
            public bool HasScope { get; internal set; }
            public string ScopeName { get; internal set; }
            public string PublishPrefix { get; internal set; }
            public string DefaultSubscriptionPrefix { get; internal set; }
            public string EntityId { get; internal set; }
            public bool IsActiveAndEnabled { get; internal set; }
            public bool ReceiveAll { get; internal set; }
            public bool CanPublish { get; internal set; }
            public int OnMessageListenerCount { get; internal set; }
            public int ExactCallbackCount { get; internal set; }
            public int ExactValueCallbackCount { get; internal set; }
            public int PrefixCallbackCount { get; internal set; }
            public int PrefixValueCallbackCount { get; internal set; }
            public string[] ExactSubscriptions { get; internal set; } = Array.Empty<string>();
            public string[] PrefixSubscriptions { get; internal set; } = Array.Empty<string>();
            public string[] ExactRegistrationLines { get; internal set; } = Array.Empty<string>();
            public string[] PrefixRegistrationLines { get; internal set; } = Array.Empty<string>();
        }

        private readonly Dictionary<string, OscMessageEvent> exactCallbacks = new Dictionary<string, OscMessageEvent>(StringComparer.Ordinal);
        private readonly Dictionary<string, OscMessageEvent> prefixCallbacks = new Dictionary<string, OscMessageEvent>(StringComparer.Ordinal);
        private readonly Dictionary<string, OscValueEvent> exactValueCallbacks = new Dictionary<string, OscValueEvent>(StringComparer.Ordinal);
        private readonly Dictionary<string, OscValueEvent> prefixValueCallbacks = new Dictionary<string, OscValueEvent>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> exactCallbackInputs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> prefixCallbackInputs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> exactValueCallbackInputs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> prefixValueCallbackInputs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private int inspectorStateVersion;
        private bool hasCachedScope;
        private bool cachedScopeFound;
        private OscScope cachedScope;
        private string cachedScopePrefix;
        private BasisAvatar cachedScopeAvatar;
        private bool cachedScopeAvatarIsOwnedLocally;

        public OscMessageEvent OnMessage { get; set; }

        private bool receiveAll;
        public bool ReceiveAll
        {
            get => receiveAll;
            set
            {
                if (receiveAll == value)
                {
                    return;
                }

                receiveAll = value;
                MarkInspectorStateDirty();
                SyncQuerySubscriptions();
            }
        }

        private bool isRegistered;
        private void OnEnable()
        {
            InvalidateScopeCache();
            BasisOscService.EnsureInitialized();
            BasisOscService.RegisterReceiver(GetEntityId(), HandleMessage);
            isRegistered = true;
            SyncQuerySubscriptions();
        }

        private void OnDisable()
        {
            if(!isRegistered) return;
            BasisOscService.UnregisterReceiver(GetEntityId());
            BasisOscService.ClearSubscriptions(GetEntityId());
            isRegistered = false;
        }

        private void OnTransformParentChanged()
        {
            InvalidateScopeCache();
            MarkInspectorStateDirty();
            SyncQuerySubscriptions();
        }

        private void OnDestroy()
        {
            if(!isRegistered) return;
            BasisOscService.UnregisterReceiver(GetEntityId());
            BasisOscService.ClearSubscriptions(GetEntityId());
            isRegistered = false;
        }

        public void Subscribe(string address, OscMessageEvent callback)
        {
            Subscribe(address, callback, out _);
        }

        public void Subscribe(string address, OscMessageEvent callback, bool localOnly)
        {
            Subscribe(address, callback, localOnly, out _);
        }

        public void Subscribe(string address, OscMessageEvent callback, out string resolvedAddress)
        {
            Subscribe(address, callback, false, out resolvedAddress);
        }

        public void Subscribe(string address, OscMessageEvent callback, bool localOnly, out string resolvedAddress)
        {
            resolvedAddress = NormalizeSubscriptionAddress(address, localOnly);
            if (resolvedAddress != null)
            {
                AddCallback(exactCallbacks, resolvedAddress, callback);
                TrackInput(exactCallbackInputs, resolvedAddress, address);
                MarkInspectorStateDirty();
                SyncQuerySubscriptions();
            }
        }

        public void SubscribeValue(string address, OscValueEvent callback)
        {
            SubscribeValue(address, callback, out _);
        }

        public void SubscribeValue(string address, OscValueEvent callback, bool localOnly)
        {
            SubscribeValue(address, callback, localOnly, out _);
        }

        public void SubscribeValue(string address, OscValueEvent callback, out string resolvedAddress)
        {
            SubscribeValue(address, callback, false, out resolvedAddress);
        }

        public void SubscribeValue(string address, OscValueEvent callback, bool localOnly, out string resolvedAddress)
        {
            resolvedAddress = NormalizeSubscriptionAddress(address, localOnly);
            if (resolvedAddress != null)
            {
                AddCallback(exactValueCallbacks, resolvedAddress, callback);
                TrackInput(exactValueCallbackInputs, resolvedAddress, address);
                MarkInspectorStateDirty();
                SyncQuerySubscriptions();
            }
        }

        public void SubscribePrefix(string prefix, OscMessageEvent callback)
        {
            SubscribePrefix(prefix, callback, out _);
        }

        public void SubscribePrefix(string prefix, OscMessageEvent callback, out string resolvedAddress)
        {
            resolvedAddress = NormalizeSubscriptionAddress(prefix);
            if (resolvedAddress != null)
            {
                AddCallback(prefixCallbacks, resolvedAddress, callback);
                TrackInput(prefixCallbackInputs, resolvedAddress, prefix);
                MarkInspectorStateDirty();
                SyncQuerySubscriptions();
            }
        }

        public void SubscribePrefixValue(string prefix, OscValueEvent callback)
        {
            SubscribePrefixValue(prefix, callback, out _);
        }

        public void SubscribePrefixValue(string prefix, OscValueEvent callback, out string resolvedAddress)
        {
            resolvedAddress = NormalizeSubscriptionAddress(prefix);
            if (resolvedAddress != null)
            {
                AddCallback(prefixValueCallbacks, resolvedAddress, callback);
                TrackInput(prefixValueCallbackInputs, resolvedAddress, prefix);
                MarkInspectorStateDirty();
                SyncQuerySubscriptions();
            }
        }

        /// <summary>
        /// Removes all subscriptions and handlers for the normalized address. This is the "remove everything for this address"
        /// variant: unlike <see cref="Unsubscribe(string, OscMessageEvent)"/>, which removes a single handler through
        /// <see cref="RemoveCallback{TDelegate}(Dictionary{string, TDelegate}, string, TDelegate)"/>, this overload clears the
        /// full address entry and all delegates that were previously added through <see cref="AddCallback{TDelegate}(Dictionary{string, TDelegate}, string, TDelegate)"/>.
        /// When <see cref="RemoveCallback{TDelegate}(Dictionary{string, TDelegate}, string, TDelegate)"/> receives a null callback it
        /// also removes the whole key, which is the same "remove all" behavior exposed intentionally by <see cref="Unsubscribe(string)"/>.
        /// </summary>
        public void Unsubscribe(string address)
        {
            string normalizedAddress = NormalizeSubscriptionAddress(address);
            if (normalizedAddress != null)
            {
                exactCallbacks.Remove(normalizedAddress);
                exactValueCallbacks.Remove(normalizedAddress);
                exactCallbackInputs.Remove(normalizedAddress);
                exactValueCallbackInputs.Remove(normalizedAddress);
                MarkInspectorStateDirty();
                SyncQuerySubscriptions();
            }
        }

        public void Unsubscribe(string address, OscMessageEvent callback)
        {
            string normalizedAddress = NormalizeSubscriptionAddress(address);
            if (normalizedAddress != null)
            {
                RemoveCallback(exactCallbacks, normalizedAddress, callback);
                RemoveTrackedInputsWhenEmpty(exactCallbacks, exactCallbackInputs, normalizedAddress);
                MarkInspectorStateDirty();
                SyncQuerySubscriptions();
            }
        }

        public void UnsubscribeValue(string address, OscValueEvent callback)
        {
            string normalizedAddress = NormalizeSubscriptionAddress(address);
            if (normalizedAddress != null)
            {
                RemoveCallback(exactValueCallbacks, normalizedAddress, callback);
                RemoveTrackedInputsWhenEmpty(exactValueCallbacks, exactValueCallbackInputs, normalizedAddress);
                MarkInspectorStateDirty();
                SyncQuerySubscriptions();
            }
        }

        public void UnsubscribePrefix(string prefix)
        {
            string normalizedPrefix = NormalizeSubscriptionAddress(prefix);
            if (normalizedPrefix != null)
            {
                prefixCallbacks.Remove(normalizedPrefix);
                prefixValueCallbacks.Remove(normalizedPrefix);
                prefixCallbackInputs.Remove(normalizedPrefix);
                prefixValueCallbackInputs.Remove(normalizedPrefix);
                MarkInspectorStateDirty();
                SyncQuerySubscriptions();
            }
        }

        public void UnsubscribePrefix(string prefix, OscMessageEvent callback)
        {
            string normalizedPrefix = NormalizeSubscriptionAddress(prefix);
            if (normalizedPrefix != null)
            {
                RemoveCallback(prefixCallbacks, normalizedPrefix, callback);
                RemoveTrackedInputsWhenEmpty(prefixCallbacks, prefixCallbackInputs, normalizedPrefix);
                MarkInspectorStateDirty();
                SyncQuerySubscriptions();
            }
        }

        public void UnsubscribePrefixValue(string prefix, OscValueEvent callback)
        {
            string normalizedPrefix = NormalizeSubscriptionAddress(prefix);
            if (normalizedPrefix != null)
            {
                RemoveCallback(prefixValueCallbacks, normalizedPrefix, callback);
                RemoveTrackedInputsWhenEmpty(prefixValueCallbacks, prefixValueCallbackInputs, normalizedPrefix);
                MarkInspectorStateDirty();
                SyncQuerySubscriptions();
            }
        }

        public void ClearSubscriptions()
        {
            exactCallbacks.Clear();
            prefixCallbacks.Clear();
            exactValueCallbacks.Clear();
            prefixValueCallbacks.Clear();
            exactCallbackInputs.Clear();
            prefixCallbackInputs.Clear();
            exactValueCallbackInputs.Clear();
            prefixValueCallbackInputs.Clear();
            MarkInspectorStateDirty();
            SyncQuerySubscriptions();
        }

        public InspectorState GetInspectorState()
        {
            TryGetOscScope(out OscScope scope, out string publishPrefix);
            return new InspectorState
            {
                HasScope = scope != OscScope.None,
                ScopeName = GetScopeName(scope),
                PublishPrefix = publishPrefix,
                DefaultSubscriptionPrefix = GetDefaultSubscriptionPrefix(scope),
                EntityId = GetEntityId().ToString(),
                IsActiveAndEnabled = isActiveAndEnabled,
                ReceiveAll = ReceiveAll,
                CanPublish = scope != OscScope.None && scope != OscScope.AvatarRemote,
                OnMessageListenerCount = GetInvocationCount(OnMessage),
                ExactCallbackCount = exactCallbacks.Count,
                ExactValueCallbackCount = exactValueCallbacks.Count,
                PrefixCallbackCount = prefixCallbacks.Count,
                PrefixValueCallbackCount = prefixValueCallbacks.Count,
                ExactSubscriptions = BuildSortedUnion(exactCallbacks.Keys, exactValueCallbacks.Keys),
                PrefixSubscriptions = BuildSortedUnion(prefixCallbacks.Keys, prefixValueCallbacks.Keys),
                ExactRegistrationLines = BuildRegistrationLines(
                    exactCallbacks, exactCallbackInputs, "Message Callback",
                    exactValueCallbacks, exactValueCallbackInputs, "Value Callback"),
                PrefixRegistrationLines = BuildRegistrationLines(
                    prefixCallbacks, prefixCallbackInputs, "Message Callback",
                    prefixValueCallbacks, prefixValueCallbackInputs, "Value Callback"),
            };
        }

        public int GetInspectorCacheKey()
        {
            unchecked
            {
                int key = inspectorStateVersion;
                key = (key * 397) ^ (isActiveAndEnabled ? 1 : 0);
                key = (key * 397) ^ (ReceiveAll ? 1 : 0);
                key = (key * 397) ^ GetInvocationCount(OnMessage);
                key = (key * 397) ^ (int)GetCurrentScopeForInspector();
                return key;
            }
        }

        public bool IsSubscribed(string address)
        {
            string normalizedAddress = NormalizeSubscriptionAddress(address);
            return normalizedAddress != null &&
                   (exactCallbacks.ContainsKey(normalizedAddress) ||
                    exactValueCallbacks.ContainsKey(normalizedAddress));
        }

        public bool IsPrefixSubscribed(string prefix)
        {
            string normalizedPrefix = NormalizeSubscriptionAddress(prefix);
            return normalizedPrefix != null &&
                   (prefixCallbacks.ContainsKey(normalizedPrefix) ||
                    prefixValueCallbacks.ContainsKey(normalizedPrefix));
        }

        public void PublishValue(string address, OscData value)
        {
            PublishValue(address, value, out _);
        }

        public void PublishValue(string address, OscData value, out string resolvedAddress)
        {
            resolvedAddress = ResolvePublishAddress(address);
            if (resolvedAddress == null)
            {
                return;
            }

            BasisOscService.PublishValue(resolvedAddress, value);
            SubmitPublishedValueToVixxy(resolvedAddress, value);
        }

        public void PublishValues(string address, OscData[] values)
        {
            PublishValues(address, values, out _);
        }

        public void PublishValues(string address, OscData[] values, out string resolvedAddress)
        {
            resolvedAddress = ResolvePublishAddress(address);
            if (resolvedAddress == null)
            {
                return;
            }

            BasisOscService.PublishValues(resolvedAddress, values);
            SubmitPublishedValuesToVixxy(resolvedAddress, values);
        }

        private void HandleMessage(OscMessage message)
        {
            if (message == null)
            {
                return;
            }

            string path = message.Path ?? string.Empty;
            bool matched = ReceiveAll && IsWithinReceiveAllScope(path);

            OscMessageEvent callback = null;
            OscValueEvent valueCallback = null;

            if (exactCallbacks.TryGetValue(path, out OscMessageEvent exactCallback))
            {
                callback += exactCallback;
                matched = true;
            }

            if (exactValueCallbacks.TryGetValue(path, out OscValueEvent exactValueCallback))
            {
                valueCallback += exactValueCallback;
                matched = true;
            }

            #region CollectPrefixCallbacks
            var prefixCallbacksSnapshot = new List<KeyValuePair<string, OscMessageEvent>>(prefixCallbacks);
            foreach (KeyValuePair<string, OscMessageEvent> entry in prefixCallbacksSnapshot)
            {
                if (IsPathWithinPrefix(path, entry.Key))
                {
                    callback += entry.Value;
                    matched = true;
                }
            }

            var prefixValueCallbacksSnapshot = new List<KeyValuePair<string, OscValueEvent>>(prefixValueCallbacks);
            foreach (KeyValuePair<string, OscValueEvent> entry in prefixValueCallbacksSnapshot)
            {
                if (IsPathWithinPrefix(path, entry.Key))
                {
                    valueCallback += entry.Value;
                    matched = true;
                }
            }
            #endregion
            if (!matched)
            {
                return;
            }

            OnMessage?.Invoke(message, message.Arguments);
            callback?.Invoke(message, message.Arguments);
            if (message.Arguments != null && message.Arguments.Length > 0)
            {
                valueCallback?.Invoke(message.Arguments[0]);
            }
        }

        private bool IsWithinReceiveAllScope(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            string receiveAllPrefix = GetReceiveAllPrefix();
            return !string.IsNullOrEmpty(receiveAllPrefix) && IsPathWithinPrefix(path, receiveAllPrefix);
        }

        private void SyncQuerySubscriptions()
        {
            if (!isActiveAndEnabled)
            {
                return;
            }

            HashSet<string> exactAddresses = new HashSet<string>(exactCallbacks.Keys, StringComparer.Ordinal);
            exactAddresses.UnionWith(exactValueCallbacks.Keys);

            HashSet<string> prefixAddresses = new HashSet<string>(prefixCallbacks.Keys, StringComparer.Ordinal);
            prefixAddresses.UnionWith(prefixValueCallbacks.Keys);

            BasisOscService.UpdateSubscriptions(GetEntityId(), ReceiveAll, exactAddresses, prefixAddresses);
        }

        private static string GetScopeName(OscScope scope)
        {
            switch (scope)
            {
                case OscScope.AvatarLocal:
                    return "Avatar (Local)";
                case OscScope.AvatarRemote:
                    return "Avatar (Remote)";
                case OscScope.Prop:
                    return "Prop";
                case OscScope.Scene:
                    return "Scene";
                default:
                    return "None";
            }
        }

        private static string GetDefaultSubscriptionPrefix(OscScope scope)
        {
            switch (scope)
            {
                case OscScope.AvatarRemote:
                case OscScope.Prop:
                case OscScope.Scene:
                    return AvatarPublicPrefix;
                default:
                    return AvatarParametersPrefix;
            }
        }

        private string GetReceiveAllPrefix()
        {
            if (TryGetOscScope(out OscScope scope, out _))
            {
                return GetDefaultSubscriptionPrefix(scope);
            }

            return AvatarParametersPrefix;
        }

        private static int GetInvocationCount(Delegate callback)
        {
            return callback?.GetInvocationList().Length ?? 0;
        }

        private void MarkInspectorStateDirty()
        {
            unchecked
            {
                inspectorStateVersion++;
            }
        }

        private static void TrackInput(Dictionary<string, HashSet<string>> inputs, string normalizedAddress, string rawAddress)
        {
            if (string.IsNullOrEmpty(normalizedAddress))
            {
                return;
            }

            if (!inputs.TryGetValue(normalizedAddress, out HashSet<string> rawInputs))
            {
                rawInputs = new HashSet<string>(StringComparer.Ordinal);
                inputs[normalizedAddress] = rawInputs;
            }

            string raw = string.IsNullOrWhiteSpace(rawAddress) ? normalizedAddress : rawAddress.Trim();
            rawInputs.Add(raw);
        }

        private static void RemoveTrackedInputsWhenEmpty<TDelegate>(
            Dictionary<string, TDelegate> callbacks,
            Dictionary<string, HashSet<string>> trackedInputs,
            string normalizedAddress)
            where TDelegate : Delegate
        {
            if (string.IsNullOrEmpty(normalizedAddress))
            {
                return;
            }

            if (!callbacks.ContainsKey(normalizedAddress))
            {
                trackedInputs.Remove(normalizedAddress);
            }
        }

        private static string[] BuildSortedUnion(params IEnumerable<string>[] sources)
        {
            HashSet<string> merged = new HashSet<string>(StringComparer.Ordinal);
            if (sources != null)
            {
                for (int sourceIndex = 0; sourceIndex < sources.Length; sourceIndex++)
                {
                    IEnumerable<string> source = sources[sourceIndex];
                    if (source == null)
                    {
                        continue;
                    }

                    foreach (string entry in source)
                    {
                        if (!string.IsNullOrEmpty(entry))
                        {
                            merged.Add(entry);
                        }
                    }
                }
            }

            string[] result = new string[merged.Count];
            merged.CopyTo(result);
            Array.Sort(result, StringComparer.Ordinal);
            return result;
        }

        private static string[] BuildRegistrationLines(
            Dictionary<string, OscMessageEvent> messageCallbacks,
            Dictionary<string, HashSet<string>> messageInputs,
            string messageLabel,
            Dictionary<string, OscValueEvent> valueCallbacks,
            Dictionary<string, HashSet<string>> valueInputs,
            string valueLabel)
        {
            List<string> lines = new List<string>();
            AddRegistrationLines(lines, messageCallbacks, messageInputs, messageLabel);
            AddRegistrationLines(lines, valueCallbacks, valueInputs, valueLabel);
            lines.Sort(StringComparer.Ordinal);
            return lines.ToArray();
        }

        private static void AddRegistrationLines<TDelegate>(
            List<string> lines,
            Dictionary<string, TDelegate> callbacks,
            Dictionary<string, HashSet<string>> inputs,
            string label)
            where TDelegate : Delegate
        {
            if (callbacks == null || inputs == null)
            {
                return;
            }

            foreach (KeyValuePair<string, TDelegate> entry in callbacks)
            {
                if (inputs.TryGetValue(entry.Key, out HashSet<string> rawInputs))
                {
                    AddInputLines(lines, label, entry.Key, rawInputs);
                }
            }
        }

        private static void AddInputLines(List<string> lines, string label, string normalizedAddress, HashSet<string> rawInputs)
        {
            if (string.IsNullOrEmpty(normalizedAddress))
            {
                return;
            }

            if (rawInputs == null || rawInputs.Count == 0)
            {
                lines.Add(label + ": " + normalizedAddress);
                return;
            }

            string[] sortedInputs = new string[rawInputs.Count];
            rawInputs.CopyTo(sortedInputs);
            Array.Sort(sortedInputs, StringComparer.Ordinal);

            for (int i = 0; i < sortedInputs.Length; i++)
            {
                string rawInput = sortedInputs[i];
                lines.Add(rawInput == normalizedAddress
                    ? label + ": " + normalizedAddress
                    : label + ": " + rawInput + " -> " + normalizedAddress);
            }
        }

        private string NormalizeSubscriptionAddress(string address)
        {
            return NormalizeSubscriptionAddress(address, false);
        }

        private string NormalizeSubscriptionAddress(string address, bool localOnly)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return null;
            }

            string trimmed = address.Trim();
            if (trimmed.StartsWith("/", StringComparison.Ordinal))
            {
                #region NormalizeAbsoluteSubscriptionAddress
                if (!TryGetOscScope(out OscScope scope, out _))
                {
                    return trimmed;
                }

                if (localOnly && scope == OscScope.AvatarRemote)
                {
                    return null;
                }

                if (scope == OscScope.AvatarRemote)
                {
                    #region NormalizeRemoteAvatarAbsoluteSubscriptionAddress
                    if (IsPathWithinPrefix(trimmed, AvatarParametersPrefix))
                    {
                        return AvatarPublicPrefix + trimmed.Substring(AvatarParametersPrefix.Length);
                    }

                    if (IsPathWithinPrefix(trimmed, AvatarPublicPrefix) || !IsPathWithinPrefix(trimmed, "/avatar"))
                    {
                        return trimmed;
                    }

                    WarnRestrictedAvatarSubscription(address, scope);
                    return null;
                    #endregion
                }

                bool restrictAvatarSubscriptions = scope == OscScope.Prop || scope == OscScope.Scene;
                if (!restrictAvatarSubscriptions || IsPathWithinPrefix(trimmed, AvatarPublicPrefix) || !IsPathWithinPrefix(trimmed, "/avatar"))
                {
                    return trimmed;
                }

                WarnRestrictedAvatarSubscription(address, scope);
                return null;
                #endregion
            }

            trimmed = trimmed.TrimStart('/');
            #region GetDefaultAvatarSubscriptionPrefix
            string defaultPrefix;
            if (TryGetOscScope(out OscScope defaultScope, out _))
            {
                if (localOnly && defaultScope == OscScope.AvatarRemote)
                {
                    return null;
                }

                defaultPrefix = defaultScope == OscScope.AvatarRemote || defaultScope == OscScope.Prop || defaultScope == OscScope.Scene
                    ? AvatarPublicPrefix
                    : AvatarParametersPrefix;
            }
            else
            {
                defaultPrefix = AvatarParametersPrefix;
            }
            #endregion

            return trimmed.Length == 0 ? defaultPrefix : defaultPrefix + "/" + trimmed;
        }

        private string ResolvePublishAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address) || !TryGetOscScope(out OscScope scope, out string prefix))
            {
                return null;
            }

            string trimmed = address.Trim();
            if (scope == OscScope.AvatarRemote)
            {
                return null;
            }

            if (scope == OscScope.AvatarLocal && IsPathWithinPrefix(trimmed, AvatarPublicPrefix))
            {
                return trimmed;
            }

            if (trimmed.StartsWith(prefix, StringComparison.Ordinal) &&
                (trimmed.Length == prefix.Length || trimmed[prefix.Length] == '/'))
            {
                return trimmed;
            }

            trimmed = trimmed.TrimStart('/');
            return trimmed.Length == 0 ? prefix : prefix + "/" + trimmed;
        }

        private void SubmitPublishedValuesToVixxy(string resolvedAddress, OscData[] values) => SubmitPublishedValueToVixxy(resolvedAddress, values?.Length > 0 ? values[0] : null);


        private void SubmitPublishedValueToVixxy(string resolvedAddress, OscData value)
        {
            if (string.IsNullOrWhiteSpace(resolvedAddress) || value == null)
            {
                return;
            }

            if (!TryReadVixxyFloat(value, out float floatValue))
            {
                return;
            }

            if (!TryResolveVixxyAddress(resolvedAddress, out string vixxyAddress))
            {
                return;
            }

            HVRVariableStore variableStore = GetVixxyVariableStore();
            if (variableStore == null)
            {
                return;
            }

            variableStore.SubmitOrDefineDefaultValue(HVRAddress.AddressToId(vixxyAddress), floatValue);
        }

        private HVRVariableStore GetVixxyVariableStore()
        {
            HVRAvatarComms avatarComms = HVRCommsUtil.GetComms(this);
            if (avatarComms != null && avatarComms.VariableStore != null)
            {
                return avatarComms.VariableStore;
            }

            return AcquisitionService.SceneInstance?.VariableStore;
        }

        private static bool TryReadVixxyFloat(OscData value, out float floatValue)
        {
            switch (value.Kind)
            {
                case OscDataKind.Boolean:
                    floatValue = value.BoolValue ? 1f : 0f;
                    return true;
                case OscDataKind.Int32:
                    floatValue = value.IntValue;
                    return true;
                case OscDataKind.Int64:
                    floatValue = value.LongValue;
                    return true;
                case OscDataKind.Float32:
                    floatValue = value.FloatValue;
                    return true;
                case OscDataKind.Float64:
                    floatValue = (float)value.DoubleValue;
                    return true;
                default:
                    floatValue = 0f;
                    return false;
            }
        }

        private static bool TryResolveVixxyAddress(string resolvedAddress, out string vixxyAddress)
        {
            vixxyAddress = null;
            if (string.IsNullOrWhiteSpace(resolvedAddress))
            {
                return false;
            }

            string trimmed = resolvedAddress.Trim();
            if (IsPathWithinPrefix(trimmed, AvatarParametersPrefix))
            {
                vixxyAddress = TrimAddressPrefix(trimmed, AvatarParametersPrefix);
                return !string.IsNullOrEmpty(vixxyAddress);
            }

            if (IsPathWithinPrefix(trimmed, AvatarPublicPrefix))
            {
                vixxyAddress = TrimAddressPrefix(trimmed, AvatarPublicPrefix);
                return !string.IsNullOrEmpty(vixxyAddress);
            }

            const string parametersSegment = "/parameters/";
            int parametersIndex = trimmed.IndexOf(parametersSegment, StringComparison.Ordinal);
            if (parametersIndex >= 0)
            {
                vixxyAddress = trimmed.Substring(parametersIndex + parametersSegment.Length);
                return !string.IsNullOrEmpty(vixxyAddress);
            }

            vixxyAddress = trimmed.TrimStart('/');
            return !string.IsNullOrEmpty(vixxyAddress);
        }

        private static string TrimAddressPrefix(string address, string prefix)
        {
            if (address.Length == prefix.Length)
            {
                return string.Empty;
            }

            return address.Substring(prefix.Length).TrimStart('/');
        }

        private static bool IsPathWithinPrefix(string path, string prefix)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(prefix))
            {
                return false;
            }
            return path.StartsWith(prefix, StringComparison.Ordinal) &&
                   (path.Length == prefix.Length || prefix[prefix.Length - 1] == '/' || path[prefix.Length] == '/');
        }

        private bool TryGetOscScope(out OscScope scope, out string prefix)
        {
            if (hasCachedScope && IsScopeCacheValid())
            {
                scope = cachedScope;
                prefix = cachedScopePrefix;
                return cachedScopeFound;
            }

            cachedScopeFound = TryGetOscScopeUncached(out cachedScope, out cachedScopePrefix, out cachedScopeAvatar);
            cachedScopeAvatarIsOwnedLocally = cachedScopeAvatar != null && cachedScopeAvatar.IsOwnedLocally;
            hasCachedScope = true;

            scope = cachedScope;
            prefix = cachedScopePrefix;
            return cachedScopeFound;
        }

        private bool IsScopeCacheValid()
        {
            if (ReferenceEquals(cachedScopeAvatar, null))
            {
                // Never had an avatar cached - scope is still valid (Prop/Scene/None)
                return true;
            }
            // If avatar was destroyed, cache is invalid
            if (cachedScopeAvatar == null)
            {
                return false;
            }
            return cachedScopeAvatar.IsOwnedLocally == cachedScopeAvatarIsOwnedLocally;
        }

        private void InvalidateScopeCache()
        {
            hasCachedScope = false;
            cachedScopeFound = false;
            cachedScope = OscScope.None;
            cachedScopePrefix = null;
            cachedScopeAvatar = null;
            cachedScopeAvatarIsOwnedLocally = false;
        }

        private bool TryGetOscScopeUncached(out OscScope scope, out string prefix, out BasisAvatar scopeAvatar)
        {
            scope = OscScope.None;
            prefix = null;
            scopeAvatar = null;

            for (Transform current = transform; current != null; current = current.parent)
            {
                if (current.TryGetComponent(out BasisProp prop))
                {
                    scope = OscScope.Prop;
                    prefix = PropPublishPrefix + "/" + GetScopedContentIdentifier(prop) + "/parameters";
                    return true;
                }

                if (current.TryGetComponent(out BasisScene sceneOnTransform))
                {
                    scope = OscScope.Scene;
                    prefix = ScenePublishPrefix + "/" + GetScopedContentIdentifier(sceneOnTransform) + "/parameters";
                    return true;
                }

                if (current.TryGetComponent(out BasisAvatar avatar))
                {
                    scope = avatar.IsOwnedLocally ? OscScope.AvatarLocal : OscScope.AvatarRemote;
                    prefix = avatar.IsOwnedLocally ? AvatarParametersPrefix : null;
                    scopeAvatar = avatar;
                    return true;
                }
            }

            if (BasisScene.SceneTraversalFindBasisScene(gameObject, out BasisScene scene))
            {
                scope = OscScope.Scene;
                prefix = ScenePublishPrefix + "/" + GetScopedContentIdentifier(scene) + "/parameters";
                return true;
            }

            return false;
        }

        private OscScope GetCurrentScopeForInspector()
        {
            TryGetOscScope(out OscScope scope, out _);
            return scope;
        }

        private static void WarnRestrictedAvatarSubscription(string address, OscScope scope)
        {
            BasisDebug.LogWarning(
                $"BasisOsc.NormalizeSubscriptionAddress rejected Subscribe address '{address}' for scope {GetScopeName(scope)}. " +
                $"Only absolute {AvatarPublicPrefix}/* avatar subscriptions are allowed in this scope. " +
                $"Use {AvatarPublicPrefix}/* or a relative address instead of {AvatarParametersPrefix}/*.",
                BasisDebug.LogTag.Shims);
        }

        private static string GetScopedContentIdentifier(BasisNetworkContentBase content)
        {
            if (content != null && content.TryGetNetworkGUIDIdentifier(out string identifier) && !string.IsNullOrWhiteSpace(identifier))
            {
                #region SanitizePathSegment
                StringBuilder builder = new StringBuilder(identifier.Length);
                for (int i = 0; i < identifier.Length; i++)
                {
                    char c = identifier[i];
                    if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                    {
                        builder.Append(c);
                    }
                    else
                    {
                        builder.Append('_');
                        builder.Append(((int)c).ToString("x4"));
                    }
                }

                return builder.ToString();
                #endregion
            }

            ulong fallbackId = content != null ? EntityId.ToULong(content.GetEntityId()) : 0ul;
            return "local-" + fallbackId.ToString("x16");
        }

        private static void AddCallback<TDelegate>(Dictionary<string, TDelegate> callbacks, string key, TDelegate callback)
            where TDelegate : Delegate
        {
            if (callback == null)
            {
                return;
            }

            if (callbacks.TryGetValue(key, out TDelegate existing))
            {
                foreach (Delegate handler in existing.GetInvocationList())
                {
                    if (Equals(handler, callback))
                    {
                        return;
                    }
                }

                callbacks[key] = (TDelegate)Delegate.Combine(existing, callback);
                return;
            }

            callbacks[key] = callback;
        }

        private static void RemoveCallback<TDelegate>(Dictionary<string, TDelegate> callbacks, string key, TDelegate callback)
            where TDelegate : Delegate
        {
            if (callback == null)
            {
                callbacks.Remove(key);
                return;
            }

            if (!callbacks.TryGetValue(key, out TDelegate existing))
            {
                return;
            }

            Delegate updated = Delegate.Remove(existing, callback);
            if (updated == null)
            {
                callbacks.Remove(key);
            }
            else
            {
                callbacks[key] = (TDelegate)updated;
            }
        }
    }
}
