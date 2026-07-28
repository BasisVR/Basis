using Basis.Network.Core.Compression;
using Xunit;
using BitQuality = Basis.Network.Core.Compression.BasisAvatarBitPacking.BitQuality;
using S = BasisServerTests.DeltaTestSupport;

namespace BasisServerTests;

/// <summary>
/// End-to-end protocol simulation. Faithfully mirrors the server's keyframe/delta decision and
/// per-(sender,receiver) baseline selection (BasisServerReductionSystemEvents) and the client's
/// baseline gate (BasisNetworkHandleAvatar capture + BasisNetworkHandleAvatarDelta apply / drop),
/// driving the REAL codec through packet loss, rate throttling, quality switches, and keyframe
/// promotion. The invariant under test: whenever the client accepts a frame, its reconstruction
/// EXACTLY equals the sender's true pose at that frame — it never applies a delta against the wrong
/// baseline — and it re-synchronizes after loss.
///
/// NOTE: baseSeq is a single byte (matching the wire), so lossy runs are capped below 256 generations
/// to stay within one non-wrapping sequence window. A byte-seq collision needs ~256 consecutively
/// lost frames for one sender (a dead connection) and self-heals at the next keyframe; the loss-free
/// long-run test exercises wrap safely because the receiver's baseline is always current.
/// </summary>
public class AvatarDeltaProtocolTests
{
    private abstract class Frame { public int Q; public byte Seq; public long TrueGen; }
    private sealed class KeyFrame : Frame { public byte[] Payload = default!; }
    private sealed class DeltaFr : Frame { public byte BaseSeq; public byte[] Body = default!; }

    private readonly record struct Stats(int Applied, int Keyframes, int Deltas, int Dropped, long LastAppliedGen);

    private static byte[] Mutate(byte[] prev, BitQuality q, Random rng, bool bigJump)
    {
        var next = (byte[])prev.Clone();
        if (rng.NextDouble() < 0.6) next[0] = (byte)rng.Next(256); // root position drifts often
        int nBones = bigJump ? S.BoneCount : rng.Next(0, 4);
        var used = new HashSet<int>();
        for (int i = 0; i < nBones; i++)
        {
            int slot = bigJump ? i : rng.Next(S.BoneCount);
            if (!used.Add(slot)) continue;
            ulong maxv = (1UL << S.BoneWidth(q, slot)) - 1UL;
            S.SetBone(next, q, slot, (ulong)rng.NextInt64() & maxv);
        }
        if (bigJump)
        {
            next[S.ScaleOffset(q)] ^= 0xFF;
            next[S.HipsRotOffset(q)] ^= 0xFF;
        }
        return next;
    }

    private static Stats RunScenario(int gens, double loss, int servePeriod, bool switchQuality,
        int kfInterval, int seed, int[]? receiverQualitySchedule = null)
    {
        var rng = new Random(seed);

        // Build four evolving quality streams; big jumps happen on the same gens across qualities.
        var poses = new byte[4][][];
        for (int qi = 0; qi < 4; qi++) poses[qi] = new byte[gens][];
        for (int qi = 0; qi < 4; qi++) poses[qi][0] = S.MakeRealisticPayload((BitQuality)qi, rng);
        for (int g = 1; g < gens; g++)
        {
            bool big = rng.NextDouble() < 0.02;
            for (int qi = 0; qi < 4; qi++) poses[qi][g] = Mutate(poses[qi][g - 1], (BitQuality)qi, rng, big);
        }

        // Server global state.
        long keyframeGen = 0;
        byte keyframeSeq = 0;
        long lastKfGen = 0;
        var keyframePayload = new byte[4][];
        for (int qi = 0; qi < 4; qi++) keyframePayload[qi] = (byte[])poses[qi][0].Clone();
        bool currentIsKeyframe = true;
        var probe = new byte[BasisAvatarDeltaCompression.MaxDeltaSize(BitQuality.High)];

        // Server per-receiver baseline view.
        long srvBaselineGen = 0;
        int srvBaselineQ = -1;

        // Client state.
        byte[]? cliBaseline = null;
        byte cliBaselineSeq = 0;
        int cliBaselineQ = -1;
        long lastApplied = -1;
        int applied = 0, kfA = 0, dA = 0, dropped = 0;

        int rq = 3; // receiver starts at High

        for (int g = 0; g < gens; g++)
        {
            byte seq = (byte)g;

            bool isKf;
            if (g == 0) isKf = true;
            else
            {
                bool periodic = (g - lastKfGen) >= kfInterval;
                bool promote = false;
                if (!periodic)
                {
                    int probeLen = BasisAvatarDeltaCompression.BuildDelta(keyframePayload[3], poses[3][g], BitQuality.High, probe, 0);
                    promote = probeLen < 0 || probeLen >= BasisAvatarDeltaCompression.PayloadSize(BitQuality.High);
                }
                isKf = periodic || promote;
            }
            if (isKf)
            {
                keyframeGen = g; keyframeSeq = seq; lastKfGen = g;
                for (int qi = 0; qi < 4; qi++) keyframePayload[qi] = (byte[])poses[qi][g].Clone();
                currentIsKeyframe = true;
            }
            else currentIsKeyframe = false;

            if (receiverQualitySchedule != null) rq = receiverQualitySchedule[g];
            else if (switchQuality && g > 0 && rng.NextDouble() < 0.03) rq = rng.Next(4);

            if (g % servePeriod != 0) continue;

            // Server send decision (mirrors the hot send loop).
            bool sendDelta = !currentIsKeyframe && srvBaselineGen == keyframeGen && srvBaselineQ == rq;
            Frame frame;
            if (sendDelta)
            {
                var dst = new byte[BasisAvatarDeltaCompression.MaxDeltaSize((BitQuality)rq)];
                int body = BasisAvatarDeltaCompression.BuildDelta(keyframePayload[rq], poses[rq][g], (BitQuality)rq, dst, 0);
                frame = new DeltaFr { Q = rq, Seq = seq, BaseSeq = keyframeSeq, Body = dst[..body], TrueGen = g };
            }
            else
            {
                frame = new KeyFrame { Q = rq, Seq = keyframeSeq, Payload = (byte[])keyframePayload[rq].Clone(), TrueGen = keyframeGen };
                srvBaselineGen = keyframeGen; // server records the (possibly-lost) send unconditionally
                srvBaselineQ = rq;
            }

            if (rng.NextDouble() < loss) continue; // lost in transit

            // Client processing.
            if (frame is KeyFrame kf)
            {
                cliBaseline = kf.Payload; cliBaselineSeq = kf.Seq; cliBaselineQ = kf.Q;
                Assert.Equal(poses[kf.Q][kf.TrueGen], kf.Payload);
                lastApplied = kf.TrueGen; applied++; kfA++;
            }
            else
            {
                var d = (DeltaFr)frame;
                if (cliBaseline != null && cliBaselineQ == d.Q && cliBaselineSeq == d.BaseSeq)
                {
                    var recon = new byte[BasisAvatarDeltaCompression.PayloadSize((BitQuality)d.Q)];
                    bool ok = BasisAvatarDeltaCompression.TryApplyDelta(cliBaseline, d.Body, 0, d.Body.Length, (BitQuality)d.Q, recon);
                    Assert.True(ok, "a delta that matched the held baseline failed to apply");
                    Assert.Equal(poses[d.Q][d.TrueGen], recon); // THE invariant: exact reconstruction
                    lastApplied = d.TrueGen; applied++; dA++;
                }
                else dropped++;
            }
        }
        return new Stats(applied, kfA, dA, dropped, lastApplied);
    }

    [Theory]
    // loss, servePeriod, switchQuality
    [InlineData(0.0, 1, false)]
    [InlineData(0.0, 1, true)]
    [InlineData(0.0, 3, true)]
    [InlineData(0.1, 1, false)]
    [InlineData(0.1, 2, true)]
    [InlineData(0.3, 1, true)]
    [InlineData(0.3, 4, false)]
    [InlineData(0.5, 1, true)]
    [InlineData(0.5, 6, true)]
    public void Protocol_ReconstructsExactly_AndResyncs(double loss, int servePeriod, bool switchQuality)
    {
        const int gens = 240; // < 256: single non-wrapping seq window for lossy runs
        var st = RunScenario(gens, loss, servePeriod, switchQuality, kfInterval: 8,
            seed: (int)(loss * 1000) * 100 + servePeriod * 10 + (switchQuality ? 1 : 0));

        Assert.True(st.Applied > 0, "client never applied a frame");
        // Correctness is asserted inline; here we assert liveness (no permanent desync).
        if (loss <= 0.5)
        {
            int servedLast = (gens - 1) / servePeriod * servePeriod;
            // Re-sync within a few keyframe intervals of the end.
            Assert.True(st.LastAppliedGen >= servedLast - 8 * 8, $"stale: lastApplied={st.LastAppliedGen}, served up to {servedLast}");
        }
    }

    [Fact]
    public void Protocol_LongRun_NoLoss_SustainedExactReconstruction()
    {
        // 1000 gens exercises seq wrap safely (loss-free => baseline always current), plus many
        // promotions from the injected big jumps. Correctness asserted inline throughout.
        var st = RunScenario(gens: 1000, loss: 0.0, servePeriod: 1, switchQuality: true, kfInterval: 10, seed: 777);
        Assert.True(st.Deltas > 0 && st.Keyframes > 0);
        Assert.Equal(999, st.LastAppliedGen);
        Assert.Equal(0, st.Dropped);
    }

    [Fact]
    public void Protocol_QualitySwitch_ForcesRebaselineBeforeDelta()
    {
        // Force a quality change every 5 gens; with no loss the client must always hold a matching
        // (quality, baseSeq) baseline before any delta, so reconstruction stays exact.
        int gens = 200;
        var schedule = new int[gens];
        var rng = new Random(4);
        int cur = 3;
        for (int g = 0; g < gens; g++) { if (g % 5 == 0) cur = rng.Next(4); schedule[g] = cur; }
        var st = RunScenario(gens, loss: 0.0, servePeriod: 1, switchQuality: false, kfInterval: 8, seed: 4, receiverQualitySchedule: schedule);
        Assert.True(st.Applied > gens / 2);
        Assert.Equal(gens - 1, st.LastAppliedGen);
    }

    [Fact]
    public void Protocol_ModerateLoss_RecoversRepeatedly()
    {
        var st = RunScenario(gens: 240, loss: 0.35, servePeriod: 1, switchQuality: true, kfInterval: 6, seed: 12345);
        Assert.True(st.Keyframes >= 3, "expected repeated keyframe re-syncs under loss");
        Assert.True(st.Deltas > 0, "expected some deltas to apply between keyframes");
    }
}
