using System.Diagnostics;
using System.Net;
using System.Text;
using Basis.Contrib.Auth.DecentralizedIds;
using Basis.Contrib.Auth.DecentralizedIds.Newtypes;
using Basis.Contrib.Crypto;
using Basis.Network.Core;
using BasisNetworkCore.Security;
using BasisNetworkServer.Security;
using Xunit;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;
using static Basis.Network.Core.Serializable.SerializableBasis;
using static SerializableBasis;
using CryptoRng = System.Security.Cryptography.RandomNumberGenerator;

namespace BasisServerTests;

public class DidKeyResolverValidationTests
{
    [Theory]
    [InlineData("did:key:")]
    [InlineData("")]
    [InlineData("did:key")]
    [InlineData("xdid:key:z6MkhaXgBZDvotDkL5257faiztiGiC2QtKLGpbnnEGta2doK")]
    [InlineData("did:web:example.com")]
    public async Task AMalformedDidKey_IsAValidationFailure(string value)
    {
        await Assert.ThrowsAsync<DidKeyDecodeException>(() => new DidKeyResolver().ResolveDocument(new Did(value)));
    }

    [Theory]
    [InlineData("did:key:z")]
    [InlineData("did:key:zz")]
    [InlineData("did:key:did:key:")]
    public async Task ATruncatedDidKey_FailsWithoutStoppingTheProcess(string value)
    {
        await Assert.ThrowsAnyAsync<Exception>(() => new DidKeyResolver().ResolveDocument(new Did(value)));
    }

    [Fact]
    public async Task AWellFormedDidKey_StillResolves()
    {
        byte[] privateBytes = new byte[Ed25519.PrivkeySize];
        CryptoRng.Fill(privateBytes);
        Did did = DidKeyResolver.EncodePubkeyAsDid(Ed25519.ConvertPrivkeyToPubkey(new PrivKey(privateBytes))!);
        DidDocument document = await new DidKeyResolver().ResolveDocument(did);
        Assert.Single(document.Pubkeys);
    }

    [Fact]
    public async Task VerifyingAResponseForAnEmptyDidKey_Throws_InsteadOfAsserting()
    {
        DidAuthentication auth = new DidAuthentication(new Config());
        Challenge challenge = auth.MakeChallenge(new Did("did:key:"));
        Response response = new Response(new Signature(new byte[64]), new DidUrlFragment(string.Empty));
        await Assert.ThrowsAsync<DidKeyDecodeException>(() => auth.VerifyResponse(response, challenge, new Payload(new byte[32])));
    }
}

[Collection("BasisServer shared network statics")]
public class DidKeyLoginValidationTests
{
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

    [Fact]
    public void ALoginClaimingAnEmptyDidKey_IsRefused_AndTheServerKeepsRunning()
    {
        using ServerStaticsScope scope = new ServerStaticsScope();
        NetworkServer.Configuration = new Configuration
        {
            PeerLimit = 100,
            UseAuth = false,
            UseAuthIdentity = true,
            HasFileSupport = false,
            HowManyDuplicateAuthCanExist = 2,
            BasisUserRestrictionMode = BasisUserRestrictionMode.Normal,
        };
        NetworkServer.Server = new FakeNetManager { ConnectedPeersCount = 0 };
        NetworkServer.Auth = new FakeAuth { Result = true };
        NetworkServer.AllowList = new BasisAllowList();
        NetworkServer.BanList = new BasisBanList();
        NetworkServer.HighQualityLength = ConvertToSize(BitQuality.High);
        BasisDidLink.BasisDIDAuthIdentity identity = new BasisDidLink.BasisDIDAuthIdentity();
        NetworkServer.AuthIdentity = identity;
        FakeNetPeer peer = LifecycleSupport.Peer(LifecycleSupport.NextPeerId());
        try
        {
            BasisCryptoHandshake.GenerateKeyPair(out _, out byte[] clientPublic);
            ReadyMessage ready = LifecycleSupport.MakeReady("did:key:", "Empty");
            byte[] data = LifecycleSupport.ConnectPayload(BasisNetworkVersion.ServerVersion, new byte[] { 1 }, ready, connectionKey: clientPublic);
            RecordingConnectionRequest request = new RecordingConnectionRequest
            {
                Data = new NetDataReader(data),
                RemoteEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.41"), 6006),
                PeerToReturn = peer,
                CanAwaitSeal = true,
            };
            BasisServerHandle.BasisServerHandleEvents.HandleConnectionRequest(request);
            Assert.True(WaitFor(() => peer.HasSentOn(BasisNetworkCommons.AuthIdentityChannel)), "no challenge was sent");

            NetDataWriter writer = new NetDataWriter();
            new BytesMessage().Serialize(writer, new byte[64]);
            new BytesMessage().Serialize(writer, Encoding.UTF8.GetBytes("N/A"));
            BasisServerHandle.BasisServerHandleEvents.HandleAuth(NetPacketReader.Create(writer.CopyData(), 0, writer.Length, static () => { }), peer);

            Assert.True(WaitFor(() => peer.DisconnectCalls > 0), "the login was neither admitted nor refused");
            Assert.False(identity.NetIDToUUID(peer, out _));
            Assert.False(NetworkServer.AuthenticatedPeers.ContainsKey(peer.Id));
            Assert.Equal(0, identity.CheckForDuplicates(new Did("did:key:")));
        }
        finally
        {
            identity.RemoveConnection(peer.Id);
            identity.DeInitialize();
        }
    }
}
