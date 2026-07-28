using System;
using System.IO;
using Basis.Network.Core;
using Basis.Scripts.Networking.Voice.Testing;
using NUnit.Framework;
using static SerializableBasis;

/// <summary>
/// End-to-end offline tests of the real voice pipeline (Opus encode -> wire ->
/// server-relay wrap -> impaired network -> real BasisAudioReceiver playback)
/// via <see cref="BasisVoiceSim"/>. Every run is deterministic from its seed.
/// </summary>
[TestFixture]
public class BasisVoicePipelineTests
{
    static BasisVoiceScenario Scenario(string name, BasisVoiceSignal signal, BasisVoiceNetProfile profile)
    {
        return new BasisVoiceScenario
        {
            Name = name,
            Signal = signal,
            Profile = profile,
            Seed = 1234,
            KeepAudio = true,
        };
    }

    static double EnergyInWindow(float[] mono, int rate, double fromSec, double toSec)
    {
        int start = Math.Max(0, (int)(fromSec * rate));
        int end = Math.Min(mono.Length, (int)(toSec * rate));
        double sum = 0;
        for (int i = start; i < end; i++)
            sum += mono[i] * mono[i];
        return end > start ? Math.Sqrt(sum / (end - start)) : 0;
    }

    // ==================== Clean path ====================

    [Test]
    public void PerfectNetwork_PlaysClean()
    {
        var r = BasisVoiceSim.Run(Scenario("perfect", BasisVoiceSignal.SpeechLike, BasisVoiceSimMatrix.Perfect()));
        Assert.IsTrue(r.Passed, r.Summary);
        Assert.AreEqual(0, r.GenuineUnderruns, "clean run must not count genuine underruns");
        Assert.AreEqual(0, r.PlcCount, "clean run must not conceal packets");
        Assert.AreEqual(0, r.NotchCount, "clean run must not bubble");
        Assert.Greater(r.PacketsSent, 100, "speech signal should transmit most ticks");
        Assert.Greater(r.SilentMicTicks, 10, "speech signal must exercise the silence path");
    }

    [Test]
    public void PerfectNetwork_LatencyWithinBudget()
    {
        var r = BasisVoiceSim.Run(Scenario("latency", BasisVoiceSignal.ImpulseTrain, BasisVoiceSimMatrix.Perfect()));
        Assert.IsTrue(r.Passed, r.Summary);
        // 5-packet pre-roll (100 ms) + 40 ms transit + callback/encode granularity.
        Assert.GreaterOrEqual(r.LatencyMs, 60, r.Summary);
        Assert.LessOrEqual(r.LatencyMs, 320, r.Summary);
    }

    [Test]
    public void LowerJitterFloor_ReducesLatency()
    {
        var deep = Scenario("floor5", BasisVoiceSignal.ImpulseTrain, BasisVoiceSimMatrix.Perfect());
        var shallow = Scenario("floor2", BasisVoiceSignal.ImpulseTrain, BasisVoiceSimMatrix.Perfect());
        shallow.JitterBufferFloor = 2;
        var rDeep = BasisVoiceSim.Run(deep);
        var rShallow = BasisVoiceSim.Run(shallow);
        Assert.IsTrue(rDeep.Passed, rDeep.Summary);
        Assert.IsTrue(rShallow.Passed, rShallow.Summary);
        Assert.Greater(rDeep.LatencyMs - rShallow.LatencyMs, 30,
            $"3 fewer pre-roll packets should save ~60 ms (deep={rDeep.LatencyMs:F0} shallow={rShallow.LatencyMs:F0})");
    }

    [Test]
    public void MuteResume_RearmsWithoutGenuineUnderruns()
    {
        var r = BasisVoiceSim.Run(Scenario("mute-resume", BasisVoiceSignal.SpeechLike, BasisVoiceSimMatrix.Perfect()));
        Assert.IsTrue(r.Passed, r.Summary);
        Assert.GreaterOrEqual(r.RearmCount, 1, "inter-utterance pauses must trigger the idle reset + rearm");
        Assert.AreEqual(0, r.GenuineUnderruns, "sender-intended silence must not count as genuine underrun");
    }

    // ==================== Loss / recovery ====================

    [Test]
    public void PacketLoss_EngagesFecOrPlc()
    {
        var r = BasisVoiceSim.Run(Scenario("loss15", BasisVoiceSignal.Sine, BasisVoiceSimMatrix.Loss15()));
        Assert.IsTrue(r.Passed, r.Summary);
        Assert.Greater(r.PacketsDropped, 10, "profile should actually drop packets");
        Assert.Greater(r.FecRecoveredCount + r.PlcCount, 0, "losses must be concealed, not ignored");
        Assert.Greater(r.ReceiverLossPercent01, 0.02f, "app-layer loss metric should register the loss");
    }

    [Test]
    public void BurstLossBeyondRingLookahead_ResyncsAndRecovers()
    {
        var s = Scenario("burst-resync", BasisVoiceSignal.Sine, BasisVoiceSimMatrix.Burst800());
        var r = BasisVoiceSim.Run(s);
        Assert.IsEmpty(r.Error, r.Summary);
        double tailEnergy = EnergyInWindow(r.OutputMono, s.OutputSampleRate, s.DurationSeconds - 1.5, s.DurationSeconds);
        Assert.Greater(tailEnergy, 0.02, "audio must resume after a 40-packet burst (resync path)");
    }

    [Test]
    public void NetworkStall_CountsGenuineUnderrunsAndGrowsDepth()
    {
        var r = BasisVoiceSim.Run(Scenario("stall", BasisVoiceSignal.Sine, BasisVoiceSimMatrix.Stall600()));
        Assert.IsEmpty(r.Error, r.Summary);
        Assert.GreaterOrEqual(r.GenuineUnderruns, 1, "a mid-stream network stall is a genuine underrun");
        Assert.Greater(r.FinalPrerollDepth, r.PrerollFloor, "adaptive depth should grow after a stall");
    }

    [Test]
    public void SenderHitch_CountsGenuineUnderrunsAndGrowsDepth()
    {
        var s = Scenario("sender-hitch", BasisVoiceSignal.Sine, BasisVoiceSimMatrix.Perfect());
        s.SenderHitchAtSeconds = 2f;
        s.SenderHitchDurationMs = 300f;
        var r = BasisVoiceSim.Run(s);
        Assert.IsEmpty(r.Error, r.Summary);
        Assert.GreaterOrEqual(r.GenuineUnderruns, 1, "a sender frame hitch starves the receiver mid-speech");
        Assert.Greater(r.FinalPrerollDepth, r.PrerollFloor, "adaptive depth should absorb repeat hitches");
    }

    [Test]
    public void ReceiverHang_RecoversAfterward()
    {
        var s = Scenario("receiver-hang", BasisVoiceSignal.Sine, BasisVoiceSimMatrix.Perfect());
        s.ReceiverHangAtSeconds = 2.5f;
        s.ReceiverHangDurationMs = 500f;
        var r = BasisVoiceSim.Run(s);
        Assert.IsEmpty(r.Error, r.Summary);
        double tailEnergy = EnergyInWindow(r.OutputMono, s.OutputSampleRate, s.DurationSeconds - 1.5, s.DurationSeconds);
        Assert.Greater(tailEnergy, 0.02, "audio must continue after the app hang");
    }

    // ==================== Codec / resample variants ====================

    [Test]
    public void Resampler44100_PlaysClean()
    {
        var s = Scenario("out44100", BasisVoiceSignal.Sweep, BasisVoiceSimMatrix.Perfect());
        s.OutputSampleRate = 44100;
        var r = BasisVoiceSim.Run(s);
        Assert.IsTrue(r.Passed, r.Summary);
        Assert.AreEqual(0, r.NotchCount, "the polyphase resample path must not notch");
    }

    [Test]
    public void FrameDuration40ms_HalvesPacketsAndPlaysClean()
    {
        var r20 = BasisVoiceSim.Run(Scenario("frame20", BasisVoiceSignal.Sine, BasisVoiceSimMatrix.Perfect()));
        var s40 = Scenario("frame40", BasisVoiceSignal.Sine, BasisVoiceSimMatrix.Perfect());
        s40.FrameDurationSeconds = 0.04f;
        var r40 = BasisVoiceSim.Run(s40);
        Assert.IsTrue(r20.Passed, r20.Summary);
        Assert.IsTrue(r40.Passed, r40.Summary);
        Assert.Less(r40.PacketsSent, (int)(r20.PacketsSent * 0.6f) + 5, "40 ms frames should send ~half the packets");
    }

    // ==================== Infrastructure ====================

    [Test]
    public void SameSeed_IsDeterministic()
    {
        var a = BasisVoiceSim.Run(Scenario("det", BasisVoiceSignal.SpeechLike, BasisVoiceSimMatrix.JitterLoss()));
        var b = BasisVoiceSim.Run(Scenario("det", BasisVoiceSignal.SpeechLike, BasisVoiceSimMatrix.JitterLoss()));
        Assert.IsEmpty(a.Error, a.Summary);
        Assert.AreEqual(a.PacketsSent, b.PacketsSent);
        Assert.AreEqual(a.PacketsDropped, b.PacketsDropped);
        Assert.AreEqual(a.PlcCount, b.PlcCount);
        Assert.AreEqual(a.GenuineUnderruns, b.GenuineUnderruns);
        Assert.AreEqual(a.OutputMono.Length, b.OutputMono.Length);
        for (int i = 0; i < a.OutputMono.Length; i++)
        {
            if (a.OutputMono[i] != b.OutputMono[i])
                Assert.Fail($"outputs diverge at sample {i}: {a.OutputMono[i]} vs {b.OutputMono[i]}");
        }
    }

    [Test]
    public void VoiceWire_RoundTripsThroughServerWrap()
    {
        var rng = new Random(7);
        byte[] payload = new byte[113];
        rng.NextBytes(payload);

        AudioSegmentDataMessage seg = default;
        seg.buffer = payload;
        seg.TotalLength = payload.Length;
        seg.LengthUsed = payload.Length;
        seg.SequenceNumber = 200;
        seg.TotalPlayedInSilence = 3;

        var clientWriter = new NetDataWriter();
        seg.Serialize(clientWriter);

        AudioSegmentDataMessage atServer = default;
        atServer.Deserialize(new NetDataReader(clientWriter.Data, 0, clientWriter.Length));

        ServerAudioSegmentMessage relay = default;
        relay.playerIdMessage.playerID = 42;
        relay.audioSegmentData = atServer;
        var serverWriter = new NetDataWriter();
        relay.Serialize(serverWriter, false);

        ServerAudioSegmentMessage atClient = default;
        atClient.Deserialize(new NetDataReader(serverWriter.Data, 0, serverWriter.Length), false);

        Assert.AreEqual(42, atClient.playerIdMessage.playerID);
        Assert.AreEqual(200, atClient.audioSegmentData.SequenceNumber);
        Assert.AreEqual(3, atClient.audioSegmentData.TotalPlayedInSilence);
        Assert.AreEqual(payload.Length, atClient.audioSegmentData.LengthUsed);
        for (int i = 0; i < payload.Length; i++)
            Assert.AreEqual(payload[i], atClient.audioSegmentData.buffer[i], $"payload byte {i}");
    }

    [Test]
    public void Diagnostic_SpeechPerfectTimelineDump()
    {
        var s = Scenario("diag", BasisVoiceSignal.SpeechLike, BasisVoiceSimMatrix.Perfect());
        s.TraceTimeline = true;
        var r = BasisVoiceSim.Run(s);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# " + r.Summary);
        var notches = BasisVoiceQualityAnalysis.FindNotches(r.OutputMono, 48000);
        foreach (var n in notches)
            sb.AppendLine($"# NOTCH @{n.StartMs / 1000.0:F3}s dur={n.DurationMs:F2}ms");
        sb.Append(r.TimelineCsv);
        File.WriteAllText("Temp/voice_diag.csv", sb.ToString());
        Assert.IsEmpty(r.Error, r.Summary);
    }

    [Test]
    public void NotchDetector_FindsInjectedUnderrunNotch()
    {
        const int rate = 48000;
        float[] x = new float[rate * 2];
        for (int i = 0; i < x.Length; i++)
            x[i] = 0.3f * (float)Math.Sin(2.0 * Math.PI * 300.0 * i / rate);
        // Punch a 2.5 ms fade-to-zero notch at 1.0 s — the underrun signature.
        int start = rate;
        int len = rate * 25 / 10000;
        for (int i = 0; i < len; i++)
            x[start + i] = 0f;

        var notches = BasisVoiceQualityAnalysis.FindNotches(x, rate);
        Assert.AreEqual(1, notches.Count);
        Assert.AreEqual(1000.0, notches[0].StartMs, 5.0);
        Assert.AreEqual(2.5, notches[0].DurationMs, 1.5);
    }
}
