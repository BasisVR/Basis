using System.Text;
using Basis.Contrib.Auth.DecentralizedIds;
using Basis.Contrib.Auth.DecentralizedIds.Newtypes;
using Basis.Contrib.Crypto;
using Basis.Network.Core;
using BasisNetworkClient;
using Xunit;
using static Basis.Network.Core.Serializable.SerializableBasis;
using CryptoRng = System.Security.Cryptography.RandomNumberGenerator;

namespace BasisServerTests;

public class DidLoginLegacyCompatTests
{
    private static (PrivKey Private, Did Did) NewPlayer()
    {
        byte[] privateBytes = new byte[Ed25519.PrivkeySize];
        CryptoRng.Fill(privateBytes);
        var privateKey = new PrivKey(privateBytes);
        return (privateKey, DidKeyResolver.EncodePubkeyAsDid(Ed25519.ConvertPrivkeyToPubkey(privateKey)!));
    }

    private static byte[] RandomBytes(int size)
    {
        byte[] bytes = new byte[size];
        CryptoRng.Fill(bytes);
        return bytes;
    }

    private static byte[] Transcript(string did, byte[] nonce)
    {
        BasisCryptoHandshake.GenerateKeyPair(out _, out byte[] clientPublic);
        BasisCryptoHandshake.GenerateKeyPair(out _, out byte[] serverPublic);
        return BasisDidLoginTranscript.Build(did, "server", nonce, clientPublic, serverPublic, RandomBytes(BasisDidLoginTranscript.PayloadHashSize));
    }

    private static NetPacketReader Challenge(params byte[][] fields)
    {
        var writer = new NetDataWriter();
        foreach (byte[] field in fields) new BytesMessage().Serialize(writer, field);
        return NetPacketReader.Create(writer.CopyData(), 0, writer.Length, static () => { });
    }

    private static bool Answer(bool legacyLogin, PrivKey key, Did did, NetPacketReader challenge, out Signature signature)
    {
        signature = null!;
        BasisCryptoHandshake.GenerateKeyPair(out _, out byte[] connectionKey);
        var client = new NetworkClient
        {
            LegacyLogin = legacyLogin,
            ConnectionPublicKey = legacyLogin ? null : connectionKey,
            ConnectionPayloadHash = legacyLogin ? null : RandomBytes(BasisDidLoginTranscript.PayloadHashSize),
        };
        if (!BasisDIDAuthIdentityClient.RespondToChallenge(client, key, did.V, challenge, out NetDataWriter writer)) return false;
        NetPacketReader reader = NetPacketReader.Create(writer.CopyData(), 0, writer.Length, static () => { });
        Assert.True(new BytesMessage().Deserialize(reader, out byte[] sig));
        Assert.True(new BytesMessage().Deserialize(reader, out byte[] fragment));
        Assert.Equal("N/A", Encoding.UTF8.GetString(fragment));
        signature = new Signature(sig);
        return true;
    }

    private static async Task<bool> Verifies(Did player, byte[] nonce, byte[] payload, Signature signature)
    {
        var didAuth = new DidAuthentication(new Config());
        var response = new Response(signature, new DidUrlFragment(string.Empty));
        return (await didAuth.VerifyResponse(response, new Challenge(player, new Nonce(nonce)), new Payload(payload))).IsOk;
    }

    [Fact]
    public async Task LegacyLogin_AnswersAProtocol55ChallengeTheWayA55ServerChecksIt()
    {
        var (key, did) = NewPlayer();
        byte[] nonce = RandomBytes(BasisDidLoginTranscript.LegacyNonceSize);
        Assert.True(Answer(true, key, did, Challenge(nonce), out Signature signature));
        Assert.True(await Verifies(did, nonce, nonce, signature));
    }

    [Fact]
    public async Task LegacySignature_CannotLogIntoAProtocol56Server()
    {
        var (key, did) = NewPlayer();
        byte[] nonce = RandomBytes(BasisDidLoginTranscript.LegacyNonceSize);
        Assert.True(Answer(true, key, did, Challenge(nonce), out Signature signature));
        Assert.False(await Verifies(did, nonce, Transcript(did.V, nonce), signature));
    }

    [Theory]
    [InlineData(BasisDidLoginTranscript.MinNonceSize)]
    [InlineData(BasisDidLoginTranscript.LegacyNonceSize - 1)]
    [InlineData(BasisDidLoginTranscript.LegacyNonceSize + 1)]
    public void LegacyLogin_RefusesNoncesA55ServerNeverSends(int size)
    {
        var (key, did) = NewPlayer();
        Assert.False(Answer(true, key, did, Challenge(RandomBytes(size)), out _));
    }

    [Fact]
    public void LegacyLogin_RefusesToSignAnythingThatStartsLikeATranscript()
    {
        var (key, did) = NewPlayer();
        byte[] transcript = Transcript(did.V, RandomBytes(BasisDidLoginTranscript.MinNonceSize));
        byte[] lookalike = new byte[BasisDidLoginTranscript.LegacyNonceSize];
        Array.Copy(transcript, lookalike, Math.Min(transcript.Length, lookalike.Length));
        Assert.False(BasisDidLoginTranscript.IsLegacyNonce(lookalike));
        Assert.False(Answer(true, key, did, Challenge(lookalike), out _));
    }

    [Fact]
    public void Transcripts_AreNeverLegacyNonces()
    {
        Assert.False(BasisDidLoginTranscript.IsLegacyNonce(Transcript(string.Empty, RandomBytes(BasisDidLoginTranscript.MinNonceSize))));
        Assert.False(BasisDidLoginTranscript.IsLegacyNonce(Transcript(string.Empty, RandomBytes(BasisDidLoginTranscript.LegacyNonceSize))));
        Assert.True(BasisDidLoginTranscript.IsLegacyNonce(RandomBytes(BasisDidLoginTranscript.LegacyNonceSize)));
    }

    [Fact]
    public void CurrentLogin_RefusesAProtocol55Challenge()
    {
        var (key, did) = NewPlayer();
        Assert.False(Answer(false, key, did, Challenge(RandomBytes(BasisDidLoginTranscript.LegacyNonceSize)), out _));
    }
}
