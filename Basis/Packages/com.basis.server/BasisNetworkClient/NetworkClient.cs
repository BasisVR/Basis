using Basis.Network.Core;
using Basis.Scripts.Networking.Steam;
using System;
using static Basis.Network.Core.Serializable.SerializableBasis;
using static SerializableBasis;
public class NetworkClient
{
    public NetManager client;
    public EventBasedNetListener listener;
    public Action<NetPeer> OnPeerConnected;
    public Action<NetPeer, DisconnectInfo> OnPeerDisconnected;
    public Action<NetPeer, NetPacketReader, byte, DeliveryMethod> OnNetworkReceive;
    private NetPeer peer;
    private bool IsInUse;
    public bool HasActiveClient => IsInUse;
    /// <summary>
    /// Initial data is typically the ready/auth payload used during connection setup.
    /// </summary> 
    /// <param name="IP"></param>
    /// <param name="port"></param>
    /// <param name="ReadyMessage"></param>
    public NetPeer StartClient(string IP, int port, ReadyMessage ReadyMessage, byte[] AuthenticationMessage, Configuration Configuration)
    {
        if (IsInUse)
        {
            BNL.LogWarning("NetworkClient.StartClient was called while a previous client was still active. Forcing disconnect before reconnect.");
            Disconnect();
        }

        listener = new EventBasedNetListener();
        listener.PeerConnectedEvent += peer => OnPeerConnected?.Invoke(peer);
        listener.PeerDisconnectedEvent += (peer, info) => OnPeerDisconnected?.Invoke(peer, info);
        listener.NetworkReceiveEvent += (peer, reader, channel, method) => OnNetworkReceive?.Invoke(peer, reader, channel, method);
        client = BasisTransportFactory.Create(listener, Configuration);
        client.Start();
        NetDataWriter Writer = new NetDataWriter(true, 12);
        // This is the only connect path that writes the version directly before auth and ready payloads.
        Writer.Put(BasisNetworkVersion.ServerVersion);
        BytesMessage AuthBytes = new BytesMessage();
        AuthBytes.Serialize(Writer, AuthenticationMessage);
        ReadyMessage.Serialize(Writer);
        peer = client.Connect(IP, port, Writer);
        IsInUse = true;
        return peer;
    }
    public void Disconnect()
    {
        IsInUse = false;
        BNL.Log("Client Called Disconnect from server");
        peer?.Disconnect();
        client?.Stop();

        BNL.Log("Worker thread stopped.");
    }
}
