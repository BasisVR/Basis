using Basis.Network.Core;

using static Basis.Network.Core.Serializable.SerializableBasis;
using static SerializableBasis;
public class NetworkClient
{
    public  NetManager client;
    public EventBasedNetListener listener;
    private NetPeer peer;
    private bool IsInUse;
    // This connection's X25519 key. DID login signs over the public half, and what the client sends
    // after login is sealed with the key derived from it, so the login cannot be used by a relay.
    private byte[] ConnectionPrivateKey;
    public byte[] ConnectionPublicKey;
    public byte[] ConnectionPayloadHash;
    public bool LegacyLogin;
    /// <summary>
    /// initial data is typically the 
    /// </summary> 
    /// <param name="IP"></param>
    /// <param name="port"></param>
    /// <param name="ReadyMessage"></param>
    public NetPeer StartClient(string IP, int port, ReadyMessage ReadyMessage, byte[] AuthenticationMessage, string CompanyName, string ProductName, Configuration Configuration, bool manualMode = false, bool legacyLogin = false)
    {
        if (IsInUse == false)
        {
            listener = new EventBasedNetListener();
            client = BasisNetworkStackRegistry.Create(Configuration.NetworkStackId, listener, Configuration);
            if (manualMode)
                client.StartManual();
            else
                client.Start();
            NetDataWriter Writer = new NetDataWriter(true,12);
            //this is the only time we dont put key!
            LegacyLogin = legacyLogin;
            Writer.Put(legacyLogin ? BasisNetworkVersion.LegacyLoginVersion : BasisNetworkVersion.ServerVersion);
            BasisNetworkApplication.Write(Writer, CompanyName, ProductName);
            BytesMessage AuthBytes = new BytesMessage();
            AuthBytes.Serialize(Writer, AuthenticationMessage);
            int PayloadStart = Writer.Length;
            ConnectionPayloadHash = null;
            if (legacyLogin)
            {
                ConnectionPrivateKey = null;
                ConnectionPublicKey = null;
            }
            else
            {
                BasisCryptoHandshake.GenerateKeyPair(out ConnectionPrivateKey, out ConnectionPublicKey);
                AuthBytes.Serialize(Writer, ConnectionPublicKey);
            }
            ReadyMessage.Serialize(Writer);
            if (!legacyLogin)
            {
                ConnectionPayloadHash = BasisDidLoginTranscript.HashConnectionPayload(Writer.Data, PayloadStart, Writer.Length - PayloadStart);
            }
            peer = client.Connect(IP, port, Writer);
            if (!legacyLogin)
            {
                peer?.ExpectSealedOutbound();
            }
            IsInUse = true;
            return peer;
        }
        else
        {
            BNL.LogError("Call Shutdown First!");
            return null;
        }
    }
    /// <summary>
    /// Derives the client-to-server key from the server's X25519 key and seals everything sent
    /// to the server with it from now on. Call once per connection, after signing the login.
    /// </summary>
    public bool TrySealToServer(byte[] ServerEphemeralPublic)
    {
        byte[] PrivateKey = ConnectionPrivateKey;
        ConnectionPrivateKey = null;
        if (peer == null || PrivateKey == null)
        {
            return false;
        }
        if (!BasisCryptoHandshake.DeriveClientKey(PrivateKey, ConnectionPublicKey, ServerEphemeralPublic, out byte[] Key))
        {
            return false;
        }
        return peer.SealOutbound(Key);
    }
    public void Poll()
    {
        client?.PollEvents();
    }
    public void Update(float elapsedMilliseconds)
    {
        client?.ManualUpdate(elapsedMilliseconds);
    }
    public void Disconnect()
    {
        BNL.Log("Client Called Disconnect from server");
        NotifyServerOfDeparture();
        Shutdown();
        BNL.Log("Worker thread stopped.");
    }
    /// <summary>
    /// Tells the server this client is leaving, and does nothing else.
    ///
    /// <para>Split out from <see cref="Shutdown"/> because the two costs are nothing alike. This
    /// writes one datagram straight to the socket and returns; shutting the transport down closes
    /// the socket and joins its logic thread. Anything stopping a whole population has to get
    /// every one of these out before it starts paying for the teardown, or the last clients are
    /// still queued behind thread joins when the process is killed and the server is left to time
    /// them out one by one.</para>
    /// </summary>
    public void NotifyServerOfDeparture()
    {
        IsInUse = false;
        peer?.Disconnect();
    }
    /// <summary>Closes the socket and joins the transport's threads.</summary>
    public void Shutdown()
    {
        client?.Stop();
    }
}
