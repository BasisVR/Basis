using Basis.Network.Core;
using Basis.Network.Core.Compression;
using BasisNetworkServer.BasisNetworkingReductionSystem;
using Xunit;
using BitQuality = Basis.Network.Core.Compression.BasisAvatarBitPacking.BitQuality;
using S = BasisServerTests.DeltaTestSupport;
using static SerializableBasis;

namespace BasisServerTests;

/// <summary>
/// Exercises the REAL server serialization — <see cref="BasisServerReductionSystemEvents.PreSerializeKeyframe"/>
/// and <see cref="BasisServerReductionSystemEvents.PreSerializeDelta"/> (not a replica) — and parses the
/// emitted frames the way the client does, confirming the keyframe payload and the delta reconstruction
/// match for byte/ushort ids, every quality, and with/without additional data.
/// </summary>
public class AvatarDeltaServerSerializationTests
{
    private static PlayerState MakeState(bool smallId, byte outSeq, byte kfSeq, bool hasAdditional) => new()
    {
        SmallId = smallId,
        OutboundSequence = outSeq,
        KeyframeSequence = kfSeq,
        HasAdditionalData = hasAdditional,
    };

    [Theory]
    [InlineData(BitQuality.VeryLow, true)]
    [InlineData(BitQuality.Low, true)]
    [InlineData(BitQuality.Medium, true)]
    [InlineData(BitQuality.High, true)]
    [InlineData(BitQuality.High, false)]
    [InlineData(BitQuality.Low, false)]
    public void RealKeyframeThenDelta_RoundTrips(BitQuality q, bool smallId)
    {
        int qi = (int)q;
        int payloadSize = S.PayloadSize(q);
        var rng = new Random(qi * 7 + (smallId ? 0 : 99));
        byte[] kfPayload = S.MakeRealisticPayload(q, rng);
        byte[] curPayload = S.MakeRealisticPayload(q, rng);
        ushort playerId = smallId ? (ushort)200 : (ushort)5000;
        int idSize = smallId ? 1 : 2;

        var state = MakeState(smallId, outSeq: 50, kfSeq: 50, hasAdditional: false);

        // --- real keyframe serialization: [id][interval][seq][payload] ---
        var kfMsg = new LocalAvatarSyncMessage { array = kfPayload, DataQualityLevel = (byte)qi };
        BasisServerReductionSystemEvents.PreSerializeKeyframe(state, qi, kfMsg, playerId);
        byte[] kbuf = state.SerializedKeyframe[qi];
        int klen = state.SerializedKeyframeLength[qi];
        Assert.True(klen > 0);
        int ko = idSize + 1;                       // skip id + interval
        Assert.Equal((byte)50, kbuf[ko]);          // sequence
        ko += 1;
        Assert.Equal(kfPayload.AsSpan(0, payloadSize).ToArray(), kbuf.AsSpan(ko, payloadSize).ToArray());
        Assert.Equal(idSize + 2 + payloadSize, klen);

        // Baseline the state on that keyframe (as PreSerializeFrame would) then emit a delta.
        state.KeyframePayload[qi] = (byte[])kfPayload.Clone();
        state.KeyframePayloadLength[qi] = payloadSize;
        state.OutboundSequence = 51;

        var curMsg = new LocalAvatarSyncMessage { array = curPayload, DataQualityLevel = (byte)qi };
        BasisServerReductionSystemEvents.PreSerializeDelta(state, qi, curMsg, playerId);
        byte[] dbuf = state.SerializedDelta[qi];
        int dlen = state.SerializedDeltaLength[qi];
        Assert.True(dlen > 0);

        int p = 0;
        byte header = dbuf[p++];
        Assert.Equal((byte)qi, BasisNetworkCommons.DeltaHeaderQuality(header));
        Assert.False(BasisNetworkCommons.DeltaHeaderHasAdditionalData(header));
        Assert.Equal(!smallId, BasisNetworkCommons.DeltaHeaderLargeId(header));
        ushort pid = smallId ? dbuf[p] : (ushort)(dbuf[p] | (dbuf[p + 1] << 8));
        p += idSize;
        Assert.Equal(playerId, pid);
        Assert.Equal((byte)0, dbuf[p++]);   // interval placeholder (patched per receiver at send)
        Assert.Equal((byte)51, dbuf[p++]);  // sequence
        Assert.Equal((byte)50, dbuf[p++]);  // baseSeq == keyframe sequence

        int body = BasisAvatarDeltaCompression.DeltaBodyLength(dbuf, p, dlen - p, q);
        var recon = new byte[payloadSize];
        Assert.True(BasisAvatarDeltaCompression.TryApplyDelta(kfPayload, dbuf, p, body, q, recon));
        Assert.Equal(curPayload, recon);
        Assert.Equal(dlen, p + body); // no trailing bytes without additional data
    }

    [Theory]
    [InlineData(BitQuality.High, true)]
    [InlineData(BitQuality.Medium, false)]
    public void RealDelta_WithAdditionalData_CarriesTrailerAndReconstructs(BitQuality q, bool smallId)
    {
        int qi = (int)q;
        int payloadSize = S.PayloadSize(q);
        var rng = new Random(qi + 500);
        byte[] kfPayload = S.MakeRealisticPayload(q, rng);
        byte[] curPayload = S.MakeRealisticPayload(q, rng);
        ushort playerId = smallId ? (ushort)7 : (ushort)9000;
        int idSize = smallId ? 1 : 2;

        var state = MakeState(smallId, outSeq: 51, kfSeq: 40, hasAdditional: true);
        state.KeyframePayload[qi] = (byte[])kfPayload.Clone();
        state.KeyframePayloadLength[qi] = payloadSize;

        var adds = new AdditionalAvatarData[]
        {
            new AdditionalAvatarData { array = new byte[] { 9, 8, 7 }, messageIndex = 2 },
            new AdditionalAvatarData { array = new byte[] { 1 }, messageIndex = 5 },
        };
        var curMsg = new LocalAvatarSyncMessage
        {
            array = curPayload,
            DataQualityLevel = (byte)qi,
            AdditionalAvatarDatas = adds,
            LinkedAvatarIndex = 3,
        };
        BasisServerReductionSystemEvents.PreSerializeDelta(state, qi, curMsg, playerId);
        byte[] dbuf = state.SerializedDelta[qi];
        int dlen = state.SerializedDeltaLength[qi];

        int p = 0;
        byte header = dbuf[p++];
        Assert.True(BasisNetworkCommons.DeltaHeaderHasAdditionalData(header));
        p += idSize + 3; // id + interval + seq + baseSeq

        int body = BasisAvatarDeltaCompression.DeltaBodyLength(dbuf, p, dlen - p, q);
        var recon = new byte[payloadSize];
        Assert.True(BasisAvatarDeltaCompression.TryApplyDelta(kfPayload, dbuf, p, body, q, recon));
        Assert.Equal(curPayload, recon);

        int addStart = p + body;
        Assert.True(addStart < dlen, "expected a trailing additional-data section");
        Assert.Equal((byte)2, dbuf[addStart]);     // count
        Assert.Equal((byte)3, dbuf[addStart + 1]); // LinkedAvatarIndex
    }
}
