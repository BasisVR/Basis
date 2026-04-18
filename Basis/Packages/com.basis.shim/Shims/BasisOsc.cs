using System;
using System.Collections.Generic;
using System.Text;
using Cilbox;
using Basis.Scripts.BasisSdk;
using HVR.Basis.Comms;
using HVR.Basis.Comms.OSC;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Basis.Shims
{
    [MovedFrom(true, null, null, "BasisOscShim")]
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

        private readonly HashSet<string> subscribedAddresses = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> subscribedPrefixes = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, OscMessageEvent> exactCallbacks = new Dictionary<string, OscMessageEvent>(StringComparer.Ordinal);
        private readonly Dictionary<string, OscMessageEvent> prefixCallbacks = new Dictionary<string, OscMessageEvent>(StringComparer.Ordinal);
        private readonly Dictionary<string, OscValueEvent> exactValueCallbacks = new Dictionary<string, OscValueEvent>(StringComparer.Ordinal);
        private readonly Dictionary<string, OscValueEvent> prefixValueCallbacks = new Dictionary<string, OscValueEvent>(StringComparer.Ordinal);

        public OscMessageEvent OnMessage { get; set; }
        public bool ReceiveAll { get; set; }

        private void OnEnable()
        {
            BasisOscService.MessageReceived -= HandleMessage;
            BasisOscService.MessageReceived += HandleMessage;
        }

        private void OnDisable()
        {
            BasisOscService.MessageReceived -= HandleMessage;
        }

        private void OnDestroy()
        {
            BasisOscService.MessageReceived -= HandleMessage;
        }

        public void Subscribe(string address)
        {
            string normalizedAddress = NormalizeSubscriptionAddress(address);
            if (normalizedAddress != null)
            {
                subscribedAddresses.Add(normalizedAddress);
            }
        }

        public void Subscribe(string address, OscMessageEvent callback)
        {
            string normalizedAddress = NormalizeSubscriptionAddress(address);
            if (normalizedAddress != null)
            {
                AddCallback(exactCallbacks, normalizedAddress, callback);
            }
        }

        public void SubscribeValue(string address, OscValueEvent callback)
        {
            string normalizedAddress = NormalizeSubscriptionAddress(address);
            if (normalizedAddress != null)
            {
                AddCallback(exactValueCallbacks, normalizedAddress, callback);
            }
        }

        public void SubscribePrefix(string prefix)
        {
            string normalizedPrefix = NormalizeSubscriptionAddress(prefix);
            if (normalizedPrefix != null)
            {
                subscribedPrefixes.Add(normalizedPrefix);
            }
        }

        public void SubscribePrefix(string prefix, OscMessageEvent callback)
        {
            string normalizedPrefix = NormalizeSubscriptionAddress(prefix);
            if (normalizedPrefix != null)
            {
                AddCallback(prefixCallbacks, normalizedPrefix, callback);
            }
        }

        public void SubscribePrefixValue(string prefix, OscValueEvent callback)
        {
            string normalizedPrefix = NormalizeSubscriptionAddress(prefix);
            if (normalizedPrefix != null)
            {
                AddCallback(prefixValueCallbacks, normalizedPrefix, callback);
            }
        }

        public void Unsubscribe(string address)
        {
            string normalizedAddress = NormalizeSubscriptionAddress(address);
            if (normalizedAddress != null)
            {
                subscribedAddresses.Remove(normalizedAddress);
                exactCallbacks.Remove(normalizedAddress);
                exactValueCallbacks.Remove(normalizedAddress);
            }
        }

        public void Unsubscribe(string address, OscMessageEvent callback)
        {
            string normalizedAddress = NormalizeSubscriptionAddress(address);
            if (normalizedAddress != null)
            {
                RemoveCallback(exactCallbacks, normalizedAddress, callback);
            }
        }

        public void UnsubscribeValue(string address, OscValueEvent callback)
        {
            string normalizedAddress = NormalizeSubscriptionAddress(address);
            if (normalizedAddress != null)
            {
                RemoveCallback(exactValueCallbacks, normalizedAddress, callback);
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
            }
        }

        public void UnsubscribePrefix(string prefix, OscMessageEvent callback)
        {
            string normalizedPrefix = NormalizeSubscriptionAddress(prefix);
            if (normalizedPrefix != null)
            {
                RemoveCallback(prefixCallbacks, normalizedPrefix, callback);
            }
        }

        public void UnsubscribePrefixValue(string prefix, OscValueEvent callback)
        {
            string normalizedPrefix = NormalizeSubscriptionAddress(prefix);
            if (normalizedPrefix != null)
            {
                RemoveCallback(prefixValueCallbacks, normalizedPrefix, callback);
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
                if (path.StartsWith(prefix, StringComparison.Ordinal))
                {
                    matched = true;
                }
            }

            foreach (KeyValuePair<string, OscMessageEvent> entry in prefixCallbacks)
            {
                if (path.StartsWith(entry.Key, StringComparison.Ordinal))
                {
                    callback += entry.Value;
                    matched = true;
                }
            }

            foreach (KeyValuePair<string, OscValueEvent> entry in prefixValueCallbacks)
            {
                if (path.StartsWith(entry.Key, StringComparison.Ordinal))
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
            valueCallback?.Invoke(message.Arguments != null && message.Arguments.Length > 0 ? message.Arguments[0] : null);
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

                    return IsPathWithinPrefix(trimmed, AvatarPublicPrefix) || !IsPathWithinPrefix(trimmed, "/avatar")
                        ? trimmed
                        : null;
                    #endregion
                }

                bool restrictAvatarSubscriptions = scope == OscScope.Prop || scope == OscScope.Scene;
                return !restrictAvatarSubscriptions || IsPathWithinPrefix(trimmed, AvatarPublicPrefix) || !IsPathWithinPrefix(trimmed, "/avatar")
                    ? trimmed
                    : null;
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
                   (path.Length == prefix.Length || path[prefix.Length] == '/');
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

            uint fallbackId = content != null ? unchecked((uint)content.GetInstanceID()) : 0u;
            return "local-" + fallbackId.ToString("x8");
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
