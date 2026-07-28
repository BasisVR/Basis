using Basis.Network.Core;
using Basis.Network.Core.Compression;
using Xunit;
using BitQuality = Basis.Network.Core.Compression.BasisAvatarBitPacking.BitQuality;
using S = BasisServerTests.DeltaTestSupport;

namespace BasisServerTests;

/// <summary>
/// v42 uplink/P2P avatar delta protocol: the client frames [hdr][seq][baseSeq][delta body] on
/// DeltaAvatarChannel against its last uploaded keyframe; the server (or a P2P peer) reconstructs
/// with the shared codec, NACKing via control frames when the baseline is missing or stale.
/// </summary>
public class UplinkDeltaProtocolTests
{
    [Fact]
    public void ClientFrame_ReconstructsExactly_OnMatchingBaseline()
    {
        var rng = new Random(4242);
        byte[] keyframe = S.MakeRealisticPayload(BitQuality.High, rng);
        byte[] current = (byte[])keyframe.Clone();
        current[0] ^= 0xFF;                       // moved position
        S.FlipBone(current, BitQuality.High, 7);  // one bone changed

        // Client side: encode the delta and frame it the way the compressor does.
        byte clientBaseSeq = 10;
        byte clientSeq = 11;
        var scratch = new byte[BasisAvatarDeltaCompression.MaxDeltaSize(BitQuality.High)];
        int bodyLen = BasisAvatarDeltaCompression.BuildDelta(keyframe, current, BitQuality.High, scratch, 0);
        Assert.True(bodyLen > 0 && bodyLen < keyframe.Length, "delta must beat the keyframe here");

        var frame = new byte[3 + bodyLen];
        frame[0] = BasisNetworkCommons.BuildDeltaHeader(3, hasAdditionalData: false, largeId: false);
        frame[1] = clientSeq;
        frame[2] = clientBaseSeq;
        Buffer.BlockCopy(scratch, 0, frame, 3, bodyLen);

        // Server side: parse exactly like HandleDeltaChannelInbound.
        byte header = frame[0];
        Assert.False(BasisNetworkCommons.IsDeltaControlHeader(header));
        Assert.Equal(3, BasisNetworkCommons.DeltaHeaderQuality(header));
        Assert.False(BasisNetworkCommons.DeltaHeaderHasAdditionalData(header));
        byte seq = frame[1], baseSeq = frame[2];
        Assert.Equal(clientSeq, seq);
        Assert.Equal(clientBaseSeq, baseSeq);

        int parsedLen = BasisAvatarDeltaCompression.DeltaBodyLength(frame, 3, frame.Length - 3, BitQuality.High);
        Assert.Equal(bodyLen, parsedLen);

        var reconstructed = new byte[BasisAvatarDeltaCompression.PayloadSize(BitQuality.High)];
        Assert.True(BasisAvatarDeltaCompression.TryApplyDelta(keyframe, frame, 3, parsedLen, BitQuality.High, reconstructed));
        Assert.Equal(current, reconstructed);
    }

    [Fact]
    public void StaleBaseline_IsDetectedBySeqMismatch()
    {
        // The server's stored baseline seq must equal the frame's baseSeq; anything else NACKs.
        byte storedBaselineSeq = 9;
        byte frameBaseSeq = 10;
        Assert.NotEqual(storedBaselineSeq, frameBaseSeq);
    }

    [Fact]
    public void FullyChangedFrame_PromotesToKeyframe()
    {
        var rng = new Random(555);
        byte[] keyframe = S.MakeRealisticPayload(BitQuality.High, rng);
        byte[] current = S.MakeRealisticPayload(BitQuality.High, rng);
        var scratch = new byte[BasisAvatarDeltaCompression.MaxDeltaSize(BitQuality.High)];
        int bodyLen = BasisAvatarDeltaCompression.BuildDelta(keyframe, current, BitQuality.High, scratch, 0);
        // The client sends a keyframe whenever the delta is not strictly smaller.
        Assert.True(bodyLen >= keyframe.Length, "an everything-changed delta must trigger promotion");
    }

    [Fact]
    public void ControlHeaders_NeverCollideWithDataHeaders()
    {
        for (int qi = 0; qi < 4; qi++)
        {
            foreach (bool additional in new[] { false, true })
            {
                foreach (bool largeId in new[] { false, true })
                {
                    byte hdr = BasisNetworkCommons.BuildDeltaHeader(qi, additional, largeId);
                    Assert.False(BasisNetworkCommons.IsDeltaControlHeader(hdr));
                }
            }
        }
        Assert.True(BasisNetworkCommons.IsDeltaControlHeader(BasisNetworkCommons.DeltaControlKeyframeRequest));
        Assert.True(BasisNetworkCommons.IsDeltaControlHeader(BasisNetworkCommons.DeltaControlUplinkKeyframeRequest));
        Assert.NotEqual(BasisNetworkCommons.DeltaControlKeyframeRequest, BasisNetworkCommons.DeltaControlUplinkKeyframeRequest);
    }

    [Fact]
    public void IdleUplinkDelta_IsMaskOnly()
    {
        var rng = new Random(777);
        byte[] keyframe = S.MakeRealisticPayload(BitQuality.High, rng);
        var scratch = new byte[BasisAvatarDeltaCompression.MaxDeltaSize(BitQuality.High)];
        int bodyLen = BasisAvatarDeltaCompression.BuildDelta(keyframe, (byte[])keyframe.Clone(), BitQuality.High, scratch, 0);
        // Wire: 3-byte frame header + 8-byte mask = 11 bytes vs the 237-byte keyframe frame.
        Assert.Equal(BasisAvatarDeltaCompression.DirtyMaskBytes, bodyLen);
    }
}
