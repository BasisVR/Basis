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
            public int PassiveExactCount { get; internal set; }
            public int ExactCallbackCount { get; internal set; }
            public int ExactValueCallbackCount { get; internal set; }
            public int PassivePrefixCount { get; internal set; }
            public int PrefixCallbackCount { get; internal set; }
            public int PrefixValueCallbackCount { get; internal set; }
            public string[] ExactSubscriptions { get; internal set; } = Array.Empty<string>();
            public string[] PrefixSubscriptions { get; internal set; } = Array.Empty<string>();
            public string[] ExactRegistrationLines { get; internal set; } = Array.Empty<string>();
            public string[] PrefixRegistrationLines { get; internal set; } = Array.Empty<string>();
        }

        private readonly HashSet<string> subscribedAddresses = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> subscribedPrefixes = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, OscMessageEvent> exactCallbacks = new Dictionary<string, OscMessageEvent>(StringComparer.Ordinal);
        private readonly Dictionary<string, OscMessageEvent> prefixCallbacks = new Dictionary<string, OscMessageEvent>(StringComparer.Ordinal);
        private readonly Dictionary<string, OscValueEvent> exactValueCallbacks = new Dictionary<string, OscValueEvent>(StringComparer.Ordinal);
        private readonly Dictionary<string, OscValueEvent> prefixValueCallbacks = new Dictionary<string, OscValueEvent>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> exactAddressInputs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> prefixAddressInputs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> exactCallbackInputs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> prefixCallbackInputs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> exactValueCallbackInputs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> prefixValueCallbackInputs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private int inspectorStateVersion;

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

        private void OnEnable()
        {
            BasisOscService.EnsureInitialized();
            BasisOscService.RegisterReceiver(GetEntityId(), HandleMessage);
            SyncQuerySubscriptions();
        }

        private void OnDisable()
        {
            BasisOscService.UnregisterReceiver(GetEntityId());
            BasisOscService.ClearSubscriptions(GetEntityId());
        }

        private void OnDestroy()
        {
            BasisOscService.UnregisterReceiver(GetEntityId());
            BasisOscService.ClearSubscriptions(GetEntityId());
        }

        public void Subscribe(string address)
        {
            string normalizedAddress = NormalizeSubscriptionAddress(address);
            if (normalizedAddress != null)
            {
                subscribedAddresses.Add(normalizedAddress);
                TrackInput(exactAddressInputs, normalizedAddress, address);
                MarkInspectorStateDirty();
                SyncQuerySubscriptions();
            }
        }

        public void Subscribe(string address, OscMessageEvent callback)
        {
            string normalizedAddress = NormalizeSubscriptionAddress(address);
            if (normalizedAddress != null)
            {
                AddCallback(exactCallbacks, normalizedAddress, callback);
                TrackInput(exactCallbackInputs, normalizedAddress, address);
                MarkInspectorStateDirty();
                SyncQuerySubscriptions();
            }
        }

        public void SubscribeValue(string address, OscValueEvent callback)
        {
            string normalizedAddress = NormalizeSubscriptionAddress(address);
            if (normalizedAddress != null)
            {
                AddCallback(exactValueCallbacks, normalizedAddress, callback);
                TrackInput(exactValueCallbackInputs, normalizedAddress, address);
                MarkInspectorStateDirty();
                SyncQuerySubscriptions();
            }
        }

        public void SubscribePrefix(string prefix)
        {
            string normalizedPrefix = NormalizeSubscriptionAddress(prefix);
            if (normalizedPrefix != null)
            {
                subscribedPrefixes.Add(normalizedPrefix);
                TrackInput(prefixAddressInputs, normalizedPrefix, prefix);
                MarkInspectorStateDirty();
                SyncQuerySubscriptions();
            }
        }

        public void SubscribePrefix(string prefix, OscMessageEvent callback)
        {
            string normalizedPrefix = NormalizeSubscriptionAddress(prefix);
            if (normalizedPrefix != null)
            {
                AddCallback(prefixCallbacks, normalizedPrefix, callback);
                TrackInput(prefixCallbackInputs, normalizedPrefix, prefix);
                MarkInspectorStateDirty();
                SyncQuerySubscriptions();
            }
        }

        public void SubscribePrefixValue(string prefix, OscValueEvent callback)
        {
            string normalizedPrefix = NormalizeSubscriptionAddress(prefix);
            if (normalizedPrefix != null)
            {
                AddCallback(prefixValueCallbacks, normalizedPrefix, callback);
                TrackInput(prefixValueCallbackInputs, normalizedPrefix, prefix);
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
                subscribedAddresses.Remove(normalizedAddress);
                exactCallbacks.Remove(normalizedAddress);
                exactValueCallbacks.Remove(normalizedAddress);
                exactAddressInputs.Remove(normalizedAddress);
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
                subscribedPrefixes.Remove(normalizedPrefix);
                prefixCallbacks.Remove(normalizedPrefix);
                prefixValueCallbacks.Remove(normalizedPrefix);
                prefixAddressInputs.Remove(normalizedPrefix);
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
            subscribedAddresses.Clear();
            subscribedPrefixes.Clear();
            exactCallbacks.Clear();
            prefixCallbacks.Clear();
            exactValueCallbacks.Clear();
            prefixValueCallbacks.Clear();
            exactAddressInputs.Clear();
            prefixAddressInputs.Clear();
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
                PassiveExactCount = subscribedAddresses.Count,
                ExactCallbackCount = exactCallbacks.Count,
                ExactValueCallbackCount = exactValueCallbacks.Count,
                PassivePrefixCount = subscribedPrefixes.Count,
                PrefixCallbackCount = prefixCallbacks.Count,
                PrefixValueCallbackCount = prefixValueCallbacks.Count,
                ExactSubscriptions = BuildSortedUnion(subscribedAddresses, exactCallbacks.Keys, exactValueCallbacks.Keys),
                PrefixSubscriptions = BuildSortedUnion(subscribedPrefixes, prefixCallbacks.Keys, prefixValueCallbacks.Keys),
                ExactRegistrationLines = BuildRegistrationLines(
                    exactAddressInputs, "Passive",
                    exactCallbacks, exactCallbackInputs, "Message Callback",
                    exactValueCallbacks, exactValueCallbackInputs, "Value Callback"),
                PrefixRegistrationLines = BuildRegistrationLines(
                    prefixAddressInputs, "Passive",
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
                   (subscribedAddresses.Contains(normalizedAddress) ||
                    exactCallbacks.ContainsKey(normalizedAddress) ||
                    exactValueCallbacks.ContainsKey(normalizedAddress));
        }

        public bool IsPrefixSubscribed(string prefix)
        {
            string normalizedPrefix = NormalizeSubscriptionAddress(prefix);
            return normalizedPrefix != null &&
                   (subscribedPrefixes.Contains(normalizedPrefix) ||
                    prefixCallbacks.ContainsKey(normalizedPrefix) ||
                    prefixValueCallbacks.ContainsKey(normalizedPrefix));
        }

        public void PublishValue(string address, OscData value)
        {
            string resolvedAddress = ResolvePublishAddress(address);
            if (resolvedAddress == null)
            {
                return;
            }

            BasisOscService.PublishValue(resolvedAddress, value);
        }

        public void PublishValues(string address, OscData[] values)
        {
            string resolvedAddress = ResolvePublishAddress(address);
            if (resolvedAddress == null)
            {
                return;
            }

            BasisOscService.PublishValues(resolvedAddress, values);
        }

        private void HandleMessage(OscMessage message)
        {
            if (message == null)
            {
                return;
            }

            string path = message.Path ?? string.Empty;
            bool matched = ReceiveAll;

            OscMessageEvent callback = null;
            OscValueEvent valueCallback = null;

            if (subscribedAddresses.Contains(path))
            {
                matched = true;
            }

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
            foreach (string prefix in subscribedPrefixes)
            {
                if (IsPathWithinPrefix(path, prefix))
                {
                    matched = true;
                }
            }

            foreach (KeyValuePair<string, OscMessageEvent> entry in prefixCallbacks)
            {
                if (IsPathWithinPrefix(path, entry.Key))
                {
                    callback += entry.Value;
                    matched = true;
                }
            }

            foreach (KeyValuePair<string, OscValueEvent> entry in prefixValueCallbacks)
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

        private void SyncQuerySubscriptions()
        {
            if (!isActiveAndEnabled)
            {
                return;
            }

            HashSet<string> exactAddresses = new HashSet<string>(subscribedAddresses, StringComparer.Ordinal);
            exactAddresses.UnionWith(exactCallbacks.Keys);
            exactAddresses.UnionWith(exactValueCallbacks.Keys);

            HashSet<string> prefixAddresses = new HashSet<string>(subscribedPrefixes, StringComparer.Ordinal);
            prefixAddresses.UnionWith(prefixCallbacks.Keys);
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
            Dictionary<string, HashSet<string>> passiveInputs,
            string passiveLabel,
            Dictionary<string, OscMessageEvent> messageCallbacks,
            Dictionary<string, HashSet<string>> messageInputs,
            string messageLabel,
            Dictionary<string, OscValueEvent> valueCallbacks,
            Dictionary<string, HashSet<string>> valueInputs,
            string valueLabel)
        {
            List<string> lines = new List<string>();
            AddRegistrationLines(lines, passiveInputs, passiveLabel);
            AddRegistrationLines(lines, messageCallbacks, messageInputs, messageLabel);
            AddRegistrationLines(lines, valueCallbacks, valueInputs, valueLabel);
            lines.Sort(StringComparer.Ordinal);
            return lines.ToArray();
        }

        private static void AddRegistrationLines(List<string> lines, Dictionary<string, HashSet<string>> inputs, string label)
        {
            if (inputs == null)
            {
                return;
            }

            foreach (KeyValuePair<string, HashSet<string>> entry in inputs)
            {
                AddInputLines(lines, label, entry.Key, entry.Value);
            }
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

        private static bool IsPathWithinPrefix(string path, string prefix)
        {
            return path.StartsWith(prefix, StringComparison.Ordinal) &&
                   (path.Length == prefix.Length || prefix[prefix.Length - 1] == '/' || path[prefix.Length] == '/');
        }

        private bool TryGetOscScope(out OscScope scope, out string prefix)
        {
            return TryGetOscScope(this, out scope, out prefix);
        }

        private static bool TryGetOscScope(BasisOsc shim, out OscScope scope, out string prefix)
        {
            scope = OscScope.None;
            prefix = null;

            for (Transform current = shim.transform; current != null; current = current.parent)
            {
                BasisProp prop = current.GetComponent<BasisProp>();
                if (prop != null)
                {
                    scope = OscScope.Prop;
                    prefix = PropPublishPrefix + "/" + GetScopedContentIdentifier(prop) + "/parameters";
                    return true;
                }

                BasisScene sceneOnTransform = current.GetComponent<BasisScene>();
                if (sceneOnTransform != null)
                {
                    scope = OscScope.Scene;
                    prefix = ScenePublishPrefix + "/" + GetScopedContentIdentifier(sceneOnTransform) + "/parameters";
                    return true;
                }

                BasisAvatar avatar = current.GetComponent<BasisAvatar>();
                if (avatar != null)
                {
                    scope = avatar.IsOwnedLocally ? OscScope.AvatarLocal : OscScope.AvatarRemote;
                    prefix = avatar.IsOwnedLocally ? AvatarParametersPrefix : null;
                    return true;
                }
            }

            if (BasisScene.SceneTraversalFindBasisScene(shim.gameObject, out BasisScene scene))
            {
                scope = OscScope.Scene;
                prefix = ScenePublishPrefix + "/" + GetScopedContentIdentifier(scene) + "/parameters";
                return true;
            }

            return false;
        }

        private OscScope GetCurrentScopeForInspector()
        {
            return GetCurrentScopeForInspector(this);
        }

        private static OscScope GetCurrentScopeForInspector(BasisOsc shim)
        {
            for (Transform current = shim.transform; current != null; current = current.parent)
            {
                if (current.GetComponent<BasisProp>() != null)
                {
                    return OscScope.Prop;
                }

                if (current.GetComponent<BasisScene>() != null)
                {
                    return OscScope.Scene;
                }

                BasisAvatar avatar = current.GetComponent<BasisAvatar>();
                if (avatar != null)
                {
                    return avatar.IsOwnedLocally ? OscScope.AvatarLocal : OscScope.AvatarRemote;
                }
            }

            if (BasisScene.SceneTraversalFindBasisScene(shim.gameObject, out _))
            {
                return OscScope.Scene;
            }

            return OscScope.None;
        }

        private static void WarnRestrictedAvatarSubscription(string address, OscScope scope)
        {
            Debug.LogWarning(
                $"BasisOsc.NormalizeSubscriptionAddress rejected Subscribe address '{address}' for scope {GetScopeName(scope)}. " +
                $"Only absolute {AvatarPublicPrefix}/* avatar subscriptions are allowed in this scope. " +
                $"Use {AvatarPublicPrefix}/* or a relative address instead of {AvatarParametersPrefix}/*.");
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
