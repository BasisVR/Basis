using System.Diagnostics;
using System.Net;
using Basis.Contrib.Auth.DecentralizedIds;
using Basis.Contrib.Auth.DecentralizedIds.Newtypes;
using Basis.Contrib.Crypto;
using Basis.Network.Core;
using Basis.Network.Server.Generic;
using BasisNetworkCore.Security;
using BasisNetworkServer.Security;
using Xunit;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;
using static Basis.Network.Core.Serializable.SerializableBasis;
using static SerializableBasis;
using CryptoRng = System.Security.Cryptography.RandomNumberGenerator;

namespace BasisServerTests;

/// <summary>
/// DID login bound to the connection: the client-to-server key derivation, the strict per-peer
/// cipher layer, the signed login transcript, and a relaying server trying to reuse a player's
/// login on another server.
/// </summary>
[Collection("LiteNetLib global logger")]
public class DidLoginChannelBindingTests
{
    private sealed class Side
    {
        public byte[] Private = null!;
        public byte[] Public = null!;

        public static Side Create()
        {
            var side = new Side();
            BasisCryptoHandshake.GenerateKeyPair(out side.Private, out side.Public);
            return side;
        }
    }

    private static byte[] ClientKey(Side client, Side server)
    {
        Assert.True(BasisCryptoHandshake.DeriveClientKey(client.Private, client.Public, server.Public, out byte[] key));
        return key;
    }

    private static byte[] ServerKey(Side client, Side server)
    {
        Assert.True(BasisCryptoHandshake.DeriveServerKey(server.Private, client.Public, server.Public, out byte[] key));
        return key;
    }

    private static (PrivKey Private, Did Did) NewPlayer()
    {
        byte[] privateBytes = new byte[Ed25519.PrivkeySize];
        CryptoRng.Fill(privateBytes);
        var privateKey = new PrivKey(privateBytes);
        PubKey publicKey = Ed25519.ConvertPrivkeyToPubkey(privateKey)!;
        return (privateKey, DidKeyResolver.EncodePubkeyAsDid(publicKey));
    }

    private static Signature Sign(PrivKey key, byte[] transcript)
    {
        Assert.True(Ed25519.Sign(key, new Payload(transcript), out Signature? sig));
        return sig!;
    }

    private static async Task<bool> Verifies(Did player, byte[] nonce, byte[] transcript, Signature signature)
    {
        var didAuth = new DidAuthentication(new Config());
        var challenge = new Challenge(player, new Nonce(nonce));
        var response = new Response(signature, new DidUrlFragment(string.Empty));
        return (await didAuth.VerifyResponse(response, challenge, new Payload(transcript))).IsOk;
    }

    private static byte[] Nonce()
    {
        byte[] nonce = new byte[BasisDidLoginTranscript.MinNonceSize];
        CryptoRng.Fill(nonce);
        return nonce;
    }

    private static byte[] PayloadHash()
    {
        byte[] hash = new byte[BasisDidLoginTranscript.PayloadHashSize];
        CryptoRng.Fill(hash);
        return hash;
    }

    internal static byte[] ConnectPayload(byte[] connectionKey, ReadyMessage ready)
    {
        var writer = new NetDataWriter();
        new BytesMessage().Serialize(writer, connectionKey);
        ready.Serialize(writer);
        return writer.CopyData();
    }

    // ------------------------------------------------------------ key derivation

    [Fact]
    public void ClientToServerKey_BothEndsDeriveTheSameKey()
    {
        Side client = Side.Create();
        Side server = Side.Create();
        Assert.Equal(ClientKey(client, server), ServerKey(client, server));
    }

    [Fact]
    public void ClientToServerKey_DependsOnBothConnectionKeys()
    {
        Side client = Side.Create();
        Side server = Side.Create();
        Side other = Side.Create();
        Assert.NotEqual(ClientKey(client, server), ClientKey(client, other));
        Assert.NotEqual(ClientKey(client, server), ClientKey(other, server));
    }

    [Fact]
    public void ClientToServerKey_DiffersFromThePeerToPeerKeys()
    {
        Side client = Side.Create();
        Side server = Side.Create();
        Assert.True(BasisCryptoHandshake.DerivePeerKeys(client.Private, client.Public, server.Public, out byte[] send, out byte[] recv));
        byte[] key = ClientKey(client, server);
        Assert.NotEqual(send, key);
        Assert.NotEqual(recv, key);
    }

    [Fact]
    public void ClientToServerKey_RejectsLowOrderAndMisSizedKeys()
    {
        Side client = Side.Create();
        Side server = Side.Create();
        byte[] zero = new byte[BasisCryptoHandshake.PublicKeySize];
        Assert.False(BasisCryptoHandshake.DeriveClientKey(client.Private, client.Public, zero, out _));
        Assert.False(BasisCryptoHandshake.DeriveClientKey(client.Private, client.Public, new byte[16], out _));
        Assert.False(BasisCryptoHandshake.DeriveServerKey(server.Private, zero, server.Public, out _));
        Assert.False(BasisCryptoHandshake.DeriveServerKey(server.Private, new byte[16], server.Public, out _));
    }

    // ------------------------------------------------------------ login transcript

    [Fact]
    public async Task Transcript_SignedByThePlayer_VerifiesAtTheServerItNames()
    {
        var (key, did) = NewPlayer();
        Side client = Side.Create();
        Side server = Side.Create();
        byte[] nonce = Nonce();
        byte[] transcript = BasisDidLoginTranscript.Build(did.V, "did:key:server-b", nonce, client.Public, server.Public, PayloadHash());

        Assert.True(await Verifies(did, nonce, transcript, Sign(key, transcript)));
    }

    [Fact]
    public async Task Transcript_BareNonceSignature_NoLongerLogsIn()
    {
        var (key, did) = NewPlayer();
        byte[] nonce = Nonce();
        byte[] transcript = BasisDidLoginTranscript.Build(did.V, "did:key:server-b", nonce, Side.Create().Public, Side.Create().Public, PayloadHash());

        Assert.False(await Verifies(did, nonce, transcript, Sign(key, nonce)));
    }

    [Fact]
    public void Transcript_EveryFieldChangesTheSignedBytes()
    {
        byte[] nonce = Nonce();
        byte[] c = Side.Create().Public;
        byte[] s = Side.Create().Public;
        byte[] h = PayloadHash();
        byte[] baseline = BasisDidLoginTranscript.Build("did:key:a", "did:key:b", nonce, c, s, h);

        Assert.NotEqual(baseline, BasisDidLoginTranscript.Build("did:key:x", "did:key:b", nonce, c, s, h));
        Assert.NotEqual(baseline, BasisDidLoginTranscript.Build("did:key:a", "did:key:x", nonce, c, s, h));
        Assert.NotEqual(baseline, BasisDidLoginTranscript.Build("did:key:a", "did:key:b", Nonce(), c, s, h));
        Assert.NotEqual(baseline, BasisDidLoginTranscript.Build("did:key:a", "did:key:b", nonce, Side.Create().Public, s, h));
        Assert.NotEqual(baseline, BasisDidLoginTranscript.Build("did:key:a", "did:key:b", nonce, c, Side.Create().Public, h));
        Assert.NotEqual(baseline, BasisDidLoginTranscript.Build("did:key:a", "did:key:b", nonce, c, s, PayloadHash()));
        // Length prefixes keep a byte from moving across a field boundary unnoticed.
        Assert.NotEqual(
            BasisDidLoginTranscript.Build("did:key:ab", "c", nonce, c, s, h),
            BasisDidLoginTranscript.Build("did:key:a", "bc", nonce, c, s, h));
    }

    [Fact]
    public void ConnectPayloadHash_CoversEveryByteAndNothingAround()
    {
        byte[] payload = new byte[64];
        CryptoRng.Fill(payload);
        byte[] baseline = BasisDidLoginTranscript.HashConnectionPayload(payload, 0, payload.Length);
        Assert.Equal(BasisDidLoginTranscript.PayloadHashSize, baseline.Length);
        for (int i = 0; i < payload.Length; i++)
        {
            byte[] changed = (byte[])payload.Clone();
            changed[i] ^= 0x01;
            Assert.NotEqual(baseline, BasisDidLoginTranscript.HashConnectionPayload(changed, 0, changed.Length));
        }
        Assert.NotEqual(baseline, BasisDidLoginTranscript.HashConnectionPayload(payload, 0, payload.Length - 1));

        byte[] framed = new byte[payload.Length + 7];
        CryptoRng.Fill(framed);
        payload.CopyTo(framed, 3);
        Assert.Equal(baseline, BasisDidLoginTranscript.HashConnectionPayload(framed, 3, payload.Length));
    }

    // ------------------------------------------------------------ relay

    [Fact]
    public async Task Relay_ReplacingThePlayersConnectionKey_BreaksTheSignature()
    {
        // The player connects to relay A. A opens its own connection to B under the player's DID,
        // using its own connection key so that it would hold B's session key, and forwards B's
        // challenge to the player.
        var (key, did) = NewPlayer();
        Side player = Side.Create();
        Side relay = Side.Create();
        Side serverB = Side.Create();
        byte[] nonceB = Nonce();
        byte[] payload = PayloadHash();

        byte[] playerSigned = BasisDidLoginTranscript.Build(did.V, "did:key:server-b", nonceB, player.Public, serverB.Public, payload);
        byte[] serverBExpects = BasisDidLoginTranscript.Build(did.V, "did:key:server-b", nonceB, relay.Public, serverB.Public, payload);

        Assert.False(await Verifies(did, nonceB, serverBExpects, Sign(key, playerSigned)));
    }

    [Fact]
    public async Task Relay_ChangingWhatThePlayerSentOnConnect_BreaksTheSignature()
    {
        var (key, did) = NewPlayer();
        Side player = Side.Create();
        Side serverB = Side.Create();
        byte[] nonceB = Nonce();
        byte[] sent = ConnectPayload(player.Public, LifecycleSupport.MakeReady(did.V, "Connie"));
        byte[] altered = ConnectPayload(player.Public, LifecycleSupport.MakeReady(did.V, "Somebody Else"));

        byte[] playerSigned = BasisDidLoginTranscript.Build(did.V, "did:key:server-b", nonceB, player.Public, serverB.Public, BasisDidLoginTranscript.HashConnectionPayload(sent, 0, sent.Length));
        byte[] serverBExpects = BasisDidLoginTranscript.Build(did.V, "did:key:server-b", nonceB, player.Public, serverB.Public, BasisDidLoginTranscript.HashConnectionPayload(altered, 0, altered.Length));

        Assert.True(await Verifies(did, nonceB, playerSigned, Sign(key, playerSigned)));
        Assert.False(await Verifies(did, nonceB, serverBExpects, Sign(key, playerSigned)));
    }

    [Fact]
    public async Task Relay_SignatureForOneServer_DoesNotLogInToAnother()
    {
        var (key, did) = NewPlayer();
        Side player = Side.Create();
        Side server = Side.Create();
        byte[] nonce = Nonce();
        byte[] payload = PayloadHash();

        byte[] forA = BasisDidLoginTranscript.Build(did.V, "did:key:server-a", nonce, player.Public, server.Public, payload);
        byte[] forB = BasisDidLoginTranscript.Build(did.V, "did:key:server-b", nonce, player.Public, server.Public, payload);

        Assert.False(await Verifies(did, nonce, forB, Sign(key, forA)));
    }
}

/// <summary>
/// The same binding through the real admission path: HandleConnectionRequest with DID login on,
/// the challenge the server sends, the key it installs on the peer and the transcript it checks.
/// </summary>
[Collection("BasisServer shared network statics")]
public class DidLoginAdmissionTests
{
    private sealed class Login
    {
        public BasisDidLink.BasisDIDAuthIdentity Identity = null!;
        public FakeNetPeer Peer = null!;
        public RecordingConnectionRequest Request = null!;
        public byte[] ClientPrivate = null!;
        public byte[] ClientPublic = null!;
        public byte[] PayloadHash = null!;
        public PrivKey PlayerKey = null!;
        public Did PlayerDid = null!;
    }

    private static BasisDidLink.BasisDIDAuthIdentity UseDidLogin(int duplicates = 2)
    {
        NetworkServer.Configuration = new Configuration
        {
            PeerLimit = 100,
            UseAuth = false,
            UseAuthIdentity = true,
            HowManyDuplicateAuthCanExist = duplicates,
            BasisUserRestrictionMode = BasisUserRestrictionMode.Normal,
        };
        NetworkServer.Server = new FakeNetManager { ConnectedPeersCount = 0 };
        NetworkServer.Auth = new FakeAuth { Result = true };
        NetworkServer.AllowList = new BasisAllowList();
        NetworkServer.BanList = new BasisBanList();
        NetworkServer.HighQualityLength = ConvertToSize(BitQuality.High);
        var identity = new BasisDidLink.BasisDIDAuthIdentity();
        NetworkServer.AuthIdentity = identity;
        return identity;
    }

    private static (PrivKey Key, Did Did) NewPlayer()
    {
        byte[] privateBytes = new byte[Ed25519.PrivkeySize];
        CryptoRng.Fill(privateBytes);
        var key = new PrivKey(privateBytes);
        return (key, DidKeyResolver.EncodePubkeyAsDid(Ed25519.ConvertPrivkeyToPubkey(key)!));
    }

    private static Login Connect(bool canSeal = true, bool canAwaitSeal = true)
        => Connect(UseDidLogin(), NewPlayer(), canSeal, canAwaitSeal);

    private static Login Connect(BasisDidLink.BasisDIDAuthIdentity identity, (PrivKey Key, Did Did) player, bool canSeal = true, bool canAwaitSeal = true)
    {
        var login = new Login { Identity = identity, PlayerKey = player.Key, PlayerDid = player.Did };
        BasisCryptoHandshake.GenerateKeyPair(out login.ClientPrivate, out login.ClientPublic);

        login.Peer = LifecycleSupport.Peer(LifecycleSupport.NextPeerId());
        login.Peer.CanSeal = canSeal;
        ReadyMessage ready = LifecycleSupport.MakeReady(login.PlayerDid.V, "Connie");
        byte[] payload = DidLoginChannelBindingTests.ConnectPayload(login.ClientPublic, ready);
        login.PayloadHash = BasisDidLoginTranscript.HashConnectionPayload(payload, 0, payload.Length);
        byte[] data = LifecycleSupport.ConnectPayload(BasisNetworkVersion.ServerVersion, new byte[] { 1 }, ready, connectionKey: login.ClientPublic);
        login.Request = new RecordingConnectionRequest
        {
            Data = new NetDataReader(data),
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 6006),
            PeerToReturn = login.Peer,
            CanAwaitSeal = canAwaitSeal,
        };
        BasisServerHandle.BasisServerHandleEvents.HandleConnectionRequest(login.Request);
        Assert.True(WaitFor(() => login.Request.WasRejected || login.Peer.DisconnectCalls > 0 || login.Peer.HasSentOn(BasisNetworkCommons.AuthIdentityChannel)));
        return login;
    }

    private static bool WaitFor(Func<bool> done, int timeoutMs = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.ElapsedMilliseconds > timeoutMs) return false;
            Thread.Sleep(5);
        }
        return true;
    }

    private static (byte[] Nonce, byte[] ServerPublic, string ServerId) ReadChallenge(FakeNetPeer peer)
    {
        byte[] data;
        lock (peer.Sent) data = Assert.Single(peer.Sent, s => s.Channel == BasisNetworkCommons.AuthIdentityChannel).Data;
        var reader = new NetDataReader(data);
        var bytes = new BytesMessage();
        Assert.True(bytes.Deserialize(reader, out byte[] nonce));
        Assert.True(bytes.Deserialize(reader, out byte[] serverPublic));
        Assert.True(bytes.Deserialize(reader, out byte[] serverId));
        return (nonce, serverPublic, System.Text.Encoding.UTF8.GetString(serverId));
    }

    private static byte[] GenuineTranscript(Login login)
    {
        var (nonce, serverPublic, serverId) = ReadChallenge(login.Peer);
        return BasisDidLoginTranscript.Build(login.PlayerDid.V, serverId, nonce, login.ClientPublic, serverPublic, login.PayloadHash);
    }

    private static Task<bool> Answer(Login login, byte[] transcript)
    {
        Assert.True(Ed25519.Sign(login.PlayerKey, new Payload(transcript), out Signature? sig));
        var entry = login.Identity.AuthIdentity[login.Peer.Id];
        return login.Identity.RecvChallengeResponse(new Response(sig!, new DidUrlFragment(string.Empty)), entry.Challenge, entry.Transcript);
    }

    private static void Respond(Login login, byte[] transcript)
    {
        Assert.True(Ed25519.Sign(login.PlayerKey, new Payload(transcript), out Signature? sig));
        var writer = new NetDataWriter();
        new BytesMessage().Serialize(writer, sig!.V);
        new BytesMessage().Serialize(writer, System.Text.Encoding.UTF8.GetBytes("N/A"));
        BasisServerHandle.BasisServerHandleEvents.HandleAuth(NetPacketReader.Create(writer.CopyData(), 0, writer.Length, static () => { }), login.Peer);
    }

    private static bool Admitted(Login login) => login.Identity.NetIDToUUID(login.Peer, out _);

    private static string RefusalReason(FakeNetPeer peer)
    {
        lock (peer.DisconnectData) return LifecycleSupport.RejectReason(Assert.Single(peer.DisconnectData));
    }

    private static void Release(Login login)
    {
        login.Identity.RemoveConnection(login.Peer.Id);
        login.Identity.DeInitialize();
    }

    [Fact]
    public async Task Challenge_CarriesTheServerKeyAndId_AndTheGenuineLoginVerifies()
    {
        using var scope = new ServerStaticsScope();
        Login login = Connect();
        try
        {
            Assert.True(login.Request.AcceptedAwaitingSeal);
            var (nonce, serverPublic, serverId) = ReadChallenge(login.Peer);
            Assert.True(nonce.Length >= BasisDidLoginTranscript.MinNonceSize);
            Assert.Equal(NetworkServer.Configuration.ServerUUID, serverId);
            Assert.True(Guid.TryParseExact(serverId, "N", out _));

            // The server only takes data sealed with the key the client derives on its side.
            Assert.True(BasisCryptoHandshake.DeriveClientKey(login.ClientPrivate, login.ClientPublic, serverPublic, out byte[] clientKey));
            Assert.Equal(clientKey, login.Peer.InboundKey);

            Assert.True(await Answer(login, GenuineTranscript(login)));
        }
        finally
        {
            Release(login);
        }
    }

    [Fact]
    public void GenuineLogin_IsAdmittedOnlyOnceItsSignatureChecksOut()
    {
        using var scope = new ServerStaticsScope();
        Login login = Connect();
        try
        {
            Assert.False(Admitted(login));
            Assert.False(login.Identity.UUIDToNetID(login.PlayerDid.V, out _));
            Assert.Equal(0, login.Identity.CheckForDuplicates(login.PlayerDid));
            Assert.False(NetworkServer.AuthenticatedPeers.ContainsKey(login.Peer.Id));

            Respond(login, GenuineTranscript(login));
            Assert.True(WaitFor(() => NetworkServer.AuthenticatedPeers.ContainsKey(login.Peer.Id) || login.Peer.DisconnectCalls > 0));

            Assert.True(login.Identity.NetIDToUUID(login.Peer, out string uuid));
            Assert.Equal(login.PlayerDid.V, uuid);
            Assert.Equal(1, login.Identity.CheckForDuplicates(login.PlayerDid));
            Assert.Equal(0, login.Peer.DisconnectCalls);
        }
        finally
        {
            Release(login);
        }
    }

    [Fact]
    public async Task RelayedLogin_SignedForAnotherServerConnectionOrPayload_IsRefused()
    {
        using var scope = new ServerStaticsScope();
        Login login = Connect();
        try
        {
            var (nonce, serverPublic, serverId) = ReadChallenge(login.Peer);
            BasisCryptoHandshake.GenerateKeyPair(out _, out byte[] relayPublic);
            byte[] relayPayload = DidLoginChannelBindingTests.ConnectPayload(login.ClientPublic, LifecycleSupport.MakeReady(login.PlayerDid.V, "Somebody Else"));
            byte[] relayHash = BasisDidLoginTranscript.HashConnectionPayload(relayPayload, 0, relayPayload.Length);

            // The player signed this server's nonce, but for the relay's connection, name or payload.
            Assert.False(await Answer(login, BasisDidLoginTranscript.Build(login.PlayerDid.V, "relay-server-uuid", nonce, login.ClientPublic, serverPublic, login.PayloadHash)));
            Assert.False(await Answer(login, BasisDidLoginTranscript.Build(login.PlayerDid.V, serverId, nonce, login.ClientPublic, relayPublic, login.PayloadHash)));
            Assert.False(await Answer(login, BasisDidLoginTranscript.Build(login.PlayerDid.V, serverId, nonce, relayPublic, serverPublic, login.PayloadHash)));
            Assert.False(await Answer(login, BasisDidLoginTranscript.Build(login.PlayerDid.V, serverId, nonce, login.ClientPublic, serverPublic, relayHash)));
        }
        finally
        {
            Release(login);
        }
    }

    [Fact]
    public void ForgedLogin_IsRefusedAndNeverCounted()
    {
        using var scope = new ServerStaticsScope();
        Login login = Connect();
        try
        {
            var (nonce, serverPublic, _) = ReadChallenge(login.Peer);
            Respond(login, BasisDidLoginTranscript.Build(login.PlayerDid.V, "relay-server-uuid", nonce, login.ClientPublic, serverPublic, login.PayloadHash));
            Assert.True(WaitFor(() => login.Peer.DisconnectCalls > 0));

            Assert.False(Admitted(login));
            Assert.False(NetworkServer.AuthenticatedPeers.ContainsKey(login.Peer.Id));
            Assert.Equal(0, login.Identity.CheckForDuplicates(login.PlayerDid));
        }
        finally
        {
            Release(login);
        }
    }

    [Fact]
    public void DuplicateLimit_CountsVerifiedLoginsOnly()
    {
        using var scope = new ServerStaticsScope();
        BasisDidLink.BasisDIDAuthIdentity identity = UseDidLogin(duplicates: 1);
        var player = NewPlayer();
        Login first = Connect(identity, player);
        Login second = Connect(identity, player);
        try
        {
            Assert.True(second.Peer.HasSentOn(BasisNetworkCommons.AuthIdentityChannel));

            Respond(first, GenuineTranscript(first));
            Assert.True(WaitFor(() => Admitted(first)));
            Respond(second, GenuineTranscript(second));
            Assert.True(WaitFor(() => second.Peer.DisconnectCalls > 0));

            Assert.Equal("To Many Auths From this DID!", RefusalReason(second.Peer));
            Assert.False(Admitted(second));
            Assert.True(Admitted(first));
            Assert.Equal(1, identity.CheckForDuplicates(player.Did));
        }
        finally
        {
            identity.RemoveConnection(second.Peer.Id);
            Release(first);
        }
    }

    [Fact]
    public void TransportThatCannotHoldBackUnsealedData_IsRefusedBeforeAccepting()
    {
        using var scope = new ServerStaticsScope();
        Login login = Connect(canAwaitSeal: false);
        try
        {
            Assert.True(login.Request.WasRejected);
            Assert.False(login.Request.WasAccepted);
            Assert.Equal("Transport cannot protect the login.", LifecycleSupport.RejectReason(login.Request.RejectPayload));
            Assert.False(login.Identity.AuthIdentity.ContainsKey(login.Peer.Id));
        }
        finally
        {
            Release(login);
        }
    }

    [Fact]
    public void TransportThatCannotSeal_RefusesTheLoginBeforeSendingAChallenge()
    {
        using var scope = new ServerStaticsScope();
        Login login = Connect(canSeal: false);
        try
        {
            Assert.True(WaitFor(() => login.Peer.DisconnectCalls > 0));
            Assert.False(login.Peer.HasSentOn(BasisNetworkCommons.AuthIdentityChannel));
            Assert.False(login.Identity.AuthIdentity.ContainsKey(login.Peer.Id));
            Assert.Equal("Transport cannot protect the login.", RefusalReason(login.Peer));
        }
        finally
        {
            Release(login);
        }
    }

    [Fact]
    public void MissingConnectionKey_IsRefused()
    {
        using var scope = new ServerStaticsScope();
        BasisDidLink.BasisDIDAuthIdentity identity = UseDidLogin();
        try
        {
            FakeNetPeer peer = LifecycleSupport.Peer(LifecycleSupport.NextPeerId());
            var writer = new NetDataWriter();
            writer.Put(BasisNetworkVersion.ServerVersion);
            BasisNetworkApplication.Write(writer, BasisNetworkApplication.DefaultCompanyName, BasisNetworkApplication.DefaultProductName);
            new BytesMessage().Serialize(writer, new byte[] { 1 });
            // A pre-56 client goes straight from the password to the ReadyMessage.
            LifecycleSupport.MakeReady(LifecycleSupport.NewUuid(), "Connie").Serialize(writer);
            BasisServerHandle.BasisServerHandleEvents.HandleConnectionRequest(LifecycleSupport.Request(writer.CopyData(), accepted: peer));

            Assert.False(peer.HasSentOn(BasisNetworkCommons.AuthIdentityChannel));
            Assert.Null(peer.InboundKey);
            Assert.Equal("Invalid connection key.", RefusalReason(peer));
        }
        finally
        {
            identity.DeInitialize();
        }
    }

    [Fact]
    public void TransportVerifiedIdentity_IsAdmittedWithoutAChallengeUnderThatIdentity()
    {
        using var scope = new ServerStaticsScope();
        BasisDidLink.BasisDIDAuthIdentity identity = UseDidLogin();
        const string verified = "steam:76561197960287930";
        FakeNetPeer peer = LifecycleSupport.Peer(LifecycleSupport.NextPeerId());
        try
        {
            byte[] data = LifecycleSupport.ConnectPayload(BasisNetworkVersion.ServerVersion, new byte[] { 1 }, LifecycleSupport.MakeReady(NewPlayer().Did.V, "Connie"));
            var request = new RecordingConnectionRequest
            {
                Data = new NetDataReader(data),
                RemoteEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 6006),
                PeerToReturn = peer,
                VerifiedIdentity = verified,
            };
            BasisServerHandle.BasisServerHandleEvents.HandleConnectionRequest(request);

            Assert.True(request.WasAccepted);
            Assert.False(request.AcceptedAwaitingSeal);
            Assert.False(peer.HasSentOn(BasisNetworkCommons.AuthIdentityChannel));
            Assert.Null(peer.InboundKey);
            Assert.True(NetworkServer.AuthenticatedPeers.ContainsKey(peer.Id));
            Assert.True(identity.NetIDToUUID(peer, out string uuid));
            Assert.Equal(verified, uuid);
            Assert.True(identity.UUIDToNetID(verified, out int id));
            Assert.Equal(peer.Id, id);
        }
        finally
        {
            identity.RemoveConnection(peer.Id);
            identity.DeInitialize();
        }
    }

    [Fact]
    public void TransportVerifiedIdentity_WithDidLoginOff_StillNamesThePlayer()
    {
        using var scope = new ServerStaticsScope();
        NetworkServer.Configuration = new Configuration
        {
            PeerLimit = 100,
            UseAuth = false,
            UseAuthIdentity = false,
            BasisUserRestrictionMode = BasisUserRestrictionMode.Normal,
        };
        NetworkServer.Server = new FakeNetManager { ConnectedPeersCount = 0 };
        NetworkServer.Auth = new FakeAuth { Result = true };
        NetworkServer.AuthIdentity = new MapAuthIdentity();
        NetworkServer.AllowList = new BasisAllowList();
        NetworkServer.BanList = new BasisBanList();
        NetworkServer.HighQualityLength = ConvertToSize(BitQuality.High);
        const string verified = "steam:76561197960287931";
        FakeNetPeer peer = LifecycleSupport.Peer(LifecycleSupport.NextPeerId());
        byte[] data = LifecycleSupport.ConnectPayload(BasisNetworkVersion.ServerVersion, new byte[] { 1 }, LifecycleSupport.MakeReady(LifecycleSupport.NewUuid(), "Connie"));
        var request = new RecordingConnectionRequest
        {
            Data = new NetDataReader(data),
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 6006),
            PeerToReturn = peer,
            VerifiedIdentity = verified,
        };

        BasisServerHandle.BasisServerHandleEvents.HandleConnectionRequest(request);

        Assert.True(NetworkServer.AuthenticatedPeers.ContainsKey(peer.Id));
        Assert.True(BasisSavedState.GetLastPlayerMetaData(peer, out ClientMetaDataMessage meta));
        Assert.Equal(verified, meta.playerUUID);
    }
}
