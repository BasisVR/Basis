using Basis.Network.Core;
using Basis.Scripts.Audio;
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
    public static void SendChatMessage(string message, bool playNotificationSound = true)
    {
        if (BasisNetworkConnection.LocalPlayerIsConnected == false)
        {
            return;
        }
        if (Basis.BasisUI.BasisSettingsDefaults.ChatDisabled.RawValue)
        {
            return;
        }
        if (BasisNetworkConnection.LocalPlayerPeer == null)
        {
            return;
        }
        message = BasisChatSanitizer.Sanitize(message);
        if (string.IsNullOrEmpty(message)) return;

        BasisNetworkHandleChatTyping.SendTypingState(false);

        byte[] payload = Encoding.UTF8.GetBytes(message);

        ChatMessage chatMessage = new ChatMessage
        {
            payload = payload,
            payloadSize = (ushort)payload.Length,
            playNotificationSound = playNotificationSound
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
        if (BasisNetworkConnection.LocalPlayerPeer == null)
        {
            return;
        }

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
        if (Basis.BasisUI.BasisSettingsDefaults.ChatDisabled.RawValue)
        {
            reader.Recycle();
            return;
        }
        ServerChatMessage serverChatMessage = new ServerChatMessage();
        serverChatMessage.Deserialize(reader);

        

        ushort senderPlayerId = serverChatMessage.playerIdMessage.playerID;
        string message = string.Empty;

        if (serverChatMessage.chatMessage.payload != null && serverChatMessage.chatMessage.payloadSize > 0)
        {
            message = Encoding.UTF8.GetString(serverChatMessage.chatMessage.payload, 0, serverChatMessage.chatMessage.payloadSize);
        }

        OnChatMessageReceived?.Invoke(senderPlayerId, message);
        // Fire-and-forget: this main-thread handler must not block on the per-player
        // settings lookup awaited inside ApplyRemoteChat.
        _ = ApplyRemoteChat(senderPlayerId, message, serverChatMessage.chatMessage.playNotificationSound);
    }

    /// <summary>
    /// Plays the chat notification audio clip through BasisDeviceManagement,
    /// matching the pattern used by other UI sounds (hover, press).
    /// </summary>
    public static void PlayChatNotification()
    {
        if (BasisDeviceManagement.Instance == null || BasisDeviceManagement.Instance.ChatNotificationUI == null)
        {
            return;
        }

        BasisUISounds.PlayAt(BasisUISoundEvent.Chat, BasisDeviceManagement.Instance.ChatNotificationUI, BasisDeviceManagement.Instance.transform.position, SMModuleAudio.ActiveMenusVolume);
    }

    private static Vector3 GetRemoteChatNotificationPosition(BasisRemotePlayer remotePlayer, Vector3 fallbackPosition)
    {
        if (remotePlayer.MouthTransform != null)
        {
            return remotePlayer.MouthTransform.position;
        }

        if (remotePlayer.BasisAvatar != null)
        {
            return remotePlayer.BasisAvatar.transform.position;
        }

        Transform namePlate = remotePlayer.NamePlateTransformProvider?.Invoke();
        if (namePlate != null)
        {
            return namePlate.position;
        }

        if (remotePlayer.RemoteAvatarDriver?.References?.head != null)
        {
            return remotePlayer.RemoteAvatarDriver.References.head.position;
        }

        if (remotePlayer.RemoteAvatarDriver?.References?.Hips != null)
        {
            return remotePlayer.RemoteAvatarDriver.References.Hips.position;
        }

        if (remotePlayer.Transform.position != Vector3.zero)
        {
            return remotePlayer.Transform.position;
        }

        return fallbackPosition;
    }

    private static async Task ApplyRemoteChat(ushort senderPlayerId, string message, bool playNotificationSound)
    {
        if (TryGetRemotePlayer(senderPlayerId, out BasisRemotePlayer remotePlayer))
        {
            var settings = await BasisPlayerSettingsManager.RequestPlayerSettings(remotePlayer.UUID);
            if (!settings.ChatVisible)
            {
                return;
            }

            remotePlayer.OnChatMessageReceived?.Invoke(message);

            if (!string.IsNullOrEmpty(message) && playNotificationSound)
            {
                PlayChatNotification(remotePlayer);
            }
        }
    }

    private static void PlayChatNotification(BasisRemotePlayer remotePlayer)
    {
        if (BasisDeviceManagement.Instance == null || BasisDeviceManagement.Instance.ChatNotificationUI == null)
        {
            return;
        }

        Vector3 position = GetRemoteChatNotificationPosition(remotePlayer, BasisDeviceManagement.Instance.transform.position);
        BasisUISounds.PlayAt(BasisUISoundEvent.Chat, BasisDeviceManagement.Instance.ChatNotificationUI, position, SMModuleAudio.ActiveMenusVolume);
    }

    private static bool TryGetRemotePlayer(ushort playerId, out BasisRemotePlayer remotePlayer)
    {
        remotePlayer = null;
        if (!BasisNetworkPlayers.Players.TryGetValue(playerId, out BasisNetworkPlayer networkPlayer))
        {
            return false;
        }

        remotePlayer = networkPlayer.Player as BasisRemotePlayer;
        return remotePlayer != null;
    }
}
