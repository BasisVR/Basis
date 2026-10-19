using System.Net;
using System.Text;
using Basis.Contrib.Crypto;
using Basis.Network.Core;
using Xunit;

namespace BasisServerTests;

/// <summary>
/// The X25519 + HKDF key agreement (BasisCryptoHandshake) behind encrypted direct connections:
/// full two-sided agreement, determinism, the documented HKDF construction and malformed-key rejection.
/// </summary>
public class CryptoHandshakeTests
{
    /// Mirror of the handshake's public-key ordering (unsigned lexicographic, length tiebreak).
    private static int CompareKeys(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int d = a[i] - b[i];
            if (d != 0) return d;
        }
        return a.Length - b.Length;
    }

    // ------------------------------------------------------------- handshake

    [Fact]
    public void GenerateKeyPair_ProducesDistinctWellFormedPairs()
    {
        BasisCryptoHandshake.GenerateKeyPair(out byte[] privateKey, out byte[] publicKey);
        Assert.Equal(BasisCryptoHandshake.PrivateKeySize, privateKey.Length);
        Assert.Equal(BasisCryptoHandshake.PublicKeySize, publicKey.Length);
        Assert.Equal(publicKey, BasisX25519.DerivePublicKey(privateKey));

        BasisCryptoHandshake.GenerateKeyPair(out byte[] secondPrivate, out byte[] secondPublic);
        Assert.NotEqual(privateKey, secondPrivate);
        Assert.NotEqual(publicKey, secondPublic);
    }

    [Fact]
    public void DerivePeerKeys_BothSides_DeriveComplementaryDirectionalKeys()
    {
        BasisCryptoHandshake.GenerateKeyPair(out byte[] clientPrivate, out byte[] clientPublic);
        BasisCryptoHandshake.GenerateKeyPair(out byte[] serverPrivate, out byte[] serverPublic);

        Assert.True(BasisCryptoHandshake.DerivePeerKeys(clientPrivate, clientPublic, serverPublic, out byte[] clientSend, out byte[] clientRecv));
        Assert.True(BasisCryptoHandshake.DerivePeerKeys(serverPrivate, serverPublic, clientPublic, out byte[] serverSend, out byte[] serverRecv));

        Assert.Equal(BasisCryptoHandshake.KeySize, clientSend.Length);
        Assert.Equal(BasisCryptoHandshake.KeySize, clientRecv.Length);
        // Each side's send key is the other side's receive key.
        Assert.Equal(clientSend, serverRecv);
        Assert.Equal(clientRecv, serverSend);
        // Directions use independent keys.
        Assert.NotEqual(clientSend, clientRecv);
    }

    [Fact]
    public void DerivePeerKeys_IsDeterministic()
    {
        BasisCryptoHandshake.GenerateKeyPair(out byte[] privateKey, out byte[] publicKey);
        BasisCryptoHandshake.GenerateKeyPair(out _, out byte[] peerPublic);

        Assert.True(BasisCryptoHandshake.DerivePeerKeys(privateKey, publicKey, peerPublic, out byte[] send1, out byte[] recv1));
        Assert.True(BasisCryptoHandshake.DerivePeerKeys(privateKey, publicKey, peerPublic, out byte[] send2, out byte[] recv2));

        Assert.Equal(send1, send2);
        Assert.Equal(recv1, recv2);
    }

    [Fact]
    public void DerivePeerKeys_MatchesDocumentedHkdfConstruction()
    {
        BasisCryptoHandshake.GenerateKeyPair(out byte[] privateA, out byte[] publicA);
        BasisCryptoHandshake.GenerateKeyPair(out byte[] privateB, out byte[] publicB);

        // Recompute the spec by hand: ECDH secret, transcript salt = lowPub || highPub,
        // HKDF-SHA256 with the two directional info strings; the lower public key is "A".
        byte[] shared = BasisX25519.Agree(privateA, publicB);
        bool aIsLow = CompareKeys(publicA, publicB) < 0;
        byte[] lowPublic = aIsLow ? publicA : publicB;
        byte[] highPublic = aIsLow ? publicB : publicA;
        byte[] salt = new byte[lowPublic.Length + highPublic.Length];
        lowPublic.CopyTo(salt, 0);
        highPublic.CopyTo(salt, lowPublic.Length);
        byte[] keyLowToHigh = BasisHkdf.DeriveKey(shared, salt, Encoding.ASCII.GetBytes("basis-crypto-v1-ab"), BasisCryptoHandshake.KeySize);
        byte[] keyHighToLow = BasisHkdf.DeriveKey(shared, salt, Encoding.ASCII.GetBytes("basis-crypto-v1-ba"), BasisCryptoHandshake.KeySize);

        Assert.True(BasisCryptoHandshake.DerivePeerKeys(privateA, publicA, publicB, out byte[] sendA, out byte[] recvA));
        Assert.True(BasisCryptoHandshake.DerivePeerKeys(privateB, publicB, publicA, out byte[] sendB, out byte[] recvB));

        Assert.Equal(aIsLow ? keyLowToHigh : keyHighToLow, sendA);
        Assert.Equal(aIsLow ? keyHighToLow : keyLowToHigh, recvA);
        Assert.Equal(aIsLow ? keyHighToLow : keyLowToHigh, sendB);
        Assert.Equal(aIsLow ? keyLowToHigh : keyHighToLow, recvB);
    }

    [Fact]
    public void DerivePeerKeys_IdenticalPublicKeys_ReturnsFalse()
    {
        BasisCryptoHandshake.GenerateKeyPair(out byte[] privateKey, out byte[] publicKey);
        Assert.False(BasisCryptoHandshake.DerivePeerKeys(privateKey, publicKey, publicKey, out byte[] send, out byte[] recv));
        Assert.Empty(send);
        Assert.Empty(recv);
    }

    [Fact]
    public void DerivePeerKeys_AllZeroPeerPublic_ReturnsFalse()
    {
        // The all-zero point yields an all-zero X25519 shared secret, which the
        // agreement rejects; the handshake must surface that as a clean false.
        BasisCryptoHandshake.GenerateKeyPair(out byte[] privateKey, out byte[] publicKey);
        byte[] zeroPublic = new byte[BasisCryptoHandshake.PublicKeySize];
        Assert.False(BasisCryptoHandshake.DerivePeerKeys(privateKey, publicKey, zeroPublic, out byte[] send, out byte[] recv));
        Assert.Empty(send);
        Assert.Empty(recv);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(31)]
    public void DerivePeerKeys_UndersizedPeerPublic_ReturnsFalse(int size)
    {
        BasisCryptoHandshake.GenerateKeyPair(out byte[] privateKey, out byte[] publicKey);
        byte[] malformed = new byte[size];
        for (int i = 0; i < size; i++) malformed[i] = (byte)(i + 1);
        Assert.False(BasisCryptoHandshake.DerivePeerKeys(privateKey, publicKey, malformed, out byte[] send, out byte[] recv));
        Assert.Empty(send);
        Assert.Empty(recv);
    }

    [Theory]
    [InlineData(33)]
    [InlineData(64)]
    public void DerivePeerKeys_OversizedPeerPublic_DoesNotThrow(int size)
    {
        BasisCryptoHandshake.GenerateKeyPair(out byte[] privateKey, out byte[] publicKey);
        byte[] oversized = new byte[size];
        for (int i = 0; i < size; i++) oversized[i] = (byte)(0x40 + i);

        // Success or failure is acceptable for garbage; escaping exceptions are not.
        bool ok = BasisCryptoHandshake.DerivePeerKeys(privateKey, publicKey, oversized, out byte[] send, out byte[] recv);
        if (ok)
        {
            Assert.Equal(BasisCryptoHandshake.KeySize, send.Length);
            Assert.Equal(BasisCryptoHandshake.KeySize, recv.Length);
        }
        else
        {
            Assert.Empty(send);
            Assert.Empty(recv);
        }
    }

    [Fact]
    public void DerivePeerKeys_UndersizedPrivateKey_ReturnsFalse()
    {
        BasisCryptoHandshake.GenerateKeyPair(out byte[] fullPrivate, out byte[] publicKey);
        BasisCryptoHandshake.GenerateKeyPair(out _, out byte[] peerPublic);
        byte[] truncatedPrivate = fullPrivate.AsSpan(0, 16).ToArray();
        Assert.False(BasisCryptoHandshake.DerivePeerKeys(truncatedPrivate, publicKey, peerPublic, out byte[] send, out byte[] recv));
        Assert.Empty(send);
        Assert.Empty(recv);
    }

    [Fact]
    public void DerivePeerKeys_DifferentPeers_ProduceDifferentKeys()
    {
        BasisCryptoHandshake.GenerateKeyPair(out byte[] privateA, out byte[] publicA);
        BasisCryptoHandshake.GenerateKeyPair(out _, out byte[] publicB);
        BasisCryptoHandshake.GenerateKeyPair(out _, out byte[] publicC);

        Assert.True(BasisCryptoHandshake.DerivePeerKeys(privateA, publicA, publicB, out byte[] sendAB, out byte[] recvAB));
        Assert.True(BasisCryptoHandshake.DerivePeerKeys(privateA, publicA, publicC, out byte[] sendAC, out byte[] recvAC));

        Assert.NotEqual(sendAB, sendAC);
        Assert.NotEqual(recvAB, recvAC);
    }
}
