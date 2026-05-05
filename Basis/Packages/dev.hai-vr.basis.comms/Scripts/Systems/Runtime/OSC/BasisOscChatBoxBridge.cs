using System;
using System.Collections.Generic;
using Basis.BasisUI;
using Basis.Network.Core;
using HVR.Basis.Comms.OSC;
using UnityEngine;

namespace HVR.Basis.Comms
{
    /// <summary>
    /// Routes supported ChatBox OSC messages into Basis's native chat systems.
    /// </summary>
    internal static class BasisOscChatBoxBridge
    {
        private const string ChatboxInputPath = "/chatbox/input";
        private static readonly HashSet<string> WarnedInvalidSignatures = new HashSet<string>(StringComparer.Ordinal);
        private static bool _subscribed;

        [RuntimeInitializeOnLoadMethod]
        private static void Initialize()
        {
            if (_subscribed)
            {
                return;
            }

            BasisOscService.EnsureInitialized();
            BasisOscService.MessageReceived += OnOscMessageReceived;
            _subscribed = true;
        }

        private static void OnOscMessageReceived(OscMessage message)
        {
            if (message == null || !string.Equals(message.Path, ChatboxInputPath, StringComparison.Ordinal))
            {
                return;
            }

            OscData[] arguments = message.Arguments ?? Array.Empty<OscData>();
            if (arguments.Length == 1 && arguments[0].Kind == OscDataKind.Boolean)
            {
                BasisNetworkHandleChatTyping.SendTypingState(arguments[0].BoolValue);
                return;
            }

            if (arguments.Length >= 1 &&
                arguments.Length <= 3 &&
                arguments[0].Kind == OscDataKind.String)
            {
                if (!TryReadOptionalBool(arguments, 1, false, out bool shouldOpenKeyboard) ||
                    !TryReadOptionalBool(arguments, 2, false, out bool playNotificationSound))
                {
                    WarnInvalidSignature(message);
                    return;
                }

                string sanitized = BasisChatSanitizer.Sanitize(arguments[0].StringValue);
                if (shouldOpenKeyboard)
                {
                    SettingsProvider.OpenChatComposer(sanitized, true, playNotificationSound);
                    return;
                }

                if (!string.IsNullOrEmpty(sanitized))
                {
                    BasisNetworkHandleChat.SendChatMessage(sanitized);
                }

                if (playNotificationSound)
                {
                    BasisNetworkHandleChat.PlayChatNotification();
                }

                return;
            }

            WarnInvalidSignature(message);
        }

        private static bool TryReadOptionalBool(OscData[] arguments, int index, bool defaultValue, out bool value)
        {
            if (index >= arguments.Length)
            {
                value = defaultValue;
                return true;
            }

            if (arguments[index].Kind == OscDataKind.Boolean)
            {
                value = arguments[index].BoolValue;
                return true;
            }

            value = defaultValue;
            return false;
        }

        private static void WarnInvalidSignature(OscMessage message)
        {
            string signature = BuildSignature(message);
            if (!WarnedInvalidSignatures.Add(signature))
            {
                return;
            }

            Debug.LogWarning($"Unsupported OSC ChatBox signature for {ChatboxInputPath}: {signature}");
        }

        private static string BuildSignature(OscMessage message)
        {
            OscData[] arguments = message.Arguments ?? Array.Empty<OscData>();
            if (arguments.Length == 0)
            {
                return "(empty)";
            }

            string[] kinds = new string[arguments.Length];
            for (int i = 0; i < arguments.Length; i++)
            {
                kinds[i] = arguments[i]?.Kind.ToString() ?? "null";
            }

            return $"({string.Join(", ", kinds)})";
        }
    }
}
