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

        private sealed class OscSubscription
        {
            public OscMessageEvent MessageCallbacks;
            public OscValueEvent ValueCallbacks;
            public readonly HashSet<string> MessageInputs = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> ValueInputs = new HashSet<string>(StringComparer.Ordinal);

            public bool IsEmpty => MessageCallbacks == null && ValueCallbacks == null;
        }

        private readonly Dictionary<string, OscSubscription> exactSubscriptions = new Dictionary<string, OscSubscription>(StringComparer.Ordinal);
        private readonly Dictionary<string, OscSubscription> prefixSubscriptions = new Dictionary<string, OscSubscription>(StringComparer.Ordinal);
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
                if (AddMessageCallback(exactSubscriptions, resolvedAddress, callback, address))
                {
                    MarkInspectorStateDirty();
                    SyncQuerySubscriptions();
                }
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
                if (AddValueCallback(exactSubscriptions, resolvedAddress, callback, address))
                {
                    MarkInspectorStateDirty();
                    SyncQuerySubscriptions();
                }
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
                if (AddMessageCallback(prefixSubscriptions, resolvedAddress, callback, prefix))
                {
                    MarkInspectorStateDirty();
                    SyncQuerySubscriptions();
                }
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
                if (AddValueCallback(prefixSubscriptions, resolvedAddress, callback, prefix))
                {
                    MarkInspectorStateDirty();
                    SyncQuerySubscriptions();
                }
            }
        }

        /// <summary>
        /// Removes all subscriptions and handlers for the normalized address. This is the "remove everything for this address"
        /// variant: unlike <see cref="Unsubscribe(string, OscMessageEvent)"/>, which removes a single handler through
        /// <see cref="RemoveCallback{TDelegate}(TDelegate, TDelegate)"/>, this overload clears the full address entry and all delegates
        /// that were previously added through <see cref="AddCallback{TDelegate}(TDelegate, TDelegate)"/>.
        /// When <see cref="RemoveCallback{TDelegate}(TDelegate, TDelegate)"/> receives a null callback it also removes that callback type,
        /// which is the same "remove all" behavior exposed intentionally by <see cref="Unsubscribe(string)"/>.
        /// </summary>
        public void Unsubscribe(string address)
        {
            string normalizedAddress = NormalizeSubscriptionAddress(address);
            if (normalizedAddress != null)
            {
                exactSubscriptions.Remove(normalizedAddress);
                MarkInspectorStateDirty();
                SyncQuerySubscriptions();
            }
        }

        public void Unsubscribe(string address, OscMessageEvent callback)
        {
            string normalizedAddress = NormalizeSubscriptionAddress(address);
            if (normalizedAddress != null)
            {
                if (RemoveMessageCallback(exactSubscriptions, normalizedAddress, callback))
                {
                    MarkInspectorStateDirty();
                    SyncQuerySubscriptions();
                }
            }
        }

        public void UnsubscribeValue(string address, OscValueEvent callback)
        {
            string normalizedAddress = NormalizeSubscriptionAddress(address);
            if (normalizedAddress != null)
            {
                if (RemoveValueCallback(exactSubscriptions, normalizedAddress, callback))
                {
                    MarkInspectorStateDirty();
                    SyncQuerySubscriptions();
                }
            }
        }

        public void UnsubscribePrefix(string prefix)
        {
            string normalizedPrefix = NormalizeSubscriptionAddress(prefix);
            if (normalizedPrefix != null)
            {
                prefixSubscriptions.Remove(normalizedPrefix);
                MarkInspectorStateDirty();
                SyncQuerySubscriptions();
            }
        }

        public void UnsubscribePrefix(string prefix, OscMessageEvent callback)
        {
            string normalizedPrefix = NormalizeSubscriptionAddress(prefix);
            if (normalizedPrefix != null)
            {
                if (RemoveMessageCallback(prefixSubscriptions, normalizedPrefix, callback))
                {
                    MarkInspectorStateDirty();
                    SyncQuerySubscriptions();
                }
            }
        }

        public void UnsubscribePrefixValue(string prefix, OscValueEvent callback)
        {
            string normalizedPrefix = NormalizeSubscriptionAddress(prefix);
            if (normalizedPrefix != null)
            {
                if (RemoveValueCallback(prefixSubscriptions, normalizedPrefix, callback))
                {
                    MarkInspectorStateDirty();
                    SyncQuerySubscriptions();
                }
            }
        }

        public void ClearSubscriptions()
        {
            exactSubscriptions.Clear();
            prefixSubscriptions.Clear();
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
                ExactCallbackCount = CountMessageSubscriptions(exactSubscriptions),
                ExactValueCallbackCount = CountValueSubscriptions(exactSubscriptions),
                PrefixCallbackCount = CountMessageSubscriptions(prefixSubscriptions),
                PrefixValueCallbackCount = CountValueSubscriptions(prefixSubscriptions),
                ExactSubscriptions = BuildSortedKeys(exactSubscriptions),
                PrefixSubscriptions = BuildSortedKeys(prefixSubscriptions),
                ExactRegistrationLines = BuildRegistrationLines(exactSubscriptions),
                PrefixRegistrationLines = BuildRegistrationLines(prefixSubscriptions),
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
                   exactSubscriptions.ContainsKey(normalizedAddress);
        }

        public bool IsPrefixSubscribed(string prefix)
        {
            string normalizedPrefix = NormalizeSubscriptionAddress(prefix);
            return normalizedPrefix != null &&
                   prefixSubscriptions.ContainsKey(normalizedPrefix);
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

            if (exactSubscriptions.TryGetValue(path, out OscSubscription exactSubscription))
            {
                callback += exactSubscription.MessageCallbacks;
                valueCallback += exactSubscription.ValueCallbacks;
                matched = true;
            }

            #region CollectPrefixCallbacks
            var prefixSubscriptionsSnapshot = new List<KeyValuePair<string, OscSubscription>>(prefixSubscriptions);
            foreach (KeyValuePair<string, OscSubscription> entry in prefixSubscriptionsSnapshot)
            {
                if (IsPathWithinPrefix(path, entry.Key))
                {
                    callback += entry.Value.MessageCallbacks;
                    valueCallback += entry.Value.ValueCallbacks;
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

            BasisOscService.UpdateSubscriptions(GetEntityId(), ReceiveAll, exactSubscriptions.Keys, prefixSubscriptions.Keys);
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

        private static bool AddMessageCallback(
            Dictionary<string, OscSubscription> subscriptions,
            string normalizedAddress,
            OscMessageEvent callback,
            string rawAddress)
        {
            if (callback == null || string.IsNullOrEmpty(normalizedAddress))
            {
                return false;
            }

            OscSubscription subscription = GetOrCreateSubscription(subscriptions, normalizedAddress);
            OscMessageEvent updated = AddCallback(subscription.MessageCallbacks, callback);
            bool changed = !Equals(updated, subscription.MessageCallbacks);
            subscription.MessageCallbacks = updated;
            return TrackInput(subscription.MessageInputs, normalizedAddress, rawAddress) || changed;
        }

        private static bool AddValueCallback(
            Dictionary<string, OscSubscription> subscriptions,
            string normalizedAddress,
            OscValueEvent callback,
            string rawAddress)
        {
            if (callback == null || string.IsNullOrEmpty(normalizedAddress))
            {
                return false;
            }

            OscSubscription subscription = GetOrCreateSubscription(subscriptions, normalizedAddress);
            OscValueEvent updated = AddCallback(subscription.ValueCallbacks, callback);
            bool changed = !Equals(updated, subscription.ValueCallbacks);
            subscription.ValueCallbacks = updated;
            return TrackInput(subscription.ValueInputs, normalizedAddress, rawAddress) || changed;
        }

        private static bool RemoveMessageCallback(
            Dictionary<string, OscSubscription> subscriptions,
            string normalizedAddress,
            OscMessageEvent callback)
        {
            if (string.IsNullOrEmpty(normalizedAddress) ||
                !subscriptions.TryGetValue(normalizedAddress, out OscSubscription subscription))
            {
                return false;
            }

            OscMessageEvent updated = RemoveCallback(subscription.MessageCallbacks, callback);
            if (Equals(updated, subscription.MessageCallbacks))
            {
                return false;
            }

            subscription.MessageCallbacks = updated;
            if (subscription.MessageCallbacks == null)
            {
                subscription.MessageInputs.Clear();
            }

            RemoveSubscriptionIfEmpty(subscriptions, normalizedAddress, subscription);
            return true;
        }

        private static bool RemoveValueCallback(
            Dictionary<string, OscSubscription> subscriptions,
            string normalizedAddress,
            OscValueEvent callback)
        {
            if (string.IsNullOrEmpty(normalizedAddress) ||
                !subscriptions.TryGetValue(normalizedAddress, out OscSubscription subscription))
            {
                return false;
            }

            OscValueEvent updated = RemoveCallback(subscription.ValueCallbacks, callback);
            if (Equals(updated, subscription.ValueCallbacks))
            {
                return false;
            }

            subscription.ValueCallbacks = updated;
            if (subscription.ValueCallbacks == null)
            {
                subscription.ValueInputs.Clear();
            }

            RemoveSubscriptionIfEmpty(subscriptions, normalizedAddress, subscription);
            return true;
        }

        private static OscSubscription GetOrCreateSubscription(Dictionary<string, OscSubscription> subscriptions, string normalizedAddress)
        {
            if (!subscriptions.TryGetValue(normalizedAddress, out OscSubscription subscription))
            {
                subscription = new OscSubscription();
                subscriptions[normalizedAddress] = subscription;
            }

            return subscription;
        }

        private static void RemoveSubscriptionIfEmpty(
            Dictionary<string, OscSubscription> subscriptions,
            string normalizedAddress,
            OscSubscription subscription)
        {
            if (subscription.IsEmpty)
            {
                subscriptions.Remove(normalizedAddress);
            }
        }

        private static bool TrackInput(HashSet<string> inputs, string normalizedAddress, string rawAddress)
        {
            if (inputs == null || string.IsNullOrEmpty(normalizedAddress))
            {
                return false;
            }

            string raw = string.IsNullOrWhiteSpace(rawAddress) ? normalizedAddress : rawAddress.Trim();
            return inputs.Add(raw);
        }

        private static int CountMessageSubscriptions(Dictionary<string, OscSubscription> subscriptions)
        {
            int count = 0;
            foreach (OscSubscription subscription in subscriptions.Values)
            {
                if (subscription.MessageCallbacks != null)
                {
                    count++;
                }
            }

            return count;
        }

        private static int CountValueSubscriptions(Dictionary<string, OscSubscription> subscriptions)
        {
            int count = 0;
            foreach (OscSubscription subscription in subscriptions.Values)
            {
                if (subscription.ValueCallbacks != null)
                {
                    count++;
                }
            }

            return count;
        }

        private static string[] BuildSortedKeys(Dictionary<string, OscSubscription> subscriptions)
        {
            string[] result = new string[subscriptions.Count];
            subscriptions.Keys.CopyTo(result, 0);
            Array.Sort(result, StringComparer.Ordinal);
            return result;
        }

        private static string[] BuildRegistrationLines(Dictionary<string, OscSubscription> subscriptions)
        {
            List<string> lines = new List<string>();
            foreach (KeyValuePair<string, OscSubscription> entry in subscriptions)
            {
                OscSubscription subscription = entry.Value;
                if (subscription.MessageCallbacks != null)
                {
                    AddInputLines(lines, "Message Callback", entry.Key, subscription.MessageInputs);
                }

                if (subscription.ValueCallbacks != null)
                {
                    AddInputLines(lines, "Value Callback", entry.Key, subscription.ValueInputs);
                }
            }

            lines.Sort(StringComparer.Ordinal);
            return lines.ToArray();
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

            int sortedInputCount = sortedInputs.Length;
            for (int i = 0; i < sortedInputCount; i++)
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
                int identifierLength = identifier.Length;
                StringBuilder builder = new StringBuilder(identifierLength);
                for (int i = 0; i < identifierLength; i++)
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

        private static TDelegate AddCallback<TDelegate>(TDelegate existing, TDelegate callback)
            where TDelegate : Delegate
        {
            if (callback == null)
            {
                return existing;
            }

            if (existing != null)
            {
                foreach (Delegate handler in existing.GetInvocationList())
                {
                    if (Equals(handler, callback))
                    {
                        return existing;
                    }
                }

                return (TDelegate)Delegate.Combine(existing, callback);
            }

            return callback;
        }

        private static TDelegate RemoveCallback<TDelegate>(TDelegate existing, TDelegate callback)
            where TDelegate : Delegate
        {
            if (existing == null)
            {
                return null;
            }

            if (callback == null)
            {
                return null;
            }

            Delegate updated = Delegate.Remove(existing, callback);
            return (TDelegate)updated;
        }
    }
}
