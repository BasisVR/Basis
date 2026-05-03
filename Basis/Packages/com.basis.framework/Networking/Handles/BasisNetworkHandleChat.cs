using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using static SerializableBasis;

/// <summary>
/// Client-side handler for sending and receiving chat text messages over the dedicated ChatChannel.
/// Chat text is displayed above the remote player's nameplate.
/// </summary>
public static class BasisNetworkHandleChat
{
    /// <summary>
    /// Maximum chat message length in characters to prevent abuse.
    /// </summary>
    public const int MaxMessageLength = 256;

    /// <summary>
    /// How long a chat message stays visible (in seconds) before auto-clearing.
    /// </summary>
    public const float MessageDisplayDuration = 10f;


    /// <summary>
    /// Fired on the main thread when a chat message is received from a remote player.
    /// Parameters: senderPlayerId, message text.
    /// </summary>
    public static event Action<ushort, string> OnChatMessageReceived;

    private static readonly ThreadLocal<NetDataWriter> threadLocalWriter = new ThreadLocal<NetDataWriter>(() => new NetDataWriter());

    /// <summary>
    /// Sends a chat message to all connected players via the dedicated ChatChannel.
    /// The message is sent to the server which applies word filtering before broadcasting.
    /// </summary>
    /// <param name="message">The text message to send.</param>
    public static void SendChatMessage(string message)
    {
        if (BasisNetworkConnection.LocalPlayerIsConnected == false)
        {
            return;
        }
        if (Basis.BasisUI.BasisSettingsDefaults.ChatDisabled.RawValue)
        {
            return;
        }
        if (string.IsNullOrEmpty(message)) return;

        if (message.Length > MaxMessageLength)
        {
            message = message.Substring(0, MaxMessageLength);
        }

        byte[] payload = Encoding.UTF8.GetBytes(message);

        ChatMessage chatMessage = new ChatMessage
        {
            payload = payload,
            payloadSize = (ushort)payload.Length
        };

        NetDataWriter writer = threadLocalWriter.Value;
        writer.Reset();
        chatMessage.Serialize(writer);
        BasisNetworkConnection.LocalPlayerPeer.Send(writer, BasisNetworkCommons.ChatChannel, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// Clears the local player's chat message for all remote players.
    /// Sends an empty payload to signal clearing.
    /// </summary>
    public static void ClearChatMessage()
    {
        ChatMessage chatMessage = new ChatMessage
        {
            payload = Array.Empty<byte>(),
            payloadSize = 0
        };

        NetDataWriter writer = threadLocalWriter.Value;
        writer.Reset();
        chatMessage.Serialize(writer);
        BasisNetworkConnection.LocalPlayerPeer.Send(writer, BasisNetworkCommons.ChatChannel, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// Called by BasisNetworkEvents when a ServerChatMessage arrives on ChatChannel.
    /// This is invoked on the main thread.
    /// </summary>
    public static void HandleServerChatMessage(NetPacketReader reader)
    {
        ServerChatMessage serverChatMessage = new ServerChatMessage();
        serverChatMessage.Deserialize(reader);

        if (Basis.BasisUI.BasisSettingsDefaults.ChatDisabled.RawValue)
        {
            return;
        }

        ushort senderPlayerId = serverChatMessage.playerIdMessage.playerID;
        string message = string.Empty;

        if (serverChatMessage.chatMessage.payload != null && serverChatMessage.chatMessage.payloadSize > 0)
        {
            message = Encoding.UTF8.GetString(serverChatMessage.chatMessage.payload, 0, serverChatMessage.chatMessage.payloadSize);
        }

        OnChatMessageReceived?.Invoke(senderPlayerId, message);
        ApplyChatToNamePlate(senderPlayerId, message);

        if (!string.IsNullOrEmpty(message))
        {
            PlayChatNotification();
        }
    }

    /// <summary>
    /// Plays the chat notification audio clip through BasisDeviceManagement,
    /// matching the pattern used by other UI sounds (hover, press).
    /// </summary>
    private static void PlayChatNotification()
    {
        if (BasisDeviceManagement.Instance == null || BasisDeviceManagement.Instance.ChatNotificationUI == null)
        {
            return;
        }

        AudioSource.PlayClipAtPoint(BasisDeviceManagement.Instance.ChatNotificationUI, BasisDeviceManagement.Instance.transform.position, SMModuleAudio.ActiveMenusVolume);
    }

    private static async Task ApplyChatToNamePlate(ushort senderPlayerId, string message)
    {
        if (BasisNetworkPlayers.Players.TryGetValue(senderPlayerId, out BasisNetworkPlayer networkPlayer))
        {
            if (networkPlayer.Player is BasisRemotePlayer remotePlayer && remotePlayer.RemoteNamePlate != null)
            {
                // Check per-player chat visibility setting
                var settings = await BasisPlayerSettingsManager.RequestPlayerSettings(remotePlayer.UUID);
                if (!settings.ChatVisible)
                {
                    return;
                }

                remotePlayer.RemoteNamePlate.SetChatText(message);
            }
        }
    }
}
