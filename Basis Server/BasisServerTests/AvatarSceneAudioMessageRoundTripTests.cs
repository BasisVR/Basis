using Basis.Network.Core;
using Basis.Network.Core.Compression;
using Xunit;
using BitQuality = Basis.Network.Core.Compression.BasisAvatarBitPacking.BitQuality;
using AvatarLoadDataMessage = BasisNetworkCore.Serializable.SerializableBasis.AvatarLoadDataMessage;
using static SerializableBasis;

namespace BasisServerTests;

/// <summary>Shared plumbing for the avatar/scene/audio wire-message round-trip tests below.</summary>
internal static class Wire
{
    public static NetDataReader Reader(NetDataWriter w) => new(w.CopyData());

    public static NetDataReader Empty() => new(Array.Empty<byte>());

    public static byte[] RandomBytes(Random rng, int count)
    {
        byte[] bytes = new byte[count];
        rng.NextBytes(bytes);
        return bytes;
    }

    public static int PayloadSize(BitQuality q) => BasisAvatarBitPacking.ConvertToSize(q);
}

/// <summary>
/// AdditionalAvatarData wire contract: [PayloadSize:1][messageIndex:1][payload:PayloadSize],
/// collapsing to a single 0 byte for null or oversized (>255) arrays.
/// </summary>
public class AdditionalAvatarDataWireTests
{
    [Fact]
    public void AdditionalAvatarData_RoundTrip_PreservesAllFields()
    {
        var rng = new Random(42);
        byte[] payload = Wire.RandomBytes(rng, 32);
        var msg = new AdditionalAvatarData { messageIndex = 7, array = payload };
        var w = new NetDataWriter();
        msg.Serialize(w);
        Assert.Equal(2 + 32, w.Length);

        var result = default(AdditionalAvatarData);
        var reader = Wire.Reader(w);
        result.Deserialize(reader);
        Assert.Equal((byte)32, result.PayloadSize);
        Assert.Equal((byte)7, result.messageIndex);
        Assert.Equal(payload, result.array);
        Assert.Equal(0, reader.AvailableBytes);
    }

    [Fact]
    public void AdditionalAvatarData_MaxPayload255_RoundTrips()
    {
        var rng = new Random(43);
        byte[] payload = Wire.RandomBytes(rng, 255);
        var msg = new AdditionalAvatarData { messageIndex = 255, array = payload };
        var w = new NetDataWriter();
        msg.Serialize(w);
        Assert.Equal(257, w.Length);

        var result = default(AdditionalAvatarData);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal((byte)255, result.PayloadSize);
        Assert.Equal((byte)255, result.messageIndex);
        Assert.Equal(payload, result.array);
    }

    [Fact]
    public void AdditionalAvatarData_NullArray_WritesFullTwoByteHeader()
    {
        // Every entry writes [size:1][messageIndex:1] even when empty — a bare size-0 byte was
        // ambiguous against the next entry's header and desynced the whole additional section.
        var msg = new AdditionalAvatarData { messageIndex = 9, array = null };
        var w = new NetDataWriter();
        msg.Serialize(w);
        Assert.Equal(2, w.Length);
        Assert.Equal((byte)0, w.Data[0]);
        Assert.Equal((byte)9, w.Data[1]);

        var result = default(AdditionalAvatarData);
        var reader = Wire.Reader(w);
        result.Deserialize(reader);
        Assert.Equal((byte)0, result.PayloadSize);
        Assert.Equal((byte)9, result.messageIndex);
        Assert.Null(result.array);
        Assert.Equal(0, reader.AvailableBytes);
    }

    [Fact]
    public void AdditionalAvatarData_ArrayOver255_RejectedAsZeroPayload()
    {
        var msg = new AdditionalAvatarData { messageIndex = 3, array = new byte[256] };
        var w = new NetDataWriter();
        msg.Serialize(w);
        Assert.Equal(2, w.Length);
        Assert.Equal((byte)0, w.Data[0]);
        Assert.Equal((byte)3, w.Data[1]);

        var result = default(AdditionalAvatarData);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal((byte)0, result.PayloadSize);
        Assert.Equal((byte)3, result.messageIndex);
        Assert.Null(result.array);
    }

    [Fact]
    public void AdditionalAvatarData_EmptyReader_NoThrow_ZeroFallback()
    {
        var result = default(AdditionalAvatarData);
        var reader = Wire.Empty();
        var ex = Record.Exception(() => result.Deserialize(reader));
        Assert.Null(ex);
        Assert.Equal((byte)0, result.PayloadSize);
        Assert.Null(result.array);
    }

    [Fact]
    public void AdditionalAvatarData_MissingMessageIndex_NoThrow()
    {
        var reader = new NetDataReader(new byte[] { 5 });
        var result = default(AdditionalAvatarData);
        var ex = Record.Exception(() => result.Deserialize(reader));
        Assert.Null(ex);
        Assert.Equal((byte)5, result.PayloadSize);
        Assert.Null(result.array);
    }

    [Fact]
    public void AdditionalAvatarData_TruncatedPayload_NoThrow_ArrayStaysNull()
    {
        var w = new NetDataWriter();
        w.Put((byte)10);
        w.Put((byte)4);
        w.Put(new byte[] { 1, 2, 3 });
        var result = default(AdditionalAvatarData);
        var ex = Record.Exception(() => result.Deserialize(Wire.Reader(w)));
        Assert.Null(ex);
        Assert.Equal((byte)10, result.PayloadSize);
        Assert.Equal((byte)4, result.messageIndex);
        Assert.Null(result.array);
    }

    [Fact]
    public void AdditionalAvatarData_DoubleRoundTrip_IsByteIdentical()
    {
        var msg = new AdditionalAvatarData { messageIndex = 12, array = Wire.RandomBytes(new Random(44), 17) };
        var w1 = new NetDataWriter();
        msg.Serialize(w1);

        var mid = default(AdditionalAvatarData);
        mid.Deserialize(Wire.Reader(w1));
        var w2 = new NetDataWriter();
        mid.Serialize(w2);
        Assert.Equal(w1.CopyData(), w2.CopyData());
    }
}

/// <summary>
/// AudioSegmentDataMessage ([seq:1][silence:1][opus bytes = remainder]) and its
/// ServerAudioSegmentMessage wrapper with byte/ushort player-id variants.
/// </summary>
public class AudioSegmentMessageWireTests
{
    [Fact]
    public void AudioSegmentDataMessage_RoundTrip_PreservesAllFields()
    {
        var rng = new Random(7);
        byte[] audio = Wire.RandomBytes(rng, 60);
        var msg = new AudioSegmentDataMessage
        {
            SequenceNumber = 200,
            TotalPlayedInSilence = 3,
            buffer = audio,
            TotalLength = audio.Length,
            LengthUsed = audio.Length,
        };
        var w = new NetDataWriter();
        msg.Serialize(w);
        Assert.Equal(62, w.Length);

        var result = default(AudioSegmentDataMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal((byte)200, result.SequenceNumber);
        Assert.Equal((byte)3, result.TotalPlayedInSilence);
        Assert.Equal(audio, result.buffer);
        Assert.Equal(60, result.TotalLength);
        Assert.Equal(60, result.LengthUsed);
    }

    [Fact]
    public void AudioSegmentDataMessage_ZeroLengthSegment_RoundTripsToNullBuffer()
    {
        var msg = new AudioSegmentDataMessage { SequenceNumber = 5, TotalPlayedInSilence = 9, buffer = null, LengthUsed = 0 };
        var w = new NetDataWriter();
        msg.Serialize(w);
        Assert.Equal(2, w.Length);

        var result = default(AudioSegmentDataMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal((byte)5, result.SequenceNumber);
        Assert.Equal((byte)9, result.TotalPlayedInSilence);
        Assert.Null(result.buffer);
        Assert.Equal(0, result.TotalLength);
        Assert.Equal(0, result.LengthUsed);
    }

    [Fact]
    public void AudioSegmentDataMessage_LengthUsed_LimitsWrittenBytes()
    {
        byte[] pooled = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var msg = new AudioSegmentDataMessage { SequenceNumber = 1, TotalPlayedInSilence = 0, buffer = pooled, TotalLength = 10, LengthUsed = 4 };
        var w = new NetDataWriter();
        msg.Serialize(w);
        Assert.Equal(6, w.Length);

        var result = default(AudioSegmentDataMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, result.buffer);
        Assert.Equal(4, result.TotalLength);
        Assert.Equal(4, result.LengthUsed);
    }

    [Fact]
    public void AudioSegmentDataMessage_DoubleRoundTrip_IsByteIdentical()
    {
        var msg = new AudioSegmentDataMessage
        {
            SequenceNumber = 90,
            TotalPlayedInSilence = 2,
            buffer = Wire.RandomBytes(new Random(8), 16),
            TotalLength = 16,
            LengthUsed = 16,
        };
        var w1 = new NetDataWriter();
        msg.Serialize(w1);

        var mid = default(AudioSegmentDataMessage);
        mid.Deserialize(Wire.Reader(w1));
        var w2 = new NetDataWriter();
        mid.Serialize(w2);
        Assert.Equal(w1.CopyData(), w2.CopyData());
    }

    [Fact]
    public void ServerAudioSegmentMessage_RoundTrip_UShortIdAndAudio()
    {
        var rng = new Random(21);
        byte[] audio = Wire.RandomBytes(rng, 48);
        var msg = new ServerAudioSegmentMessage
        {
            playerIdMessage = new PlayerIdMessage { playerID = ushort.MaxValue },
            audioSegmentData = new AudioSegmentDataMessage
            {
                SequenceNumber = 9,
                TotalPlayedInSilence = 1,
                buffer = audio,
                TotalLength = audio.Length,
                LengthUsed = audio.Length,
            },
        };
        var w = new NetDataWriter();
        msg.Serialize(w);
        Assert.Equal(2 + 2 + 48, w.Length);

        var result = default(ServerAudioSegmentMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal(ushort.MaxValue, result.playerIdMessage.playerID);
        Assert.Equal((byte)9, result.audioSegmentData.SequenceNumber);
        Assert.Equal((byte)1, result.audioSegmentData.TotalPlayedInSilence);
        Assert.Equal(audio, result.audioSegmentData.buffer);
        Assert.Equal(48, result.audioSegmentData.LengthUsed);
    }

    [Fact]
    public void ServerAudioSegmentMessage_SmallIdVariant_RoundTrips()
    {
        byte[] audio = Wire.RandomBytes(new Random(22), 20);
        var msg = new ServerAudioSegmentMessage
        {
            playerIdMessage = new PlayerIdMessage { playerID = 200 },
            audioSegmentData = new AudioSegmentDataMessage { SequenceNumber = 3, TotalPlayedInSilence = 0, buffer = audio, TotalLength = 20, LengthUsed = 20 },
        };
        var w = new NetDataWriter();
        msg.Serialize(w, largeId: false);
        Assert.Equal(1 + 2 + 20, w.Length);

        var result = default(ServerAudioSegmentMessage);
        result.Deserialize(Wire.Reader(w), largeId: false);
        Assert.Equal((ushort)200, result.playerIdMessage.playerID);
        Assert.Equal(audio, result.audioSegmentData.buffer);
    }

    [Fact]
    public void ServerAudioSegmentMessage_LargeIdVariant_RoundTrips()
    {
        byte[] audio = Wire.RandomBytes(new Random(23), 10);
        var msg = new ServerAudioSegmentMessage
        {
            playerIdMessage = new PlayerIdMessage { playerID = 40000 },
            audioSegmentData = new AudioSegmentDataMessage { SequenceNumber = 8, TotalPlayedInSilence = 4, buffer = audio, TotalLength = 10, LengthUsed = 10 },
        };
        var w = new NetDataWriter();
        msg.Serialize(w, largeId: true);
        Assert.Equal(2 + 2 + 10, w.Length);

        var result = default(ServerAudioSegmentMessage);
        result.Deserialize(Wire.Reader(w), largeId: true);
        Assert.Equal((ushort)40000, result.playerIdMessage.playerID);
        Assert.Equal(audio, result.audioSegmentData.buffer);
    }

    [Fact]
    public void ServerAudioSegmentMessage_ZeroLengthAudio_SmallId_RoundTrips()
    {
        var msg = new ServerAudioSegmentMessage
        {
            playerIdMessage = new PlayerIdMessage { playerID = 1 },
            audioSegmentData = new AudioSegmentDataMessage { SequenceNumber = 77, TotalPlayedInSilence = 255, buffer = null, LengthUsed = 0 },
        };
        var w = new NetDataWriter();
        msg.Serialize(w, largeId: false);
        Assert.Equal(3, w.Length);

        var result = default(ServerAudioSegmentMessage);
        result.Deserialize(Wire.Reader(w), largeId: false);
        Assert.Equal((ushort)1, result.playerIdMessage.playerID);
        Assert.Equal((byte)77, result.audioSegmentData.SequenceNumber);
        Assert.Equal((byte)255, result.audioSegmentData.TotalPlayedInSilence);
        Assert.Null(result.audioSegmentData.buffer);
    }
}

/// <summary>
/// AvatarDataMessage: [playerID:2][AvatarLinkIndex:1][messageIndex:1][recipientsSize:2][recipients...][payload = remainder].
/// Null recipients means broadcast (size 0 on the wire, empty array after deserialize).
/// </summary>
public class AvatarDataMessageWireTests
{
    [Fact]
    public void AvatarDataMessage_RoundTrip_PreservesAllFields()
    {
        var rng = new Random(11);
        byte[] payload = Wire.RandomBytes(rng, 20);
        ushort[] recipients = { 1, 500, ushort.MaxValue };
        var msg = new AvatarDataMessage
        {
            PlayerIdMessage = new PlayerIdMessage { playerID = 4242 },
            AvatarLinkIndex = 5,
            messageIndex = 77,
            recipients = recipients,
            payload = payload,
        };
        var w = new NetDataWriter();
        msg.Serialize(w);
        Assert.Equal(2 + 1 + 1 + 2 + 6 + 20, w.Length);

        var result = default(AvatarDataMessage);
        var reader = Wire.Reader(w);
        result.Deserialize(reader);
        Assert.Equal((ushort)4242, result.PlayerIdMessage.playerID);
        Assert.Equal((byte)5, result.AvatarLinkIndex);
        Assert.Equal((byte)77, result.messageIndex);
        Assert.Equal((ushort)3, result.recipientsSize);
        Assert.Equal(recipients, result.recipients);
        Assert.Equal(payload, result.payload);
        Assert.Equal(0, reader.AvailableBytes);
    }

    [Fact]
    public void AvatarDataMessage_UShortMaxPlayerId_RoundTrips()
    {
        var msg = new AvatarDataMessage
        {
            PlayerIdMessage = new PlayerIdMessage { playerID = ushort.MaxValue },
            AvatarLinkIndex = 255,
            messageIndex = 255,
            payload = new byte[] { 42 },
        };
        var w = new NetDataWriter();
        msg.Serialize(w);

        var result = default(AvatarDataMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal(ushort.MaxValue, result.PlayerIdMessage.playerID);
        Assert.Equal((byte)255, result.AvatarLinkIndex);
        Assert.Equal((byte)255, result.messageIndex);
        Assert.Equal(new byte[] { 42 }, result.payload);
    }

    [Fact]
    public void AvatarDataMessage_NullRecipients_DeserializesToEmptyArray_PayloadIntact()
    {
        byte[] payload = Wire.RandomBytes(new Random(12), 8);
        var msg = new AvatarDataMessage
        {
            PlayerIdMessage = new PlayerIdMessage { playerID = 3 },
            AvatarLinkIndex = 1,
            messageIndex = 2,
            recipients = null,
            payload = payload,
        };
        var w = new NetDataWriter();
        msg.Serialize(w);

        var result = default(AvatarDataMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal((ushort)0, result.recipientsSize);
        Assert.NotNull(result.recipients);
        Assert.Empty(result.recipients);
        Assert.Equal(payload, result.payload);
    }

    [Fact]
    public void AvatarDataMessage_RecipientsOnly_NoPayload_DeserializesToNullPayload()
    {
        // recipientsSize == AvailableBytes / 2 exactly: the size guard boundary must pass.
        ushort[] recipients = { 10, 20, 30 };
        var msg = new AvatarDataMessage
        {
            PlayerIdMessage = new PlayerIdMessage { playerID = 6 },
            AvatarLinkIndex = 0,
            messageIndex = 1,
            recipients = recipients,
            payload = null,
        };
        var w = new NetDataWriter();
        msg.Serialize(w);

        var result = default(AvatarDataMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal(recipients, result.recipients);
        Assert.Null(result.payload);
    }

    [Fact]
    public void AvatarDataMessage_OversizedRecipientsSize_ThrowsArgumentException()
    {
        var w = new NetDataWriter();
        w.Put((ushort)1);  // playerID
        w.Put((byte)0);    // AvatarLinkIndex
        w.Put((byte)0);    // messageIndex
        w.Put((ushort)4);  // recipientsSize claims 4 entries
        w.Put((byte)9);    // but only 1 byte remains
        var reader = Wire.Reader(w);
        var msg = default(AvatarDataMessage);
        Assert.Throws<ArgumentException>(() => msg.Deserialize(reader));
    }

    [Fact]
    public void AvatarDataMessage_TruncatedAfterPlayerId_ThrowsArgumentException()
    {
        var w = new NetDataWriter();
        w.Put((ushort)9);
        var reader = Wire.Reader(w);
        var msg = default(AvatarDataMessage);
        Assert.Throws<ArgumentException>(() => msg.Deserialize(reader));
    }

    [Fact]
    public void AvatarDataMessage_MissingRecipientsSize_NoThrow_NullFallback()
    {
        var w = new NetDataWriter();
        w.Put((ushort)9);
        w.Put((byte)4);
        w.Put((byte)8);
        var msg = default(AvatarDataMessage);
        var ex = Record.Exception(() => msg.Deserialize(Wire.Reader(w)));
        Assert.Null(ex);
        Assert.Equal((byte)4, msg.AvatarLinkIndex);
        Assert.Equal((byte)8, msg.messageIndex);
        Assert.Null(msg.recipients);
        Assert.Null(msg.payload);
    }

    [Fact]
    public void AvatarDataMessage_DoubleRoundTrip_IsByteIdentical()
    {
        var msg = new AvatarDataMessage
        {
            PlayerIdMessage = new PlayerIdMessage { playerID = 100 },
            AvatarLinkIndex = 2,
            messageIndex = 3,
            recipients = new ushort[] { 7, 8 },
            payload = Wire.RandomBytes(new Random(13), 11),
        };
        var w1 = new NetDataWriter();
        msg.Serialize(w1);

        var mid = default(AvatarDataMessage);
        mid.Deserialize(Wire.Reader(w1));
        var w2 = new NetDataWriter();
        mid.Serialize(w2);
        Assert.Equal(w1.CopyData(), w2.CopyData());
    }
}

/// <summary>
/// RemoteAvatarDataMessage: [playerID:2][AvatarLinkIndex:1][messageIndex:1][payload = remainder].
/// </summary>
public class RemoteAvatarDataMessageWireTests
{
    [Fact]
    public void RemoteAvatarDataMessage_RoundTrip_PreservesAllFields()
    {
        byte[] payload = Wire.RandomBytes(new Random(14), 25);
        var msg = new RemoteAvatarDataMessage
        {
            PlayerIdMessage = new PlayerIdMessage { playerID = ushort.MaxValue },
            AvatarLinkIndex = 9,
            messageIndex = 44,
            payload = payload,
        };
        var w = new NetDataWriter();
        msg.Serialize(w);
        Assert.Equal(2 + 1 + 1 + 25, w.Length);

        var result = default(RemoteAvatarDataMessage);
        var reader = Wire.Reader(w);
        result.Deserialize(reader);
        Assert.Equal(ushort.MaxValue, result.PlayerIdMessage.playerID);
        Assert.Equal((byte)9, result.AvatarLinkIndex);
        Assert.Equal((byte)44, result.messageIndex);
        Assert.Equal(payload, result.payload);
        Assert.Equal(0, reader.AvailableBytes);
    }

    [Fact]
    public void RemoteAvatarDataMessage_NoPayload_DeserializesToNull()
    {
        var msg = new RemoteAvatarDataMessage
        {
            PlayerIdMessage = new PlayerIdMessage { playerID = 5 },
            AvatarLinkIndex = 1,
            messageIndex = 2,
            payload = null,
        };
        var w = new NetDataWriter();
        msg.Serialize(w);
        Assert.Equal(4, w.Length);

        var result = default(RemoteAvatarDataMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Null(result.payload);
    }

    [Fact]
    public void RemoteAvatarDataMessage_TruncatedHeader_ThrowsArgumentException()
    {
        var w = new NetDataWriter();
        w.Put((ushort)5);
        w.Put((byte)1); // AvatarLinkIndex present, messageIndex missing
        var reader = Wire.Reader(w);
        var msg = default(RemoteAvatarDataMessage);
        Assert.Throws<ArgumentException>(() => msg.Deserialize(reader));
    }

    [Fact]
    public void RemoteAvatarDataMessage_DoubleRoundTrip_IsByteIdentical()
    {
        var msg = new RemoteAvatarDataMessage
        {
            PlayerIdMessage = new PlayerIdMessage { playerID = 321 },
            AvatarLinkIndex = 7,
            messageIndex = 6,
            payload = Wire.RandomBytes(new Random(15), 9),
        };
        var w1 = new NetDataWriter();
        msg.Serialize(w1);

        var mid = default(RemoteAvatarDataMessage);
        mid.Deserialize(Wire.Reader(w1));
        var w2 = new NetDataWriter();
        mid.Serialize(w2);
        Assert.Equal(w1.CopyData(), w2.CopyData());
    }
}

/// <summary>
/// SceneDataMessage: [messageIndex:2][recipientsSize:2][recipients...][payload = remainder],
/// same recipients semantics as AvatarDataMessage.
/// </summary>
public class SceneDataMessageWireTests
{
    [Fact]
    public void SceneDataMessage_RoundTrip_PreservesAllFields()
    {
        byte[] payload = Wire.RandomBytes(new Random(16), 16);
        ushort[] recipients = { 2, 40000, ushort.MaxValue };
        var msg = new SceneDataMessage
        {
            messageIndex = ushort.MaxValue,
            recipients = recipients,
            payload = payload,
        };
        var w = new NetDataWriter();
        msg.Serialize(w);
        Assert.Equal(2 + 2 + 6 + 16, w.Length);

        var result = default(SceneDataMessage);
        var reader = Wire.Reader(w);
        result.Deserialize(reader);
        Assert.Equal(ushort.MaxValue, result.messageIndex);
        Assert.Equal((ushort)3, result.recipientsSize);
        Assert.Equal(recipients, result.recipients);
        Assert.Equal(payload, result.payload);
        Assert.Equal(0, reader.AvailableBytes);
    }

    [Fact]
    public void SceneDataMessage_NullRecipientsAndPayload_RoundTrips()
    {
        var msg = new SceneDataMessage { messageIndex = 12, recipients = null, payload = null };
        var w = new NetDataWriter();
        msg.Serialize(w);
        Assert.Equal(4, w.Length);

        var result = default(SceneDataMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal((ushort)12, result.messageIndex);
        Assert.Equal((ushort)0, result.recipientsSize);
        Assert.NotNull(result.recipients);
        Assert.Empty(result.recipients);
        Assert.Null(result.payload);
    }

    [Fact]
    public void SceneDataMessage_OversizedRecipientsSize_ThrowsArgumentException()
    {
        var w = new NetDataWriter();
        w.Put((ushort)1);    // messageIndex
        w.Put((ushort)1000); // recipientsSize claims 1000 entries
        w.Put((byte)1);      // but only 1 byte remains
        var reader = Wire.Reader(w);
        var msg = default(SceneDataMessage);
        Assert.Throws<ArgumentException>(() => msg.Deserialize(reader));
    }

    [Fact]
    public void SceneDataMessage_MissingRecipientsSize_NoThrow_NullFallback()
    {
        var w = new NetDataWriter();
        w.Put((ushort)77);
        var msg = default(SceneDataMessage);
        var ex = Record.Exception(() => msg.Deserialize(Wire.Reader(w)));
        Assert.Null(ex);
        Assert.Equal((ushort)77, msg.messageIndex);
        Assert.Null(msg.recipients);
        Assert.Null(msg.payload);
    }

    [Fact]
    public void SceneDataMessage_EmptyReader_ThrowsArgumentException()
    {
        var msg = default(SceneDataMessage);
        var reader = Wire.Empty();
        Assert.Throws<ArgumentException>(() => msg.Deserialize(reader));
    }

    [Fact]
    public void SceneDataMessage_DoubleRoundTrip_IsByteIdentical()
    {
        var msg = new SceneDataMessage
        {
            messageIndex = 900,
            recipients = new ushort[] { 4 },
            payload = Wire.RandomBytes(new Random(17), 5),
        };
        var w1 = new NetDataWriter();
        msg.Serialize(w1);

        var mid = default(SceneDataMessage);
        mid.Deserialize(Wire.Reader(w1));
        var w2 = new NetDataWriter();
        mid.Serialize(w2);
        Assert.Equal(w1.CopyData(), w2.CopyData());
    }
}

/// <summary>
/// RemoteSceneDataMessage: [messageIndex:2][payload = remainder]; payloadLength tracks the
/// valid prefix of a possibly pooled/oversized payload buffer on the send side.
/// </summary>
public class RemoteSceneDataMessageWireTests
{
    [Fact]
    public void RemoteSceneDataMessage_RoundTrip_PreservesAllFields()
    {
        byte[] payload = Wire.RandomBytes(new Random(18), 24);
        var msg = new RemoteSceneDataMessage { messageIndex = 700, payload = payload, payloadLength = payload.Length };
        var w = new NetDataWriter();
        msg.Serialize(w);
        Assert.Equal(26, w.Length);

        var result = default(RemoteSceneDataMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal((ushort)700, result.messageIndex);
        Assert.Equal(payload, result.payload);
        Assert.Equal(24, result.payloadLength);
    }

    [Fact]
    public void RemoteSceneDataMessage_NoPayload_LeavesPayloadNull()
    {
        var msg = new RemoteSceneDataMessage { messageIndex = 8, payload = null, payloadLength = 0 };
        var w = new NetDataWriter();
        msg.Serialize(w);
        Assert.Equal(2, w.Length);

        var result = default(RemoteSceneDataMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal((ushort)8, result.messageIndex);
        Assert.Null(result.payload);
        Assert.Equal(0, result.payloadLength);
    }

    [Fact]
    public void RemoteSceneDataMessage_PayloadLength_LimitsWrittenBytes()
    {
        byte[] pooled = { 1, 2, 3, 4, 5, 6, 7, 8 };
        var msg = new RemoteSceneDataMessage { messageIndex = 1, payload = pooled, payloadLength = 5 };
        var w = new NetDataWriter();
        msg.Serialize(w);
        Assert.Equal(7, w.Length);

        var result = default(RemoteSceneDataMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, result.payload);
        Assert.Equal(5, result.payloadLength);
    }

    [Fact]
    public void RemoteSceneDataMessage_EmptyReader_ThrowsArgumentException()
    {
        var msg = default(RemoteSceneDataMessage);
        var reader = Wire.Empty();
        Assert.Throws<ArgumentException>(() => msg.Deserialize(reader));
    }

    [Fact]
    public void RemoteSceneDataMessage_Release_ClearsPayload()
    {
        var msg = new RemoteSceneDataMessage { messageIndex = 2, payload = new byte[] { 1, 2 }, payloadLength = 2 };
        msg.Release();
        Assert.Null(msg.payload);
        Assert.Equal(0, msg.payloadLength);
    }

    [Fact]
    public void RemoteSceneDataMessage_DoubleRoundTrip_IsByteIdentical()
    {
        var msg = new RemoteSceneDataMessage { messageIndex = 31, payload = Wire.RandomBytes(new Random(19), 12), payloadLength = 12 };
        var w1 = new NetDataWriter();
        msg.Serialize(w1);

        var mid = default(RemoteSceneDataMessage);
        mid.Deserialize(Wire.Reader(w1));
        var w2 = new NetDataWriter();
        mid.Serialize(w2);
        Assert.Equal(w1.CopyData(), w2.CopyData());
    }
}

/// <summary>
/// Server wrapper messages that prepend a ushort player id to an inner message:
/// ServerAvatarDataMessage, ServerSceneDataMessage, ServerAvatarChangeMessage.
/// </summary>
public class ServerCompositeMessageWireTests
{
    [Fact]
    public void ServerAvatarDataMessage_RoundTrip_PreservesNestedFields()
    {
        byte[] payload = Wire.RandomBytes(new Random(24), 10);
        var msg = new ServerAvatarDataMessage
        {
            playerIdMessage = new PlayerIdMessage { playerID = 77 },
            avatarDataMessage = new RemoteAvatarDataMessage
            {
                PlayerIdMessage = new PlayerIdMessage { playerID = 88 },
                AvatarLinkIndex = 3,
                messageIndex = 9,
                payload = payload,
            },
        };
        var w = new NetDataWriter();
        msg.Serialize(w);
        Assert.Equal(2 + 2 + 1 + 1 + 10, w.Length);

        var result = default(ServerAvatarDataMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal((ushort)77, result.playerIdMessage.playerID);
        Assert.Equal((ushort)88, result.avatarDataMessage.PlayerIdMessage.playerID);
        Assert.Equal((byte)3, result.avatarDataMessage.AvatarLinkIndex);
        Assert.Equal((byte)9, result.avatarDataMessage.messageIndex);
        Assert.Equal(payload, result.avatarDataMessage.payload);
    }

    [Fact]
    public void ServerAvatarDataMessage_DoubleRoundTrip_IsByteIdentical()
    {
        var msg = new ServerAvatarDataMessage
        {
            playerIdMessage = new PlayerIdMessage { playerID = 1 },
            avatarDataMessage = new RemoteAvatarDataMessage
            {
                PlayerIdMessage = new PlayerIdMessage { playerID = 2 },
                AvatarLinkIndex = 0,
                messageIndex = 1,
                payload = Wire.RandomBytes(new Random(25), 7),
            },
        };
        var w1 = new NetDataWriter();
        msg.Serialize(w1);

        var mid = default(ServerAvatarDataMessage);
        mid.Deserialize(Wire.Reader(w1));
        var w2 = new NetDataWriter();
        mid.Serialize(w2);
        Assert.Equal(w1.CopyData(), w2.CopyData());
    }

    [Fact]
    public void ServerSceneDataMessage_RoundTrip_PreservesNestedFields()
    {
        byte[] payload = Wire.RandomBytes(new Random(26), 6);
        var msg = new ServerSceneDataMessage
        {
            playerIdMessage = new PlayerIdMessage { playerID = 4 },
            sceneDataMessage = new RemoteSceneDataMessage { messageIndex = 700, payload = payload, payloadLength = payload.Length },
        };
        var w = new NetDataWriter();
        msg.Serialize(w);
        Assert.Equal(2 + 2 + 6, w.Length);

        var result = default(ServerSceneDataMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal((ushort)4, result.playerIdMessage.playerID);
        Assert.Equal((ushort)700, result.sceneDataMessage.messageIndex);
        Assert.Equal(payload, result.sceneDataMessage.payload);
        Assert.Equal(6, result.sceneDataMessage.payloadLength);
    }

    [Fact]
    public void ServerSceneDataMessage_DoubleRoundTrip_IsByteIdentical()
    {
        var msg = new ServerSceneDataMessage
        {
            playerIdMessage = new PlayerIdMessage { playerID = ushort.MaxValue },
            sceneDataMessage = new RemoteSceneDataMessage { messageIndex = 1, payload = Wire.RandomBytes(new Random(27), 3), payloadLength = 3 },
        };
        var w1 = new NetDataWriter();
        msg.Serialize(w1);

        var mid = default(ServerSceneDataMessage);
        mid.Deserialize(Wire.Reader(w1));
        var w2 = new NetDataWriter();
        mid.Serialize(w2);
        Assert.Equal(w1.CopyData(), w2.CopyData());
    }

    [Fact]
    public void ServerAvatarChangeMessage_RoundTrip_PreservesNestedFields()
    {
        byte[] avatarBytes = Wire.RandomBytes(new Random(28), 12);
        var msg = new ServerAvatarChangeMessage
        {
            uShortPlayerId = new PlayerIdMessage { playerID = 123 },
            clientAvatarChangeMessage = new ClientAvatarChangeMessage
            {
                loadMode = 1,
                byteArray = avatarBytes,
                LocalAvatarIndex = 200,
            },
        };
        var w = new NetDataWriter();
        msg.Serialize(w);
        Assert.Equal(2 + 1 + 2 + 12 + 1 + 6, w.Length);    // +6 = three quantized body-fit scales

        var result = default(ServerAvatarChangeMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal((ushort)123, result.uShortPlayerId.playerID);
        Assert.Equal((byte)1, result.clientAvatarChangeMessage.loadMode);
        Assert.Equal(avatarBytes, result.clientAvatarChangeMessage.byteArray);
        Assert.Equal((byte)200, result.clientAvatarChangeMessage.LocalAvatarIndex);
    }

    [Fact]
    public void ServerAvatarChangeMessage_DoubleRoundTrip_IsByteIdentical()
    {
        var msg = new ServerAvatarChangeMessage
        {
            uShortPlayerId = new PlayerIdMessage { playerID = 9 },
            clientAvatarChangeMessage = new ClientAvatarChangeMessage
            {
                loadMode = 0,
                byteArray = Wire.RandomBytes(new Random(29), 5),
                LocalAvatarIndex = 3,
            },
        };
        var w1 = new NetDataWriter();
        msg.Serialize(w1);

        var mid = default(ServerAvatarChangeMessage);
        mid.Deserialize(Wire.Reader(w1));
        var w2 = new NetDataWriter();
        mid.Serialize(w2);
        Assert.Equal(w1.CopyData(), w2.CopyData());
    }
}

/// <summary>
/// ClientAvatarChangeMessage: [loadMode:1][length:2][bytes][LocalAvatarIndex:1][arm:2][leg:2][torso:2];
/// a zero length round-trips to a null byteArray.
/// </summary>
public class ClientAvatarChangeMessageWireTests
{
    const int FitBytes = 6;   // 3 scales x ushort, quantized over [0.5, 1.5]
    // 16 bits over a range of 1.0 => 1.5e-5 step, so a round-tripped scale lands within half of that.
    const float FitTol = 1e-4f;

    [Fact]
    public void ClientAvatarChangeMessage_RoundTrip_PreservesAllFields()
    {
        byte[] avatarBytes = Wire.RandomBytes(new Random(51), 40);
        var msg = new ClientAvatarChangeMessage
        {
            loadMode = 2,
            byteArray = avatarBytes,
            LocalAvatarIndex = 254,
            ArmScale = 1.0625f,
            LegScale = 0.9375f,
            TorsoScale = 1.125f,
        };
        var w = new NetDataWriter();
        msg.Serialize(w);
        Assert.Equal(1 + 2 + 40 + 1 + FitBytes, w.Length);

        var result = default(ClientAvatarChangeMessage);
        var reader = Wire.Reader(w);
        result.Deserialize(reader);
        Assert.Equal((byte)2, result.loadMode);
        Assert.Equal(avatarBytes, result.byteArray);
        Assert.Equal((byte)254, result.LocalAvatarIndex);
        Assert.Equal(1.0625f, result.ArmScale, FitTol);
        Assert.Equal(0.9375f, result.LegScale, FitTol);
        Assert.Equal(1.125f, result.TorsoScale, FitTol);
        Assert.Equal(0, reader.AvailableBytes);
    }

    [Fact]
    public void ClientAvatarChangeMessage_NullByteArray_RoundTrips()
    {
        var msg = new ClientAvatarChangeMessage { loadMode = 1, byteArray = null, LocalAvatarIndex = 7 };
        var w = new NetDataWriter();
        msg.Serialize(w);
        Assert.Equal(4 + FitBytes, w.Length);

        var result = default(ClientAvatarChangeMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal((byte)1, result.loadMode);
        Assert.Null(result.byteArray);
        Assert.Equal((byte)7, result.LocalAvatarIndex);
    }

    /// <summary>
    /// Most construction sites never touch the fit fields, so a default-constructed message must put
    /// identity on the wire — a raw 0 would collapse every fitted bone to zero length on the receiver.
    /// </summary>
    [Fact]
    public void ClientAvatarChangeMessage_UnsetFit_SerializesAsIdentityNotZero()
    {
        var msg = new ClientAvatarChangeMessage { loadMode = 0, byteArray = null, LocalAvatarIndex = 0 };
        Assert.Equal(0f, msg.ArmScale);   // default(struct) really is zero

        var w = new NetDataWriter();
        msg.Serialize(w);

        var result = default(ClientAvatarChangeMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal(1f, result.ArmScale, FitTol);
        Assert.Equal(1f, result.LegScale, FitTol);
        Assert.Equal(1f, result.TorsoScale, FitTol);
    }

    [Theory]
    [InlineData(0f, 1f)]              // unset / collapse
    [InlineData(-2f, 1f)]             // negative would mirror the bone through its parent
    [InlineData(float.NaN, 1f)]
    [InlineData(float.PositiveInfinity, 1f)]
    [InlineData(1e9f, 1.5f)]          // above the band clamps to the ceiling
    [InlineData(1e-9f, 0.5f)]         // below the band clamps to the floor
    [InlineData(1.15f, 1.15f)]        // a legitimate fit passes through untouched
    public void SanitizeFitScale_ClampsToTheValidBand(float input, float expected)
    {
        Assert.Equal(expected, ClientAvatarChangeMessage.SanitizeFitScale(input));
    }

    [Fact]
    public void ClientAvatarChangeMessage_EmptyByteArray_SameBytesAsNull_DeserializesToNull()
    {
        var nullMsg = new ClientAvatarChangeMessage { loadMode = 1, byteArray = null, LocalAvatarIndex = 7 };
        var emptyMsg = new ClientAvatarChangeMessage { loadMode = 1, byteArray = Array.Empty<byte>(), LocalAvatarIndex = 7 };
        var w1 = new NetDataWriter();
        nullMsg.Serialize(w1);
        var w2 = new NetDataWriter();
        emptyMsg.Serialize(w2);
        Assert.Equal(w1.CopyData(), w2.CopyData());

        var result = default(ClientAvatarChangeMessage);
        result.Deserialize(Wire.Reader(w2));
        Assert.Null(result.byteArray);
    }

    [Fact]
    public void ClientAvatarChangeMessage_LengthBeyondAvailable_ThrowsArgumentException()
    {
        var w = new NetDataWriter();
        w.Put((byte)1);    // loadMode
        w.Put((ushort)50); // claims 50 bytes
        w.Put(new byte[] { 1, 2, 3 });
        var reader = Wire.Reader(w);
        var msg = default(ClientAvatarChangeMessage);
        Assert.Throws<ArgumentException>(() => msg.Deserialize(reader));
    }

    [Fact]
    public void ClientAvatarChangeMessage_DoubleRoundTrip_IsByteIdentical()
    {
        var msg = new ClientAvatarChangeMessage { loadMode = 3, byteArray = Wire.RandomBytes(new Random(52), 21), LocalAvatarIndex = 90 };
        var w1 = new NetDataWriter();
        msg.Serialize(w1);

        var mid = default(ClientAvatarChangeMessage);
        mid.Deserialize(Wire.Reader(w1));
        var w2 = new NetDataWriter();
        mid.Serialize(w2);
        Assert.Equal(w1.CopyData(), w2.CopyData());
    }
}

/// <summary>
/// ClientBodyFitMessage / ServerBodyFitMessage — the body-fit-only update that rides
/// AvatarChangeMessageChannel under AvatarChangeKindBodyFit. Three floats, no avatar bytes, so a
/// recalibration never makes a receiver reload the avatar.
/// </summary>
public class BodyFitMessageWireTests
{
    // 16 bits over a range of 1.0 => 1.5e-5 step, so a round-tripped scale lands within half of that.
    const float FitTol = 1e-4f;

    /// <summary>
    /// Everything BasisBodyFitCore can solve lands in [0.5, 1.5] (it clamps to 1 +/- maxDeviation with
    /// MaxDeviationCeiling 0.5), which is exactly the quantized range — so no legitimate fit is degraded
    /// beyond ~0.013 mm on a leg span, and nothing outside the band is representable at all.
    /// </summary>
    [Fact]
    public void EveryScaleTheSolverCanProduce_SurvivesQuantization()
    {
        for (int i = 0; i <= 1000; i++)
        {
            float scale = 0.5f + i * (1f / 1000f);
            float roundTripped = ClientAvatarChangeMessage.DecompressFitScale(
                ClientAvatarChangeMessage.CompressFitScale(scale));
            Assert.Equal(scale, roundTripped, FitTol);
        }
    }

    [Fact]
    public void QuantizedScale_IsNeverOutsideTheValidBand()
    {
        foreach (ushort raw in new ushort[] { 0, 1, 32767, 32768, 65534, 65535 })
        {
            float decoded = ClientAvatarChangeMessage.DecompressFitScale(raw);
            Assert.InRange(decoded, ClientAvatarChangeMessage.FitScaleMin, ClientAvatarChangeMessage.FitScaleMax);
        }
    }

    [Fact]
    public void ClientBodyFitMessage_RoundTrip_PreservesScales()
    {
        var msg = new ClientBodyFitMessage { ArmScale = 1.0625f, LegScale = 0.9375f, TorsoScale = 1.125f };
        var w = new NetDataWriter();
        msg.Serialize(w);
        Assert.Equal(6, w.Length);

        var result = default(ClientBodyFitMessage);
        var reader = Wire.Reader(w);
        result.Deserialize(reader);
        Assert.Equal(1.0625f, result.ArmScale, FitTol);
        Assert.Equal(0.9375f, result.LegScale, FitTol);
        Assert.Equal(1.125f, result.TorsoScale, FitTol);
        Assert.Equal(0, reader.AvailableBytes);
    }

    [Fact]
    public void ClientBodyFitMessage_UnsetScales_ReadBackAsIdentity()
    {
        var w = new NetDataWriter();
        default(ClientBodyFitMessage).Serialize(w);

        var result = default(ClientBodyFitMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal(1f, result.ArmScale, FitTol);
        Assert.Equal(1f, result.LegScale, FitTol);
        Assert.Equal(1f, result.TorsoScale, FitTol);
    }

    [Fact]
    public void ServerBodyFitMessage_RoundTrip_PreservesSenderAndScales()
    {
        var msg = new ServerBodyFitMessage
        {
            uShortPlayerId = new PlayerIdMessage { playerID = 4242 },
            bodyFit = new ClientBodyFitMessage { ArmScale = 1.05f, LegScale = 0.95f, TorsoScale = 1.02f },
        };
        var w = new NetDataWriter();
        msg.Serialize(w);
        Assert.Equal(2 + 6, w.Length);

        var result = default(ServerBodyFitMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal((ushort)4242, result.uShortPlayerId.playerID);
        Assert.Equal(1.05f, result.bodyFit.ArmScale, FitTol);
        Assert.Equal(0.95f, result.bodyFit.LegScale, FitTol);
        Assert.Equal(1.02f, result.bodyFit.TorsoScale, FitTol);
    }

    [Fact]
    public void ServerBodyFitMessage_DoubleRoundTrip_IsByteIdentical()
    {
        var msg = new ServerBodyFitMessage
        {
            uShortPlayerId = new PlayerIdMessage { playerID = 17 },
            bodyFit = new ClientBodyFitMessage { ArmScale = 0.88f, LegScale = 1.12f, TorsoScale = 0.94f },
        };
        var w1 = new NetDataWriter();
        msg.Serialize(w1);

        var mid = default(ServerBodyFitMessage);
        mid.Deserialize(Wire.Reader(w1));
        var w2 = new NetDataWriter();
        mid.Serialize(w2);
        Assert.Equal(w1.CopyData(), w2.CopyData());
    }

    /// <summary>
    /// A hostile or corrupt scale must be clamped at the boundary, not carried into a remote's skeleton.
    /// </summary>
    [Fact]
    public void ClientBodyFitMessage_HostileScales_AreClampedOnRead()
    {
        var w = new NetDataWriter();
        // Values that cannot survive the quantizer: written through the same compressor a client
        // would use, so this pins that the compressor is where the clamping happens.
        w.Put(ClientAvatarChangeMessage.CompressFitScale(0f));
        w.Put(ClientAvatarChangeMessage.CompressFitScale(float.NaN));
        w.Put(ClientAvatarChangeMessage.CompressFitScale(1e9f));

        var result = default(ClientBodyFitMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal(1f, result.ArmScale, FitTol);
        Assert.Equal(1f, result.LegScale, FitTol);
        Assert.Equal(1.5f, result.TorsoScale, FitTol);
    }
}

/// <summary>
/// BasisCompactId — the polymorphic player-id encoding. Deployments use did:key, Steam64, Meta/Oculus
/// numeric ids, GUIDs, or anything an operator plugs in, so the contract that matters is: whatever goes
/// in comes back out byte-identical, and the recognised shapes get smaller.
/// </summary>
public class BasisCompactIdWireTests
{
    static string RoundTrip(string input)
    {
        var w = new NetDataWriter();
        BasisCompactId.Write(w, input);
        return BasisCompactId.Read(Wire.Reader(w));
    }

    static int Encoded(string input)
    {
        var w = new NetDataWriter();
        BasisCompactId.Write(w, input);
        return w.Length;
    }

    /// <summary>Old cost: a 2-byte length prefix plus UTF-8.</summary>
    static int Legacy(string input) => 2 + System.Text.Encoding.UTF8.GetByteCount(input);

    [Theory]
    // Steam64 and Meta/Oculus ids are plain decimal and fit a ulong.
    [InlineData("76561198012345678")]
    [InlineData("76561197960287930")]
    [InlineData("18446744073709551615")]   // ulong.MaxValue, the longest numeric id that still packs
    [InlineData("0")]
    // GUIDs, all four renderings.
    [InlineData("d3b07384-d9a0-4f1e-8b1a-2c3d4e5f6071")]
    [InlineData("D3B07384-D9A0-4F1E-8B1A-2C3D4E5F6071")]
    [InlineData("d3b07384d9a04f1e8b1a2c3d4e5f6071")]
    [InlineData("D3B07384D9A04F1E8B1A2C3D4E5F6071")]
    // did:key — this project's own auth identity.
    [InlineData("did:key:z6MkhaXgBZDvotDkL5257faiztiGiC2QtKLGpbnnEGta2doK")]
    // Hex ids (SHA-256 and friends).
    [InlineData("7a0ab549e93cf2bc804168473e065a2f4d293b1be6cefb87df862eb6de086219")]
    [InlineData("7A0AB549E93CF2BC804168473E065A2F4D293B1BE6CEFB87DF862EB6DE086219")]
    // Shapes that must fall back rather than be mangled.
    [InlineData("")]
    [InlineData("Failure")]
    [InlineData("007")]                                  // leading zeros would not survive a ulong
    [InlineData("99999999999999999999999")]              // overflows ulong
    [InlineData("dEadBeEf")]                             // mixed-case hex
    [InlineData("steam:76561198012345678")]              // prefixed / operator-specific
    [InlineData("did:web:example.com:users:alice")]
    [InlineData("a-perfectly-ordinary-username")]
    [InlineData("ünïcøde-ïd-ヘ")]
    public void AnyId_RoundTripsExactly(string input)
    {
        Assert.Equal(input, RoundTrip(input));
    }

    [Fact]
    public void NullId_RoundTripsAsEmpty()
    {
        Assert.Equal(string.Empty, RoundTrip(null!));
    }

    [Theory]
    [InlineData("76561198012345678", 9)]                                                   // was 19
    [InlineData("d3b07384-d9a0-4f1e-8b1a-2c3d4e5f6071", 18)]                               // was 38
    [InlineData("d3b07384d9a04f1e8b1a2c3d4e5f6071", 18)]                                   // was 34
    [InlineData("7a0ab549e93cf2bc804168473e065a2f4d293b1be6cefb87df862eb6de086219", 35)]   // was 66
    public void RecognisedShapes_GetSmaller(string input, int expectedBytes)
    {
        Assert.Equal(expectedBytes, Encoded(input));
        Assert.True(Encoded(input) < Legacy(input),
            $"{input} encoded to {Encoded(input)}B, legacy was {Legacy(input)}B");
    }

    [Fact]
    public void DidKey_ElidesItsFixedPrefix()
    {
        const string did = "did:key:z6MkhaXgBZDvotDkL5257faiztiGiC2QtKLGpbnnEGta2doK";
        Assert.Equal(Legacy(did) - 8, Encoded(did));
    }

    /// <summary>
    /// The fallback must never cost more than one byte over the old encoding, so an id shape nobody
    /// anticipated cannot regress the wire.
    /// </summary>
    [Theory]
    [InlineData("a-perfectly-ordinary-username")]
    [InlineData("steam:76561198012345678")]
    [InlineData("dEadBeEf")]
    [InlineData("")]
    public void UnrecognisedShapes_CostAtMostOneExtraByte(string input)
    {
        Assert.True(Encoded(input) <= Legacy(input) + 1,
            $"{input} encoded to {Encoded(input)}B vs legacy {Legacy(input)}B");
    }

    [Fact]
    public void LongIds_StillRoundTrip()
    {
        string longHex = new string('a', 600);          // past the hex fast path
        string longText = new string('x', 4000);
        Assert.Equal(longHex, RoundTrip(longHex));
        Assert.Equal(longText, RoundTrip(longText));
    }
}

/// <summary>
/// BasisPlatformCodec — Application.platform names collapse to one byte; anything unknown still
/// round-trips as a string so a new Unity platform is never blocked.
/// </summary>
public class BasisPlatformCodecWireTests
{
    static string RoundTrip(string input)
    {
        var w = new NetDataWriter();
        BasisPlatformCodec.Write(w, input);
        return BasisPlatformCodec.Read(Wire.Reader(w));
    }

    static int Encoded(string input)
    {
        var w = new NetDataWriter();
        BasisPlatformCodec.Write(w, input);
        return w.Length;
    }

    [Theory]
    [InlineData("WindowsPlayer")]
    [InlineData("WindowsEditor")]
    [InlineData("Android")]
    [InlineData("OSXPlayer")]
    [InlineData("LinuxPlayer")]
    [InlineData("IPhonePlayer")]
    [InlineData("PS5")]
    [InlineData("VisionOS")]
    [InlineData("WebGLPlayer")]
    public void KnownPlatform_RoundTripsInOneByte(string platform)
    {
        Assert.Equal(platform, RoundTrip(platform));
        Assert.Equal(1, Encoded(platform));
    }

    /// <summary>
    /// The load-test console reports "Headless", which is not a Unity platform. Left out of the table
    /// it falls back to a 10-byte string on every simulated client, which quietly overstates per-player
    /// metadata in exactly the 2000-client runs the tool exists to measure.
    /// </summary>
    [Fact]
    public void HeadlessLoadTestPlatform_IsInTheTable()
    {
        Assert.Equal("Headless", RoundTrip("Headless"));
        Assert.Equal(1, Encoded("Headless"));
    }

    [Theory]
    [InlineData("SomeFuturePlatform")]
    [InlineData("Failure")]
    [InlineData("")]
    [InlineData("windowsplayer")]   // case-sensitive on purpose: Application.platform is stable
    public void UnknownPlatform_FallsBackToAString(string platform)
    {
        Assert.Equal(platform, RoundTrip(platform));
    }

    [Fact]
    public void NullPlatform_RoundTripsAsEmpty()
    {
        Assert.Equal(string.Empty, RoundTrip(null!));
    }
}

/// <summary>
/// ClientMetaDataMessage now carries a compact id + platform. These pin the join-fill saving, since the
/// message is replicated once per existing player to every joiner.
/// </summary>
public class ClientMetaDataMessageWireTests
{
    [Fact]
    public void MetaData_RoundTripsAllThreeFields()
    {
        var msg = new ClientMetaDataMessage
        {
            playerUUID = "76561198012345678",
            playerDisplayName = "Some Player",
            playerPlatform = "WindowsPlayer",
        };
        var w = new NetDataWriter();
        msg.Serialize(w);

        var result = default(ClientMetaDataMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal("76561198012345678", result.playerUUID);
        Assert.Equal("Some Player", result.playerDisplayName);
        Assert.Equal("WindowsPlayer", result.playerPlatform);
    }

    [Fact]
    public void EmptyFields_StillReportFailureAsBefore()
    {
        var msg = new ClientMetaDataMessage();
        var w = new NetDataWriter();
        msg.Serialize(w);

        var result = default(ClientMetaDataMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal("Failure", result.playerUUID);
        Assert.Equal("Failure", result.playerDisplayName);
        Assert.Equal("Failure", result.playerPlatform);
    }

    [Fact]
    public void SteamIdOnWindows_IsSmallerThanTheOldEncoding()
    {
        var msg = new ClientMetaDataMessage
        {
            playerUUID = "76561198012345678",
            playerDisplayName = "Some Player",
            playerPlatform = "WindowsPlayer",
        };
        var w = new NetDataWriter();
        msg.Serialize(w);

        int legacy = (2 + 17) + (2 + 11) + (2 + 13);   // three length-prefixed UTF-8 strings
        Assert.True(w.Length < legacy, $"encoded {w.Length}B, legacy {legacy}B");
        Assert.Equal(9 + (2 + 11) + 1, w.Length);
    }
}

/// <summary>
/// ServerReadyBatchMessage — the join fill. One packet per player at 2000 players meant 1999 reliable
/// sends and per-record compression that recovered almost nothing; batching moves the compression to
/// where the redundancy actually lives.
/// </summary>
public class ServerReadyBatchWireTests
{
    static byte[] Payload(int length, int seed)
    {
        // Join-fill-shaped data: a small alphabet with heavy repetition across records, which is
        // exactly why batch compression pays where per-record compression did not.
        var rng = new Random(seed);
        string[] urls =
        {
            "https://BasisFramework.b-cdn.net/Avatars/BEE/BEE/leona/27ca99b1efe04383b061c7def2684f60.BEE",
            "https://BasisFramework.b-cdn.net/Avatars/BEE/BEE/rex/8812aa4cfe1140239bb17ce4a1120fa2.BEE",
        };
        var sb = new System.Text.StringBuilder();
        while (sb.Length < length) sb.Append(urls[rng.Next(urls.Length)]);
        return System.Text.Encoding.UTF8.GetBytes(sb.ToString(0, length));
    }

    [Fact]
    public void Batch_RoundTripsPayloadAndCount()
    {
        byte[] payload = Payload(4096, 11);
        var batch = new ServerReadyBatchMessage { Count = 37, Payload = payload };
        var w = new NetDataWriter();
        batch.Serialize(w);

        var result = default(ServerReadyBatchMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal((ushort)37, result.Count);
        Assert.Equal(payload, result.Payload);
    }

    [Fact]
    public void RepetitiveBatch_IsActuallyCompressed()
    {
        byte[] payload = Payload(8192, 12);
        var batch = new ServerReadyBatchMessage { Count = 60, Payload = payload };
        var w = new NetDataWriter();
        batch.Serialize(w);

        Assert.True(batch.WasCompressed);
        Assert.True(w.Length < payload.Length / 2, $"batch was {w.Length}B for {payload.Length}B of payload");
    }

    /// <summary>
    /// Deflate expands short or high-entropy input, so the encoder must be free to skip it — and the
    /// decoder must honour the per-batch flag rather than assuming compression happened.
    /// </summary>
    [Fact]
    public void TinyBatch_SkipsCompressionAndStillRoundTrips()
    {
        byte[] payload = System.Text.Encoding.UTF8.GetBytes("one small record");
        var batch = new ServerReadyBatchMessage { Count = 1, Payload = payload };
        var w = new NetDataWriter();
        batch.Serialize(w);

        Assert.False(batch.WasCompressed);
        var result = default(ServerReadyBatchMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal(payload, result.Payload);
    }

    [Fact]
    public void IncompressibleBatch_IsNotStoredLargerThanRaw()
    {
        byte[] payload = Wire.RandomBytes(new Random(13), 4096);   // high entropy, deflate cannot win
        var batch = new ServerReadyBatchMessage { Count = 5, Payload = payload };
        var w = new NetDataWriter();
        batch.Serialize(w);

        var result = default(ServerReadyBatchMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal(payload, result.Payload);
        Assert.True(w.Length <= payload.Length + 16, $"batch grew to {w.Length}B from {payload.Length}B");
    }

    [Fact]
    public void EmptyBatch_RoundTrips()
    {
        var batch = new ServerReadyBatchMessage { Count = 0, Payload = Array.Empty<byte>() };
        var w = new NetDataWriter();
        batch.Serialize(w);

        var result = default(ServerReadyBatchMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal((ushort)0, result.Count);
        Assert.Empty(result.Payload);
    }

    [Fact]
    public void NullPayload_SerializesAsEmpty()
    {
        var batch = new ServerReadyBatchMessage { Count = 0, Payload = null };
        var w = new NetDataWriter();
        batch.Serialize(w);

        var result = default(ServerReadyBatchMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Empty(result.Payload);
    }

    [Fact]
    public void LengthBeyondAvailable_Throws()
    {
        var w = new NetDataWriter();
        w.Put((ushort)3);
        w.Put(false);
        w.Put(9999);                       // claims far more than follows
        w.Put(new byte[] { 1, 2, 3 });

        var batch = default(ServerReadyBatchMessage);
        Assert.Throws<ArgumentException>(() => batch.Deserialize(Wire.Reader(w)));
    }

    /// <summary>
    /// The real shape: many ServerReadyMessages concatenated, then read back one at a time.
    /// </summary>
    [Fact]
    public void ConcatenatedReadyMessages_ReadBackIndividually()
    {
        var inner = new NetDataWriter();
        const int count = 25;
        for (int i = 0; i < count; i++)
        {
            new ServerReadyMessage
            {
                playerIdMessage = new PlayerIdMessage { playerID = (ushort)(1000 + i) },
                localReadyMessage = new ReadyMessage
                {
                    playerMetaDataMessage = new ClientMetaDataMessage
                    {
                        playerUUID = $"7656119801234{i:D4}",
                        playerDisplayName = $"Player{i}",
                        playerPlatform = "WindowsPlayer",
                    },
                    clientAvatarChangeMessage = new ClientAvatarChangeMessage
                    {
                        loadMode = 1,
                        byteArray = new byte[] { 1, 2, 3, 4 },
                        LocalAvatarIndex = (byte)i,
                    },
                    localAvatarSyncMessage = new LocalAvatarSyncMessage
                    {
                        DataQualityLevel = (byte)BitQuality.High,
                        array = new byte[Wire.PayloadSize(BitQuality.High)],
                    },
                },
            }.Serialize(inner);
        }

        var batch = new ServerReadyBatchMessage { Count = count, Payload = inner.CopyData() };
        var w = new NetDataWriter();
        batch.Serialize(w);

        var received = default(ServerReadyBatchMessage);
        received.Deserialize(Wire.Reader(w));
        Assert.Equal((ushort)count, received.Count);

        var batchReader = new NetDataReader(received.Payload);
        for (int i = 0; i < count; i++)
        {
            var srm = default(ServerReadyMessage);
            srm.Deserialize(batchReader);
            Assert.Equal((ushort)(1000 + i), srm.playerIdMessage.playerID);
            Assert.Equal($"7656119801234{i:D4}", srm.localReadyMessage.playerMetaDataMessage.playerUUID);
            Assert.Equal("WindowsPlayer", srm.localReadyMessage.playerMetaDataMessage.playerPlatform);
            Assert.Equal((byte)i, srm.localReadyMessage.clientAvatarChangeMessage.LocalAvatarIndex);
        }
        Assert.Equal(0, batchReader.AvailableBytes);
    }
}

/// <summary>
/// BasisAvatarCloneRequest/Response (bare ushort), PlayerIdMessage byte/ushort id variants,
/// and the AvatarLoadDataMessage serialize layout.
/// </summary>
public class AvatarCloneAndPlayerIdWireTests
{
    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)1)]
    [InlineData(ushort.MaxValue)]
    public void BasisAvatarCloneRequest_RoundTrip_BoundaryIds(ushort id)
    {
        var msg = new BasisAvatarCloneRequest { requestingUser = id };
        var w = new NetDataWriter();
        msg.Serialize(w);
        Assert.Equal(2, w.Length);

        var result = default(BasisAvatarCloneRequest);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal(id, result.requestingUser);
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData(ushort.MaxValue)]
    public void BasisAvatarCloneResponse_RoundTrip_BoundaryIds(ushort id)
    {
        var msg = new BasisAvatarCloneResponse { requestingUser = id };
        var w = new NetDataWriter();
        msg.Serialize(w);
        Assert.Equal(2, w.Length);

        var result = default(BasisAvatarCloneResponse);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal(id, result.requestingUser);
    }

    [Fact]
    public void PlayerIdMessage_DefaultPath_RoundTripsUShort()
    {
        var msg = new PlayerIdMessage { playerID = 4242 };
        var w = new NetDataWriter();
        msg.Serialize(w);
        Assert.Equal(2, w.Length);

        var result = default(PlayerIdMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal((ushort)4242, result.playerID);
    }

    [Fact]
    public void PlayerIdMessage_SmallIdVariant_WritesSingleByte()
    {
        var msg = new PlayerIdMessage { playerID = 200 };
        var w = new NetDataWriter();
        msg.Serialize(w, largeId: false);
        Assert.Equal(1, w.Length);
        Assert.Equal((byte)200, w.Data[0]);

        var result = default(PlayerIdMessage);
        result.Deserialize(Wire.Reader(w), largeId: false);
        Assert.Equal((ushort)200, result.playerID);
    }

    [Fact]
    public void PlayerIdMessage_LargeIdVariant_RoundTripsUShortMax()
    {
        var msg = new PlayerIdMessage { playerID = ushort.MaxValue };
        var w = new NetDataWriter();
        msg.Serialize(w, largeId: true);
        Assert.Equal(2, w.Length);

        var result = default(PlayerIdMessage);
        result.Deserialize(Wire.Reader(w), largeId: true);
        Assert.Equal(ushort.MaxValue, result.playerID);
    }

    [Fact]
    public void AvatarLoadDataMessage_SerializeLayout_HeaderSenderSizeThenRawPayload()
    {
        var msg = new AvatarLoadDataMessage
        {
            messageIndex = 4,
            WhoSentUsThis = 777,
            payload = new byte[] { 1, 2, 3, 4, 5 },
        };
        var w = new NetDataWriter();
        msg.Serialize(w);

        var r = Wire.Reader(w);
        Assert.Equal((byte)4, r.GetByte());
        Assert.Equal((ushort)777, r.GetUShort());
        Assert.Equal((ushort)5, r.GetUShort());
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, r.GetRemainingBytes());

        var wNull = new NetDataWriter();
        new AvatarLoadDataMessage { messageIndex = 1, WhoSentUsThis = 2, payload = null }.Serialize(wNull);
        Assert.Equal(5, wNull.Length); // header + sender + size 0, no payload bytes
    }

    [Fact]
    public void AvatarLoadDataMessage_EmptyReader_ThrowsArgumentException()
    {
        var msg = default(AvatarLoadDataMessage);
        var reader = Wire.Empty();
        Assert.Throws<ArgumentException>(() => msg.Deserialize(reader));
    }

    [Fact]
    public void AvatarLoadDataMessage_RoundTrip_PreservesAllFields()
    {
        var msg = new AvatarLoadDataMessage
        {
            messageIndex = 4,
            WhoSentUsThis = ushort.MaxValue,
            payload = new byte[] { 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 },
        };
        var w = new NetDataWriter();
        msg.Serialize(w);

        var result = default(AvatarLoadDataMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal((byte)4, result.messageIndex);
        Assert.Equal(ushort.MaxValue, result.WhoSentUsThis);
        Assert.Equal((ushort)10, result.payloadSize);
        Assert.Equal(msg.payload, result.payload);
    }

    [Fact]
    public void AvatarLoadDataMessage_NullPayload_RoundTripsAsNull()
    {
        var msg = new AvatarLoadDataMessage { messageIndex = 1, WhoSentUsThis = 2, payload = null };
        var w = new NetDataWriter();
        msg.Serialize(w);

        var result = default(AvatarLoadDataMessage);
        result.Deserialize(Wire.Reader(w));
        Assert.Equal((byte)1, result.messageIndex);
        Assert.Equal((ushort)2, result.WhoSentUsThis);
        Assert.Equal((ushort)0, result.payloadSize);
        Assert.Null(result.payload);
    }
}

/// <summary>
/// LocalAvatarSyncMessage: quality-in-payload path ([quality:1][payload][additionalSize:1=0]),
/// channel-derived path (bare payload, optional additional section), and the standalone
/// additional-data section [count:1][linkedIndex:1][entries...].
/// </summary>
public class LocalAvatarSyncMessageWireTests
{
    [Theory]
    [InlineData(BitQuality.VeryLow)]
    [InlineData(BitQuality.Low)]
    [InlineData(BitQuality.Medium)]
    [InlineData(BitQuality.High)]
    public void LocalAvatarSyncMessage_PayloadPath_RoundTrips(BitQuality q)
    {
        int size = Wire.PayloadSize(q);
        byte[] payload = Wire.RandomBytes(new Random(100 + (int)q), size);
        var msg = new LocalAvatarSyncMessage { array = payload };
        var w = new NetDataWriter();
        msg.Serialize(w, q);
        Assert.Equal(1 + size + 1, w.Length);
        Assert.Equal((byte)q, w.Data[0]);

        var result = default(LocalAvatarSyncMessage);
        var reader = Wire.Reader(w);
        result.Deserialize(reader);
        Assert.Equal((byte)q, result.DataQualityLevel);
        Assert.Equal(payload, result.array);
        Assert.Equal((byte)0, result.AdditionalAvatarDataSize);
        Assert.Null(result.AdditionalAvatarDatas);
        Assert.Equal(0, reader.AvailableBytes);
    }

    [Theory]
    [InlineData(BitQuality.VeryLow)]
    [InlineData(BitQuality.Low)]
    [InlineData(BitQuality.Medium)]
    [InlineData(BitQuality.High)]
    public void LocalAvatarSyncMessage_ChannelPathNoAdditional_WritesBarePayload(BitQuality q)
    {
        int size = Wire.PayloadSize(q);
        byte[] payload = Wire.RandomBytes(new Random(200 + (int)q), size);
        var msg = new LocalAvatarSyncMessage { array = payload };
        var w = new NetDataWriter();
        msg.SerializeForChannel(w, q);
        Assert.Equal(size, w.Length);

        var result = default(LocalAvatarSyncMessage);
        var reader = Wire.Reader(w);
        result.Deserialize(reader, (byte)q, hasAdditionalData: false);
        Assert.Equal((byte)q, result.DataQualityLevel);
        Assert.Equal(payload, result.array);
        Assert.Equal((byte)0, result.AdditionalAvatarDataSize);
        Assert.Null(result.AdditionalAvatarDatas);
        Assert.Equal(0, reader.AvailableBytes);
    }

    [Fact]
    public void LocalAvatarSyncMessage_ChannelPathWithAdditionalData_RoundTrips()
    {
        var q = BitQuality.Medium;
        int size = Wire.PayloadSize(q);
        byte[] payload = Wire.RandomBytes(new Random(30), size);
        var msg = new LocalAvatarSyncMessage
        {
            array = payload,
            AdditionalAvatarDatas = new[]
            {
                new AdditionalAvatarData { messageIndex = 1, array = new byte[] { 10, 20, 30 } },
                new AdditionalAvatarData { messageIndex = 6, array = new byte[] { 42 } },
            },
            LinkedAvatarIndex = 4,
        };
        var w = new NetDataWriter();
        msg.SerializeForChannel(w, q);
        Assert.Equal(size + 2 + 5 + 3, w.Length); // payload + [count][linked] + entries

        var result = default(LocalAvatarSyncMessage);
        var reader = Wire.Reader(w);
        result.Deserialize(reader, (byte)q, hasAdditionalData: true);
        Assert.Equal(payload, result.array);
        Assert.Equal((byte)2, result.AdditionalAvatarDataSize);
        Assert.Equal((byte)4, result.LinkedAvatarIndex);
        Assert.Equal((byte)1, result.AdditionalAvatarDatas[0].messageIndex);
        Assert.Equal(new byte[] { 10, 20, 30 }, result.AdditionalAvatarDatas[0].array);
        Assert.Equal((byte)6, result.AdditionalAvatarDatas[1].messageIndex);
        Assert.Equal(new byte[] { 42 }, result.AdditionalAvatarDatas[1].array);
        Assert.Equal(0, reader.AvailableBytes);
    }

    [Fact]
    public void LocalAvatarSyncMessage_AdditionalOnlySection_RoundTrips_IncludingNullEntry()
    {
        var msg = new LocalAvatarSyncMessage
        {
            AdditionalAvatarDatas = new[]
            {
                new AdditionalAvatarData { messageIndex = 1, array = new byte[] { 5, 6, 7 } },
                new AdditionalAvatarData { messageIndex = 2, array = null },
                new AdditionalAvatarData { messageIndex = 3, array = new byte[] { 9 } },
            },
            LinkedAvatarIndex = 11,
        };
        var w = new NetDataWriter();
        msg.SerializeAdditionalOnly(w);
        Assert.Equal(2 + 5 + 2 + 3, w.Length); // null entry keeps its full [size:0][messageIndex] header

        var result = default(LocalAvatarSyncMessage);
        var reader = Wire.Reader(w);
        result.DeserializeAdditionalData(reader);
        Assert.Equal((byte)3, result.AdditionalAvatarDataSize);
        Assert.Equal((byte)11, result.LinkedAvatarIndex);
        Assert.Equal((byte)1, result.AdditionalAvatarDatas[0].messageIndex);
        Assert.Equal(new byte[] { 5, 6, 7 }, result.AdditionalAvatarDatas[0].array);
        Assert.Equal((byte)0, result.AdditionalAvatarDatas[1].PayloadSize);
        Assert.Equal((byte)2, result.AdditionalAvatarDatas[1].messageIndex);
        Assert.Null(result.AdditionalAvatarDatas[1].array);
        Assert.Equal((byte)3, result.AdditionalAvatarDatas[2].messageIndex);
        Assert.Equal(new byte[] { 9 }, result.AdditionalAvatarDatas[2].array);
        Assert.Equal(0, reader.AvailableBytes);
    }

    [Fact]
    public void LocalAvatarSyncMessage_EmptyAdditionalArray_SerializesSameAsNone()
    {
        var q = BitQuality.VeryLow;
        byte[] payload = Wire.RandomBytes(new Random(33), Wire.PayloadSize(q));
        var none = new LocalAvatarSyncMessage { array = payload };
        var empty = new LocalAvatarSyncMessage { array = payload, AdditionalAvatarDatas = Array.Empty<AdditionalAvatarData>() };
        var w1 = new NetDataWriter();
        none.Serialize(w1, q);
        var w2 = new NetDataWriter();
        empty.Serialize(w2, q);
        Assert.Equal(w1.CopyData(), w2.CopyData());

        var result = default(LocalAvatarSyncMessage);
        result.Deserialize(Wire.Reader(w2));
        Assert.Null(result.AdditionalAvatarDatas);
    }

    [Fact]
    public void LocalAvatarSyncMessage_AdditionalCountOver255_SerializesSameAsNone()
    {
        var q = BitQuality.Low;
        byte[] payload = Wire.RandomBytes(new Random(34), Wire.PayloadSize(q));
        var none = new LocalAvatarSyncMessage { array = payload };
        var oversized = new LocalAvatarSyncMessage { array = payload, AdditionalAvatarDatas = new AdditionalAvatarData[256] };
        var w1 = new NetDataWriter();
        none.Serialize(w1, q);
        var w2 = new NetDataWriter();
        oversized.Serialize(w2, q);
        Assert.Equal(w1.CopyData(), w2.CopyData());
    }

    [Fact]
    public void LocalAvatarSyncMessage_NullArray_WritesStub_DeserializeNoThrow()
    {
        var msg = new LocalAvatarSyncMessage { array = null };
        var w = new NetDataWriter();
        msg.Serialize(w, BitQuality.High);
        Assert.Equal(2, w.Length);
        Assert.Equal((byte)BitQuality.High, w.Data[0]);
        Assert.Equal((byte)0, w.Data[1]);

        var result = default(LocalAvatarSyncMessage);
        var reader = Wire.Reader(w);
        var ex = Record.Exception(() => result.Deserialize(reader));
        Assert.Null(ex);
        Assert.Equal((byte)BitQuality.High, result.DataQualityLevel);
        Assert.Null(result.array);
    }

    [Fact]
    public void LocalAvatarSyncMessage_InvalidQuality_WritesStub_DeserializeNoThrow()
    {
        var msg = new LocalAvatarSyncMessage { array = new byte[4] };
        var w = new NetDataWriter();
        msg.Serialize(w, (BitQuality)9);
        Assert.Equal(2, w.Length);
        Assert.Equal((byte)9, w.Data[0]);

        var result = default(LocalAvatarSyncMessage);
        var ex = Record.Exception(() => result.Deserialize(Wire.Reader(w)));
        Assert.Null(ex);
        Assert.Equal((byte)9, result.DataQualityLevel);
        Assert.Null(result.array);
    }

    [Fact]
    public void LocalAvatarSyncMessage_EmptyReader_DeserializeNoThrow()
    {
        var result = default(LocalAvatarSyncMessage);
        var reader = Wire.Empty();
        var ex = Record.Exception(() => result.Deserialize(reader));
        Assert.Null(ex);
        Assert.Equal((byte)0, result.DataQualityLevel);
        Assert.Null(result.array);
    }

    [Fact]
    public void LocalAvatarSyncMessage_ChannelPathTruncatedPayload_NoThrow()
    {
        var reader = new NetDataReader(new byte[] { 1, 2, 3, 4, 5 });
        var result = default(LocalAvatarSyncMessage);
        var ex = Record.Exception(() => result.Deserialize(reader, (byte)BitQuality.High, hasAdditionalData: false));
        Assert.Null(ex);
        Assert.Equal((byte)BitQuality.High, result.DataQualityLevel);
        Assert.Null(result.array);
    }

    [Fact]
    public void LocalAvatarSyncMessage_PayloadPath_DoubleRoundTrip_IsByteIdentical()
    {
        var q = BitQuality.Medium;
        var msg = new LocalAvatarSyncMessage { array = Wire.RandomBytes(new Random(32), Wire.PayloadSize(q)) };
        var w1 = new NetDataWriter();
        msg.Serialize(w1, q);

        var mid = default(LocalAvatarSyncMessage);
        mid.Deserialize(Wire.Reader(w1));
        var w2 = new NetDataWriter();
        mid.Serialize(w2, (BitQuality)mid.DataQualityLevel);
        Assert.Equal(w1.CopyData(), w2.CopyData());
    }

    [Fact]
    public void LocalAvatarSyncMessage_ChannelPath_DoubleRoundTrip_IsByteIdentical()
    {
        var q = BitQuality.Low;
        var msg = new LocalAvatarSyncMessage
        {
            array = Wire.RandomBytes(new Random(31), Wire.PayloadSize(q)),
            AdditionalAvatarDatas = new[] { new AdditionalAvatarData { messageIndex = 8, array = new byte[] { 1, 2 } } },
            LinkedAvatarIndex = 6,
        };
        var w1 = new NetDataWriter();
        msg.SerializeForChannel(w1, q);

        var mid = default(LocalAvatarSyncMessage);
        mid.Deserialize(Wire.Reader(w1), (byte)q, hasAdditionalData: true);
        var w2 = new NetDataWriter();
        mid.SerializeForChannel(w2, q);
        Assert.Equal(w1.CopyData(), w2.CopyData());
    }
}

/// <summary>
/// ServerSideSyncPlayerMessage: [playerID][interval:1][sequence:1] followed by the
/// LocalAvatarSyncMessage payload (quality byte inline, or derived from the channel).
/// </summary>
public class ServerSideSyncPlayerMessageWireTests
{
    [Theory]
    [InlineData(BitQuality.Low)]
    [InlineData(BitQuality.High)]
    public void ServerSideSyncPlayerMessage_RoundTrip_PreservesAllFields(BitQuality q)
    {
        int size = Wire.PayloadSize(q);
        byte[] payload = Wire.RandomBytes(new Random(300 + (int)q), size);
        var msg = new ServerSideSyncPlayerMessage
        {
            playerIdMessage = new PlayerIdMessage { playerID = ushort.MaxValue },
            interval = 33,
            sequence = 250,
            avatarSerialization = new LocalAvatarSyncMessage { array = payload, DataQualityLevel = (byte)q },
        };
        var w = new NetDataWriter();
        msg.Serialize(w);
        Assert.Equal(2 + 1 + 1 + 1 + size + 1, w.Length);

        var result = default(ServerSideSyncPlayerMessage);
        var reader = Wire.Reader(w);
        result.Deserialize(reader);
        Assert.Equal(ushort.MaxValue, result.playerIdMessage.playerID);
        Assert.Equal((byte)33, result.interval);
        Assert.Equal((byte)250, result.sequence);
        Assert.Equal((byte)q, result.avatarSerialization.DataQualityLevel);
        Assert.Equal(payload, result.avatarSerialization.array);
        Assert.Equal(0, reader.AvailableBytes);
    }

    [Fact]
    public void ServerSideSyncPlayerMessage_ChannelDeserialize_LargeIdNoAdditional()
    {
        var q = BitQuality.VeryLow;
        int size = Wire.PayloadSize(q);
        byte[] payload = Wire.RandomBytes(new Random(40), size);
        var pid = new PlayerIdMessage { playerID = 5000 };
        var lasm = new LocalAvatarSyncMessage { array = payload };
        var w = new NetDataWriter();
        pid.Serialize(w);
        w.Put((byte)55);  // interval
        w.Put((byte)128); // sequence
        lasm.SerializeForChannel(w, q);

        var result = default(ServerSideSyncPlayerMessage);
        var reader = Wire.Reader(w);
        result.Deserialize(reader, (byte)q, hasAdditionalData: false);
        Assert.Equal((ushort)5000, result.playerIdMessage.playerID);
        Assert.Equal((byte)55, result.interval);
        Assert.Equal((byte)128, result.sequence);
        Assert.Equal(payload, result.avatarSerialization.array);
        Assert.Null(result.avatarSerialization.AdditionalAvatarDatas);
        Assert.Equal(0, reader.AvailableBytes);
    }

    [Fact]
    public void ServerSideSyncPlayerMessage_ChannelDeserialize_SmallIdWithAdditionalData()
    {
        var q = BitQuality.High;
        int size = Wire.PayloadSize(q);
        byte[] payload = Wire.RandomBytes(new Random(41), size);
        var pid = new PlayerIdMessage { playerID = 42 };
        var lasm = new LocalAvatarSyncMessage
        {
            array = payload,
            AdditionalAvatarDatas = new[] { new AdditionalAvatarData { messageIndex = 2, array = new byte[] { 4, 5, 6 } } },
            LinkedAvatarIndex = 1,
        };
        var w = new NetDataWriter();
        pid.Serialize(w, largeId: false);
        w.Put((byte)120); // interval
        w.Put((byte)7);   // sequence
        lasm.SerializeForChannel(w, q);

        var result = default(ServerSideSyncPlayerMessage);
        var reader = Wire.Reader(w);
        result.Deserialize(reader, (byte)q, hasAdditionalData: true, largeId: false);
        Assert.Equal((ushort)42, result.playerIdMessage.playerID);
        Assert.Equal((byte)120, result.interval);
        Assert.Equal((byte)7, result.sequence);
        Assert.Equal(payload, result.avatarSerialization.array);
        Assert.Equal((byte)1, result.avatarSerialization.AdditionalAvatarDataSize);
        Assert.Equal((byte)1, result.avatarSerialization.LinkedAvatarIndex);
        Assert.Equal(new byte[] { 4, 5, 6 }, result.avatarSerialization.AdditionalAvatarDatas[0].array);
        Assert.Equal(0, reader.AvailableBytes);
    }

    [Fact]
    public void ServerSideSyncPlayerMessage_DoubleRoundTrip_IsByteIdentical()
    {
        var q = BitQuality.Medium;
        var msg = new ServerSideSyncPlayerMessage
        {
            playerIdMessage = new PlayerIdMessage { playerID = 77 },
            interval = 50,
            sequence = 1,
            avatarSerialization = new LocalAvatarSyncMessage
            {
                array = Wire.RandomBytes(new Random(42), Wire.PayloadSize(q)),
                DataQualityLevel = (byte)q,
            },
        };
        var w1 = new NetDataWriter();
        msg.Serialize(w1);

        var mid = default(ServerSideSyncPlayerMessage);
        mid.Deserialize(Wire.Reader(w1));
        var w2 = new NetDataWriter();
        mid.Serialize(w2);
        Assert.Equal(w1.CopyData(), w2.CopyData());
    }
}

/// <summary>
/// VoiceReceiversMessage: [count:1|2][ushort ids...] with byte/ushort count width chosen by
/// channel. Deserialize rents from ArrayPool, so only the UsersLength prefix is meaningful.
/// </summary>
public class VoiceReceiversMessageWireTests
{
    [Fact]
    public void VoiceReceiversMessage_LargeCount_RoundTrips()
    {
        ushort[] users = { 1, 2, 70, ushort.MaxValue };
        var msg = new VoiceReceiversMessage { Users = users, UsersLength = users.Length };
        var w = new NetDataWriter();
        msg.Serialize(w, largeCount: true);
        Assert.Equal(2 + users.Length * 2, w.Length);

        var result = default(VoiceReceiversMessage);
        result.Deserialize(Wire.Reader(w), largeCount: true);
        Assert.Equal(users.Length, result.UsersLength);
        Assert.NotNull(result.Users);
        Assert.True(result.Users.Length >= result.UsersLength); // pooled array may be larger
        Assert.Equal(users, result.Users.AsSpan(0, result.UsersLength).ToArray());
        result.ReturnPool();
    }

    [Fact]
    public void VoiceReceiversMessage_ByteCount_RoundTrips()
    {
        ushort[] users = { 5, 10, 15 };
        var msg = new VoiceReceiversMessage { Users = users, UsersLength = users.Length };
        var w = new NetDataWriter();
        msg.Serialize(w, largeCount: false);
        Assert.Equal(1 + users.Length * 2, w.Length);
        Assert.Equal((byte)3, w.Data[0]);

        var result = default(VoiceReceiversMessage);
        result.Deserialize(Wire.Reader(w), largeCount: false);
        Assert.Equal(users.Length, result.UsersLength);
        Assert.Equal(users, result.Users.AsSpan(0, result.UsersLength).ToArray());
        result.ReturnPool();
    }

    [Fact]
    public void VoiceReceiversMessage_DefaultSerialize_MatchesLargeCount()
    {
        ushort[] users = { 3, 9 };
        var m1 = new VoiceReceiversMessage { Users = users, UsersLength = users.Length };
        var m2 = new VoiceReceiversMessage { Users = users, UsersLength = users.Length };
        var w1 = new NetDataWriter();
        m1.Serialize(w1);
        var w2 = new NetDataWriter();
        m2.Serialize(w2, largeCount: true);
        Assert.Equal(w1.CopyData(), w2.CopyData());
    }

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    public void VoiceReceiversMessage_EmptyUsers_WritesZeroCount(bool largeCount, int expectedBytes)
    {
        var msg = new VoiceReceiversMessage();
        var w = new NetDataWriter();
        msg.Serialize(w, largeCount);
        Assert.Equal(expectedBytes, w.Length);

        var result = default(VoiceReceiversMessage);
        result.Deserialize(Wire.Reader(w), largeCount);
        Assert.NotNull(result.Users);
        Assert.Empty(result.Users);
        Assert.Equal(0, result.UsersLength);
    }

    [Fact]
    public void VoiceReceiversMessage_EmptyReader_NoThrow_EmptyUsers()
    {
        var result = default(VoiceReceiversMessage);
        var reader = Wire.Empty();
        var ex = Record.Exception(() => result.Deserialize(reader, largeCount: true));
        Assert.Null(ex);
        Assert.NotNull(result.Users);
        Assert.Empty(result.Users);
    }

    [Fact]
    public void VoiceReceiversMessage_TruncatedCount_NoThrow_EmptyUsers()
    {
        var reader = new NetDataReader(new byte[] { 42 }); // 1 byte, large channel needs 2
        var result = default(VoiceReceiversMessage);
        var ex = Record.Exception(() => result.Deserialize(reader, largeCount: true));
        Assert.Null(ex);
        Assert.NotNull(result.Users);
        Assert.Empty(result.Users);
        Assert.Equal(0, reader.AvailableBytes);
    }

    [Fact]
    public void VoiceReceiversMessage_CountExceedsData_NoThrow_NullUsers()
    {
        var w = new NetDataWriter();
        w.Put((byte)10);  // claims 10 recipients
        w.Put((ushort)1); // but only 2 fit
        w.Put((ushort)2);
        var reader = Wire.Reader(w);
        var result = default(VoiceReceiversMessage);
        var ex = Record.Exception(() => result.Deserialize(reader, largeCount: false));
        Assert.Null(ex);
        Assert.Null(result.Users);
        Assert.Equal(0, result.UsersLength);
        Assert.Equal(0, reader.AvailableBytes);
    }

    [Fact]
    public void VoiceReceiversMessage_ByteCountOver255_TruncatesTo255()
    {
        var users = new ushort[300];
        for (int i = 0; i < users.Length; i++)
        {
            users[i] = (ushort)i;
        }
        var msg = new VoiceReceiversMessage { Users = users, UsersLength = users.Length };
        var w = new NetDataWriter();
        msg.Serialize(w, largeCount: false);
        Assert.Equal(1 + 255 * 2, w.Length);
        Assert.Equal((byte)255, w.Data[0]);

        var result = default(VoiceReceiversMessage);
        result.Deserialize(Wire.Reader(w), largeCount: false);
        Assert.Equal(255, result.UsersLength);
        Assert.Equal(users.AsSpan(0, 255).ToArray(), result.Users.AsSpan(0, 255).ToArray());
        result.ReturnPool();
    }
}
