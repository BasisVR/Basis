using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;

/// <summary>
/// Sends and receives transient player chat typing state on EventsChannel.
/// </summary>
public static class BasisNetworkHandleChatTyping
{
    private static bool initialized;
    private static bool hasLastSentTypingState;
    private static bool lastSentTypingState;

    public static void Initialize()
    {
        if (initialized)
        {
            return;
        }

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
    }

    public static void ClearState()
    {
        hasLastSentTypingState = false;
        lastSentTypingState = false;
    }

    public static void SendTypingState(bool isTyping)
    {
        if (BasisNetworkConnection.LocalPlayerIsConnected == false || BasisNetworkConnection.LocalPlayerPeer == null)
        {
            return;
        }

        if (hasLastSentTypingState && lastSentTypingState == isTyping)
        {
            return;
        }

        hasLastSentTypingState = true;
        lastSentTypingState = isTyping;

        NetDataWriter writer = new NetDataWriter();
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

        remotePlayer.IsChatTyping = isTyping;
        if (remotePlayer.RemoteNamePlate != null)
        {
            remotePlayer.RemoteNamePlate.SetTypingIndicatorVisible(isTyping);
        }
    }
}
