using Basis.Network.Core;
using Xunit;
using CryptoRng = System.Security.Cryptography.RandomNumberGenerator;

namespace BasisServerTests;

public class BasisMessageCipherTests
{
    private static byte[] Key()
    {
        byte[] key = new byte[32];
        CryptoRng.Fill(key);
        return key;
    }

    private static byte[] Sealed(BasisMessageCipher cipher, int stream, byte channel, DeliveryMethod method, byte[] payload)
    {
        byte[] buffer = new byte[payload.Length + BasisMessageCipher.Overhead];
        Buffer.BlockCopy(payload, 0, buffer, 0, payload.Length);
        cipher.Seal(stream, channel, method, buffer, 0, payload.Length);
        return buffer;
    }

    private static bool Open(BasisMessageCipher cipher, int stream, byte channel, DeliveryMethod method, byte[] sealedBytes, out byte[] payload)
    {
        byte[] copy = (byte[])sealedBytes.Clone();
        bool opened = cipher.TryOpen(stream, channel, method, copy, 0, copy.Length, out int length);
        payload = opened ? copy.AsSpan(0, length).ToArray() : Array.Empty<byte>();
        return opened;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1400)]
    public void SealedMessagesOpenToTheOriginalBytes(int size)
    {
        byte[] key = Key();
        byte[] payload = new byte[size];
        CryptoRng.Fill(payload);
        byte[] sealedBytes = Sealed(new BasisMessageCipher(key), 0, 7, DeliveryMethod.ReliableOrdered, payload);
        Assert.Equal(size + BasisMessageCipher.Overhead, sealedBytes.Length);
        Assert.True(Open(new BasisMessageCipher(key), 0, 7, DeliveryMethod.ReliableOrdered, sealedBytes, out byte[] opened));
        Assert.Equal(payload, opened);
    }

    [Fact]
    public void ChangingTheChannelMethodOrStreamBreaksTheSeal()
    {
        byte[] key = Key();
        byte[] sealedBytes = Sealed(new BasisMessageCipher(key, 2), 0, 7, DeliveryMethod.ReliableOrdered, new byte[] { 1, 2, 3 });
        Assert.False(Open(new BasisMessageCipher(key, 2), 0, 8, DeliveryMethod.ReliableOrdered, sealedBytes, out _));
        Assert.False(Open(new BasisMessageCipher(key, 2), 0, 7, DeliveryMethod.Unreliable, sealedBytes, out _));
        Assert.False(Open(new BasisMessageCipher(key, 2), 1, 7, DeliveryMethod.ReliableOrdered, sealedBytes, out _));
        Assert.True(Open(new BasisMessageCipher(key, 2), 0, 7, DeliveryMethod.ReliableOrdered, sealedBytes, out _));
    }

    [Fact]
    public void AnyFlippedBitAnotherKeyOrATruncatedMessageIsRejected()
    {
        byte[] key = Key();
        byte[] sealedBytes = Sealed(new BasisMessageCipher(key), 0, 3, DeliveryMethod.Unreliable, new byte[] { 9, 9, 9, 9 });
        for (int index = 0; index < sealedBytes.Length; index++)
        {
            byte[] tampered = (byte[])sealedBytes.Clone();
            tampered[index] ^= 0x01;
            Assert.False(Open(new BasisMessageCipher(key), 0, 3, DeliveryMethod.Unreliable, tampered, out _));
        }
        Assert.False(Open(new BasisMessageCipher(Key()), 0, 3, DeliveryMethod.Unreliable, sealedBytes, out _));
        Assert.False(Open(new BasisMessageCipher(key), 0, 3, DeliveryMethod.Unreliable, new byte[BasisMessageCipher.Overhead - 1], out _));
    }

    [Fact]
    public void ReplaysAreRejectedButReorderingInsideTheWindowIsNot()
    {
        byte[] key = Key();
        var sender = new BasisMessageCipher(key);
        var receiver = new BasisMessageCipher(key);
        byte[] first = Sealed(sender, 0, 1, DeliveryMethod.Unreliable, new byte[] { 1 });
        byte[] second = Sealed(sender, 0, 1, DeliveryMethod.Unreliable, new byte[] { 2 });
        Assert.True(Open(receiver, 0, 1, DeliveryMethod.Unreliable, second, out _));
        Assert.True(Open(receiver, 0, 1, DeliveryMethod.Unreliable, first, out _));
        Assert.False(Open(receiver, 0, 1, DeliveryMethod.Unreliable, first, out _));
        Assert.False(Open(receiver, 0, 1, DeliveryMethod.Unreliable, second, out _));
    }

    [Fact]
    public void MessagesOlderThanTheWindowAreRejected()
    {
        byte[] key = Key();
        var sender = new BasisMessageCipher(key);
        var receiver = new BasisMessageCipher(key);
        byte[] old = Sealed(sender, 0, 1, DeliveryMethod.Unreliable, new byte[] { 1 });
        byte[] latest = old;
        for (int index = 0; index < BasisReplayWindow.Size; index++)
        {
            latest = Sealed(sender, 0, 1, DeliveryMethod.Unreliable, new byte[] { 2 });
        }
        Assert.True(Open(receiver, 0, 1, DeliveryMethod.Unreliable, latest, out _));
        Assert.False(Open(receiver, 0, 1, DeliveryMethod.Unreliable, old, out _));
    }

    [Fact]
    public void AStarvedStreamIsNotPushedOutByABusyOne()
    {
        byte[] key = Key();
        var sender = new BasisMessageCipher(key, 3);
        var receiver = new BasisMessageCipher(key, 3);
        byte[] late = Sealed(sender, 2, BasisNetworkCommons.SceneChannel, DeliveryMethod.ReliableOrdered, new byte[] { 1 });
        for (int index = 0; index < BasisReplayWindow.Size * 2; index++)
        {
            Assert.True(Open(receiver, 0, 1, DeliveryMethod.Unreliable, Sealed(sender, 0, 1, DeliveryMethod.Unreliable, new byte[] { 2 }), out _));
        }
        Assert.True(Open(receiver, 2, BasisNetworkCommons.SceneChannel, DeliveryMethod.ReliableOrdered, late, out _));
    }
}
