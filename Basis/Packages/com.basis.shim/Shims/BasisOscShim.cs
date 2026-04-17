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
    public sealed class BasisOscShim : CilboxShim
    {
        public delegate void OscMessageEvent(OscMessage message, OscData[] arguments);
        private const string AvatarPublishPrefix = "/avatar/parameters";
        private const string PropPublishPrefix = "/prop";
        private const string ScenePublishPrefix = "/scene";

        private readonly HashSet<string> subscribedAddresses = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> subscribedPrefixes = new HashSet<string>(StringComparer.Ordinal);

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

        public void SubscribePrefix(string prefix)
        {
            string normalizedPrefix = NormalizeSubscriptionAddress(prefix);
            if (normalizedPrefix != null)
            {
                subscribedPrefixes.Add(normalizedPrefix);
            }
        }

        public void Unsubscribe(string address)
        {
            string normalizedAddress = NormalizeSubscriptionAddress(address);
            if (normalizedAddress != null)
            {
                subscribedAddresses.Remove(normalizedAddress);
            }
        }

        public void UnsubscribePrefix(string prefix)
        {
            string normalizedPrefix = NormalizeSubscriptionAddress(prefix);
            if (normalizedPrefix != null)
            {
                subscribedPrefixes.Remove(normalizedPrefix);
            }
        }

        public void ClearSubscriptions()
        {
            subscribedAddresses.Clear();
            subscribedPrefixes.Clear();
        }

        public bool IsSubscribed(string address)
        {
            string normalizedAddress = NormalizeSubscriptionAddress(address);
            return normalizedAddress != null && subscribedAddresses.Contains(normalizedAddress);
        }

        public bool IsPrefixSubscribed(string prefix)
        {
            string normalizedPrefix = NormalizeSubscriptionAddress(prefix);
            return normalizedPrefix != null && subscribedPrefixes.Contains(normalizedPrefix);
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
            if (OnMessage == null || message == null)
            {
                return;
            }

            string path = message.Path ?? string.Empty;
            if (!ReceiveAll && !subscribedAddresses.Contains(path) && !MatchesPrefix(path))
            {
                return;
            }

            OnMessage.Invoke(message, message.Arguments);
        }

        private bool MatchesPrefix(string path)
        {
            foreach (string prefix in subscribedPrefixes)
            {
                if (path.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeSubscriptionAddress(string address)
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

            trimmed = trimmed.TrimStart('/');
            return trimmed.Length == 0 ? AvatarPublishPrefix : AvatarPublishPrefix + "/" + trimmed;
        }

        private string ResolvePublishAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address) || !TryGetPublishPrefix(out string prefix))
            {
                return null;
            }

            string trimmed = address.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                return trimmed;
            }

            trimmed = trimmed.TrimStart('/');
            return trimmed.Length == 0 ? prefix : prefix + "/" + trimmed;
        }

        private bool TryGetPublishPrefix(out string prefix)
        {
            prefix = null;

            for (Transform current = transform; current != null; current = current.parent)
            {
                BasisProp prop = current.GetComponent<BasisProp>();
                if (prop != null)
                {
                    prefix = PropPublishPrefix + "/" + GetScopedContentIdentifier(prop) + "/parameters";
                    return true;
                }

                BasisScene sceneOnTransform = current.GetComponent<BasisScene>();
                if (sceneOnTransform != null)
                {
                    prefix = ScenePublishPrefix + "/" + GetScopedContentIdentifier(sceneOnTransform) + "/parameters";
                    return true;
                }

                BasisAvatar avatar = current.GetComponent<BasisAvatar>();
                if (avatar != null)
                {
                    if (!avatar.IsOwnedLocally)
                    {
                        return false;
                    }

                    prefix = AvatarPublishPrefix;
                    return true;
                }
            }

            if (BasisScene.SceneTraversalFindBasisScene(gameObject, out BasisScene scene))
            {
                prefix = ScenePublishPrefix + "/" + GetScopedContentIdentifier(scene) + "/parameters";
                return true;
            }

            return false;
        }

        private static string GetScopedContentIdentifier(BasisNetworkContentBase content)
        {
            if (content != null && content.TryGetNetworkGUIDIdentifier(out string identifier) && !string.IsNullOrWhiteSpace(identifier))
            {
                return SanitizePathSegment(identifier);
            }

            uint fallbackId = content != null ? unchecked((uint)content.GetInstanceID()) : 0u;
            return "local-" + fallbackId.ToString("x8");
        }

        private static string SanitizePathSegment(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return "unnamed";
            }

            StringBuilder builder = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
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
        }
    }
}
