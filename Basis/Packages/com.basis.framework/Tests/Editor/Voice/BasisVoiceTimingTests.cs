using Basis.Scripts.Networking.Voice.Testing;
using NUnit.Framework;

/// <summary>
/// Latency, late-arrival, and early-arrival behavior of the real voice pipeline
/// under the offline sim: the adaptive latency loop (deadline-hold salvage, PLC
/// starve bridge, catch-up drain, preemptive expand, arrival-gap depth tracking)
/// and the latency-over-time / standing-depth metrics that observe it.
/// </summary>
[TestFixture]
public class BasisVoiceTimingTests
{
    static BasisVoiceScenario Scenario(string name, BasisVoiceSignal signal, BasisVoiceNetProfile profile, int seed = 1234)
    {
        return new BasisVoiceScenario
        {
            Name = name,
            Signal = signal,
            Profile = profile,
            Seed = seed,
            KeepAudio = true,
        };
    }

    // ==================== Late arrivals ====================

    [Test]
    public void ReorderedStragglers_ArePlayedNotConcealed()
    {
        // 10% of packets arrive +80 ms late, out of order, nothing lost. The
        // deadline-hold must salvage far more of them than it conceals.
        var r = BasisVoiceSim.Run(Scenario("reorder", BasisVoiceSignal.ImpulseTrain, BasisVoiceSimMatrix.Reorder10()));
        Assert.IsTrue(r.Passed, r.Summary);
        Assert.Greater(r.LateSalvagedCount, 15, "held gaps should be resolved by the late packet itself: " + r.Summary);
        Assert.Less(r.PlcCount + r.FecRecoveredCount, r.LateSalvagedCount,
            "salvage should beat concealment on pure reordering: " + r.Summary);
        Assert.AreEqual(0, r.NotchCount, "reordering without loss must not bubble: " + r.Summary);
    }

    [Test]
    public void SalvagedLatePacket_KeepsSineFidelity()
    {
        // On a continuous tone, jitter-induced reordering used to conceal packets
        // (audible warble, SNR collapse). With hold+salvage the tone must survive
        // nearly transparently.
        var r = BasisVoiceSim.Run(Scenario("sine-jitter", BasisVoiceSignal.Sine, BasisVoiceSimMatrix.Jitter30()));
        Assert.IsTrue(r.Passed, r.Summary);
        Assert.GreaterOrEqual(r.MedianSegSnrDb, 40.0, "salvage should keep the tone intact: " + r.Summary);
        Assert.AreEqual(0, r.GenuineUnderruns, r.Summary);
    }

    // ==================== Late spikes / starve bridging ====================

    [Test]
    public void LateSpikes_AreBridgedWithoutNotches()
    {
        // Every 2 s, six packets arrive +150 ms late together. The PLC bridge must
        // cover the gap (no silence notch punched into the audio) and the standing
        // depth must settle near the grown target, not accumulate per spike.
        var r = BasisVoiceSim.Run(Scenario("latespike", BasisVoiceSignal.ImpulseTrain, BasisVoiceSimMatrix.LateSpike()));
        Assert.IsTrue(r.Passed, r.Summary);
        Assert.AreEqual(0, r.NotchCount, "spike gaps must be bridged, not notched: " + r.Summary);
        Assert.Greater(r.StarvePlcCount, 0, "the starve bridge should have engaged: " + r.Summary);
        Assert.LessOrEqual(r.StandingFramesEnd, 10, "standing depth must not accumulate per spike: " + r.Summary);
        Assert.Greater(r.FinalPrerollDepth, r.PrerollFloor, "repeated spikes should grow the depth target: " + r.Summary);
    }

    // ==================== Standing-latency recovery ====================

    [Test]
    public void NetworkStall_StandingLatencyRecovers()
    {
        // A 600 ms delivery stall floods ~30 packets on release. Without the
        // catch-up drain that backlog stayed as permanent latency on a continuous
        // talker; the flush must cut it and the trim/decay bring it near target.
        var r = BasisVoiceSim.Run(Scenario("stall", BasisVoiceSignal.ImpulseTrain, BasisVoiceSimMatrix.Stall600()));
        Assert.IsTrue(r.Passed, r.Summary);
        Assert.Greater(r.StandingFramesMax, 15, "the stall should have piled a real backlog: " + r.Summary);
        Assert.LessOrEqual(r.StandingFramesEnd, 10, "backlog must drain back toward target: " + r.Summary);
        Assert.Greater(r.FlushedPackets, 0, "a hopeless backlog takes the flush path: " + r.Summary);
    }

    [Test]
    public void CongestionSwell_StandingLatencyRecovers()
    {
        // Transit rises +250 ms and back over 1.5 s. The falling edge piles a
        // backlog (and delivers reordered); the drain must reclaim it.
        var r = BasisVoiceSim.Run(Scenario("congestion", BasisVoiceSignal.ImpulseTrain, BasisVoiceSimMatrix.Congestion()));
        Assert.IsTrue(r.Passed, r.Summary);
        Assert.LessOrEqual(r.StandingFramesEnd, 10, "post-congestion backlog must drain: " + r.Summary);
        Assert.AreEqual(0, r.NotchCount, r.Summary);
    }

    [Test]
    public void ReceiverHang_BacklogIsFlushedAfterward()
    {
        // A 500 ms app hang leaves ~24 packets of backlog when callbacks resume —
        // the same standing-latency problem from the receiving side.
        var s = Scenario("hang", BasisVoiceSignal.SpeechLike, BasisVoiceSimMatrix.Perfect());
        s.ReceiverHangAtSeconds = 2.5f;
        s.ReceiverHangDurationMs = 500f;
        var r = BasisVoiceSim.Run(s);
        Assert.IsEmpty(r.Error, r.Summary);
        Assert.Greater(r.FlushedPackets, 0, "hang backlog should flush: " + r.Summary);
        Assert.LessOrEqual(r.StandingFramesEnd, 8, "standing depth must return to target: " + r.Summary);
    }

    // ==================== Early arrivals ====================

    [Test]
    public void BatchedDelivery_StaysNearTargetWithoutUnderruns()
    {
        // Delivery batched in 5-packet clumps: the tail of each batch is early,
        // depth pulses by 5. The gap tracker must size the target for the batch
        // period; pulses must neither underrun nor ratchet the latency up.
        var r = BasisVoiceSim.Run(Scenario("earlyburst", BasisVoiceSignal.ImpulseTrain, BasisVoiceSimMatrix.EarlyBurst()));
        Assert.IsTrue(r.Passed, r.Summary);
        Assert.AreEqual(0, r.NotchCount, r.Summary);
        Assert.LessOrEqual(r.GenuineUnderruns, 2, "batching should be absorbed after the tracker learns: " + r.Summary);
        Assert.LessOrEqual(r.StandingFramesEnd, 9, "batching must not ratchet standing latency: " + r.Summary);
    }

    [Test]
    public void DuplicatedPackets_DoNotInflateThePrerollGate()
    {
        // 30% duplication: dup arrivals must not advance the fill gate (a dup adds
        // no audio) — playback must still start clean and stay clean.
        var p = new BasisVoiceNetProfile { Name = "dup30", LatencyMs = 40f, DupChance = 0.30f };
        var r = BasisVoiceSim.Run(Scenario("dup", BasisVoiceSignal.SpeechLike, p));
        Assert.IsEmpty(r.Error, r.Summary);
        Assert.Greater(r.PacketsDuped, 20, "profile should actually duplicate: " + r.Summary);
        Assert.AreEqual(0, r.GenuineUnderruns, r.Summary);
        Assert.AreEqual(0, r.NotchCount, r.Summary);
    }

    // ==================== Adaptive target / expand ====================

    [Test]
    public void GrownTarget_IsBackedByRealStandingDepth()
    {
        // Repeated reordering grows the depth target; on a continuous talker only
        // the preemptive expand can raise the LIVE standing depth to meet it.
        var r = BasisVoiceSim.Run(Scenario("reorder-depth", BasisVoiceSignal.ImpulseTrain, BasisVoiceSimMatrix.Reorder10()));
        Assert.IsTrue(r.Passed, r.Summary);
        Assert.Greater(r.FinalPrerollDepth, r.PrerollFloor, "stragglers should grow the target: " + r.Summary);
        Assert.GreaterOrEqual(r.StandingFramesEnd, r.FinalPrerollDepth - 2,
            "standing depth must rise to make the grown target real: " + r.Summary);
    }

    [Test]
    public void CleanNetwork_LatencyStaysAtFloorAndFlat()
    {
        // The whole adaptive apparatus must cost nothing when the network is clean:
        // standing stays at the floor target and the latency curve stays flat.
        var r = BasisVoiceSim.Run(Scenario("clean", BasisVoiceSignal.ImpulseTrain, BasisVoiceSimMatrix.Perfect()));
        Assert.IsTrue(r.Passed, r.Summary);
        Assert.AreEqual(0, r.GenuineUnderruns, r.Summary);
        Assert.LessOrEqual(r.StandingFramesMax, 7, "clean network must not inflate depth: " + r.Summary);
        Assert.That(r.LatencyEndMs, Is.EqualTo(r.LatencyStartMs).Within(30), "no latency creep: " + r.Summary);
        Assert.AreEqual(0, r.FlushedPackets, r.Summary);
        Assert.AreEqual(0, r.TrimmedQuietFrames + r.AcceleratedFrames, "no catch-up on a clean net: " + r.Summary);
    }

    [Test]
    public void CleanSpeech_NoArtifactsAcrossSeeds()
    {
        // Guard across pause patterns: no seed of clean speech may bubble, conceal,
        // or count underruns (seed 2045 caught a real marginal case before).
        foreach (int seed in new[] { 1234, 2045, 2856, 3667 })
        {
            var r = BasisVoiceSim.Run(Scenario("clean-speech", BasisVoiceSignal.SpeechLike, BasisVoiceSimMatrix.Perfect(), seed));
            Assert.IsTrue(r.Passed, $"seed {seed}: {r.Summary}");
            Assert.AreEqual(0, r.GenuineUnderruns, $"seed {seed}: {r.Summary}");
            Assert.AreEqual(0, r.NotchCount, $"seed {seed}: {r.Summary}");
            Assert.AreEqual(0, r.PlcCount, $"seed {seed}: {r.Summary}");
        }
    }

    // ==================== Time compression unit behavior ====================

    [Test]
    public void Accelerate_ShortensPeriodicAudioTransparently()
    {
        const int rate = 48000;
        float[] pcm = new float[960];
        // 150 Hz voiced-like tone: period 320 samples, well inside the search range.
        for (int i = 0; i < pcm.Length; i++)
            pcm[i] = 0.4f * (float)System.Math.Sin(2.0 * System.Math.PI * 150.0 * i / rate);

        int newLen = BasisVoiceTimeCompress.AccelerateInPlace(pcm, pcm.Length, rate, out int saved);
        Assert.Greater(saved, 0, "periodic audio must yield a period");
        Assert.AreEqual(pcm.Length - saved, newLen);
        Assert.That(saved, Is.EqualTo(320).Within(6), "removed lag should match the pitch period");

        // The compressed audio must still be the same tone — no discontinuity. Verify
        // by checking the max sample-to-sample step stays in the same class as the
        // tone's own slope (a splice click would step by ~2x the amplitude).
        float maxStep = 0f;
        for (int i = 1; i < newLen; i++)
        {
            float step = System.Math.Abs(pcm[i] - pcm[i - 1]);
            if (step > maxStep) maxStep = step;
        }
        float toneSlope = 0.4f * 2f * (float)System.Math.PI * 150f / rate;
        Assert.LessOrEqual(maxStep, toneSlope * 1.5f, "splice must stay phase-continuous");
    }

    [Test]
    public void Accelerate_LeavesNoiseAlone()
    {
        var rng = new System.Random(99);
        float[] pcm = new float[960];
        for (int i = 0; i < pcm.Length; i++)
            pcm[i] = 0.2f * (float)(rng.NextDouble() * 2.0 - 1.0);
        int newLen = BasisVoiceTimeCompress.AccelerateInPlace(pcm, pcm.Length, 48000, out int saved);
        Assert.AreEqual(0, saved, "white noise has no confident period");
        Assert.AreEqual(pcm.Length, newLen);
    }
}
