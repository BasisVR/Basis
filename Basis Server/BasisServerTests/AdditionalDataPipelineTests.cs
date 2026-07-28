using Basis.Network.Core;
using Basis.Network.Core.Compression;
using BasisNetworkServer.BasisNetworkingReductionSystem;
using Xunit;
using BitQuality = Basis.Network.Core.Compression.BasisAvatarBitPacking.BitQuality;
using S = BasisServerTests.DeltaTestSupport;
using static SerializableBasis;

namespace BasisServerTests;

/// <summary>
/// End-to-end AdditionalAvatarData (face tracking / behaviour params) transport:
/// sender wire bytes → server ingest → reduction-system pre-serialization → receiver parse.
/// Every hop uses the REAL serializers (LocalAvatarSyncMessage, ServerSideSyncPlayerMessage,
/// BasisAvatarDeltaCompression, PreSerializeFrame/Keyframe/Delta) over actual byte buffers,
/// so any wire-layout drift that would silently drop face data fails here instead of in-game.
/// </summary>
[Collection("Basis reduction statics")]
public class AdditionalDataPipelineTests : IDisposable
{
    private readonly bool _savedStrip = BasisServerReductionSystemEvents.StripAdditionalDataAtLowQuality;
    public void Dispose() => BasisServerReductionSystemEvents.StripAdditionalDataAtLowQuality = _savedStrip;

    private static readonly byte[] FaceBytes = { 16, 3, 200, 150, 100, 50, 25, 12 }; // HVR high-frequency variables shape: [packetId=16][timing][values...]
    private const byte FaceMessageIndex = 1;   // HVR VariableNetworkingCarrier slot
    private const byte LinkedIndex = 5;

    private static AdditionalAvatarData[] MakeAdditional() => new[]
    {
        new AdditionalAvatarData { messageIndex = FaceMessageIndex, array = (byte[])FaceBytes.Clone() },
    };

    private static void AssertFaceSurvived(LocalAvatarSyncMessage msg)
    {
        Assert.True(msg.AdditionalAvatarDataSize > 0, "additional data was dropped");
        Assert.NotNull(msg.AdditionalAvatarDatas);
        Assert.Equal(1, (int)msg.AdditionalAvatarDataSize);
        Assert.Equal(FaceMessageIndex, msg.AdditionalAvatarDatas[0].messageIndex);
        Assert.Equal(FaceBytes, msg.AdditionalAvatarDatas[0].array);
        Assert.Equal(LinkedIndex, msg.LinkedAvatarIndex);
    }

    // ── Sender-side framing, exactly as BasisNetworkAvatarCompressor.Compress writes it ──

    private static NetDataWriter WriteUplinkKeyframe(byte seq, byte[] payload, AdditionalAvatarData[] additional)
    {
        var lasm = new LocalAvatarSyncMessage
        {
            array = payload,
            AdditionalAvatarDatas = additional,
            LinkedAvatarIndex = LinkedIndex,
        };
        var w = new NetDataWriter();
        w.Put(seq);
        lasm.SerializeForChannel(w, BitQuality.High);
        return w;
    }

    private static NetDataWriter WriteUplinkDelta(byte seq, byte baseSeq, byte[] baseline, byte[] current, AdditionalAvatarData[] additional)
    {
        var scratch = new byte[BasisAvatarDeltaCompression.MaxDeltaSize(BitQuality.High)];
        int deltaLen = BasisAvatarDeltaCompression.BuildDelta(baseline, current, BitQuality.High, scratch, 0);
        Assert.True(deltaLen > 0 && deltaLen < current.Length, "test expects a genuine delta, not a promotion");

        bool hasAdditional = additional != null && additional.Length > 0;
        var lasm = new LocalAvatarSyncMessage
        {
            array = current,
            AdditionalAvatarDatas = additional,
            LinkedAvatarIndex = LinkedIndex,
        };
        var w = new NetDataWriter();
        w.Put(BasisNetworkCommons.BuildDeltaHeader(3, hasAdditional, largeId: false));
        w.Put(seq);
        w.Put(baseSeq);
        w.Put(scratch, 0, deltaLen);
        if (hasAdditional) lasm.SerializeAdditionalOnly(w);
        return w;
    }

    // ── Server ingest, mirroring HandleAvatarMovement / HandleDeltaChannelInbound ──

    private static LocalAvatarSyncMessage IngestKeyframe(NetDataWriter clientWire, bool channelSaysAdditional, out byte seq)
    {
        var reader = new NetDataReader(clientWire.CopyData());
        Assert.True(reader.TryGetByte(out seq));
        var msg = new LocalAvatarSyncMessage();
        msg.Deserialize(reader, 3, channelSaysAdditional);
        Assert.Equal(0, reader.AvailableBytes); // whole frame consumed — no trailing garbage
        return msg;
    }

    private static LocalAvatarSyncMessage IngestDelta(NetDataWriter clientWire, byte[] serverBaseline, byte expectedBaseSeq, out byte seq)
    {
        var reader = new NetDataReader(clientWire.CopyData());
        Assert.True(reader.TryGetByte(out byte header));
        Assert.False(BasisNetworkCommons.IsDeltaControlHeader(header));
        Assert.Equal(3, BasisNetworkCommons.DeltaHeaderQuality(header));
        bool hasAdditional = BasisNetworkCommons.DeltaHeaderHasAdditionalData(header);
        Assert.True(reader.TryGetByte(out seq));
        Assert.True(reader.TryGetByte(out byte baseSeq));
        Assert.Equal(expectedBaseSeq, baseSeq);

        int bodyLen = BasisAvatarDeltaCompression.DeltaBodyLength(reader.RawData, reader.Position, reader.AvailableBytes, BitQuality.High);
        Assert.True(bodyLen > 0 && bodyLen <= reader.AvailableBytes, "delta body length probe failed");

        var msg = new LocalAvatarSyncMessage
        {
            array = new byte[BasisAvatarDeltaCompression.PayloadSize(BitQuality.High)],
            DataQualityLevel = 3,
        };
        Assert.True(BasisAvatarDeltaCompression.TryApplyDelta(serverBaseline, reader.RawData, reader.Position, bodyLen, BitQuality.High, msg.array));
        reader.SkipBytes(bodyLen);

        msg.AdditionalAvatarDataSize = 0;
        msg.AdditionalAvatarDatas = null;
        if (hasAdditional) msg.DeserializeAdditionalData(reader);
        Assert.Equal(0, reader.AvailableBytes); // additional section consumed exactly
        return msg;
    }

    // ── Server state builder, mirroring ProcessMessage ──

    private static PlayerState BuildState(LocalAvatarSyncMessage inbound, ushort playerId, byte outboundSeq)
    {
        int expected = BasisAvatarBitPacking.ConvertToSize(BitQuality.High);
        var owned = new byte[expected];
        Buffer.BlockCopy(inbound.array, 0, owned, 0, expected);
        var high = new LocalAvatarSyncMessage
        {
            DataQualityLevel = 3,
            AdditionalAvatarDatas = inbound.AdditionalAvatarDatas,
            AdditionalAvatarDataSize = inbound.AdditionalAvatarDataSize,
            LinkedAvatarIndex = inbound.LinkedAvatarIndex,
            array = owned,
        };
        var state = new PlayerState
        {
            SyncMessage = new ServerSideSyncPlayerMessage { playerIdMessage = new PlayerIdMessage { playerID = playerId } },
            AvatarHigh = high,
            HighArrayActualSize = expected,
            SmallId = playerId <= byte.MaxValue,
            OutboundSequence = outboundSeq,
            DataGeneration = 1,
        };
        AvatarQualityRepacker.BuildAllLowerFromHighInto(high, ref state.AvatarMedium, ref state.AvatarLow, ref state.AvatarVeryLow);
        BasisServerReductionSystemEvents.PropagateAdditionalData(high, ref state.AvatarMedium, ref state.AvatarLow, ref state.AvatarVeryLow);
        state.HasAdditionalData = high.AdditionalAvatarDatas != null && high.AdditionalAvatarDatas.Length > 0;
        return state;
    }

    private static void ReplaceHigh(PlayerState state, LocalAvatarSyncMessage inbound, byte outboundSeq)
    {
        int expected = BasisAvatarBitPacking.ConvertToSize(BitQuality.High);
        var owned = new byte[expected];
        Buffer.BlockCopy(inbound.array, 0, owned, 0, expected);
        var high = new LocalAvatarSyncMessage
        {
            DataQualityLevel = 3,
            AdditionalAvatarDatas = inbound.AdditionalAvatarDatas,
            AdditionalAvatarDataSize = inbound.AdditionalAvatarDataSize,
            LinkedAvatarIndex = inbound.LinkedAvatarIndex,
            array = owned,
        };
        state.AvatarHigh = high;
        state.HighArrayActualSize = expected;
        state.OutboundSequence = outboundSeq;
        AvatarQualityRepacker.BuildAllLowerFromHighInto(high, ref state.AvatarMedium, ref state.AvatarLow, ref state.AvatarVeryLow);
        BasisServerReductionSystemEvents.PropagateAdditionalData(high, ref state.AvatarMedium, ref state.AvatarLow, ref state.AvatarVeryLow);
        state.HasAdditionalData = high.AdditionalAvatarDatas != null && high.AdditionalAvatarDatas.Length > 0;
    }

    // ── Receiver-side parsing, mirroring BasisNetworkHandleAvatar / BasisNetworkHandleAvatarDelta ──

    private static ServerSideSyncPlayerMessage ParseFanoutKeyframe(byte[] wire, int length, byte channel)
    {
        var reader = new NetDataReader(wire, 0, length);
        var ssm = new ServerSideSyncPlayerMessage();
        byte quality = BasisNetworkCommons.GetQualityFromChannel(channel);
        bool hasAdditional = BasisNetworkCommons.ChannelHasAdditionalData(channel);
        bool largeId = BasisNetworkCommons.IsLargePlayerIdChannel(channel);
        ssm.Deserialize(reader, quality, hasAdditional, largeId);
        Assert.Equal(0, reader.AvailableBytes);
        return ssm;
    }

    private static (ServerSideSyncPlayerMessage ssm, byte quality) ParseFanoutDelta(byte[] wire, int length, byte[] receiverBaseline, byte expectedBaseSeq)
    {
        var reader = new NetDataReader(wire, 0, length);
        Assert.True(reader.TryGetByte(out byte header));
        Assert.False(BasisNetworkCommons.IsDeltaControlHeader(header));
        byte quality = BasisNetworkCommons.DeltaHeaderQuality(header);
        bool hasAdditional = BasisNetworkCommons.DeltaHeaderHasAdditionalData(header);
        bool largeId = BasisNetworkCommons.DeltaHeaderLargeId(header);

        ushort playerId = largeId ? reader.GetUShort() : reader.GetByte();
        Assert.True(reader.TryGetByte(out _)); // interval
        Assert.True(reader.TryGetByte(out byte sequence));
        Assert.True(reader.TryGetByte(out byte baseSeq));
        Assert.Equal(expectedBaseSeq, baseSeq);

        var q = (BitQuality)quality;
        int bodyLen = BasisAvatarDeltaCompression.DeltaBodyLength(reader.RawData, reader.Position, reader.AvailableBytes, q);
        Assert.True(bodyLen > 0 && bodyLen <= reader.AvailableBytes);

        var recon = new byte[BasisAvatarDeltaCompression.PayloadSize(q)];
        Assert.True(BasisAvatarDeltaCompression.TryApplyDelta(receiverBaseline, reader.RawData, reader.Position, bodyLen, q, recon));
        reader.SkipBytes(bodyLen);

        var ssm = new ServerSideSyncPlayerMessage
        {
            playerIdMessage = new PlayerIdMessage { playerID = playerId },
            sequence = sequence,
            avatarSerialization = new LocalAvatarSyncMessage { array = recon, DataQualityLevel = quality },
        };
        if (hasAdditional)
        {
            ssm.avatarSerialization.DeserializeAdditionalData(reader);
        }
        Assert.Equal(0, reader.AvailableBytes);
        return (ssm, quality);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Tests
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UplinkKeyframe_WithFaceData_SurvivesServerIngest()
    {
        var rng = new Random(1001);
        byte[] payload = S.MakeRealisticPayload(BitQuality.High, rng);

        NetDataWriter wire = WriteUplinkKeyframe(seq: 7, payload, MakeAdditional());
        // Client picks the odd (additional) channel; the server derives hasAdditional from it.
        byte channel = BasisNetworkCommons.GetPlayerAvatarChannelForQuality(3, hasAdditionalData: true);
        Assert.True(BasisNetworkCommons.ChannelHasAdditionalData(channel));

        LocalAvatarSyncMessage ingested = IngestKeyframe(wire, channelSaysAdditional: true, out byte seq);
        Assert.Equal(7, seq);
        Assert.Equal(payload, ingested.array);
        AssertFaceSurvived(ingested);
    }

    [Fact]
    public void ReadySnapshot_SelfDescribingPath_RoundTripsFaceData()
    {
        var rng = new Random(1005);
        byte[] payloadA = S.MakeRealisticPayload(BitQuality.High, rng);
        byte[] payloadB = S.MakeRealisticPayload(BitQuality.High, rng);

        var withFace = new LocalAvatarSyncMessage
        {
            array = payloadA,
            AdditionalAvatarDatas = MakeAdditional(),
            LinkedAvatarIndex = LinkedIndex,
        };
        var withoutFace = new LocalAvatarSyncMessage { array = payloadB };

        var w = new NetDataWriter();
        withFace.Serialize(w, BitQuality.High);
        withoutFace.Serialize(w, BitQuality.High);

        var reader = new NetDataReader(w.CopyData());

        var first = new LocalAvatarSyncMessage();
        first.Deserialize(reader);
        Assert.Equal(payloadA, first.array);
        AssertFaceSurvived(first);

        var second = new LocalAvatarSyncMessage();
        second.Deserialize(reader);
        Assert.Equal(payloadB, second.array);
        Assert.Equal(0, (int)second.AdditionalAvatarDataSize);
        Assert.Null(second.AdditionalAvatarDatas);

        Assert.Equal(0, reader.AvailableBytes);
    }

    [Fact]
    public void UplinkDelta_WithFaceData_SurvivesServerIngest()
    {
        var rng = new Random(1002);
        byte[] baseline = S.MakeRealisticPayload(BitQuality.High, rng);
        byte[] current = (byte[])baseline.Clone();
        current[0] ^= 0xFF;
        S.FlipBone(current, BitQuality.High, 12);

        NetDataWriter wire = WriteUplinkDelta(seq: 8, baseSeq: 7, baseline, current, MakeAdditional());
        LocalAvatarSyncMessage ingested = IngestDelta(wire, baseline, expectedBaseSeq: 7, out byte seq);
        Assert.Equal(8, seq);
        Assert.Equal(current, ingested.array);
        AssertFaceSurvived(ingested);
    }

    [Fact]
    public void UplinkDelta_WithoutFaceData_HeaderSaysNone()
    {
        var rng = new Random(1003);
        byte[] baseline = S.MakeRealisticPayload(BitQuality.High, rng);
        byte[] current = (byte[])baseline.Clone();
        S.FlipBone(current, BitQuality.High, 3);

        NetDataWriter wire = WriteUplinkDelta(seq: 9, baseSeq: 7, baseline, current, additional: null);
        LocalAvatarSyncMessage ingested = IngestDelta(wire, baseline, expectedBaseSeq: 7, out _);
        Assert.Equal(current, ingested.array);
        Assert.Equal(0, (int)ingested.AdditionalAvatarDataSize);
        Assert.Null(ingested.AdditionalAvatarDatas);
    }

    [Fact]
    public void FanoutKeyframe_High_CarriesFaceData_EndToEnd()
    {
        var rng = new Random(1004);
        byte[] payload = S.MakeRealisticPayload(BitQuality.High, rng);
        NetDataWriter wire = WriteUplinkKeyframe(seq: 1, payload, MakeAdditional());
        LocalAvatarSyncMessage ingested = IngestKeyframe(wire, channelSaysAdditional: true, out _);

        PlayerState state = BuildState(ingested, playerId: 42, outboundSeq: 1);
        BasisServerReductionSystemEvents.PreSerializeKeyframe(state, 3, state.AvatarHigh, 42);

        Assert.True(state.SerializedKeyframeLength[3] > 0, "keyframe was not serialized");
        Assert.True(state.SerializedHasAdditional[3], "server lost the additional flag at High");
        byte channel = BasisNetworkCommons.GetPlayerAvatarChannelForQuality(3, state.SerializedHasAdditional[3]);

        ServerSideSyncPlayerMessage ssm = ParseFanoutKeyframe(state.SerializedKeyframe[3], state.SerializedKeyframeLength[3], channel);
        Assert.Equal(42, ssm.playerIdMessage.playerID);
        Assert.Equal(payload, ssm.avatarSerialization.array);
        AssertFaceSurvived(ssm.avatarSerialization);
    }

    [Fact]
    public void FanoutKeyframe_Medium_KeepsFaceData_LowTiersStripIt()
    {
        BasisServerReductionSystemEvents.StripAdditionalDataAtLowQuality = true;

        var rng = new Random(1005);
        byte[] payload = S.MakeRealisticPayload(BitQuality.High, rng);
        NetDataWriter wire = WriteUplinkKeyframe(seq: 1, payload, MakeAdditional());
        LocalAvatarSyncMessage ingested = IngestKeyframe(wire, channelSaysAdditional: true, out _);
        PlayerState state = BuildState(ingested, playerId: 42, outboundSeq: 1);

        for (int qi = 0; qi < 4; qi++)
        {
            LocalAvatarSyncMessage msg = qi switch
            {
                0 => state.AvatarVeryLow,
                1 => state.AvatarLow,
                2 => state.AvatarMedium,
                _ => state.AvatarHigh,
            };
            BasisServerReductionSystemEvents.PreSerializeKeyframe(state, qi, msg, 42);
            Assert.True(state.SerializedKeyframeLength[qi] > 0, $"tier {qi} not serialized");

            byte channel = BasisNetworkCommons.GetPlayerAvatarChannelForQuality(qi, state.SerializedHasAdditional[qi]);
            ServerSideSyncPlayerMessage ssm = ParseFanoutKeyframe(state.SerializedKeyframe[qi], state.SerializedKeyframeLength[qi], channel);

            if (qi >= 2)
            {
                AssertFaceSurvived(ssm.avatarSerialization); // High + Medium keep face data
            }
            else
            {
                Assert.False(state.SerializedHasAdditional[qi], $"tier {qi} should strip additional");
                Assert.Equal(0, (int)ssm.avatarSerialization.AdditionalAvatarDataSize);
            }
        }
    }

    [Fact]
    public void FanoutDelta_High_CarriesFaceData_EndToEnd()
    {
        var rng = new Random(1006);

        // Generation 1: keyframe (no face this frame) establishes the baseline.
        byte[] kfPayload = S.MakeRealisticPayload(BitQuality.High, rng);
        NetDataWriter kfWire = WriteUplinkKeyframe(seq: 1, kfPayload, additional: null);
        LocalAvatarSyncMessage kfIngested = IngestKeyframe(kfWire, channelSaysAdditional: false, out _);
        PlayerState state = BuildState(kfIngested, playerId: 42, outboundSeq: 1);

        int payloadSize = BasisAvatarBitPacking.ConvertToSize(BitQuality.High);
        state.KeyframePayload[3] = new byte[payloadSize];
        Buffer.BlockCopy(state.AvatarHigh.array, 0, state.KeyframePayload[3], 0, payloadSize);
        state.KeyframePayloadLength[3] = payloadSize;
        state.KeyframeSequence = state.OutboundSequence; // = 1
        BasisServerReductionSystemEvents.PreSerializeKeyframe(state, 3, state.AvatarHigh, 42);

        // The receiver captured that keyframe (sequence byte embedded in the wire = 1).
        var receiverBaseline = new byte[payloadSize];
        Buffer.BlockCopy(kfPayload, 0, receiverBaseline, 0, payloadSize);

        // Generation 2: the avatar moved AND the wearer's face changed — delta with additional.
        byte[] curPayload = (byte[])kfPayload.Clone();
        curPayload[0] ^= 0xFF;
        S.FlipBone(curPayload, BitQuality.High, 20);
        NetDataWriter deltaWire = WriteUplinkDelta(seq: 2, baseSeq: 1, kfPayload, curPayload, MakeAdditional());
        LocalAvatarSyncMessage deltaIngested = IngestDelta(deltaWire, kfPayload, expectedBaseSeq: 1, out _);
        AssertFaceSurvived(deltaIngested); // face made it INTO the server

        ReplaceHigh(state, deltaIngested, outboundSeq: 2);
        BasisServerReductionSystemEvents.PreSerializeDelta(state, 3, state.AvatarHigh, 42);
        Assert.True(state.SerializedDeltaLength[3] > 0, "fan-out delta was not serialized");

        // Receiver reconstructs against the gen-1 keyframe and must recover the face bytes.
        (ServerSideSyncPlayerMessage ssm, byte quality) = ParseFanoutDelta(
            state.SerializedDelta[3], state.SerializedDeltaLength[3], receiverBaseline, expectedBaseSeq: 1);
        Assert.Equal(3, quality);
        Assert.Equal(42, ssm.playerIdMessage.playerID);
        Assert.Equal(curPayload, ssm.avatarSerialization.array);
        AssertFaceSurvived(ssm.avatarSerialization);
    }

    [Fact]
    public void FanoutViaPreSerializeFrame_DeltaGeneration_KeepsFaceData()
    {
        // Same as above but through the REAL PreSerializeFrame decision path (private) —
        // exercised via ProcessMessage-equivalent ordering: keyframe gen then delta gen.
        var rng = new Random(1007);
        byte[] kfPayload = S.MakeRealisticPayload(BitQuality.High, rng);
        NetDataWriter kfWire = WriteUplinkKeyframe(seq: 1, kfPayload, MakeAdditional());
        LocalAvatarSyncMessage kfIngested = IngestKeyframe(kfWire, channelSaysAdditional: true, out _);
        PlayerState state = BuildState(kfIngested, playerId: 7, outboundSeq: 1);

        // Gen 1 = forced keyframe (what ProcessMessage does for a new player).
        BasisServerReductionSystemEvents.TestOnly_PreSerializeFrame(state, 1, forceKeyframe: true);
        Assert.True(state.CurrentIsKeyframe);
        Assert.True(state.SerializedKeyframeLength[3] > 0);
        Assert.True(state.SerializedHasAdditional[3]);

        var receiverBaseline = new byte[state.KeyframePayloadLength[3]];
        Buffer.BlockCopy(state.KeyframePayload[3], 0, receiverBaseline, 0, state.KeyframePayloadLength[3]);
        byte baseSeq = state.KeyframeSequence;

        // Gen 2: small pose change + fresh face bytes → delta generation.
        byte[] curPayload = (byte[])kfPayload.Clone();
        S.FlipBone(curPayload, BitQuality.High, 9);
        NetDataWriter deltaWire = WriteUplinkDelta(seq: 2, baseSeq: 1, kfPayload, curPayload, MakeAdditional());
        LocalAvatarSyncMessage deltaIngested = IngestDelta(deltaWire, kfPayload, expectedBaseSeq: 1, out _);
        ReplaceHigh(state, deltaIngested, outboundSeq: 2);
        BasisServerReductionSystemEvents.TestOnly_PreSerializeFrame(state, 2, forceKeyframe: false);

        Assert.False(state.CurrentIsKeyframe, "a one-bone delta must not promote to keyframe");
        Assert.True(state.SerializedDeltaLength[3] > 0);

        (ServerSideSyncPlayerMessage ssm, _) = ParseFanoutDelta(
            state.SerializedDelta[3], state.SerializedDeltaLength[3], receiverBaseline, baseSeq);
        Assert.Equal(curPayload, ssm.avatarSerialization.array);
        AssertFaceSurvived(ssm.avatarSerialization);
    }

    [Fact]
    public void P2PSplice_Keyframe_CarriesFaceData()
    {
        // BroadcastAvatarViaP2P keyframe splice: [playerId:1][interval:1][clientWire...]
        var rng = new Random(1008);
        byte[] payload = S.MakeRealisticPayload(BitQuality.High, rng);
        NetDataWriter clientWire = WriteUplinkKeyframe(seq: 3, payload, MakeAdditional());
        byte channel = BasisNetworkCommons.GetPlayerAvatarChannelForQuality(3, true);

        var spliced = new NetDataWriter();
        spliced.Put((byte)42);          // localId (small)
        spliced.Put((byte)0);           // interval
        spliced.Put(clientWire.Data, 0, clientWire.Length);

        ServerSideSyncPlayerMessage ssm = ParseFanoutKeyframe(spliced.CopyData(), spliced.Length, channel);
        Assert.Equal(42, ssm.playerIdMessage.playerID);
        Assert.Equal(3, (int)ssm.sequence);
        Assert.Equal(payload, ssm.avatarSerialization.array);
        AssertFaceSurvived(ssm.avatarSerialization);
    }

    [Fact]
    public void P2PSplice_Delta_CarriesFaceData()
    {
        // BroadcastAvatarViaP2P delta splice: [hdr(+largeId)][playerId][interval][uplink frame after hdr...]
        var rng = new Random(1009);
        byte[] baseline = S.MakeRealisticPayload(BitQuality.High, rng);
        byte[] current = (byte[])baseline.Clone();
        S.FlipBone(current, BitQuality.High, 30);

        NetDataWriter clientWire = WriteUplinkDelta(seq: 4, baseSeq: 3, baseline, current, MakeAdditional());
        byte[] raw = clientWire.CopyData();

        var spliced = new NetDataWriter();
        spliced.Put(raw[0]);            // header (small id — bit 3 unset)
        spliced.Put((byte)42);          // localId
        spliced.Put((byte)0);           // interval
        spliced.Put(raw, 1, raw.Length - 1);

        (ServerSideSyncPlayerMessage ssm, byte quality) = ParseFanoutDelta(
            spliced.CopyData(), spliced.Length, baseline, expectedBaseSeq: 3);
        Assert.Equal(3, quality);
        Assert.Equal(42, ssm.playerIdMessage.playerID);
        Assert.Equal(current, ssm.avatarSerialization.array);
        AssertFaceSurvived(ssm.avatarSerialization);
    }

    [Fact]
    public void PooledMessageReuse_DoesNotMutateSnapshottedEntries()
    {
        // The Unity client hands AdditionalAvatarData entries across threads (P2P socket thread →
        // main thread) as a shallow copy of the pooled message's entry array. That is only safe
        // while DeserializeAdditionalData allocates a fresh payload byte[] per entry per packet —
        // if the serializer ever starts reusing entry payload buffers, this test must fail.
        var rng = new Random(1011);
        byte[] payload = S.MakeRealisticPayload(BitQuality.High, rng);

        var pooled = new LocalAvatarSyncMessage();

        // Packet 1 arrives and is snapshotted (as the P2P receive path does).
        NetDataWriter wire1 = WriteUplinkKeyframe(1, payload, new[]
        {
            new AdditionalAvatarData { messageIndex = FaceMessageIndex, array = new byte[] { 16, 1, 11, 0, 200 } },
        });
        var reader1 = new NetDataReader(wire1.CopyData());
        Assert.True(reader1.TryGetByte(out _));
        pooled.Deserialize(reader1, 3, true);
        var snapshot = new AdditionalAvatarData[pooled.AdditionalAvatarDataSize];
        Array.Copy(pooled.AdditionalAvatarDatas, snapshot, pooled.AdditionalAvatarDataSize);

        // Packet 2 reuses the same pooled message (same entry count → outer array is reused).
        var outerBefore = pooled.AdditionalAvatarDatas;
        NetDataWriter wire2 = WriteUplinkKeyframe(2, payload, new[]
        {
            new AdditionalAvatarData { messageIndex = FaceMessageIndex, array = new byte[] { 16, 1, 22, 0, 50 } },
        });
        var reader2 = new NetDataReader(wire2.CopyData());
        Assert.True(reader2.TryGetByte(out _));
        pooled.Deserialize(reader2, 3, true);
        Assert.Same(outerBefore, pooled.AdditionalAvatarDatas); // outer array IS pooled…

        // …but the snapshot taken from packet 1 must still hold packet 1's bytes.
        Assert.Equal(new byte[] { 16, 1, 11, 0, 200 }, snapshot[0].array);
        Assert.Equal(new byte[] { 16, 1, 22, 0, 50 }, pooled.AdditionalAvatarDatas[0].array);
        Assert.NotSame(snapshot[0].array, pooled.AdditionalAvatarDatas[0].array);
    }

    [Fact]
    public void FanoutChannel_OddOnlyWhenAdditionalPresent()
    {
        var rng = new Random(1010);
        byte[] payload = S.MakeRealisticPayload(BitQuality.High, rng);

        // With face data → odd channel.
        LocalAvatarSyncMessage withFace = IngestKeyframe(WriteUplinkKeyframe(1, payload, MakeAdditional()), true, out _);
        PlayerState s1 = BuildState(withFace, 42, 1);
        BasisServerReductionSystemEvents.PreSerializeKeyframe(s1, 3, s1.AvatarHigh, 42);
        Assert.True(s1.SerializedHasAdditional[3]);
        Assert.Equal(BasisNetworkCommons.PlayerAvatarHighAdditionalChannel,
            BasisNetworkCommons.GetPlayerAvatarChannelForQuality(3, s1.SerializedHasAdditional[3]));

        // Without → even channel; a parse expecting additional data must not be attempted.
        LocalAvatarSyncMessage noFace = IngestKeyframe(WriteUplinkKeyframe(2, payload, null), false, out _);
        PlayerState s2 = BuildState(noFace, 42, 2);
        BasisServerReductionSystemEvents.PreSerializeKeyframe(s2, 3, s2.AvatarHigh, 42);
        Assert.False(s2.SerializedHasAdditional[3]);
        Assert.Equal(BasisNetworkCommons.PlayerAvatarHighChannel,
            BasisNetworkCommons.GetPlayerAvatarChannelForQuality(3, s2.SerializedHasAdditional[3]));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Condition matrix
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LargePlayerId_KeyframeAndDelta_CarryFaceData()
    {
        // Player ids > 255 use the ushort-id channels (41-48) and the largeId delta-header bit.
        var rng = new Random(1012);
        byte[] kfPayload = S.MakeRealisticPayload(BitQuality.High, rng);
        const ushort bigId = 300;

        LocalAvatarSyncMessage ingested = IngestKeyframe(WriteUplinkKeyframe(1, kfPayload, MakeAdditional()), true, out _);
        PlayerState state = BuildState(ingested, bigId, 1);
        Assert.False(state.SmallId);

        BasisServerReductionSystemEvents.TestOnly_PreSerializeFrame(state, 1, forceKeyframe: true);
        Assert.True(state.SerializedHasAdditional[3]);
        byte channel = BasisNetworkCommons.GetPlayerAvatarLargeChannelForQuality(3, true);
        Assert.True(BasisNetworkCommons.IsLargePlayerIdChannel(channel));

        ServerSideSyncPlayerMessage kf = ParseFanoutKeyframe(state.SerializedKeyframe[3], state.SerializedKeyframeLength[3], channel);
        Assert.Equal(bigId, kf.playerIdMessage.playerID);
        AssertFaceSurvived(kf.avatarSerialization);

        var receiverBaseline = new byte[state.KeyframePayloadLength[3]];
        Buffer.BlockCopy(state.KeyframePayload[3], 0, receiverBaseline, 0, state.KeyframePayloadLength[3]);

        byte[] cur = (byte[])kfPayload.Clone();
        S.FlipBone(cur, BitQuality.High, 5);
        LocalAvatarSyncMessage deltaIngested = IngestDelta(WriteUplinkDelta(2, 1, kfPayload, cur, MakeAdditional()), kfPayload, 1, out _);
        ReplaceHigh(state, deltaIngested, 2);
        BasisServerReductionSystemEvents.TestOnly_PreSerializeFrame(state, 2, forceKeyframe: false);
        Assert.False(state.CurrentIsKeyframe);
        Assert.True(state.SerializedDeltaLength[3] > 0);

        // Header must carry the largeId bit and the parse must recover the ushort id.
        Assert.True(BasisNetworkCommons.DeltaHeaderLargeId(state.SerializedDelta[3][0]));
        (ServerSideSyncPlayerMessage dssm, _) = ParseFanoutDelta(state.SerializedDelta[3], state.SerializedDeltaLength[3], receiverBaseline, state.KeyframeSequence);
        Assert.Equal(bigId, dssm.playerIdMessage.playerID);
        AssertFaceSurvived(dssm.avatarSerialization);
    }

    [Fact]
    public void IdleAvatar_FaceOnlyChange_MaskOnlyDelta_CarriesFaceData()
    {
        // "Standing still while the face moves": pose is byte-identical to the baseline, so the
        // uplink and the fan-out deltas are mask-only (8 bytes) — the additional tail must still
        // ride along and parse at exactly the right offset.
        var rng = new Random(1013);
        byte[] kfPayload = S.MakeRealisticPayload(BitQuality.High, rng);

        LocalAvatarSyncMessage kfIngested = IngestKeyframe(WriteUplinkKeyframe(1, kfPayload, MakeAdditional()), true, out _);
        PlayerState state = BuildState(kfIngested, 42, 1);
        BasisServerReductionSystemEvents.TestOnly_PreSerializeFrame(state, 1, forceKeyframe: true);
        var receiverBaseline = (byte[])state.KeyframePayload[3].Clone();

        // Uplink: identical pose + fresh face bytes → mask-only delta body.
        var scratch = new byte[BasisAvatarDeltaCompression.MaxDeltaSize(BitQuality.High)];
        int bodyLen = BasisAvatarDeltaCompression.BuildDelta(kfPayload, (byte[])kfPayload.Clone(), BitQuality.High, scratch, 0);
        Assert.Equal(BasisAvatarDeltaCompression.DirtyMaskBytes, bodyLen);

        var lasm = new LocalAvatarSyncMessage { array = kfPayload, AdditionalAvatarDatas = MakeAdditional(), LinkedAvatarIndex = LinkedIndex };
        var w = new NetDataWriter();
        w.Put(BasisNetworkCommons.BuildDeltaHeader(3, true, false));
        w.Put((byte)2);
        w.Put((byte)1);
        w.Put(scratch, 0, bodyLen);
        lasm.SerializeAdditionalOnly(w);

        LocalAvatarSyncMessage deltaIngested = IngestDelta(w, kfPayload, 1, out _);
        Assert.Equal(kfPayload, deltaIngested.array);
        AssertFaceSurvived(deltaIngested);

        // Fan-out: the delta generation for an unchanged pose must still carry the face bytes.
        ReplaceHigh(state, deltaIngested, 2);
        BasisServerReductionSystemEvents.TestOnly_PreSerializeFrame(state, 2, forceKeyframe: false);
        Assert.False(state.CurrentIsKeyframe);
        (ServerSideSyncPlayerMessage ssm, _) = ParseFanoutDelta(state.SerializedDelta[3], state.SerializedDeltaLength[3], receiverBaseline, state.KeyframeSequence);
        Assert.Equal(kfPayload, ssm.avatarSerialization.array);
        AssertFaceSurvived(ssm.avatarSerialization);
    }

    [Fact]
    public void DeltaPromotion_FullyChangedPose_FallsBackToKeyframe_WithFaceData()
    {
        // A delta that would be larger than a keyframe is promoted — face data must follow.
        var rng = new Random(1014);
        byte[] kfPayload = S.MakeRealisticPayload(BitQuality.High, rng);
        LocalAvatarSyncMessage kfIngested = IngestKeyframe(WriteUplinkKeyframe(1, kfPayload, null), false, out _);
        PlayerState state = BuildState(kfIngested, 42, 1);
        BasisServerReductionSystemEvents.TestOnly_PreSerializeFrame(state, 1, forceKeyframe: true);

        byte[] whollyNew = S.MakeRealisticPayload(BitQuality.High, rng); // every field differs
        LocalAvatarSyncMessage next = IngestKeyframe(WriteUplinkKeyframe(2, whollyNew, MakeAdditional()), true, out _);
        ReplaceHigh(state, next, 2);
        BasisServerReductionSystemEvents.TestOnly_PreSerializeFrame(state, 2, forceKeyframe: false);

        Assert.True(state.CurrentIsKeyframe, "an everything-changed frame must promote to keyframe");
        Assert.True(state.SerializedHasAdditional[3]);
        byte channel = BasisNetworkCommons.GetPlayerAvatarChannelForQuality(3, true);
        ServerSideSyncPlayerMessage ssm = ParseFanoutKeyframe(state.SerializedKeyframe[3], state.SerializedKeyframeLength[3], channel);
        Assert.Equal(whollyNew, ssm.avatarSerialization.array);
        AssertFaceSurvived(ssm.avatarSerialization);
    }

    [Fact]
    public void StripOn_RealPreSerializeFrame_AllTiers_ChannelsAndSectionsAgree()
    {
        BasisServerReductionSystemEvents.StripAdditionalDataAtLowQuality = true;

        var rng = new Random(1015);
        byte[] payload = S.MakeRealisticPayload(BitQuality.High, rng);
        LocalAvatarSyncMessage ingested = IngestKeyframe(WriteUplinkKeyframe(1, payload, MakeAdditional()), true, out _);
        PlayerState state = BuildState(ingested, 42, 1);
        BasisServerReductionSystemEvents.TestOnly_PreSerializeFrame(state, 1, forceKeyframe: true);

        for (int qi = 0; qi < 4; qi++)
        {
            Assert.True(state.SerializedKeyframeLength[qi] > 0, $"tier {qi} not serialized");
            bool expectFace = qi >= 2; // High + Medium keep it; Low + VeryLow strip it
            Assert.Equal(expectFace, state.SerializedHasAdditional[qi]);

            byte channel = BasisNetworkCommons.GetPlayerAvatarChannelForQuality(qi, state.SerializedHasAdditional[qi]);
            // ParseFanoutKeyframe asserts the frame is consumed EXACTLY — a stripped tier that
            // still contained section bytes (or vice versa) would fail here.
            ServerSideSyncPlayerMessage ssm = ParseFanoutKeyframe(state.SerializedKeyframe[qi], state.SerializedKeyframeLength[qi], channel);
            if (expectFace) AssertFaceSurvived(ssm.avatarSerialization);
            else Assert.Equal(0, (int)ssm.avatarSerialization.AdditionalAvatarDataSize);
        }
    }

    [Fact]
    public void MediumTierDelta_CarriesFaceData_LowTierDeltaStripsIt()
    {
        BasisServerReductionSystemEvents.StripAdditionalDataAtLowQuality = true;

        var rng = new Random(1016);
        byte[] kfPayload = S.MakeRealisticPayload(BitQuality.High, rng);
        LocalAvatarSyncMessage kfIngested = IngestKeyframe(WriteUplinkKeyframe(1, kfPayload, MakeAdditional()), true, out _);
        PlayerState state = BuildState(kfIngested, 42, 1);
        BasisServerReductionSystemEvents.TestOnly_PreSerializeFrame(state, 1, forceKeyframe: true);
        var mediumBaseline = (byte[])state.KeyframePayload[2].Clone();
        var lowBaseline = (byte[])state.KeyframePayload[1].Clone();

        byte[] cur = (byte[])kfPayload.Clone();
        S.FlipBone(cur, BitQuality.High, 8);
        LocalAvatarSyncMessage deltaIngested = IngestDelta(WriteUplinkDelta(2, 1, kfPayload, cur, MakeAdditional()), kfPayload, 1, out _);
        ReplaceHigh(state, deltaIngested, 2);
        BasisServerReductionSystemEvents.TestOnly_PreSerializeFrame(state, 2, forceKeyframe: false);
        Assert.False(state.CurrentIsKeyframe);

        // Medium (qi=2): repacked pose delta + face data.
        Assert.True(state.SerializedDeltaLength[2] > 0, "medium delta not serialized");
        Assert.True(BasisNetworkCommons.DeltaHeaderHasAdditionalData(state.SerializedDelta[2][0]));
        (ServerSideSyncPlayerMessage med, byte mq) = ParseFanoutDelta(state.SerializedDelta[2], state.SerializedDeltaLength[2], mediumBaseline, state.KeyframeSequence);
        Assert.Equal(2, mq);
        AssertFaceSurvived(med.avatarSerialization);

        // Low (qi=1): stripped — header bit clear, no trailing section.
        Assert.True(state.SerializedDeltaLength[1] > 0, "low delta not serialized");
        Assert.False(BasisNetworkCommons.DeltaHeaderHasAdditionalData(state.SerializedDelta[1][0]));
        (ServerSideSyncPlayerMessage low, byte lq) = ParseFanoutDelta(state.SerializedDelta[1], state.SerializedDeltaLength[1], lowBaseline, state.KeyframeSequence);
        Assert.Equal(1, lq);
        Assert.Equal(0, (int)low.avatarSerialization.AdditionalAvatarDataSize);
    }

    [Fact]
    public void MultiEntry_EdgePayloads_KeepStreamAlignment()
    {
        // Several entries with edge payloads: 0-length, 255-byte max, and a null array (wire form
        // is a lone size-0 byte with NO messageIndex). Entries after each edge case must still
        // parse from the correct offset — misalignment here is how face data turns to garbage.
        var rng = new Random(1017);
        byte[] payload = S.MakeRealisticPayload(BitQuality.High, rng);
        var max = new byte[255];
        rng.NextBytes(max);

        var entries = new[]
        {
            new AdditionalAvatarData { messageIndex = 1, array = (byte[])FaceBytes.Clone() },
            new AdditionalAvatarData { messageIndex = 2, array = null },                      // size-0 wire form
            new AdditionalAvatarData { messageIndex = 3, array = Array.Empty<byte>() },       // 0-length but indexed
            new AdditionalAvatarData { messageIndex = 4, array = max },                       // max payload
            new AdditionalAvatarData { messageIndex = 5, array = new byte[] { 7 } },
        };

        LocalAvatarSyncMessage ingested = IngestKeyframe(WriteUplinkKeyframe(1, payload, entries), true, out _);
        Assert.Equal(entries.Length, (int)ingested.AdditionalAvatarDataSize);

        // Every entry keeps its 2-byte [size][messageIndex] header, so null / empty payloads
        // decode as empty entries WITH their index — and never shift the entries after them.
        Assert.Equal(1, (int)ingested.AdditionalAvatarDatas[0].messageIndex);
        Assert.Equal(FaceBytes, ingested.AdditionalAvatarDatas[0].array);
        Assert.Equal(2, (int)ingested.AdditionalAvatarDatas[1].messageIndex);
        Assert.Null(ingested.AdditionalAvatarDatas[1].array);
        Assert.Equal(3, (int)ingested.AdditionalAvatarDatas[2].messageIndex);
        Assert.Equal(0, ingested.AdditionalAvatarDatas[2].array?.Length ?? 0);
        Assert.Equal(4, (int)ingested.AdditionalAvatarDatas[3].messageIndex);
        Assert.Equal(max, ingested.AdditionalAvatarDatas[3].array);
        Assert.Equal(5, (int)ingested.AdditionalAvatarDatas[4].messageIndex);
        Assert.Equal(new byte[] { 7 }, ingested.AdditionalAvatarDatas[4].array);

        // And through the server fan-out.
        PlayerState state = BuildState(ingested, 42, 1);
        BasisServerReductionSystemEvents.PreSerializeKeyframe(state, 3, state.AvatarHigh, 42);
        byte channel = BasisNetworkCommons.GetPlayerAvatarChannelForQuality(3, state.SerializedHasAdditional[3]);
        ServerSideSyncPlayerMessage ssm = ParseFanoutKeyframe(state.SerializedKeyframe[3], state.SerializedKeyframeLength[3], channel);
        Assert.Equal(entries.Length, (int)ssm.avatarSerialization.AdditionalAvatarDataSize);
        Assert.Equal(FaceBytes, ssm.avatarSerialization.AdditionalAvatarDatas[0].array);
        Assert.Equal(max, ssm.avatarSerialization.AdditionalAvatarDatas[3].array);
        Assert.Equal(new byte[] { 7 }, ssm.avatarSerialization.AdditionalAvatarDatas[4].array);
    }

    [Fact]
    public void MaxEntryCount255_RoundTrips()
    {
        var rng = new Random(1018);
        byte[] payload = S.MakeRealisticPayload(BitQuality.High, rng);
        var entries = new AdditionalAvatarData[255];
        for (int i = 0; i < 255; i++)
        {
            entries[i] = new AdditionalAvatarData { messageIndex = (byte)i, array = new byte[] { (byte)i } };
        }

        LocalAvatarSyncMessage ingested = IngestKeyframe(WriteUplinkKeyframe(1, payload, entries), true, out _);
        Assert.Equal(255, (int)ingested.AdditionalAvatarDataSize);

        PlayerState state = BuildState(ingested, 42, 1);
        BasisServerReductionSystemEvents.PreSerializeKeyframe(state, 3, state.AvatarHigh, 42);
        byte channel = BasisNetworkCommons.GetPlayerAvatarChannelForQuality(3, state.SerializedHasAdditional[3]);
        ServerSideSyncPlayerMessage ssm = ParseFanoutKeyframe(state.SerializedKeyframe[3], state.SerializedKeyframeLength[3], channel);
        Assert.Equal(255, (int)ssm.avatarSerialization.AdditionalAvatarDataSize);
        for (int i = 0; i < 255; i++)
        {
            Assert.Equal((byte)i, ssm.avatarSerialization.AdditionalAvatarDatas[i].messageIndex);
            Assert.Equal(new byte[] { (byte)i }, ssm.avatarSerialization.AdditionalAvatarDatas[i].array);
        }
    }

    [Fact]
    public void BundlePath_KeyframeAndDelta_WithFaceData_RoundTripLosslessly()
    {
        // Channel-52 bundles: the server packs [chan:1][len:2-LE][bytes]* and LZ4s the block;
        // the client inflates and re-dispatches by inner channel. Face data and the per-receiver
        // interval patch must survive; the patch must not bleed into the additional tail.
        var rng = new Random(1019);
        byte[] kfPayload = S.MakeRealisticPayload(BitQuality.High, rng);
        LocalAvatarSyncMessage kfIngested = IngestKeyframe(WriteUplinkKeyframe(1, kfPayload, MakeAdditional()), true, out _);
        PlayerState state = BuildState(kfIngested, 42, 1);
        BasisServerReductionSystemEvents.TestOnly_PreSerializeFrame(state, 1, forceKeyframe: true);
        var receiverBaseline = (byte[])state.KeyframePayload[3].Clone();

        byte[] cur = (byte[])kfPayload.Clone();
        S.FlipBone(cur, BitQuality.High, 14);
        LocalAvatarSyncMessage deltaIngested = IngestDelta(WriteUplinkDelta(2, 1, kfPayload, cur, MakeAdditional()), kfPayload, 1, out _);
        // Snapshot the keyframe wire BEFORE the delta generation replaces the state's High message.
        var kfWire = new byte[state.SerializedKeyframeLength[3]];
        Buffer.BlockCopy(state.SerializedKeyframe[3], 0, kfWire, 0, kfWire.Length);
        ReplaceHigh(state, deltaIngested, 2);
        BasisServerReductionSystemEvents.TestOnly_PreSerializeFrame(state, 2, forceKeyframe: false);
        Assert.False(state.CurrentIsKeyframe);

        // Pending buffer exactly as the send loop stages it: one keyframe + one delta, each with
        // a per-receiver interval byte to patch.
        var pending = new PendingAvatarSend[]
        {
            new()
            {
                Source = kfWire, Length = kfWire.Length,
                Channel = BasisNetworkCommons.GetPlayerAvatarChannelForQuality(3, true),
                Interval = 37, IntervalOffset = 1, // keyframe: [playerId:1][interval:1]...
            },
            new()
            {
                Source = state.SerializedDelta[3], Length = state.SerializedDeltaLength[3],
                Channel = BasisNetworkCommons.DeltaAvatarChannel,
                Interval = 53, IntervalOffset = 2, // delta: [header:1][playerId:1][interval:1]...
            },
        };

        int rawLen = BasisServerReductionSystemEvents.TestOnly_BuildRawForRange(state, pending, 0, pending.Length);
        Assert.True(rawLen > 0);

        // Emit exactly like TryDeflateAndEmit: [count:1][rawLen:2-LE][LZ4 block].
        byte[] compressed = new byte[3 + K4os.Compression.LZ4.LZ4Codec.MaximumOutputSize(rawLen)];
        int compressedLen = K4os.Compression.LZ4.LZ4Codec.Encode(
            state.BundleRawScratch.AsSpan(0, rawLen), compressed.AsSpan(3), K4os.Compression.LZ4.LZ4Level.L00_FAST);
        Assert.True(compressedLen > 0);
        compressed[0] = (byte)pending.Length;
        compressed[1] = (byte)(rawLen & 0xFF);
        compressed[2] = (byte)((rawLen >> 8) & 0xFF);

        // Decode exactly like BasisNetworkHandleCompressedBundle.Handle.
        var reader = new NetDataReader(compressed, 0, 3 + compressedLen);
        Assert.True(reader.TryGetByte(out _));
        Assert.True(reader.TryGetUShort(out ushort parsedRawLen));
        Assert.Equal(rawLen, parsedRawLen);
        byte[] scratch = new byte[parsedRawLen];
        int decoded = K4os.Compression.LZ4.LZ4Codec.Decode(
            reader.RawData.AsSpan(reader.Position, reader.AvailableBytes), scratch.AsSpan(0, parsedRawLen));
        Assert.Equal(parsedRawLen, decoded);

        int offset = 0;
        int innerSeen = 0;
        while (offset + 3 <= decoded)
        {
            byte innerChannel = scratch[offset];
            ushort msgLen = (ushort)(scratch[offset + 1] | (scratch[offset + 2] << 8));
            offset += 3;
            Assert.True(msgLen > 0 && offset + msgLen <= decoded);

            var inner = new byte[msgLen];
            Buffer.BlockCopy(scratch, offset, inner, 0, msgLen);

            if (innerChannel == BasisNetworkCommons.DeltaAvatarChannel)
            {
                // Interval byte was patched per receiver inside the bundle copy.
                Assert.Equal(53, inner[2]);
                (ServerSideSyncPlayerMessage dssm, _) = ParseFanoutDelta(inner, msgLen, receiverBaseline, state.KeyframeSequence);
                Assert.Equal(cur, dssm.avatarSerialization.array);
                AssertFaceSurvived(dssm.avatarSerialization);
            }
            else
            {
                Assert.Equal(BasisNetworkCommons.GetPlayerAvatarChannelForQuality(3, true), innerChannel);
                Assert.Equal(37, inner[1]);
                ServerSideSyncPlayerMessage kssm = ParseFanoutKeyframe(inner, msgLen, innerChannel);
                Assert.Equal(kfPayload, kssm.avatarSerialization.array);
                AssertFaceSurvived(kssm.avatarSerialization);
            }
            offset += msgLen;
            innerSeen++;
        }
        Assert.Equal(2, innerSeen);
    }

    [Fact]
    public void SerializedWireBytes_AreImmuneToSourceMutationAfterPreSerialize()
    {
        // The server pre-serializes inside ProcessMessage while the inbound message is still
        // pool-owned; the wire buffers must be byte snapshots, not views over pooled data.
        var rng = new Random(1020);
        byte[] payload = S.MakeRealisticPayload(BitQuality.High, rng);
        AdditionalAvatarData[] entries = MakeAdditional();
        LocalAvatarSyncMessage ingested = IngestKeyframe(WriteUplinkKeyframe(1, payload, entries), true, out _);
        PlayerState state = BuildState(ingested, 42, 1);
        BasisServerReductionSystemEvents.PreSerializeKeyframe(state, 3, state.AvatarHigh, 42);

        var before = new byte[state.SerializedKeyframeLength[3]];
        Buffer.BlockCopy(state.SerializedKeyframe[3], 0, before, 0, before.Length);

        // Simulate the pool overwriting the inbound entries after ProcessMessage returned.
        for (int i = 0; i < ingested.AdditionalAvatarDatas.Length; i++)
        {
            if (ingested.AdditionalAvatarDatas[i].array != null)
                Array.Clear(ingested.AdditionalAvatarDatas[i].array, 0, ingested.AdditionalAvatarDatas[i].array.Length);
        }

        var after = new byte[state.SerializedKeyframeLength[3]];
        Buffer.BlockCopy(state.SerializedKeyframe[3], 0, after, 0, after.Length);
        Assert.Equal(before, after);
    }

    [Fact]
    public void TruncatedAdditionalSection_FailsSafely()
    {
        // A frame cut mid-additional-section (worst-case UDP corruption reaching the parser)
        // must fail without throwing and without inventing data.
        var rng = new Random(1021);
        byte[] payload = S.MakeRealisticPayload(BitQuality.High, rng);
        NetDataWriter full = WriteUplinkKeyframe(1, payload, MakeAdditional());
        byte[] wire = full.CopyData();

        for (int cut = payload.Length + 2; cut < wire.Length; cut++)
        {
            var reader = new NetDataReader(wire, 0, cut);
            Assert.True(reader.TryGetByte(out _)); // seq
            var msg = new LocalAvatarSyncMessage();
            var ex = Record.Exception(() => msg.Deserialize(reader, 3, true));
            Assert.Null(ex);
            if (msg.AdditionalAvatarDataSize > 0 && msg.AdditionalAvatarDatas != null && msg.AdditionalAvatarDatas.Length > 0)
            {
                var entry = msg.AdditionalAvatarDatas[0];
                // Either the entry failed to materialize or it holds exactly the original bytes.
                if (entry.array != null && entry.array.Length == FaceBytes.Length && entry.PayloadSize == FaceBytes.Length)
                {
                    Assert.Equal(FaceBytes, entry.array);
                }
            }
        }
    }
}
