using Unity.Scripting.LifecycleManagement;
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
    [AutoStaticsCleanup]
    internal static partial class BasisOscChatBoxBridge
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
            if (arguments.Length == 1 && arguments[0]?.Kind == OscDataKind.Boolean)
            {
                BasisNetworkHandleChatTyping.SendTypingState(arguments[0].BoolValue);
                return;
            }

            if (TryParseChatInput(arguments, out string inputText, out bool sendImmediately, out bool playNotificationSound))
            {
                string sanitized = BasisChatSanitizer.Sanitize(inputText);
                if (sendImmediately)
                {
                    if (!string.IsNullOrEmpty(sanitized))
                    {
                        BasisNetworkHandleChat.SendChatMessage(sanitized, playNotificationSound);
                    }

                    return;
                }

                SettingsProvider.OpenChatComposer(sanitized, true, playNotificationSound);
                return;
            }

            WarnInvalidSignature(message);
        }

        private static bool TryParseChatInput(OscData[] arguments, out string inputText, out bool sendImmediately, out bool playNotificationSound)
        {
            inputText = string.Empty;
            sendImmediately = true;
            playNotificationSound = false;

            if (arguments.Length < 1 || arguments.Length > 3)
            {
                return false;
            }

            // Support both string-first positional flags and any-order OSC input:
            // inputText comes from the string, then bools map to sendImmediately and playNotificationSound.
            if (arguments[0]?.Kind == OscDataKind.String)
            {
                inputText = arguments[0].StringValue;
                return TryReadOptionalBool(arguments, 1, true, out sendImmediately) &&
                       TryReadOptionalBool(arguments, 2, false, out playNotificationSound);
            }

            int stringIndex = -1;
            int boolCount = 0;
            for (int index = 0; index < arguments.Length; index++)
            {
                if (arguments[index] == null)
                {
                    return false;
                }

                switch (arguments[index].Kind)
                {
                    case OscDataKind.String:
                        if (stringIndex != -1)
                        {
                            return false;
                        }

                        stringIndex = index;
                        inputText = arguments[index].StringValue;
                        break;
                    case OscDataKind.Boolean:
                        boolCount++;
                        if (boolCount == 1)
                        {
                            sendImmediately = arguments[index].BoolValue;
                        }
                        else if (boolCount == 2)
                        {
                            playNotificationSound = arguments[index].BoolValue;
                        }
                        else
                        {
                            return false;
                        }
                        break;
                    default:
                        return false;
                }
            }

            return stringIndex != -1;
        }

        private static bool TryReadOptionalBool(OscData[] arguments, int index, bool defaultValue, out bool value)
        {
            if (index >= arguments.Length)
            {
                value = defaultValue;
                return true;
            }

            if (arguments[index]?.Kind == OscDataKind.Boolean)
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

            BasisDebug.LogWarning($"Unsupported OSC ChatBox signature for {ChatboxInputPath}: {signature}", BasisDebug.LogTag.LocalNetwork);
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
