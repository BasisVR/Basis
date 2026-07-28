using Basis.Network.Core;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.VoiceRecording;

/// <summary>
/// Sends and receives voice-record consent messages between peers on
/// <see cref="BasisNetworkCommons.EventsChannel"/> using
/// <see cref="BasisNetworkCommons.EventType_VoiceRecordRequest"/> and
/// <see cref="BasisNetworkCommons.EventType_VoiceRecordConsent"/>. The server relays each
/// message to the single target peer, stamping it with the sender's id (see
/// <c>BasisNetworkServer.BasisNetworkHandleVoiceRecord</c>). All decisions are made
/// client-side in <see cref="BasisVoiceRecording"/>.
/// </summary>
public static class BasisNetworkHandleVoiceRecord
{
    /// <summary>Ask <paramref name="targetPlayerId"/> for permission for the given purpose.</summary>
    public static void SendRecordRequest(ushort targetPlayerId, byte purpose)
    {
        if (!BasisNetworkConnection.LocalPlayerIsConnected || BasisNetworkConnection.LocalPlayerPeer == null)
        {
            return;
        }
        NetDataWriter writer = new NetDataWriter();
        writer.Put(BasisNetworkCommons.EventType_VoiceRecordRequest);
        writer.Put(targetPlayerId);
        writer.Put(purpose);
        BasisNetworkConnection.LocalPlayerPeer.Send(writer, BasisNetworkCommons.EventsChannel, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Reply to <paramref name="targetPlayerId"/> with a <see cref="BasisVoiceConsentState"/> for a purpose.</summary>
    public static void SendConsent(ushort targetPlayerId, byte state, byte purpose)
    {
        if (!BasisNetworkConnection.LocalPlayerIsConnected || BasisNetworkConnection.LocalPlayerPeer == null)
        {
            return;
        }
        NetDataWriter writer = new NetDataWriter();
        writer.Put(BasisNetworkCommons.EventType_VoiceRecordConsent);
        writer.Put(targetPlayerId);
        writer.Put(state);
        writer.Put(purpose);
        BasisNetworkConnection.LocalPlayerPeer.Send(writer, BasisNetworkCommons.EventsChannel, DeliveryMethod.ReliableOrdered);
    }

    public static void OnRemoteRecordRequestReceived(ushort senderPlayerId, byte purpose)
    {
        BasisVoiceRecording.HandleIncomingRequest(senderPlayerId, purpose);
    }

    public static void OnRemoteConsentReceived(ushort senderPlayerId, byte state, byte purpose)
    {
        BasisVoiceRecording.HandleIncomingConsent(senderPlayerId, state, purpose);
    }
}
