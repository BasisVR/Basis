using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using System.Threading;

/// <summary>
/// Sends and receives transient player chat typing state on EventsChannel.
/// </summary>
public static class BasisNetworkHandleChatTyping
{
    private static ThreadLocal<NetDataWriter> threadLocalWriter;
    private static bool initialized;
    private static bool hasLastSentTypingState;
    private static bool lastSentTypingState;

    public static void Initialize()
    {
        if (initialized)
        {
            return;
        }

        threadLocalWriter ??= new ThreadLocal<NetDataWriter>(() => new NetDataWriter());
        initialized = true;
        ClearState();
    }

public static void Shutdown()
    {
        if (!initialized)
        {
            return;
        }

        initialized = false;
        ClearState();
        threadLocalWriter?.Dispose();
        threadLocalWriter = null;
    }

    public static void ClearState()
    {
        hasLastSentTypingState = false;
        lastSentTypingState = false;
    }

    public static void SendTypingState(bool isTyping)
    {
        if (!initialized)
        {
            return;
        }

        if (BasisNetworkConnection.LocalPlayerIsConnected == false || BasisNetworkConnection.LocalPlayerPeer == null)
        {
            return;
        }

        if (BasisNetworkHandleChat.LockedByServer)
        {
            return;
        }

        if (hasLastSentTypingState && lastSentTypingState == isTyping)
        {
            return;
        }

        hasLastSentTypingState = true;
        lastSentTypingState = isTyping;

        NetDataWriter writer = threadLocalWriter.Value;
        writer.Reset();
        writer.Put(BasisNetworkCommons.EventType_PlayerChatTyping);
        writer.Put(isTyping);
        BasisNetworkConnection.LocalPlayerPeer.Send(writer, BasisNetworkCommons.EventsChannel, DeliveryMethod.ReliableSequenced);
    }

    public static void OnRemoteTypingStateReceived(ushort senderPlayerId, bool isTyping)
    {
        if (!BasisNetworkPlayers.Players.TryGetValue(senderPlayerId, out BasisNetworkPlayer networkPlayer))
        {
            return;
        }

        if (networkPlayer.Player is not BasisRemotePlayer remotePlayer)
        {
            return;
        }

        // Typing shares the chat bubble, so it obeys the same visibility controls as messages:
        // global chat-off, blocks, and the per-player hide-chat setting. Coerce to false rather
        // than dropping so an earlier visible "typing" always converges to cleared.
        if (isTyping && (Basis.BasisUI.BasisSettingsDefaults.ChatDisabled.RawValue
            || remotePlayer.IsEffectivelyBlocked
            || (BasisPlayerSettingsManager.TryGetCached(remotePlayer.UUID, out var settings) && !settings.ChatVisible)))
        {
            isTyping = false;
        }

        remotePlayer.IsChatTyping = isTyping;
        remotePlayer.OnChatTypingStateChanged?.Invoke(isTyping);
    }
}
