using System.Text;
using Basis.Contrib.Crypto;
using Xunit;
using CryptoRng = System.Security.Cryptography.RandomNumberGenerator;
using SystemChaCha = System.Security.Cryptography.ChaCha20Poly1305;

namespace BasisServerTests;

public class BasisChaCha20Poly1305Tests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    private static byte[] RandomBytes(int size)
    {
        byte[] bytes = new byte[size];
        CryptoRng.Fill(bytes);
        return bytes;
    }

    [Fact]
    public void Rfc8439BlockFunction()
    {
        byte[] key = Hex("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        byte[] output = new byte[64];
        BasisChaCha20Poly1305.Block(BasisChaCha20Poly1305.ExpandKey(key), 1, Hex("000000090000004a00000000"), output);
        Assert.Equal(Hex("10f1e7e4d13b5915500fdd1fa32071c4c7d1f4c733c068030422aa9ac3d46c4ed2826446079faa0914c2d705d98b02a2b5129cd1de164eb9cbd083e8a2503c4e"), output);
    }

    [Fact]
    public void Rfc8439Poly1305()
    {
        byte[] tag = new byte[16];
        BasisChaCha20Poly1305.Poly1305(Hex("85d6be7857556d337f4452fe42d506a80103808afb0db2fd4abff6af4149f51b"), Encoding.ASCII.GetBytes("Cryptographic Forum Research Group"), tag);
        Assert.Equal(Hex("a8061dc1305136c6c22b8baf0c0127a9"), tag);
    }

    [Fact]
    public void Rfc8439Aead()
    {
        uint[] key = BasisChaCha20Poly1305.ExpandKey(Hex("808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f"));
        byte[] nonce = Hex("070000004041424344454647");
        byte[] aad = Hex("50515253c0c1c2c3c4c5c6c7");
        byte[] data = Encoding.ASCII.GetBytes("Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.");
        byte[] tag = new byte[16];
        BasisChaCha20Poly1305.Seal(key, nonce, aad, data, tag);
        Assert.Equal(Hex("d31a8d34648e60db7b86afbc53ef7ec2a4aded51296e08fea9e2b5a736ee62d63dbea45e8ca9671282fafb69da92728b1a71de0a9e060b2905d6a5b67ecd3b3692ddbd7f2d778b8c9803aee328091b58fab324e4fad675945585808b4831d7bc3ff4def08e4b7a9de576d26586cec64b6116"), data);
        Assert.Equal(Hex("1ae10b594f09e26a7e902ecbd0600691"), tag);
        Assert.True(BasisChaCha20Poly1305.Open(key, nonce, aad, data, tag));
        Assert.Equal("Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.", Encoding.ASCII.GetString(data));
    }

    [Fact]
    public void MatchesTheSystemImplementationBothWays()
    {
        if (!SystemChaCha.IsSupported) return;
        Random sizes = new Random(1234);
        for (int round = 0; round < 400; round++)
        {
            byte[] keyBytes = RandomBytes(32), nonce = RandomBytes(12), aad = RandomBytes(sizes.Next(0, 40)), plain = RandomBytes(round < 300 ? round : sizes.Next(0, 3000));
            uint[] key = BasisChaCha20Poly1305.ExpandKey(keyBytes);
            using SystemChaCha reference = new SystemChaCha(keyBytes);

            byte[] expectedCipher = new byte[plain.Length], expectedTag = new byte[16];
            reference.Encrypt(nonce, plain, expectedCipher, expectedTag, aad);
            byte[] ours = (byte[])plain.Clone(), ourTag = new byte[16];
            BasisChaCha20Poly1305.Seal(key, nonce, aad, ours, ourTag);
            Assert.Equal(expectedCipher, ours);
            Assert.Equal(expectedTag, ourTag);

            byte[] reopened = (byte[])expectedCipher.Clone();
            Assert.True(BasisChaCha20Poly1305.Open(key, nonce, aad, reopened, expectedTag));
            Assert.Equal(plain, reopened);
            byte[] byReference = new byte[plain.Length];
            reference.Decrypt(nonce, ours, ourTag, byReference, aad);
            Assert.Equal(plain, byReference);
        }
    }

    [Fact]
    public void ATamperedMessageIsRejectedAndLeftUntouched()
    {
        uint[] key = BasisChaCha20Poly1305.ExpandKey(RandomBytes(32));
        byte[] nonce = RandomBytes(12), aad = { 7 }, data = RandomBytes(100), tag = new byte[16];
        BasisChaCha20Poly1305.Seal(key, nonce, aad, data, tag);
        for (int index = 0; index < data.Length; index += 9)
        {
            byte[] tampered = (byte[])data.Clone();
            tampered[index] ^= 0x40;
            byte[] before = (byte[])tampered.Clone();
            Assert.False(BasisChaCha20Poly1305.Open(key, nonce, aad, tampered, tag));
            Assert.Equal(before, tampered);
        }
        byte[] badTag = (byte[])tag.Clone();
        badTag[15] ^= 1;
        Assert.False(BasisChaCha20Poly1305.Open(key, nonce, aad, (byte[])data.Clone(), badTag));
        Assert.False(BasisChaCha20Poly1305.Open(key, nonce, new byte[] { 8 }, (byte[])data.Clone(), tag));
    }

    [Fact]
    public void OneCipherIsSafeToShareAcrossThreads()
    {
        byte[] keyBytes = RandomBytes(32);
        BasisAeadCipher cipher = new BasisAeadCipher(keyBytes);
        Parallel.For(0, 2000, index =>
        {
            byte[] nonce = new byte[12];
            BitConverter.GetBytes((long)index).CopyTo(nonce, 0);
            byte[] plain = RandomBytes(16 + index % 200);
            byte[] buffer = new byte[plain.Length + 16];
            Buffer.BlockCopy(plain, 0, buffer, 0, plain.Length);
            cipher.Seal(nonce, 3, buffer, 0, plain.Length, buffer, plain.Length);
            Assert.True(cipher.Open(nonce, 3, buffer, 0, plain.Length, buffer, plain.Length));
            Assert.Equal(plain, buffer.AsSpan(0, plain.Length).ToArray());
        });
    }

    [Fact]
    public void OpenRefusesOutOfRangeArgumentsInsteadOfThrowing()
    {
        BasisAeadCipher cipher = new BasisAeadCipher(RandomBytes(32));
        byte[] buffer = new byte[40];
        Assert.False(cipher.Open(new byte[12], 0, buffer, 30, 20, buffer, 0));
        Assert.False(cipher.Open(new byte[12], 0, buffer, 0, -1, buffer, 0));
        Assert.False(cipher.Open(new byte[12], 0, buffer, 0, 10, buffer, 30));
        Assert.False(cipher.Open(new byte[11], 0, buffer, 0, 10, buffer, 20));
    }
}
