using Basis.Network.Core.Compression;
using Xunit;
using Xunit.Abstractions;
using BitQuality = Basis.Network.Core.Compression.BasisAvatarBitPacking.BitQuality;
using S = BasisServerTests.DeltaTestSupport;

namespace BasisServerTests;

/// <summary>
/// Idle-suppression decision (<see cref="BasisAvatarIdleSuppression.ShouldSend"/>) and a faithful
/// simulation of the client send gate. Suppression only drops byte-identical, additional-free frames,
/// so it is lossless; these tests pin that contract and characterize the packet reduction.
/// </summary>
public class IdleSuppressionTests
{
    private readonly ITestOutputHelper _out;
    public IdleSuppressionTests(ITestOutputHelper output) => _out = output;

    const double HB = BasisAvatarIdleSuppression.DefaultHeartbeatSeconds; // 1.0 s
    const double Dt = 0.05;                                               // 20 Hz

    static bool Send(byte[] cur, byte[] last, bool hasLast, bool add, bool linked, double now, double lastT)
        => BasisAvatarIdleSuppression.ShouldSend(cur, last, hasLast, add, linked, now, lastT, HB);

    [Fact]
    public void FirstFrame_AlwaysSends()
    {
        var p = S.MakeRealisticPayload(BitQuality.High, new Random(1));
        Assert.True(Send(p, Array.Empty<byte>(), hasLast: false, add: false, linked: false, now: 0, lastT: 0));
    }

    [Fact]
    public void IdenticalWithinHeartbeat_Suppressed()
    {
        var p = S.MakeRealisticPayload(BitQuality.High, new Random(2));
        var last = (byte[])p.Clone();
        Assert.False(Send(p, last, hasLast: true, add: false, linked: false, now: 0.5, lastT: 0.0));
    }

    [Fact]
    public void IdenticalHeartbeatElapsed_Sends()
    {
        var p = S.MakeRealisticPayload(BitQuality.High, new Random(3));
        var last = (byte[])p.Clone();
        Assert.True(Send(p, last, hasLast: true, add: false, linked: false, now: HB, lastT: 0.0));
    }

    [Fact]
    public void OneBitChanged_Sends()
    {
        var p = S.MakeRealisticPayload(BitQuality.High, new Random(4));
        var last = (byte[])p.Clone();
        p[10] ^= 0x01;
        Assert.True(Send(p, last, hasLast: true, add: false, linked: false, now: 0.1, lastT: 0.0));
    }

    [Fact]
    public void AdditionalData_ForcesSend()
    {
        var p = S.MakeRealisticPayload(BitQuality.High, new Random(5));
        var last = (byte[])p.Clone();
        Assert.True(Send(p, last, hasLast: true, add: true, linked: false, now: 0.1, lastT: 0.0));
    }

    [Fact]
    public void LinkedAvatarChange_ForcesSend()
    {
        var p = S.MakeRealisticPayload(BitQuality.High, new Random(6));
        var last = (byte[])p.Clone();
        Assert.True(Send(p, last, hasLast: true, add: false, linked: true, now: 0.1, lastT: 0.0));
    }

    [Fact]
    public void LengthChange_ForcesSend()
    {
        var p = S.MakeRealisticPayload(BitQuality.High, new Random(7));
        var last = new byte[p.Length - 1];
        Assert.True(Send(p, last, hasLast: true, add: false, linked: false, now: 0.1, lastT: 0.0));
    }

    /// <summary>Faithful model of the client gate (Compress + RecordLastSent): a frame is emitted only
    /// when ShouldSend is true, and that becomes the new baseline.</summary>
    static int SimulateSends(IReadOnlyList<byte[]> frames, double dt, double heartbeat)
    {
        byte[] last = Array.Empty<byte>();
        double lastT = 0;
        bool hasLast = false;
        int sends = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            double now = i * dt;
            if (BasisAvatarIdleSuppression.ShouldSend(frames[i], last, hasLast, false, false, now, lastT, heartbeat))
            {
                sends++;
                last = (byte[])frames[i].Clone();
                lastT = now;
                hasLast = true;
            }
        }
        return sends;
    }

    [Fact]
    public void IdlePlayer_SendsOnlyHeartbeats()
    {
        var pose = S.MakeRealisticPayload(BitQuality.High, new Random(100));
        int n = (int)(10.0 / Dt); // 200 frames, 10 s
        var frames = new List<byte[]>(n);
        for (int i = 0; i < n; i++) frames.Add(pose); // motionless: identical every frame
        int sends = SimulateSends(frames, Dt, HB);
        double reduction = 1.0 - (double)sends / n;
        _out.WriteLine($"Idle 10 s @20 Hz: {sends}/{n} frames sent ({reduction:P1} fewer packets)");
        Assert.True(sends <= 12, $"idle sends {sends} exceeded heartbeat budget");
        Assert.True(reduction >= 0.90, $"idle packet reduction {reduction:P1} < 90%");
    }

    [Fact]
    public void MixedTimeline_ReducesPackets()
    {
        var rng = new Random(200);
        var frames = new List<byte[]>();
        // 4 s idle
        var restA = S.MakeRealisticPayload(BitQuality.High, rng);
        for (int i = 0; i < 80; i++) frames.Add(restA);
        // 2 s moving: a distinct quantized pose every frame
        byte[] lastMove = restA;
        for (int i = 0; i < 40; i++) { lastMove = S.MakeRealisticPayload(BitQuality.High, rng); frames.Add(lastMove); }
        // 4 s idle where the player came to rest (holds the last moving pose)
        for (int i = 0; i < 80; i++) frames.Add(lastMove);

        int n = frames.Count; // 200
        int sends = SimulateSends(frames, Dt, HB);
        double reduction = 1.0 - (double)sends / n;
        _out.WriteLine($"Mixed (idle/move/idle) 10 s @20 Hz: {sends}/{n} frames sent ({reduction:P1} fewer packets)");
        Assert.True(reduction >= 0.50, $"mixed packet reduction {reduction:P1} < 50%");
    }

    [Fact]
    public void ContinuousMotion_SendsEveryFrame()
    {
        var rng = new Random(300);
        int n = 100;
        var frames = new List<byte[]>(n);
        for (int i = 0; i < n; i++) frames.Add(S.MakeRealisticPayload(BitQuality.High, rng));
        int sends = SimulateSends(frames, Dt, HB);
        Assert.Equal(n, sends); // no suppression under real motion — never drops a moving frame
    }

    [Fact]
    public void PrintPacketAndByteTable()
    {
        int payload = S.PayloadSize(BitQuality.High); // 232 B High
        const int wireOverhead = 1;                   // app sequence byte (LiteNetLib header excluded)
        int perPacket = payload + wireOverhead;
        _out.WriteLine($"High payload = {payload} B, per-packet wire ≈ {perPacket} B (excl. UDP/LiteNetLib header)");
        _out.WriteLine("");
        _out.WriteLine("scenario (10 s @20 Hz, 200 frames) | packets before→after | uplink B/s before→after | reduction");

        void Row(string name, List<byte[]> frames)
        {
            int n = frames.Count;
            int sends = SimulateSends(frames, Dt, HB);
            double before = n * perPacket / 10.0;
            double after = sends * perPacket / 10.0;
            _out.WriteLine($"  {name,-24} | {n,4} → {sends,3}          | {before,7:F0} → {after,6:F0}        | {1.0 - (double)sends / n,6:P1}");
        }

        var rng = new Random(2025);
        var idle = new List<byte[]>();
        var idlePose = S.MakeRealisticPayload(BitQuality.High, rng);
        for (int i = 0; i < 200; i++) idle.Add(idlePose);

        var mixed = new List<byte[]>();
        var rest = S.MakeRealisticPayload(BitQuality.High, rng);
        for (int i = 0; i < 120; i++) mixed.Add(rest);
        byte[] lm = rest;
        for (int i = 0; i < 20; i++) { lm = S.MakeRealisticPayload(BitQuality.High, rng); mixed.Add(lm); }
        for (int i = 0; i < 60; i++) mixed.Add(lm);

        var moving = new List<byte[]>();
        for (int i = 0; i < 200; i++) moving.Add(S.MakeRealisticPayload(BitQuality.High, rng));

        Row("fully idle", idle);
        Row("mostly idle (10% move)", mixed);
        Row("continuous motion", moving);
    }
}
