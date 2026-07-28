using System.Collections.Concurrent;
using System.Text;
using Basis.Network.Core;
using Basis.Network.Core.Compression;
using Xunit;
using static SerializableBasis;
using AdminRequest = BasisNetworkCore.Serializable.SerializableBasis.AdminRequest;
using AdminRequestMode = BasisNetworkCore.Serializable.SerializableBasis.AdminRequestMode;
using BitQuality = Basis.Network.Core.Compression.BasisAvatarBitPacking.BitQuality;
using BytesMessage = Basis.Network.Core.Serializable.SerializableBasis.BytesMessage;
using ConsoleData = BasisNetworkCore.Serializable.SerializableBasis.ConsoleData;
using ErrorMessage = BasisNetworkCore.Serializable.SerializableBasis.ErrorMessage;
using NetIDMessage = BasisNetworkCore.Serializable.SerializableBasis.NetIDMessage;
using OwnershipTransferMessage = DarkRift.Basis_Common.Serializable.SerializableBasis.OwnershipTransferMessage;
using ServerNetIDMessage = BasisNetworkCore.Serializable.SerializableBasis.ServerNetIDMessage;
using ServerStatisticMessage = BasisNetworkCore.Serializable.SerializableBasis.ServerStatisticMessage;
using ServerUniqueIDMessages = BasisNetworkCore.Serializable.SerializableBasis.ServerUniqueIDMessages;
using UshortUniqueIDMessage = BasisNetworkCore.Serializable.SerializableBasis.UshortUniqueIDMessage;

namespace BasisServerTests;

/// <summary>
/// Wire-format lock for the control/resource/misc message structs: serialize→deserialize
/// round-trips (including nested composites like ServerReadyMessage), null/empty and
/// boundary values, size-guard behavior, and no-throw fallbacks on truncated input.
/// </summary>
public class ControlAndResourceMessageRoundTripTests
{
    static ControlAndResourceMessageRoundTripTests()
    {
        BNL.LogErrorOutput = static _ => { };
    }

    private static NetDataReader ReaderFor(NetDataWriter writer) => new(writer.CopyData());

    private static NetDataReader EmptyReader() => new(Array.Empty<byte>());

    private static byte[] SeededBytes(int length, int seed)
    {
        var rng = new Random(seed);
        byte[] bytes = new byte[length];
        rng.NextBytes(bytes);
        return bytes;
    }

    // ── AdminRequest ────────────────────────────────────────────────────────

    [Fact]
    public void AdminRequest_RoundTripsEveryMode()
    {
        foreach (AdminRequestMode mode in Enum.GetValues<AdminRequestMode>())
        {
            var writer = new NetDataWriter();
            var request = new AdminRequest();
            request.Serialize(writer, mode);
            Assert.Equal(1, writer.Length);

            var back = new AdminRequest();
            back.Deserialize(ReaderFor(writer));
            Assert.Equal(mode, back.GetAdminRequestMode());
        }
    }

    [Fact]
    public void AdminRequest_EmptyReader_DoesNotThrowAndDefaultsToBan()
    {
        var request = new AdminRequest();
        request.Deserialize(EmptyReader());
        Assert.Equal(AdminRequestMode.Ban, request.GetAdminRequestMode());
    }

    // ── BytesMessage ────────────────────────────────────────────────────────

    [Fact]
    public void BytesMessage_RoundTripsPayload()
    {
        byte[] source = SeededBytes(257, 101);
        var writer = new NetDataWriter();
        var msg = new BytesMessage();
        msg.Serialize(writer, source);
        Assert.Equal(2 + source.Length, writer.Length);

        Assert.True(msg.Deserialize(ReaderFor(writer), out byte[] data));
        Assert.Equal(source, data);
    }

    [Fact]
    public void BytesMessage_RoundTripsMaxUShortPayload()
    {
        byte[] source = SeededBytes(ushort.MaxValue, 102);
        var writer = new NetDataWriter();
        var msg = new BytesMessage();
        msg.Serialize(writer, source);

        Assert.True(msg.Deserialize(ReaderFor(writer), out byte[] data));
        Assert.Equal(source, data);
    }

    [Fact]
    public void BytesMessage_EmptyPayload_RoundTripsToEmpty()
    {
        var writer = new NetDataWriter();
        var msg = new BytesMessage();
        msg.Serialize(writer, Array.Empty<byte>());
        Assert.Equal(2, writer.Length);

        Assert.True(msg.Deserialize(ReaderFor(writer), out byte[] data));
        Assert.NotNull(data);
        Assert.Empty(data);
    }

    [Fact]
    public void BytesMessage_DeclaredLengthBeyondAvailable_ReturnsFalse()
    {
        var writer = new NetDataWriter();
        writer.Put((ushort)10);
        writer.Put(new byte[] { 1, 2, 3 });

        var msg = new BytesMessage();
        Assert.False(msg.Deserialize(ReaderFor(writer), out byte[] data));
        Assert.Null(data);
    }

    [Fact]
    public void BytesMessage_EmptyReader_ReturnsFalse()
    {
        var msg = new BytesMessage();
        Assert.False(msg.Deserialize(EmptyReader(), out byte[] data));
        Assert.Null(data);
    }

    // ── Camera PIP messages ─────────────────────────────────────────────────

    [Fact]
    public void CameraPIPStateMessage_ActiveRoundTripsAllFields()
    {
        var msg = new CameraPIPStateMessage
        {
            PlayerID = 1234,
            IsActive = true,
            PositionX = -1.5f,
            PositionY = 2.25f,
            PositionZ = -300.125f,
            RotationX = 0.1f,
            RotationY = -0.2f,
            RotationZ = 0.3f,
            RotationW = -0.9f,
        };
        var writer = new NetDataWriter();
        msg.Serialize(writer);
        Assert.Equal(3 + 7 * 4, writer.Length);

        var back = new CameraPIPStateMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal((ushort)1234, back.PlayerID);
        Assert.True(back.IsActive);
        Assert.Equal(-1.5f, back.PositionX);
        Assert.Equal(2.25f, back.PositionY);
        Assert.Equal(-300.125f, back.PositionZ);
        Assert.Equal(0.1f, back.RotationX);
        Assert.Equal(-0.2f, back.RotationY);
        Assert.Equal(0.3f, back.RotationZ);
        Assert.Equal(-0.9f, back.RotationW);
    }

    [Fact]
    public void CameraPIPStateMessage_InactiveOmitsTransform()
    {
        var msg = new CameraPIPStateMessage
        {
            PlayerID = ushort.MaxValue,
            IsActive = false,
            PositionX = 99f,
            RotationW = 1f,
        };
        var writer = new NetDataWriter();
        msg.Serialize(writer);
        Assert.Equal(3, writer.Length);

        var back = new CameraPIPStateMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal(ushort.MaxValue, back.PlayerID);
        Assert.False(back.IsActive);
        Assert.Equal(0f, back.PositionX);
        Assert.Equal(0f, back.RotationW);
    }

    [Fact]
    public void CameraPIPStateMessage_DoubleRoundTripIsByteIdentical()
    {
        var msg = new CameraPIPStateMessage
        {
            PlayerID = 77,
            IsActive = true,
            PositionX = 1f,
            PositionY = 2f,
            PositionZ = 3f,
            RotationX = 4f,
            RotationY = 5f,
            RotationZ = 6f,
            RotationW = 7f,
        };
        var first = new NetDataWriter();
        msg.Serialize(first);

        var back = new CameraPIPStateMessage();
        back.Deserialize(ReaderFor(first));
        var second = new NetDataWriter();
        back.Serialize(second);

        Assert.Equal(first.CopyData(), second.CopyData());
    }

    [Fact]
    public void CameraPIPPositionMessage_RoundTripsAllFields()
    {
        var msg = new CameraPIPPositionMessage
        {
            PlayerID = ushort.MaxValue,
            PositionX = 10.5f,
            PositionY = -0.0625f,
            PositionZ = 512f,
            RotationX = -0.5f,
            RotationY = 0.5f,
            RotationZ = -0.25f,
            RotationW = 0.75f,
        };
        var writer = new NetDataWriter();
        msg.Serialize(writer);
        Assert.Equal(2 + 7 * 4, writer.Length);

        var back = new CameraPIPPositionMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal(ushort.MaxValue, back.PlayerID);
        Assert.Equal(10.5f, back.PositionX);
        Assert.Equal(-0.0625f, back.PositionY);
        Assert.Equal(512f, back.PositionZ);
        Assert.Equal(-0.5f, back.RotationX);
        Assert.Equal(0.5f, back.RotationY);
        Assert.Equal(-0.25f, back.RotationZ);
        Assert.Equal(0.75f, back.RotationW);
    }

    [Fact]
    public void ClientCameraPIPStateMessage_ActiveRoundTrips()
    {
        var msg = new ClientCameraPIPStateMessage
        {
            IsActive = true,
            PositionX = 1.25f,
            PositionY = -2.5f,
            PositionZ = 3.75f,
            RotationX = -0.125f,
            RotationY = 0.375f,
            RotationZ = -0.625f,
            RotationW = 0.875f,
        };
        var writer = new NetDataWriter();
        msg.Serialize(writer);
        Assert.Equal(1 + 7 * 4, writer.Length);

        var back = new ClientCameraPIPStateMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.True(back.IsActive);
        Assert.Equal(1.25f, back.PositionX);
        Assert.Equal(-2.5f, back.PositionY);
        Assert.Equal(3.75f, back.PositionZ);
        Assert.Equal(-0.125f, back.RotationX);
        Assert.Equal(0.375f, back.RotationY);
        Assert.Equal(-0.625f, back.RotationZ);
        Assert.Equal(0.875f, back.RotationW);
    }

    [Fact]
    public void ClientCameraPIPStateMessage_InactiveWritesSingleByte()
    {
        var msg = new ClientCameraPIPStateMessage { IsActive = false, PositionX = 42f };
        var writer = new NetDataWriter();
        msg.Serialize(writer);
        Assert.Equal(1, writer.Length);

        var back = new ClientCameraPIPStateMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.False(back.IsActive);
        Assert.Equal(0f, back.PositionX);
    }

    [Fact]
    public void ClientCameraPIPPositionMessage_RoundTrips()
    {
        var msg = new ClientCameraPIPPositionMessage
        {
            PositionX = float.MaxValue,
            PositionY = float.Epsilon,
            PositionZ = -1f,
            RotationX = 0f,
            RotationY = 1f,
            RotationZ = -0.5f,
            RotationW = 0.25f,
        };
        var writer = new NetDataWriter();
        msg.Serialize(writer);
        Assert.Equal(28, writer.Length);

        var back = new ClientCameraPIPPositionMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal(float.MaxValue, back.PositionX);
        Assert.Equal(float.Epsilon, back.PositionY);
        Assert.Equal(-1f, back.PositionZ);
        Assert.Equal(0f, back.RotationX);
        Assert.Equal(1f, back.RotationY);
        Assert.Equal(-0.5f, back.RotationZ);
        Assert.Equal(0.25f, back.RotationW);
    }

    // ── Camera shutter / countdown ──────────────────────────────────────────

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)1)]
    [InlineData(ushort.MaxValue)]
    public void CameraShutterSoundMessage_RoundTrips(ushort playerId)
    {
        var msg = new CameraShutterSoundMessage { PlayerID = playerId };
        var writer = new NetDataWriter();
        msg.Serialize(writer);
        Assert.Equal(2, writer.Length);

        var back = new CameraShutterSoundMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal(playerId, back.PlayerID);
    }

    [Theory]
    [InlineData((ushort)0, (byte)0)]
    [InlineData((ushort)500, (byte)3)]
    [InlineData(ushort.MaxValue, byte.MaxValue)]
    public void CameraCountdownMessage_RoundTrips(ushort playerId, byte seconds)
    {
        var msg = new CameraCountdownMessage { PlayerID = playerId, Seconds = seconds };
        var writer = new NetDataWriter();
        msg.Serialize(writer);
        Assert.Equal(3, writer.Length);

        var back = new CameraCountdownMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal(playerId, back.PlayerID);
        Assert.Equal(seconds, back.Seconds);
    }

    [Fact]
    public void ClientCameraCountdownMessage_RoundTrips()
    {
        var msg = new ClientCameraCountdownMessage { Seconds = byte.MaxValue };
        var writer = new NetDataWriter();
        msg.Serialize(writer);
        Assert.Equal(1, writer.Length);

        var back = new ClientCameraCountdownMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal(byte.MaxValue, back.Seconds);
    }

    // ── ChatMessage ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChatMessage_RoundTripsUtf8PayloadAndSoundFlag(bool sound)
    {
        byte[] payload = Encoding.UTF8.GetBytes("chat ✦ 你好, ωorld");
        var msg = new ChatMessage { payload = payload, playNotificationSound = sound };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new ChatMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal(payload, back.payload);
        Assert.Equal((ushort)payload.Length, back.payloadSize);
        Assert.Equal(sound, back.playNotificationSound);
    }

    [Fact]
    public void ChatMessage_NullPayload_RoundTripsAsEmpty()
    {
        var msg = new ChatMessage { payload = null!, playNotificationSound = false };
        var writer = new NetDataWriter();
        msg.Serialize(writer);
        Assert.Equal(3, writer.Length);

        var back = new ChatMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.NotNull(back.payload);
        Assert.Empty(back.payload);
        Assert.Equal((ushort)0, back.payloadSize);
        Assert.False(back.playNotificationSound);
    }

    [Fact]
    public void ChatMessage_SerializeCapsPayloadAt512Bytes()
    {
        byte[] oversized = SeededBytes(600, 103);
        var msg = new ChatMessage { payload = oversized, playNotificationSound = true };
        var writer = new NetDataWriter();
        msg.Serialize(writer);
        Assert.Equal(2 + ChatMessage.MaxPayloadBytes + 1, writer.Length);

        var back = new ChatMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal((ushort)ChatMessage.MaxPayloadBytes, back.payloadSize);
        Assert.Equal(oversized.AsSpan(0, ChatMessage.MaxPayloadBytes).ToArray(), back.payload);
        Assert.True(back.playNotificationSound);
    }

    [Fact]
    public void ChatMessage_OversizedWireDeclaration_ReadsCapAndSkipsExcess()
    {
        byte[] wirePayload = SeededBytes(600, 104);
        var writer = new NetDataWriter();
        writer.Put((ushort)600);
        writer.Put(wirePayload);
        writer.Put(true);

        var back = new ChatMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal((ushort)ChatMessage.MaxPayloadBytes, back.payloadSize);
        Assert.Equal(wirePayload.AsSpan(0, ChatMessage.MaxPayloadBytes).ToArray(), back.payload);
        Assert.True(back.playNotificationSound);
    }

    [Fact]
    public void ChatMessage_TruncatedPayload_FallsBackToEmptyWithoutThrowing()
    {
        var writer = new NetDataWriter();
        writer.Put((ushort)100);
        writer.Put(SeededBytes(10, 105));

        var back = new ChatMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.NotNull(back.payload);
        Assert.Empty(back.payload);
        Assert.Equal((ushort)0, back.payloadSize);
        Assert.True(back.playNotificationSound);
    }

    [Fact]
    public void ChatMessage_MissingSoundByte_DefaultsToTrue()
    {
        byte[] payload = { 10, 20, 30 };
        var writer = new NetDataWriter();
        writer.Put((ushort)payload.Length);
        writer.Put(payload);

        var back = new ChatMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal(payload, back.payload);
        Assert.True(back.playNotificationSound);
    }

    // ── ClientMetaDataMessage / ServerMetaDataMessage ───────────────────────

    [Fact]
    public void ClientMetaDataMessage_RoundTripsUnicodeFields()
    {
        var msg = new ClientMetaDataMessage
        {
            playerUUID = "uuid-Ω-123",
            playerDisplayName = "Ada 🚀 ラブレス",
            playerPlatform = "OpenXR",
        };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new ClientMetaDataMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal("uuid-Ω-123", back.playerUUID);
        Assert.Equal("Ada 🚀 ラブレス", back.playerDisplayName);
        Assert.Equal("OpenXR", back.playerPlatform);
    }

    [Fact]
    public void ClientMetaDataMessage_NullOrEmptyFields_SerializeAsFailure()
    {
        var msg = new ClientMetaDataMessage
        {
            playerUUID = null!,
            playerDisplayName = "",
            playerPlatform = null!,
        };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new ClientMetaDataMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal("Failure", back.playerUUID);
        Assert.Equal("Failure", back.playerDisplayName);
        Assert.Equal("Failure", back.playerPlatform);
    }

    [Fact]
    public void ServerMetaDataMessage_RoundTripsAllFields()
    {
        var msg = new ServerMetaDataMessage
        {
            ClientMetaDataMessage = new ClientMetaDataMessage
            {
                playerUUID = "uuid-42",
                playerDisplayName = "Tester 猫",
                playerPlatform = "Desktop",
            },
            SyncInterval = 33,
            BaseMultiplier = 4,
            IncreaseRate = 0.25f,
            SlowestSendRate = 1.75f,
            PeerLimit = 128,
            UplinkDeltaEnabled = true,
        };
        msg.SetPermissions(new[] { "basis.moderation", "basis.moderation.kick", "custom.perm.alpha", "custom.perm.beta" });

        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new ServerMetaDataMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal("uuid-42", back.ClientMetaDataMessage.playerUUID);
        Assert.Equal("Tester 猫", back.ClientMetaDataMessage.playerDisplayName);
        Assert.Equal("Desktop", back.ClientMetaDataMessage.playerPlatform);
        Assert.Equal(33, back.SyncInterval);
        Assert.Equal(4, back.BaseMultiplier);
        Assert.Equal(0.25f, back.IncreaseRate);
        Assert.Equal(1.75f, back.SlowestSendRate);
        Assert.Equal(128, back.PeerLimit);
        Assert.True(back.UplinkDeltaEnabled);
        Assert.Equal(msg.PermissionsBitset, back.PermissionsBitset);
        Assert.Equal(new[] { "custom.perm.alpha", "custom.perm.beta" }, back.ExtraPermissions);
        Assert.True(back.GetPermissions().SetEquals(
            new[] { "basis.moderation", "basis.moderation.kick", "custom.perm.alpha", "custom.perm.beta" }));
    }

    [Fact]
    public void ServerMetaDataMessage_ZeroTuningValues_SerializeAsDefaults()
    {
        var msg = new ServerMetaDataMessage
        {
            ClientMetaDataMessage = new ClientMetaDataMessage
            {
                playerUUID = "u",
                playerDisplayName = "n",
                playerPlatform = "p",
            },
            SyncInterval = 0,
            BaseMultiplier = 0,
            IncreaseRate = 0f,
            SlowestSendRate = 0f,
            PeerLimit = 32,
        };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new ServerMetaDataMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal(50, back.SyncInterval);
        Assert.Equal(1, back.BaseMultiplier);
        Assert.Equal(0.005f, back.IncreaseRate);
        Assert.Equal(2.55f, back.SlowestSendRate);
        Assert.Equal(32, back.PeerLimit);
    }

    [Fact]
    public void ServerMetaDataMessage_EmptyPermissions_RoundTrip()
    {
        var msg = new ServerMetaDataMessage
        {
            ClientMetaDataMessage = new ClientMetaDataMessage
            {
                playerUUID = "u",
                playerDisplayName = "n",
                playerPlatform = "p",
            },
            SyncInterval = 50,
            BaseMultiplier = 1,
            IncreaseRate = 0.005f,
            SlowestSendRate = 2.55f,
            PeerLimit = 8,
            PermissionsBitset = null!,
            ExtraPermissions = null!,
            UplinkDeltaEnabled = false,
        };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new ServerMetaDataMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.NotNull(back.PermissionsBitset);
        Assert.Empty(back.PermissionsBitset);
        Assert.NotNull(back.ExtraPermissions);
        Assert.Empty(back.ExtraPermissions);
        Assert.Empty(back.GetPermissions());
        Assert.False(back.UplinkDeltaEnabled);
    }

    [Fact]
    public void ServerMetaDataMessage_WildcardPermission_ExpandsToAllKnownNodes()
    {
        var msg = new ServerMetaDataMessage
        {
            ClientMetaDataMessage = new ClientMetaDataMessage
            {
                playerUUID = "u",
                playerDisplayName = "n",
                playerPlatform = "p",
            },
            SyncInterval = 50,
            BaseMultiplier = 1,
            IncreaseRate = 0.005f,
            SlowestSendRate = 2.55f,
            PeerLimit = 8,
        };
        msg.SetPermissions(new[] { "*" });

        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new ServerMetaDataMessage();
        back.Deserialize(ReaderFor(writer));
        HashSet<string> perms = back.GetPermissions();
        Assert.Equal(PermissionBitsetMap.KnownCount, perms.Count);
        Assert.Contains("*", perms);
        Assert.Contains("basis.server.stats", perms);
        Assert.Contains("basis.moderation.headlessaudio", perms);
    }

    [Fact]
    public void ServerMetaDataMessage_DoubleRoundTripIsByteIdentical()
    {
        var msg = new ServerMetaDataMessage
        {
            ClientMetaDataMessage = new ClientMetaDataMessage
            {
                playerUUID = "uuid-7",
                playerDisplayName = "Name",
                playerPlatform = "Android",
            },
            SyncInterval = 66,
            BaseMultiplier = 2,
            IncreaseRate = 0.5f,
            SlowestSendRate = 3.5f,
            PeerLimit = 64,
            UplinkDeltaEnabled = true,
        };
        msg.SetPermissions(new[] { "basis.protection", "custom.node.one" });

        var first = new NetDataWriter();
        msg.Serialize(first);

        var back = new ServerMetaDataMessage();
        back.Deserialize(ReaderFor(first));
        var second = new NetDataWriter();
        back.Serialize(second);

        Assert.Equal(first.CopyData(), second.CopyData());
    }

    // ── ConsoleData ─────────────────────────────────────────────────────────

    [Fact]
    public void ConsoleData_RoundTripsPayload()
    {
        var msg = new ConsoleData { messageIndex = 200, array = SeededBytes(40, 106) };
        var writer = new NetDataWriter();
        msg.Serialize(writer);
        Assert.Equal(1 + 2 + 40, writer.Length);

        var back = new ConsoleData();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal((byte)200, back.messageIndex);
        Assert.Equal(msg.array, back.array);
    }

    [Fact]
    public void ConsoleData_NullArray_RoundTripsToEmpty()
    {
        var msg = new ConsoleData { messageIndex = 5, array = null! };
        var writer = new NetDataWriter();
        msg.Serialize(writer);
        Assert.Equal(3, writer.Length);

        var back = new ConsoleData();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal((byte)5, back.messageIndex);
        Assert.NotNull(back.array);
        Assert.Empty(back.array);
    }

    [Fact]
    public void ConsoleData_DeclaredPayloadBeyondAvailable_YieldsEmptyWithoutThrowing()
    {
        var writer = new NetDataWriter();
        writer.Put((byte)9);
        writer.Put((ushort)100);
        writer.Put(SeededBytes(3, 107));

        var back = new ConsoleData();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal((byte)9, back.messageIndex);
        Assert.NotNull(back.array);
        Assert.Empty(back.array);
    }

    [Fact]
    public void ConsoleData_EmptyReader_DoesNotThrow()
    {
        var back = new ConsoleData();
        back.Deserialize(EmptyReader());
        Assert.Equal((byte)0, back.messageIndex);
        Assert.Null(back.array);
    }

    // ── ErrorMessage ────────────────────────────────────────────────────────

    [Fact]
    public void ErrorMessage_RoundTripsUnicode()
    {
        var msg = new ErrorMessage { Message = "błąd: 接続に失敗しました ⚠" };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new ErrorMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal("błąd: 接続に失敗しました ⚠", back.Message);
    }

    [Fact]
    public void ErrorMessage_LongMessage_RoundTrips()
    {
        string longMessage = new string('x', 10_000) + "終";
        var msg = new ErrorMessage { Message = longMessage };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new ErrorMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal(longMessage, back.Message);
    }

    [Fact]
    public void ErrorMessage_NullMessage_RoundTripsAsEmpty()
    {
        var msg = new ErrorMessage { Message = null! };
        var writer = new NetDataWriter();
        msg.Serialize(writer);
        Assert.Equal(2, writer.Length);

        var back = new ErrorMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal(string.Empty, back.Message);
    }

    [Fact]
    public void ErrorMessage_EmptyReader_DoesNotThrow()
    {
        var back = new ErrorMessage();
        back.Deserialize(EmptyReader());
        Assert.Null(back.Message);
    }

    // ── ModifyResource ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ModifyResource_RoundTripsAllFields(bool isStatic, bool adminLocked)
    {
        var msg = new ModifyResource
        {
            LoadedNetID = "net-α-ボール",
            Mode = 1,
            Static = isStatic,
            StaticAdminLocked = adminLocked,
        };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new ModifyResource();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal("net-α-ボール", back.LoadedNetID);
        Assert.Equal((byte)1, back.Mode);
        Assert.Equal(isStatic, back.Static);
        Assert.Equal(adminLocked, back.StaticAdminLocked);
    }

    [Fact]
    public void ModifyResource_DoubleRoundTripIsByteIdentical()
    {
        var msg = new ModifyResource
        {
            LoadedNetID = "prop-99",
            Mode = 0,
            Static = true,
            StaticAdminLocked = true,
        };
        var first = new NetDataWriter();
        msg.Serialize(first);

        var back = new ModifyResource();
        back.Deserialize(ReaderFor(first));
        var second = new NetDataWriter();
        back.Serialize(second);

        Assert.Equal(first.CopyData(), second.CopyData());
    }

    // ── NetIDMessage ────────────────────────────────────────────────────────

    [Fact]
    public void NetIDMessage_RoundTripsPlayerID()
    {
        var msg = new NetIDMessage { playerID = "プレイヤー-42-Ø" };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new NetIDMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal("プレイヤー-42-Ø", back.playerID);
    }

    [Fact]
    public void NetIDMessage_256CharacterId_RoundTripsExactly()
    {
        string id = new string('a', 256);
        var msg = new NetIDMessage { playerID = id };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new NetIDMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal(id, back.playerID);
    }

    [Fact]
    public void NetIDMessage_IdLongerThan256Chars_ReadsAsEmpty()
    {
        var msg = new NetIDMessage { playerID = new string('b', 300) };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new NetIDMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal(string.Empty, back.playerID);
    }

    [Fact]
    public void NetIDMessage_SerializeNullOrEmpty_WritesNothing()
    {
        var nullMsg = new NetIDMessage { playerID = null! };
        var writer = new NetDataWriter();
        nullMsg.Serialize(writer);
        Assert.Equal(0, writer.Length);

        var emptyMsg = new NetIDMessage { playerID = "" };
        emptyMsg.Serialize(writer);
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void NetIDMessage_EmptyReader_DoesNotThrow()
    {
        var back = new NetIDMessage();
        back.Deserialize(EmptyReader());
        Assert.Null(back.playerID);
    }

    // ── OwnershipTransferMessage ────────────────────────────────────────────

    [Fact]
    public void OwnershipTransferMessage_RoundTrips()
    {
        var msg = new OwnershipTransferMessage
        {
            playerIdMessage = new PlayerIdMessage { playerID = ushort.MaxValue },
            ownershipID = "владелец-Ω/pickup_01",
        };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new OwnershipTransferMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal(ushort.MaxValue, back.playerIdMessage.playerID);
        Assert.Equal("владелец-Ω/pickup_01", back.ownershipID);
    }

    [Fact]
    public void OwnershipTransferMessage_OwnershipIdOver256Chars_ReadsAsEmpty()
    {
        var msg = new OwnershipTransferMessage
        {
            playerIdMessage = new PlayerIdMessage { playerID = 3 },
            ownershipID = new string('o', 300),
        };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new OwnershipTransferMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal((ushort)3, back.playerIdMessage.playerID);
        Assert.Equal(string.Empty, back.ownershipID);
    }

    // ── PlayerIdMessage ─────────────────────────────────────────────────────

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)1)]
    [InlineData((ushort)255)]
    [InlineData((ushort)256)]
    [InlineData(ushort.MaxValue)]
    public void PlayerIdMessage_RoundTripsUShort(ushort id)
    {
        var msg = new PlayerIdMessage { playerID = id };
        var writer = new NetDataWriter();
        msg.Serialize(writer);
        Assert.Equal(2, writer.Length);

        var back = new PlayerIdMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal(id, back.playerID);
    }

    [Fact]
    public void PlayerIdMessage_LargeIdFlagControlsWireWidth()
    {
        var small = new PlayerIdMessage { playerID = 200 };
        var byteWriter = new NetDataWriter();
        small.Serialize(byteWriter, false);
        Assert.Equal(1, byteWriter.Length);
        var backSmall = new PlayerIdMessage();
        backSmall.Deserialize(ReaderFor(byteWriter), false);
        Assert.Equal((ushort)200, backSmall.playerID);

        var large = new PlayerIdMessage { playerID = ushort.MaxValue };
        var ushortWriter = new NetDataWriter();
        large.Serialize(ushortWriter, true);
        Assert.Equal(2, ushortWriter.Length);
        var backLarge = new PlayerIdMessage();
        backLarge.Deserialize(ReaderFor(ushortWriter), true);
        Assert.Equal(ushort.MaxValue, backLarge.playerID);
    }

    // ── ReadyMessage / ServerReadyMessage ───────────────────────────────────

    private static ReadyMessage MakeReadyMessage(BitQuality quality, int seed)
    {
        int payloadSize = BasisAvatarBitPacking.ConvertToSize(quality);
        return new ReadyMessage
        {
            playerMetaDataMessage = new ClientMetaDataMessage
            {
                playerUUID = "uuid-πλ-9000",
                playerDisplayName = "Réady Player 一",
                playerPlatform = "Desktop",
            },
            clientAvatarChangeMessage = new ClientAvatarChangeMessage
            {
                loadMode = 1,
                byteArray = SeededBytes(48, seed + 1),
                LocalAvatarIndex = 250,
            },
            localAvatarSyncMessage = new LocalAvatarSyncMessage
            {
                DataQualityLevel = (byte)quality,
                array = SeededBytes(payloadSize, seed),
            },
        };
    }

    [Theory]
    [InlineData(BitQuality.VeryLow)]
    [InlineData(BitQuality.Low)]
    [InlineData(BitQuality.Medium)]
    [InlineData(BitQuality.High)]
    public void ReadyMessage_DeepRoundTripsAcrossAllQualities(BitQuality quality)
    {
        ReadyMessage msg = MakeReadyMessage(quality, 200 + (int)quality);
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new ReadyMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.True(back.WasDeserializedCorrectly());
        Assert.Equal(msg.playerMetaDataMessage.playerUUID, back.playerMetaDataMessage.playerUUID);
        Assert.Equal(msg.playerMetaDataMessage.playerDisplayName, back.playerMetaDataMessage.playerDisplayName);
        Assert.Equal(msg.playerMetaDataMessage.playerPlatform, back.playerMetaDataMessage.playerPlatform);
        Assert.Equal((byte)1, back.clientAvatarChangeMessage.loadMode);
        Assert.Equal(msg.clientAvatarChangeMessage.byteArray, back.clientAvatarChangeMessage.byteArray);
        Assert.Equal((byte)250, back.clientAvatarChangeMessage.LocalAvatarIndex);
        Assert.Equal((byte)quality, back.localAvatarSyncMessage.DataQualityLevel);
        Assert.Equal(msg.localAvatarSyncMessage.array, back.localAvatarSyncMessage.array);
        Assert.Null(back.localAvatarSyncMessage.AdditionalAvatarDatas);
    }

    [Fact]
    public void ReadyMessage_NullAvatarChangeBytes_FailsWasDeserializedCorrectly()
    {
        ReadyMessage msg = MakeReadyMessage(BitQuality.High, 210);
        msg.clientAvatarChangeMessage.byteArray = null!;
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new ReadyMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Null(back.clientAvatarChangeMessage.byteArray);
        Assert.False(back.WasDeserializedCorrectly());
    }

    [Fact]
    public void ReadyMessage_DoubleRoundTripIsByteIdentical()
    {
        ReadyMessage msg = MakeReadyMessage(BitQuality.High, 220);
        var first = new NetDataWriter();
        msg.Serialize(first);

        var back = new ReadyMessage();
        back.Deserialize(ReaderFor(first));
        var second = new NetDataWriter();
        back.Serialize(second);

        Assert.Equal(first.CopyData(), second.CopyData());
    }

    [Fact]
    public void ServerReadyMessage_DeepRoundTripPreservesEverything()
    {
        var msg = new ServerReadyMessage
        {
            playerIdMessage = new PlayerIdMessage { playerID = 4242 },
            localReadyMessage = MakeReadyMessage(BitQuality.Medium, 230),
        };
        var first = new NetDataWriter();
        msg.Serialize(first);

        var back = new ServerReadyMessage();
        back.Deserialize(ReaderFor(first));
        Assert.Equal((ushort)4242, back.playerIdMessage.playerID);
        Assert.True(back.localReadyMessage.WasDeserializedCorrectly());
        Assert.Equal(msg.localReadyMessage.playerMetaDataMessage.playerUUID,
            back.localReadyMessage.playerMetaDataMessage.playerUUID);
        Assert.Equal(msg.localReadyMessage.clientAvatarChangeMessage.byteArray,
            back.localReadyMessage.clientAvatarChangeMessage.byteArray);
        Assert.Equal(msg.localReadyMessage.localAvatarSyncMessage.array,
            back.localReadyMessage.localAvatarSyncMessage.array);
        Assert.Equal((byte)BitQuality.Medium, back.localReadyMessage.localAvatarSyncMessage.DataQualityLevel);

        var second = new NetDataWriter();
        back.Serialize(second);
        Assert.Equal(first.CopyData(), second.CopyData());
    }

    // ── LocalLoadResource / PreloadReadyMessage / SpawnPreloadedMessage ─────

    [Fact]
    public void LocalLoadResource_GameObjectMode_RoundTripsAllFields()
    {
        var msg = new LocalLoadResource
        {
            Mode = 0,
            LoadedNetID = "net-α",
            UnlockPassword = "pässwörd",
            CombinedURL = "https://example.com/bundle#雪",
            UUIDOfCreator = "creator-9",
            IsAdminLocked = true,
            Persist = true,
            Static = true,
            StaticAdminLocked = true,
            ModifyScale = true,
            LoadStrategy = 3,
            PositionX = 1.5f,
            PositionY = -2.25f,
            PositionZ = 3.125f,
            QuaternionX = -0.5f,
            QuaternionY = 0.25f,
            QuaternionZ = -0.125f,
            QuaternionW = 0.875f,
            ScaleX = 2f,
            ScaleY = 0.5f,
            ScaleZ = 4f,
        };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new LocalLoadResource();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal((byte)0, back.Mode);
        Assert.Equal("net-α", back.LoadedNetID);
        Assert.Equal("pässwörd", back.UnlockPassword);
        Assert.Equal("https://example.com/bundle#雪", back.CombinedURL);
        Assert.Equal("creator-9", back.UUIDOfCreator);
        Assert.True(back.IsAdminLocked);
        Assert.True(back.Persist);
        Assert.True(back.Static);
        Assert.True(back.StaticAdminLocked);
        Assert.True(back.ModifyScale);
        Assert.Equal((byte)3, back.LoadStrategy);
        Assert.Equal(1.5f, back.PositionX);
        Assert.Equal(-2.25f, back.PositionY);
        Assert.Equal(3.125f, back.PositionZ);
        Assert.Equal(-0.5f, back.QuaternionX);
        Assert.Equal(0.25f, back.QuaternionY);
        Assert.Equal(-0.125f, back.QuaternionZ);
        Assert.Equal(0.875f, back.QuaternionW);
        Assert.Equal(2f, back.ScaleX);
        Assert.Equal(0.5f, back.ScaleY);
        Assert.Equal(4f, back.ScaleZ);
    }

    [Fact]
    public void LocalLoadResource_SceneMode_OmitsTransform()
    {
        var msg = new LocalLoadResource
        {
            Mode = 1,
            LoadedNetID = "scene-1",
            UnlockPassword = "",
            CombinedURL = "https://example.com/world",
            UUIDOfCreator = "creator",
            IsAdminLocked = false,
            Persist = true,
            Static = false,
            StaticAdminLocked = false,
            ModifyScale = false,
            LoadStrategy = 2,
            PositionX = 123f,
            ScaleZ = 456f,
        };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new LocalLoadResource();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal((byte)1, back.Mode);
        Assert.Equal("scene-1", back.LoadedNetID);
        Assert.Equal(string.Empty, back.UnlockPassword);
        Assert.Equal("https://example.com/world", back.CombinedURL);
        Assert.True(back.Persist);
        Assert.Equal((byte)2, back.LoadStrategy);
        Assert.Equal(0f, back.PositionX);
        Assert.Equal(0f, back.QuaternionW);
        Assert.Equal(0f, back.ScaleZ);
    }

    [Fact]
    public void LocalLoadResource_DoubleRoundTripIsByteIdentical()
    {
        var msg = new LocalLoadResource
        {
            Mode = 0,
            LoadedNetID = "net-β",
            UnlockPassword = "pw",
            CombinedURL = "https://a/b",
            UUIDOfCreator = "c",
            IsAdminLocked = true,
            Persist = false,
            Static = true,
            StaticAdminLocked = false,
            ModifyScale = true,
            LoadStrategy = 0,
            PositionX = 1f,
            PositionY = 2f,
            PositionZ = 3f,
            QuaternionX = 4f,
            QuaternionY = 5f,
            QuaternionZ = 6f,
            QuaternionW = 7f,
            ScaleX = 8f,
            ScaleY = 9f,
            ScaleZ = 10f,
        };
        var first = new NetDataWriter();
        msg.Serialize(first);

        var back = new LocalLoadResource();
        back.Deserialize(ReaderFor(first));
        var second = new NetDataWriter();
        back.Serialize(second);

        Assert.Equal(first.CopyData(), second.CopyData());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PreloadReadyMessage_RoundTrips(bool isReady)
    {
        var msg = new PreloadReadyMessage { LoadedNetID = "preload-µ", IsReady = isReady };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new PreloadReadyMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal("preload-µ", back.LoadedNetID);
        Assert.Equal(isReady, back.IsReady);
    }

    [Fact]
    public void SpawnPreloadedMessage_RoundTrips()
    {
        var msg = new SpawnPreloadedMessage { LoadedNetID = "spawn-λ-01" };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new SpawnPreloadedMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal("spawn-λ-01", back.LoadedNetID);
    }

    // ── ServerChatMessage ───────────────────────────────────────────────────

    [Fact]
    public void ServerChatMessage_DeepRoundTrip()
    {
        byte[] payload = Encoding.UTF8.GetBytes("hello from the sérver 🌐");
        var msg = new ServerChatMessage
        {
            playerIdMessage = new PlayerIdMessage { playerID = ushort.MaxValue },
            chatMessage = new ChatMessage { payload = payload, playNotificationSound = false },
        };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new ServerChatMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal(ushort.MaxValue, back.playerIdMessage.playerID);
        Assert.Equal(payload, back.chatMessage.payload);
        Assert.Equal((ushort)payload.Length, back.chatMessage.payloadSize);
        Assert.False(back.chatMessage.playNotificationSound);
    }

    // ── ServerLibraryItem / ServerLibraryMessage ────────────────────────────

    [Fact]
    public void ServerLibraryItem_RoundTrips()
    {
        var item = new ServerLibraryItem { Mode = 2, Url = "https://cdn/prop.bee#日本", Password = "s3cret-ß" };
        var writer = new NetDataWriter();
        item.Serialize(writer);

        var back = new ServerLibraryItem();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal((byte)2, back.Mode);
        Assert.Equal("https://cdn/prop.bee#日本", back.Url);
        Assert.Equal("s3cret-ß", back.Password);
    }

    [Fact]
    public void ServerLibraryItem_NullStrings_SerializeAsEmpty()
    {
        var item = new ServerLibraryItem { Mode = 0, Url = null!, Password = null! };
        var writer = new NetDataWriter();
        item.Serialize(writer);

        var back = new ServerLibraryItem();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal(string.Empty, back.Url);
        Assert.Equal(string.Empty, back.Password);
    }

    [Fact]
    public void ServerLibraryMessage_RoundTripsMultipleItems()
    {
        var msg = new ServerLibraryMessage
        {
            Items = new[]
            {
                new ServerLibraryItem { Mode = 0, Url = "https://a/avatar", Password = "" },
                new ServerLibraryItem { Mode = 1, Url = "https://b/world", Password = "秘密" },
                new ServerLibraryItem { Mode = 2, Url = "", Password = "p2" },
            },
        };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new ServerLibraryMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal(3, back.Items.Length);
        Assert.Equal((byte)0, back.Items[0].Mode);
        Assert.Equal("https://a/avatar", back.Items[0].Url);
        Assert.Equal(string.Empty, back.Items[0].Password);
        Assert.Equal((byte)1, back.Items[1].Mode);
        Assert.Equal("https://b/world", back.Items[1].Url);
        Assert.Equal("秘密", back.Items[1].Password);
        Assert.Equal((byte)2, back.Items[2].Mode);
        Assert.Equal(string.Empty, back.Items[2].Url);
        Assert.Equal("p2", back.Items[2].Password);
    }

    [Fact]
    public void ServerLibraryMessage_NullItems_RoundTripsToEmptyArray()
    {
        var msg = new ServerLibraryMessage { Items = null! };
        var writer = new NetDataWriter();
        msg.Serialize(writer);
        Assert.Equal(2, writer.Length);

        var back = new ServerLibraryMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.NotNull(back.Items);
        Assert.Empty(back.Items);
    }

    // ── ServerStatisticMessage ──────────────────────────────────────────────

    [Fact]
    public void ServerStatisticMessage_RoundTripsRawBytes()
    {
        var msg = new ServerStatisticMessage { Data = SeededBytes(33, 108) };
        var writer = new NetDataWriter();
        msg.Serialize(writer);
        Assert.Equal(33, writer.Length);

        var back = new ServerStatisticMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal(msg.Data, back.Data);
    }

    [Fact]
    public void ServerStatisticMessage_EmptyData_RoundTrips()
    {
        var msg = new ServerStatisticMessage { Data = Array.Empty<byte>() };
        var writer = new NetDataWriter();
        msg.Serialize(writer);
        Assert.Equal(0, writer.Length);

        var back = new ServerStatisticMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.NotNull(back.Data);
        Assert.Empty(back.Data);
    }

    // ── ServerNetIDMessage / ServerUniqueIDMessages ─────────────────────────

    [Fact]
    public void ServerNetIDMessage_RoundTrips()
    {
        var msg = new ServerNetIDMessage
        {
            NetIDMessage = new NetIDMessage { playerID = "object-Ω" },
            UshortUniqueIDMessage = new UshortUniqueIDMessage { UniqueIDUshort = ushort.MaxValue },
        };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new ServerNetIDMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal("object-Ω", back.NetIDMessage.playerID);
        Assert.Equal(ushort.MaxValue, back.UshortUniqueIDMessage.UniqueIDUshort);
    }

    [Fact]
    public void ServerUniqueIDMessages_RoundTripsEntries()
    {
        var msg = new ServerUniqueIDMessages
        {
            Messages = new[]
            {
                new ServerNetIDMessage
                {
                    NetIDMessage = new NetIDMessage { playerID = "alpha" },
                    UshortUniqueIDMessage = new UshortUniqueIDMessage { UniqueIDUshort = 1 },
                },
                new ServerNetIDMessage
                {
                    NetIDMessage = new NetIDMessage { playerID = "βeta" },
                    UshortUniqueIDMessage = new UshortUniqueIDMessage { UniqueIDUshort = 32768 },
                },
                new ServerNetIDMessage
                {
                    NetIDMessage = new NetIDMessage { playerID = "gamma-γ" },
                    UshortUniqueIDMessage = new UshortUniqueIDMessage { UniqueIDUshort = ushort.MaxValue },
                },
            },
        };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new ServerUniqueIDMessages();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal((ushort)3, back.MessageCount);
        Assert.Equal(3, back.Messages.Length);
        Assert.Equal("alpha", back.Messages[0].NetIDMessage.playerID);
        Assert.Equal((ushort)1, back.Messages[0].UshortUniqueIDMessage.UniqueIDUshort);
        Assert.Equal("βeta", back.Messages[1].NetIDMessage.playerID);
        Assert.Equal((ushort)32768, back.Messages[1].UshortUniqueIDMessage.UniqueIDUshort);
        Assert.Equal("gamma-γ", back.Messages[2].NetIDMessage.playerID);
        Assert.Equal(ushort.MaxValue, back.Messages[2].UshortUniqueIDMessage.UniqueIDUshort);
    }

    [Fact]
    public void ServerUniqueIDMessages_EmptyArray_RoundTrips()
    {
        var msg = new ServerUniqueIDMessages { Messages = Array.Empty<ServerNetIDMessage>() };
        var writer = new NetDataWriter();
        msg.Serialize(writer);
        Assert.Equal(2, writer.Length);

        var back = new ServerUniqueIDMessages();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal((ushort)0, back.MessageCount);
        Assert.NotNull(back.Messages);
        Assert.Empty(back.Messages);
    }

    [Fact]
    public void ServerUniqueIDMessages_SerializeNullMessages_WritesNothing()
    {
        var msg = new ServerUniqueIDMessages { Messages = null! };
        var writer = new NetDataWriter();
        msg.Serialize(writer);
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void ServerUniqueIDMessages_TruncatedReader_LeavesMessagesNullWithoutThrowing()
    {
        var fromEmpty = new ServerUniqueIDMessages();
        fromEmpty.Deserialize(EmptyReader());
        Assert.Null(fromEmpty.Messages);

        var fromOneByte = new ServerUniqueIDMessages();
        fromOneByte.Deserialize(new NetDataReader(new byte[] { 0x7F }));
        Assert.Null(fromOneByte.Messages);
    }

    // ── UnLoadResource ──────────────────────────────────────────────────────

    [Fact]
    public void UnLoadResource_RoundTripsAndReportsSuccess()
    {
        var msg = new UnLoadResource { Mode = 1, LoadedNetID = "unload-ζ" };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new UnLoadResource();
        Assert.True(back.Deserialize(ReaderFor(writer)));
        Assert.Equal((byte)1, back.Mode);
        Assert.Equal("unload-ζ", back.LoadedNetID);
    }

    [Fact]
    public void UnLoadResource_EmptyReader_ReturnsFalse()
    {
        var back = new UnLoadResource();
        Assert.False(back.Deserialize(EmptyReader()));
    }

    [Fact]
    public void UnLoadResource_TruncatedString_ReturnsFalse()
    {
        var modeOnly = new NetDataWriter();
        modeOnly.Put((byte)1);
        var backModeOnly = new UnLoadResource();
        Assert.False(backModeOnly.Deserialize(ReaderFor(modeOnly)));

        var truncated = new NetDataWriter();
        truncated.Put((byte)1);
        truncated.Put((ushort)50);
        var backTruncated = new UnLoadResource();
        Assert.False(backTruncated.Deserialize(ReaderFor(truncated)));
        Assert.Null(backTruncated.LoadedNetID);
    }

    // ── UshortUniqueIDMessage ───────────────────────────────────────────────

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)1)]
    [InlineData(ushort.MaxValue)]
    public void UshortUniqueIDMessage_RoundTrips(ushort id)
    {
        var msg = new UshortUniqueIDMessage { UniqueIDUshort = id };
        var writer = new NetDataWriter();
        msg.Serialize(writer);
        Assert.Equal(2, writer.Length);

        var back = new UshortUniqueIDMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal(id, back.UniqueIDUshort);
    }

    [Fact]
    public void UshortUniqueIDMessage_EmptyReader_DoesNotThrow()
    {
        var back = new UshortUniqueIDMessage();
        back.Deserialize(EmptyReader());
        Assert.Equal((ushort)0, back.UniqueIDUshort);
    }

    // ── DatabasePrimativeMessage / DataBaseRequest ──────────────────────────

    [Fact]
    public void DatabasePrimativeMessage_RoundTripsEveryValueType()
    {
        var payload = new ConcurrentDictionary<string, object>();
        payload["s"] = "héllo 世界";
        payload["i"] = 42;
        payload["b"] = true;
        payload["f"] = 1.5f;
        payload["d"] = 2.25;
        payload["l"] = 123456789012345L;
        payload["ul"] = ulong.MaxValue;
        payload["sh"] = (short)-1234;
        payload["us"] = ushort.MaxValue;
        payload["by"] = (byte)255;
        payload["sb"] = (sbyte)-128;
        payload["ch"] = 'Ω';
        payload["de"] = 1234567.8912m;
        payload["nul"] = null!;

        var msg = new DatabasePrimativeMessage { Name = "db-Ψ", jsonPayload = payload };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new DatabasePrimativeMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal("db-Ψ", back.Name);
        Assert.Equal(14, back.jsonPayload.Count);
        Assert.Equal("héllo 世界", Assert.IsType<string>(back.jsonPayload["s"]));
        Assert.Equal(42, Assert.IsType<int>(back.jsonPayload["i"]));
        Assert.True(Assert.IsType<bool>(back.jsonPayload["b"]));
        Assert.Equal(1.5f, Assert.IsType<float>(back.jsonPayload["f"]));
        Assert.Equal(2.25, Assert.IsType<double>(back.jsonPayload["d"]));
        Assert.Equal(123456789012345L, Assert.IsType<long>(back.jsonPayload["l"]));
        Assert.Equal(ulong.MaxValue, Assert.IsType<ulong>(back.jsonPayload["ul"]));
        Assert.Equal((short)-1234, Assert.IsType<short>(back.jsonPayload["sh"]));
        Assert.Equal(ushort.MaxValue, Assert.IsType<ushort>(back.jsonPayload["us"]));
        Assert.Equal((byte)255, Assert.IsType<byte>(back.jsonPayload["by"]));
        Assert.Equal((sbyte)-128, Assert.IsType<sbyte>(back.jsonPayload["sb"]));
        Assert.Equal('Ω', Assert.IsType<char>(back.jsonPayload["ch"]));
        Assert.Equal(1234567.8912m, Assert.IsType<decimal>(back.jsonPayload["de"]));
        Assert.Null(back.jsonPayload["nul"]);
    }

    [Fact]
    public void DatabasePrimativeMessage_NullPayload_RoundTripsToEmptyDictionary()
    {
        var msg = new DatabasePrimativeMessage { Name = "empty-db", jsonPayload = null! };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new DatabasePrimativeMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal("empty-db", back.Name);
        Assert.NotNull(back.jsonPayload);
        Assert.Empty(back.jsonPayload);
    }

    [Theory]
    [InlineData(1_000_000)]
    [InlineData(-5)]
    public void DatabasePrimativeMessage_BadEntryCount_ThrowsArgumentException(int declaredCount)
    {
        var writer = new NetDataWriter();
        writer.Put("db");
        writer.Put(declaredCount);

        var back = new DatabasePrimativeMessage();
        NetDataReader reader = ReaderFor(writer);
        Assert.Throws<ArgumentException>(() => back.Deserialize(reader));
    }

    [Fact]
    public void DataBaseRequest_RoundTrips()
    {
        var msg = new DataBaseRequest { DatabaseID = "request-φ" };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new DataBaseRequest();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal("request-φ", back.DatabaseID);
    }

    [Fact]
    public void DataBaseRequest_NullId_RoundTripsAsEmpty()
    {
        var msg = new DataBaseRequest { DatabaseID = null! };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new DataBaseRequest();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal(string.Empty, back.DatabaseID);
    }

    // ── ContentShare messages ───────────────────────────────────────────────

    [Theory]
    [InlineData(ContentShareType.Avatar)]
    [InlineData(ContentShareType.Prop)]
    [InlineData(ContentShareType.World)]
    [InlineData(ContentShareType.Server)]
    public void ContentShareMessage_RoundTripsAllFields(ContentShareType type)
    {
        var msg = new ContentShareMessage
        {
            SphereNetID = "sphere-β-42",
            ContentURL = "https://example.com/bundle?v=1&q=日本",
            UnlockPassword = "pässword",
            ContentType = type,
            PositionX = -12.5f,
            PositionY = 0.03125f,
            PositionZ = 4096f,
        };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new ContentShareMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal("sphere-β-42", back.SphereNetID);
        Assert.Equal("https://example.com/bundle?v=1&q=日本", back.ContentURL);
        Assert.Equal("pässword", back.UnlockPassword);
        Assert.Equal(type, back.ContentType);
        Assert.Equal(-12.5f, back.PositionX);
        Assert.Equal(0.03125f, back.PositionY);
        Assert.Equal(4096f, back.PositionZ);
    }

    [Fact]
    public void ContentShareMessage_NullAndEmptyStrings_RoundTripAsEmpty()
    {
        var msg = new ContentShareMessage
        {
            SphereNetID = null!,
            ContentURL = "https://example.com",
            UnlockPassword = "",
            ContentType = ContentShareType.Avatar,
        };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new ContentShareMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal(string.Empty, back.SphereNetID);
        Assert.Equal("https://example.com", back.ContentURL);
        Assert.Equal(string.Empty, back.UnlockPassword);
    }

    [Fact]
    public void ServerContentShareMessage_DeepRoundTrip()
    {
        var msg = new ServerContentShareMessage
        {
            playerIdMessage = new PlayerIdMessage { playerID = ushort.MaxValue },
            SharerUUID = "sharer-uuid-δ",
            SharerDisplayName = "Sharer 分享",
            contentShareMessage = new ContentShareMessage
            {
                SphereNetID = "sphere-1",
                ContentURL = "https://cdn/av",
                UnlockPassword = "pw",
                ContentType = ContentShareType.World,
                PositionX = 1f,
                PositionY = 2f,
                PositionZ = 3f,
            },
        };
        var first = new NetDataWriter();
        msg.Serialize(first);

        var back = new ServerContentShareMessage();
        back.Deserialize(ReaderFor(first));
        Assert.Equal(ushort.MaxValue, back.playerIdMessage.playerID);
        Assert.Equal("sharer-uuid-δ", back.SharerUUID);
        Assert.Equal("Sharer 分享", back.SharerDisplayName);
        Assert.Equal("sphere-1", back.contentShareMessage.SphereNetID);
        Assert.Equal("https://cdn/av", back.contentShareMessage.ContentURL);
        Assert.Equal("pw", back.contentShareMessage.UnlockPassword);
        Assert.Equal(ContentShareType.World, back.contentShareMessage.ContentType);
        Assert.Equal(1f, back.contentShareMessage.PositionX);
        Assert.Equal(2f, back.contentShareMessage.PositionY);
        Assert.Equal(3f, back.contentShareMessage.PositionZ);

        var second = new NetDataWriter();
        back.Serialize(second);
        Assert.Equal(first.CopyData(), second.CopyData());
    }

    [Fact]
    public void ServerContentShareMessage_NullSharerIdentity_SerializesAsEmpty()
    {
        var msg = new ServerContentShareMessage
        {
            playerIdMessage = new PlayerIdMessage { playerID = 7 },
            SharerUUID = null!,
            SharerDisplayName = null!,
            contentShareMessage = new ContentShareMessage
            {
                SphereNetID = "s",
                ContentURL = "u",
                UnlockPassword = "p",
                ContentType = ContentShareType.Prop,
            },
        };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new ServerContentShareMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal((ushort)7, back.playerIdMessage.playerID);
        Assert.Equal(string.Empty, back.SharerUUID);
        Assert.Equal(string.Empty, back.SharerDisplayName);
        Assert.Equal(ContentShareType.Prop, back.contentShareMessage.ContentType);
    }

    [Fact]
    public void ContentShareCleanupMessage_RoundTrips()
    {
        var msg = new ContentShareCleanupMessage { SphereNetID = "cleanup-ω" };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new ContentShareCleanupMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal("cleanup-ω", back.SphereNetID);
    }

    [Fact]
    public void ServerContentShareCleanupMessage_DeepRoundTrip()
    {
        var msg = new ServerContentShareCleanupMessage
        {
            playerIdMessage = new PlayerIdMessage { playerID = 888 },
            contentShareCleanupMessage = new ContentShareCleanupMessage { SphereNetID = "sphere-χ" },
        };
        var writer = new NetDataWriter();
        msg.Serialize(writer);

        var back = new ServerContentShareCleanupMessage();
        back.Deserialize(ReaderFor(writer));
        Assert.Equal((ushort)888, back.playerIdMessage.playerID);
        Assert.Equal("sphere-χ", back.contentShareCleanupMessage.SphereNetID);
    }
}
