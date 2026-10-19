using System.Diagnostics;
using System.Net;
using System.Text;
using Basis.Contrib.Auth.DecentralizedIds;
using Basis.Contrib.Auth.DecentralizedIds.Newtypes;
using Basis.Contrib.Crypto;
using Basis.Network.Core;
using BasisNetworkCore.Security;
using BasisNetworkServer.Security;
using BasisNetworkServer.Storage;
using Xunit;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;
using static BasisPermissions.PermissionManager;
using static Basis.Network.Core.Serializable.SerializableBasis;
using static SerializableBasis;
using CryptoRng = System.Security.Cryptography.RandomNumberGenerator;

namespace BasisServerTests;

[Collection("BasisServer shared network statics")]
public class BasisStorageDidLoginTests
{
    private sealed class Login
    {
        public FakeNetPeer Peer = null!;
        public RecordingConnectionRequest Request = null!;
        public byte[] ClientPublic = null!;
        public byte[] PayloadHash = null!;
        public PrivKey SigningKey = null!;
        public Did ClaimedDid = null!;
    }

    private static BasisDidLink.BasisDIDAuthIdentity UseDidLogin()
    {
        NetworkServer.Configuration = new Configuration
        {
            PeerLimit = 100,
            UseAuth = false,
            UseAuthIdentity = true,
            HasFileSupport = false,
            HowManyDuplicateAuthCanExist = 3,
            BasisUserRestrictionMode = BasisUserRestrictionMode.Normal,
        };
        NetworkServer.Server = new FakeNetManager { ConnectedPeersCount = 0 };
        NetworkServer.Auth = new FakeAuth { Result = true };
        NetworkServer.AllowList = new BasisAllowList();
        NetworkServer.BanList = new BasisBanList();
        NetworkServer.HighQualityLength = ConvertToSize(BitQuality.High);
        BasisDidLink.BasisDIDAuthIdentity identity = new BasisDidLink.BasisDIDAuthIdentity();
        NetworkServer.AuthIdentity = identity;
        return identity;
    }

    private static (PrivKey Key, Did Did) NewPlayer()
    {
        byte[] privateBytes = new byte[Ed25519.PrivkeySize];
        CryptoRng.Fill(privateBytes);
        PrivKey key = new PrivKey(privateBytes);
        return (key, DidKeyResolver.EncodePubkeyAsDid(Ed25519.ConvertPrivkeyToPubkey(key)!));
    }

    private static Login Connect(PrivKey signingKey, Did claimedDid)
    {
        Login login = new Login { SigningKey = signingKey, ClaimedDid = claimedDid };
        BasisCryptoHandshake.GenerateKeyPair(out _, out login.ClientPublic);
        login.Peer = LifecycleSupport.Peer(LifecycleSupport.NextPeerId());
        ReadyMessage ready = LifecycleSupport.MakeReady(claimedDid.V, "Saver");
        byte[] payload = DidLoginChannelBindingTests.ConnectPayload(login.ClientPublic, ready);
        login.PayloadHash = BasisDidLoginTranscript.HashConnectionPayload(payload, 0, payload.Length);
        byte[] data = LifecycleSupport.ConnectPayload(BasisNetworkVersion.ServerVersion, new byte[] { 1 }, ready, connectionKey: login.ClientPublic);
        login.Request = new RecordingConnectionRequest
        {
            Data = new NetDataReader(data),
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.40"), 6006),
            PeerToReturn = login.Peer,
            CanAwaitSeal = true,
        };
        BasisServerHandle.BasisServerHandleEvents.HandleConnectionRequest(login.Request);
        Assert.True(WaitFor(() => login.Peer.HasSentOn(BasisNetworkCommons.AuthIdentityChannel)), "no challenge was sent");
        byte[] challenge;
        lock (login.Peer.Sent) challenge = login.Peer.Sent.First(s => s.Channel == BasisNetworkCommons.AuthIdentityChannel).Data;
        NetDataReader reader = new NetDataReader(challenge);
        BytesMessage bytes = new BytesMessage();
        Assert.True(bytes.Deserialize(reader, out byte[] nonce));
        Assert.True(bytes.Deserialize(reader, out byte[] serverPublic));
        Assert.True(bytes.Deserialize(reader, out byte[] serverId));
        byte[] transcript = BasisDidLoginTranscript.Build(claimedDid.V, Encoding.UTF8.GetString(serverId), nonce, login.ClientPublic, serverPublic, login.PayloadHash);
        Assert.True(Ed25519.Sign(signingKey, new Payload(transcript), out Signature? signature));
        NetDataWriter writer = new NetDataWriter();
        new BytesMessage().Serialize(writer, signature!.V);
        new BytesMessage().Serialize(writer, Encoding.UTF8.GetBytes("N/A"));
        BasisServerHandle.BasisServerHandleEvents.HandleAuth(NetPacketReader.Create(writer.CopyData(), 0, writer.Length, static () => { }), login.Peer);
        Assert.True(WaitFor(() => NetworkServer.AuthenticatedPeers.ContainsKey(login.Peer.Id) || login.Peer.DisconnectCalls > 0), "the login neither finished nor was refused");
        return login;
    }

    private static bool WaitFor(Func<bool> done, int timeoutMs = 5000)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.ElapsedMilliseconds > timeoutMs) return false;
            Thread.Sleep(5);
        }
        return true;
    }

    private static BasisStorageReply Ask(FakeNetPeer peer, BasisStorageRequest request)
    {
        NetDataWriter writer = new NetDataWriter();
        BasisStorageProtocol.WriteRequest(writer, request);
        byte[] data = writer.CopyData();
        BasisServerStorage.HandleRequest(NetPacketReader.Create(data, 0, data.Length, static () => { }), peer);
        BasisStorageReply found = default;
        Assert.True(WaitFor(() =>
        {
            lock (peer.Sent)
            {
                foreach ((byte[] sent, byte channel, DeliveryMethod _) in peer.Sent)
                {
                    if (channel == BasisNetworkCommons.StorageChannel && BasisStorageProtocol.TryReadReply(new NetDataReader(sent), out BasisStorageReply reply) && reply.RequestId == request.RequestId)
                    {
                        found = reply;
                        return true;
                    }
                }
            }
            return false;
        }), "no storage reply");
        return found;
    }

    [Fact]
    public void Storage_FollowsTheIdentityAPlayerProved_AndNothingElse()
    {
        using ServerStaticsScope scope = new ServerStaticsScope();
        BasisDidLink.BasisDIDAuthIdentity identity = UseDidLogin();
        PermissionIntegration.Manager.EnsureDefaults();
        Assert.True(BasisServerStorage.Start(NetworkServer.Configuration));
        List<Login> logins = new List<Login>();
        try
        {
            (PrivKey key, Did did) = NewPlayer();
            Login first = Connect(key, did);
            logins.Add(first);
            Assert.True(identity.NetIDToUUID(first.Peer, out string uuid));
            Assert.Equal(did.V, uuid);
            Assert.Equal(BasisStorageStatus.Ok, Ask(first.Peer, StorageTestSupport.Set("world", "coins", Encoding.UTF8.GetBytes("42"), 1)).Status);

            Login again = Connect(key, did);
            logins.Add(again);
            Assert.Equal("42", Encoding.UTF8.GetString(Ask(again.Peer, StorageTestSupport.Get("world", "coins", 2)).Value));

            Login impostor = Connect(NewPlayer().Key, did);
            logins.Add(impostor);
            Assert.False(identity.NetIDToUUID(impostor.Peer, out _), "a signature from the wrong key must not admit the claimed DID");
            Assert.Equal(BasisStorageStatus.Denied, Ask(impostor.Peer, StorageTestSupport.Get("world", "coins", 3)).Status);
            Assert.Equal(BasisStorageStatus.Denied, Ask(impostor.Peer, StorageTestSupport.Set("world", "coins", Encoding.UTF8.GetBytes("0"), 4)).Status);

            (PrivKey otherKey, Did otherDid) = NewPlayer();
            Login stranger = Connect(otherKey, otherDid);
            logins.Add(stranger);
            Assert.Equal(BasisStorageStatus.NotFound, Ask(stranger.Peer, StorageTestSupport.Get("world", "coins", 5)).Status);
            Assert.Equal("42", Encoding.UTF8.GetString(Ask(first.Peer, StorageTestSupport.Get("world", "coins", 6)).Value));
        }
        finally
        {
            foreach (Login login in logins) identity.RemoveConnection(login.Peer.Id);
            identity.DeInitialize();
            BasisServerStorage.Stop();
        }
    }
}
