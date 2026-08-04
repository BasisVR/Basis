using System;
using System.Collections.Generic;
using Basis.Network.Core;
using Basis.Scripts.Networking.Receivers;
using static SerializableBasis;

namespace Basis.Scripts.Networking.Voice.Testing
{
    /// <summary>
    /// Offline, deterministic exerciser for the voice pipeline:
    /// mic frames -> REAL Opus encode (same CTLs as <c>BasisAudioTransmission</c>) ->
    /// REAL <see cref="AudioSegmentDataMessage"/> wire serialize -> simulated server relay
    /// (mirrors <c>HandleVoiceMessage</c>/<c>SendVoiceMessageToClients</c>: deserialize, wrap in a REAL
    /// <see cref="ServerAudioSegmentMessage"/>, serialize once) -> seeded network impairments
    /// (latency / jitter / loss / bursts / stalls) -> REAL <see cref="BasisAudioReceiver"/>
    /// (jitter buffer, FEC/PLC, idle reset, adaptive depth, fades, resampler, limiter) pulled
    /// on a virtual audio clock. The rendered output is scored against the input signal and a
    /// codec-only baseline, so network/timing damage separates from inherent Opus loss.
    ///
    /// Out of scope (needs a live client): the AudioSource enable/disable state machine,
    /// Steam Audio spatialization, mic-device capture, and the recipient/range filtering
    /// (the sim assumes the listener is in range the whole run).
    /// </summary>
    public enum BasisVoiceSignal
    {
        /// <summary>Vowel-like harmonic tone with syllable modulation and real inter-utterance silences.</summary>
        SpeechLike,
        /// <summary>Continuous 440 Hz sine — cleanest SNR probe.</summary>
        Sine,
        /// <summary>Log sweep 100 Hz → 8 kHz — resampler/codec fidelity probe.</summary>
        Sweep,
        /// <summary>Damped clicks every 500 ms over a low noise floor — latency probe.</summary>
        ImpulseTrain,
    }

    public sealed class BasisVoiceNetProfile
    {
        public string Name = "perfect";
        public float LatencyMs = 40f;
        public float JitterMs = 0f;
        public float LossChance = 0f;
        public float DupChance = 0f;
        /// <summary>Every this many seconds, drop <see cref="BurstLossPackets"/> consecutive packets. 0 = off.</summary>
        public float BurstIntervalSeconds = 0f;
        public int BurstLossPackets = 0;
        /// <summary>One-shot delivery stall: everything due inside the window arrives at its end. 0 = off.</summary>
        public float StallAtSeconds = 0f;
        public float StallDurationMs = 0f;

        /// <summary>
        /// Chance a packet individually takes a slow path: +<see cref="ReorderDelayMs"/>
        /// transit on top of base latency/jitter, arriving out of order behind packets
        /// sent after it. The classic "late arrival" a jitter buffer must re-slot.
        /// </summary>
        public float ReorderChance = 0f;
        public float ReorderDelayMs = 0f;

        /// <summary>
        /// Every interval, <see cref="LateSpikePackets"/> consecutive packets are all
        /// delayed by +<see cref="LateSpikeDelayMs"/> — a correlated lateness burst
        /// (wifi retransmit clump, bufferbloat pulse). Nothing is lost; everything
        /// arrives, late and bunched.
        /// </summary>
        public float LateSpikeIntervalSeconds = 0f;
        public int LateSpikePackets = 0;
        public float LateSpikeDelayMs = 0f;

        /// <summary>
        /// Delivery batching: arrivals are held and released together on a grid of
        /// this many packet-periods (20 ms each). Models transports that coalesce
        /// datagrams — the tail of each batch arrives EARLY relative to its playback
        /// slot, piling queue depth in steps instead of a smooth 1-in-1-out cadence.
        /// </summary>
        public int EarlyCoalescePackets = 0;

        /// <summary>
        /// Congestion swell: transit latency ramps linearly from base up to
        /// +<see cref="LatencyRampPeakMs"/> at the midpoint of the window and back
        /// down by its end. The falling edge naturally delivers packets out of order
        /// (later-sent overtakes earlier-sent), exactly like a clearing queue.
        /// </summary>
        public float LatencyRampAtSeconds = 0f;
        public float LatencyRampPeakMs = 0f;
        public float LatencyRampDurationSeconds = 0f;

        public bool Impaired =>
            JitterMs > 0f || LossChance > 0f || DupChance > 0f ||
            (BurstIntervalSeconds > 0f && BurstLossPackets > 0) || StallDurationMs > 0f ||
            (ReorderChance > 0f && ReorderDelayMs > 0f) ||
            (LateSpikeIntervalSeconds > 0f && LateSpikePackets > 0 && LateSpikeDelayMs > 0f) ||
            EarlyCoalescePackets > 1 ||
            (LatencyRampPeakMs > 0f && LatencyRampDurationSeconds > 0f);

        public BasisVoiceNetProfile Clone() => (BasisVoiceNetProfile)MemberwiseClone();
    }

    public sealed class BasisVoiceScenario
    {
        public string Name = "unnamed";
        public BasisVoiceSignal Signal = BasisVoiceSignal.SpeechLike;
        public double DurationSeconds = 6.0;
        public int Seed = 1234;
        public BasisVoiceNetProfile Profile = new BasisVoiceNetProfile();

        public int Bitrate = LocalOpusSettings.DefaultBitrate;
        public int EncoderPacketLossPercent = 10;
        /// <summary>0.02 or 0.04 — applied to the SharedOpusSettings static for the run and restored after.</summary>
        public float FrameDurationSeconds = 0.02f;
        /// <summary>Applied to RemoteOpusSettings.JitterBufferSize for the run and restored after.</summary>
        public int JitterBufferFloor = 5;

        /// <summary>48000 = no resample; 44100 exercises the polyphase resampler path.</summary>
        public int OutputSampleRate = 48000;
        public int CallbackFrames = 1024;
        public int OutputChannels = 2;

        /// <summary>Sender main-thread hitch: mic ticks inside the window all fire, bunched, at its end. 0 = off.</summary>
        public float SenderHitchAtSeconds = 0f;
        public float SenderHitchDurationMs = 0f;
        /// <summary>Receiver full-app hang: audio callbacks inside the window are skipped (device gets silence). 0 = off.</summary>
        public float ReceiverHangAtSeconds = 0f;
        public float ReceiverHangDurationMs = 0f;

        /// <summary>When true and the run is unimpaired, Evaluate applies hard clean-playback thresholds.</summary>
        public bool HardQuality = true;
        /// <summary>Keep the rendered/reference/baseline PCM on the result (for WAV export / tests).</summary>
        public bool KeepAudio = true;

        /// <summary>Record a per-event timeline CSV (callbacks + packet arrivals) onto the result for debugging.</summary>
        public bool TraceTimeline;

        public bool AnyLocalImpairment => SenderHitchDurationMs > 0f || ReceiverHangDurationMs > 0f;

        public BasisVoiceScenario Clone()
        {
            var c = (BasisVoiceScenario)MemberwiseClone();
            c.Profile = Profile.Clone();
            return c;
        }
    }

    public sealed class BasisVoiceSimResult
    {
        public string ScenarioName;
        public string ProfileName;
        public BasisVoiceSignal Signal;
        public int Seed;

        // Transport
        public int PacketsSent;
        public int PacketsDropped;
        public int PacketsDuped;
        public int PacketsDelivered;
        public int SilentMicTicks;

        // Receiver counters (real pipeline diagnostics)
        public int PlcCount;
        public int FecRecoveredCount;
        public int SilenceInjectedCount;
        public int GenuineUnderruns;
        public int RearmCount;
        public int FinalPrerollDepth;
        public int PrerollFloor;
        public float ReceiverLossPercent01;
        public int StarvePlcCount;
        public int TrimmedQuietFrames;
        public int AcceleratedFrames;
        public double AcceleratedSavedMs;
        public int FlushedPackets;
        public int LateSalvagedCount;
        public int ExpandInsertedFrames;

        // Rendered-audio quality
        public double LatencyMs = -1;
        public double MedianSegSnrDb = double.NaN;
        public int NotchCount;
        public double NotchTotalMs;
        public double DroppedAudioMs;
        public double OutputSeconds;

        // Latency over time (windowed envelope alignment; empty when the signal has
        // no envelope structure to align on). Start/Max/End summarize the curve so a
        // latency spike that never drains back down is visible as End >> Start.
        public List<BasisVoiceQualityAnalysis.LagPoint> LatencyCurve = new List<BasisVoiceQualityAnalysis.LagPoint>();
        public double LatencyStartMs = -1;
        public double LatencyMaxMs = -1;
        public double LatencyEndMs = -1;

        // Ground-truth standing buffer depth (packets, encoded span + decoded),
        // sampled at every audio callback from the live jitter buffer. Exact — no
        // content alignment involved — so tests assert on these for latency
        // recovery. Max ignores the first second (initial pre-roll fill).
        public int StandingFramesMax;
        public int StandingFramesEnd;

        public bool Passed;
        public string Failure = "";
        public string Error = "";

        public float[] ReferenceMono;   // 48 kHz input signal
        public float[] OutputMono;      // at OutputSampleRate
        public float[] BaselineMono;    // codec-only render at OutputSampleRate

        /// <summary>Per-event timeline (see BasisVoiceScenario.TraceTimeline). Null unless requested.</summary>
        public string TimelineCsv;

        public string Summary =>
            $"{ScenarioName} [{ProfileName}] sent={PacketsSent} lost={PacketsDropped} plc={PlcCount} fec={FecRecoveredCount} " +
            $"underruns={GenuineUnderruns} depth={FinalPrerollDepth}/{PrerollFloor} notches={NotchCount} " +
            $"snr={MedianSegSnrDb:F1}dB lat={LatencyMs:F0}ms" +
            (LatencyEndMs >= 0 ? $" latCurve={LatencyStartMs:F0}>{LatencyMaxMs:F0}>{LatencyEndMs:F0}ms" : "") +
            $" standing={StandingFramesMax}>{StandingFramesEnd}" +
            (StarvePlcCount + TrimmedQuietFrames + AcceleratedFrames + FlushedPackets + LateSalvagedCount + ExpandInsertedFrames > 0
                ? $" bridge={StarvePlcCount} trim={TrimmedQuietFrames} accel={AcceleratedFrames}({AcceleratedSavedMs:F0}ms) flush={FlushedPackets} salvage={LateSalvagedCount} expand={ExpandInsertedFrames}"
                : "") +
            $" {(Passed ? "PASS" : "FAIL " + Failure)}{Error}";
    }

    public static class BasisVoiceSim
    {
        public static BasisVoiceSimResult Run(BasisVoiceScenario s)
        {
            float savedDuration = SharedOpusSettings.DesiredDurationInSeconds;
            int savedFloor = RemoteOpusSettings.JitterBufferSize;
            float savedMainVolume = SMModuleAudio.ActiveMainVolume;
            try
            {
                SharedOpusSettings.DesiredDurationInSeconds = s.FrameDurationSeconds;
                RemoteOpusSettings.JitterBufferSize = s.JitterBufferFloor;
                SMModuleAudio.ActiveMainVolume = 1f; // unit gain so metrics are absolute

                float[] reference = GenerateSignal(s.Signal, s.DurationSeconds, s.Seed);

                BasisVoiceSimResult result = RunPass(s, reference);

                bool needsBaseline = s.Profile.Impaired || s.AnyLocalImpairment;
                if (!needsBaseline)
                {
                    result.BaselineMono = result.OutputMono;
                }
                else
                {
                    BasisVoiceScenario b = s.Clone();
                    b.Name = s.Name + " (baseline)";
                    b.Profile = new BasisVoiceNetProfile { Name = "baseline", LatencyMs = s.Profile.LatencyMs };
                    b.SenderHitchDurationMs = 0f;
                    b.ReceiverHangDurationMs = 0f;
                    BasisVoiceSimResult baseline = RunPass(b, reference);
                    result.BaselineMono = baseline.OutputMono;
                }

                Score(s, result);
                Evaluate(s, result);
                if (!s.KeepAudio)
                {
                    result.ReferenceMono = null;
                    result.OutputMono = null;
                    result.BaselineMono = null;
                }
                return result;
            }
            finally
            {
                SharedOpusSettings.DesiredDurationInSeconds = savedDuration;
                RemoteOpusSettings.JitterBufferSize = savedFloor;
                SMModuleAudio.ActiveMainVolume = savedMainVolume;
            }
        }

        // ==================== Single pipeline pass ====================

        static BasisVoiceSimResult RunPass(BasisVoiceScenario s, float[] reference)
        {
            var result = new BasisVoiceSimResult
            {
                ScenarioName = s.Name,
                ProfileName = s.Profile.Name,
                Signal = s.Signal,
                Seed = s.Seed,
                PrerollFloor = s.JitterBufferFloor,
                ReferenceMono = reference,
            };

            const double micStep = 0.02;
            const int micChunk = 960;
            int targetSamples = (int)Math.Ceiling(s.FrameDurationSeconds * LocalOpusSettings.MicrophoneSampleRate);
            double cbStep = s.CallbackFrames / (double)s.OutputSampleRate;
            int micTicks = (int)Math.Round(s.DurationSeconds / micStep);
            double endTime = s.DurationSeconds + 1.0;

            VoiceSimSender sender = null;
            VoiceSimReceiverRig rig = null;
            try
            {
                sender = new VoiceSimSender(targetSamples, s.Bitrate, s.EncoderPacketLossPercent);
                rig = new VoiceSimReceiverRig(s.OutputSampleRate);
                var net = new VoiceSimNetwork(s.Profile, new Random(s.Seed * 31 + 7));

                var output = new List<float>((int)(endTime * s.OutputSampleRate) + s.CallbackFrames);
                float[] micBuf = new float[micChunk];
                float[] cbBuf = new float[s.CallbackFrames * s.OutputChannels];
                System.Text.StringBuilder trace = s.TraceTimeline ? new System.Text.StringBuilder() : null;
                trace?.AppendLine("kind,t,peak,decodedFrames,encodedBuffered,receivedSinceStart,depth,genuineUnderruns,silentUnits,rearms");
                Action<byte[], int> deliver = rig.DeliverWire;
                if (trace != null)
                {
                    deliver = (data, len) =>
                    {
                        rig.DeliverWire(data, len);
                        var vb = rig.Receiver.VoiceBuffer;
                        trace.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "pkt,{0:F4},,{1},{2},{3},{4},{5},{6},{7}",
                            rig.CurrentTime, vb.DecodedFrameCount, vb.EncodedBufferedCount, vb.ReceivedSinceStart,
                            vb.InitialBufferDepth, vb.GenuineUnderruns, rig.Receiver._silentUnits20ms, rig.Receiver.RearmCount));
                    };
                }

                double senderHitchEnd = s.SenderHitchAtSeconds + s.SenderHitchDurationMs / 1000.0;
                double hangEnd = s.ReceiverHangAtSeconds + s.ReceiverHangDurationMs / 1000.0;

                int mic = 0;
                double nextCb = 0.0;
                while (true)
                {
                    double tMic = double.MaxValue;
                    if (mic < micTicks)
                    {
                        tMic = mic * micStep;
                        if (s.SenderHitchDurationMs > 0f && tMic >= s.SenderHitchAtSeconds && tMic < senderHitchEnd)
                            tMic = senderHitchEnd + mic * 1e-7; // bunched catch-up, order preserved
                    }
                    double tCb = nextCb <= endTime ? nextCb : double.MaxValue;
                    if (tMic == double.MaxValue && tCb == double.MaxValue) break;

                    if (tMic <= tCb)
                    {
                        rig.SetTime(tMic);
                        net.DeliverDue(tMic, deliver);
                        int srcStart = mic * micChunk;
                        for (int i = 0; i < micChunk; i++)
                        {
                            int src = srcStart + i;
                            micBuf[i] = src < reference.Length ? reference[src] : 0f;
                        }
                        byte[] wire = sender.MicTick(micBuf, out int wireLen);
                        if (wire != null)
                            net.Send(wire, wireLen, tMic);
                        mic++;
                    }
                    else
                    {
                        rig.SetTime(tCb);
                        net.DeliverDue(tCb, deliver);
                        int standingSample = rig.Receiver.VoiceBuffer.StandingBufferedFrames;
                        if (tCb >= 1.0 && standingSample > result.StandingFramesMax)
                            result.StandingFramesMax = standingSample;
                        if (tCb <= s.DurationSeconds)
                            result.StandingFramesEnd = standingSample;

                        bool inHang = s.ReceiverHangDurationMs > 0f && tCb >= s.ReceiverHangAtSeconds && tCb < hangEnd;
                        if (inHang)
                        {
                            for (int i = 0; i < s.CallbackFrames; i++)
                                output.Add(0f);
                        }
                        else
                        {
                            Array.Clear(cbBuf, 0, cbBuf.Length);
                            rig.Receiver.OnAudioFilterRead(cbBuf, s.OutputChannels, cbBuf.Length);
                            float peak = 0f;
                            for (int f = 0; f < s.CallbackFrames; f++)
                            {
                                float v = cbBuf[f * s.OutputChannels];
                                output.Add(v);
                                float a = v < 0f ? -v : v;
                                if (a > peak) peak = a;
                            }
                            if (trace != null)
                            {
                                var vb = rig.Receiver.VoiceBuffer;
                                trace.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                    "cb,{0:F4},{1:F4},{2},{3},{4},{5},{6},{7},{8}",
                                    tCb, peak, vb.DecodedFrameCount, vb.EncodedBufferedCount, vb.ReceivedSinceStart,
                                    vb.InitialBufferDepth, vb.GenuineUnderruns, rig.Receiver._silentUnits20ms, rig.Receiver.RearmCount));
                            }
                        }
                        nextCb += cbStep;
                    }
                }

                result.PacketsSent = sender.PacketsSent;
                result.SilentMicTicks = sender.SilentTicks;
                result.PacketsDropped = net.Dropped;
                result.PacketsDuped = net.Duped;
                result.PacketsDelivered = net.Delivered;

                result.PlcCount = rig.Receiver.PlcCount;
                result.FecRecoveredCount = rig.Receiver.FecRecoveredCount;
                result.SilenceInjectedCount = rig.Receiver.SilenceInjectedCount;
                result.RearmCount = rig.Receiver.RearmCount;
                result.GenuineUnderruns = rig.Receiver.VoiceBuffer.GenuineUnderruns;
                result.FinalPrerollDepth = rig.Receiver.VoiceBuffer.InitialBufferDepth;
                result.ReceiverLossPercent01 = rig.Receiver.VoiceBuffer.LossPercent01;
                result.StarvePlcCount = rig.Receiver.StarvePlcCount;
                result.TrimmedQuietFrames = rig.Receiver.TrimmedQuietFrames;
                result.AcceleratedFrames = rig.Receiver.AcceleratedFrames;
                result.AcceleratedSavedMs = rig.Receiver.AcceleratedSavedSamples * 1000.0 / RemoteOpusSettings.NetworkSampleRate;
                result.FlushedPackets = rig.Receiver.FlushedPackets;
                result.LateSalvagedCount = rig.Receiver.VoiceBuffer.LateSalvagedCount;
                result.ExpandInsertedFrames = rig.Receiver.ExpandInsertedFrames;

                result.OutputMono = output.ToArray();
                result.OutputSeconds = result.OutputMono.Length / (double)s.OutputSampleRate;
                result.TimelineCsv = trace?.ToString();
            }
            catch (Exception ex)
            {
                result.Error = $" EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                sender?.Dispose();
                rig?.Dispose();
            }
            return result;
        }

        // ==================== Scoring / verdict ====================

        static void Score(BasisVoiceScenario s, BasisVoiceSimResult r)
        {
            if (r.OutputMono == null || r.OutputMono.Length == 0) return;

            r.LatencyMs = BasisVoiceQualityAnalysis.EstimateLagMs(
                r.ReferenceMono, LocalOpusSettings.MicrophoneSampleRate,
                r.OutputMono, s.OutputSampleRate, 1200.0);

            r.LatencyCurve = BasisVoiceQualityAnalysis.LatencyCurve(
                r.ReferenceMono, LocalOpusSettings.MicrophoneSampleRate,
                r.OutputMono, s.OutputSampleRate);
            if (r.LatencyCurve.Count > 0)
            {
                r.LatencyStartMs = r.LatencyCurve[0].LagMs;
                r.LatencyEndMs = r.LatencyCurve[r.LatencyCurve.Count - 1].LagMs;
                foreach (var p in r.LatencyCurve)
                    if (p.LagMs > r.LatencyMaxMs) r.LatencyMaxMs = p.LagMs;
            }

            var notches = BasisVoiceQualityAnalysis.FindNotches(r.OutputMono, s.OutputSampleRate);
            r.NotchCount = notches.Count;
            foreach (var n in notches) r.NotchTotalMs += n.DurationMs;

            if (r.BaselineMono != null && r.BaselineMono.Length > 0 && !ReferenceEquals(r.BaselineMono, r.OutputMono))
            {
                int lag = BasisVoiceQualityAnalysis.SampleAlign(r.BaselineMono, r.OutputMono, s.OutputSampleRate);
                r.MedianSegSnrDb = BasisVoiceQualityAnalysis.MedianSegmentalSnrDb(r.BaselineMono, r.OutputMono, s.OutputSampleRate, lag);
                r.DroppedAudioMs = BasisVoiceQualityAnalysis.DroppedAudioMs(r.BaselineMono, r.OutputMono, s.OutputSampleRate, lag);
            }
            else if (ReferenceEquals(r.BaselineMono, r.OutputMono))
            {
                r.MedianSegSnrDb = 60.0;
            }
        }

        static void Evaluate(BasisVoiceScenario s, BasisVoiceSimResult r)
        {
            var fails = new List<string>();
            if (r.Error.Length > 0)
                fails.Add("exception");
            else
            {
                bool outputHasEnergy = false;
                if (r.OutputMono != null)
                {
                    for (int i = 0; i < r.OutputMono.Length; i++)
                        if (r.OutputMono[i] > 0.02f || r.OutputMono[i] < -0.02f) { outputHasEnergy = true; break; }
                }
                if (r.PacketsSent > 0 && r.PacketsDelivered > 0 && !outputHasEnergy)
                    fails.Add("no audio rendered");

                bool hard = s.HardQuality && !s.Profile.Impaired && !s.AnyLocalImpairment;
                if (hard)
                {
                    if (r.GenuineUnderruns > 0) fails.Add($"underruns={r.GenuineUnderruns}");
                    if (r.PlcCount > 0) fails.Add($"plc={r.PlcCount}");
                    if (r.NotchCount > 0) fails.Add($"notches={r.NotchCount}");
                    if (!double.IsNaN(r.MedianSegSnrDb) && r.MedianSegSnrDb < 30.0) fails.Add($"snr={r.MedianSegSnrDb:F1}dB");
                    if (r.LatencyMs >= 0 && (r.LatencyMs < 20 || r.LatencyMs > 400)) fails.Add($"latency={r.LatencyMs:F0}ms");
                }
            }
            r.Passed = fails.Count == 0;
            r.Failure = string.Join(", ", fails);
        }

        // ==================== Signal generation ====================

        public static float[] GenerateSignal(BasisVoiceSignal kind, double seconds, int seed)
        {
            int rate = LocalOpusSettings.MicrophoneSampleRate;
            int n = (int)(seconds * rate);
            float[] x = new float[n];
            var rng = new Random(seed * 733 + 101);

            switch (kind)
            {
                case BasisVoiceSignal.Sine:
                    for (int i = 0; i < n; i++)
                        x[i] = 0.4f * (float)Math.Sin(2.0 * Math.PI * 440.0 * i / rate);
                    break;

                case BasisVoiceSignal.Sweep:
                {
                    double f0 = 100.0, f1 = 8000.0, phase = 0.0;
                    for (int i = 0; i < n; i++)
                    {
                        double u = i / (double)n;
                        double f = f0 * Math.Pow(f1 / f0, u);
                        phase += 2.0 * Math.PI * f / rate;
                        x[i] = 0.35f * (float)Math.Sin(phase);
                    }
                    break;
                }

                case BasisVoiceSignal.ImpulseTrain:
                {
                    for (int i = 0; i < n; i++)
                        x[i] = 0.008f * (float)(rng.NextDouble() * 2.0 - 1.0);
                    // Aperiodic click spacing (330-650 ms, seeded): a strictly periodic
                    // train lets windowed cross-correlation lock onto a neighbouring
                    // click one period away, aliasing the latency estimate. Unequal
                    // gaps make every window's click pattern unique.
                    int clickLen = rate * 5 / 1000;
                    int c = rate / 4;
                    while (c + clickLen < n)
                    {
                        for (int k = 0; k < clickLen; k++)
                        {
                            double decay = Math.Exp(-6.0 * k / clickLen);
                            x[c + k] += 0.8f * (float)(Math.Sin(2.0 * Math.PI * 1500.0 * k / rate) * decay);
                        }
                        c += (int)((0.33 + rng.NextDouble() * 0.32) * rate);
                    }
                    break;
                }

                case BasisVoiceSignal.SpeechLike:
                default:
                {
                    int i = 0;
                    while (i < n)
                    {
                        int utterLen = (int)((0.7 + rng.NextDouble() * 0.5) * rate);
                        // Long enough that, after the sender's ~200 ms rolling-RMS hangover and
                        // the receiver's buffered audio drain, the 200 ms idle reset still fires.
                        int pauseLen = (int)((0.45 + rng.NextDouble() * 0.3) * rate);
                        double f0 = 120.0 + rng.NextDouble() * 60.0;
                        int end = Math.Min(n, i + utterLen);
                        for (int k = 0; i < end; i++, k++)
                        {
                            double t = k / (double)rate;
                            double vib = 1.0 + 0.01 * Math.Sin(2.0 * Math.PI * 5.0 * t);
                            // Floor keeps the envelope from touching zero between syllables so a
                            // notch detected in the OUTPUT is always a playback fault, not signal.
                            double syll = 0.25 + 0.75 * Math.Pow(Math.Abs(Math.Sin(Math.PI * 3.5 * t)), 0.7);
                            double onset = Math.Min(1.0, k / (0.02 * rate));
                            double tail = Math.Min(1.0, (end - 1 - i) / (0.02 * rate));
                            double v = Math.Sin(2.0 * Math.PI * f0 * vib * t)
                                     + 0.5 * Math.Sin(2.0 * Math.PI * 2.0 * f0 * vib * t)
                                     + 0.25 * Math.Sin(2.0 * Math.PI * 3.0 * f0 * vib * t);
                            x[i] = (float)(0.3 * v * syll * onset * tail / 1.75);
                        }
                        i = Math.Min(n, i + pauseLen); // exact zeros: real silence for the send gate
                    }
                    break;
                }
            }
            return x;
        }
    }

    /// <summary>
    /// Faithful mirror of the send path that is welded to MonoBehaviours/peers in shipping
    /// code (<c>BasisAudioTransmission.OnAudioReady/SendSilenceOverNetwork/EncodeAndSend</c> plus the mic
    /// driver's rolling-RMS transmit gate). Uses the REAL Opus encoder with the same CTLs and
    /// the REAL wire serialization; only the cadence/gating logic is reproduced.
    /// </summary>
    sealed class VoiceSimSender : IDisposable
    {
        readonly OpusSharp.Core.Interfaces.IOpusEncoder _encoder;
        readonly NetDataWriter _writer = new NetDataWriter();
        AudioSegmentDataMessage _segment;
        readonly int _targetSamples;
        readonly float[] _accum;
        int _accumFilled;
        byte _sequenceNumber;
        int _silentForHowLong;

        readonly float[] _rmsWindow = new float[LocalOpusSettings.rmsWindowSize];
        int _rmsCount, _rmsIndex;

        public int PacketsSent;
        public int SilentTicks;

        public VoiceSimSender(int targetSamples, int bitrate, int packetLossPercent)
        {
            _targetSamples = targetSamples;
            _accum = new float[targetSamples];
            _encoder = new OpusSharp.Core.Dynamic.OpusEncoder(
                LocalOpusSettings.MicrophoneSampleRate,
                LocalOpusSettings.Channels,
                LocalOpusSettings.OpusApplication);
            if (bitrate < LocalOpusSettings.DefaultBitrate / 8) bitrate = LocalOpusSettings.DefaultBitrate / 8;
            _encoder.Ctl(OpusSharp.Core.EncoderCTL.OPUS_SET_BITRATE, bitrate);
            _encoder.Ctl(OpusSharp.Core.EncoderCTL.OPUS_SET_COMPLEXITY, 5);
            _encoder.Ctl(OpusSharp.Core.EncoderCTL.OPUS_SET_INBAND_FEC, 1);
            if (packetLossPercent < 0) packetLossPercent = 0;
            else if (packetLossPercent > 100) packetLossPercent = 100;
            _encoder.Ctl(OpusSharp.Core.EncoderCTL.OPUS_SET_PACKET_LOSS_PERC, packetLossPercent);

            _segment.buffer = new byte[targetSamples * 4];
            _segment.TotalLength = _segment.buffer.Length;
        }

        /// <summary>One 20 ms mic tick. Returns the client wire bytes, or null (silent tick / 40 ms accumulation).</summary>
        public byte[] MicTick(float[] chunk960, out int wireLen)
        {
            wireLen = 0;

            double sum = 0;
            for (int i = 0; i < chunk960.Length; i++)
                sum += chunk960[i] * chunk960[i];
            float rms = (float)Math.Sqrt(sum / chunk960.Length);
            _rmsWindow[_rmsIndex] = rms;
            _rmsIndex = (_rmsIndex + 1) % _rmsWindow.Length;
            if (_rmsCount < _rmsWindow.Length) _rmsCount++;
            float avg = 0f;
            for (int i = 0; i < _rmsCount; i++) avg += _rmsWindow[i];
            avg /= _rmsCount;

            if (avg <= LocalOpusSettings.silenceThreshold)
            {
                _accumFilled = 0;
                _silentForHowLong++;
                SilentTicks++;
                return null;
            }

            if (_targetSamples <= chunk960.Length)
                return Encode(chunk960, _targetSamples, out wireLen);

            int copy = Math.Min(chunk960.Length, _targetSamples - _accumFilled);
            Array.Copy(chunk960, 0, _accum, _accumFilled, copy);
            _accumFilled += copy;
            if (_accumFilled < _targetSamples)
                return null;

            byte[] wire = Encode(_accum, _targetSamples, out wireLen);
            _accumFilled = 0;
            return wire;
        }

        byte[] Encode(float[] pcm, int sampleCount, out int wireLen)
        {
            _segment.LengthUsed = _encoder.Encode(pcm, sampleCount, _segment.buffer, _segment.TotalLength);
            _segment.SequenceNumber = _sequenceNumber++;
            _segment.TotalPlayedInSilence = _silentForHowLong > 255 ? (byte)255 : (byte)_silentForHowLong;
            _silentForHowLong = 0;
            _writer.Reset();
            _segment.Serialize(_writer);
            PacketsSent++;
            wireLen = _writer.Length;
            byte[] copy = new byte[wireLen];
            Array.Copy(_writer.Data, copy, wireLen);
            return copy;
        }

        public void Dispose() => _encoder?.Dispose();
    }

    /// <summary>
    /// Wraps a REAL <see cref="BasisAudioReceiver"/> for headless driving: no AudioSource,
    /// virtual millisecond clock into the jitter buffer, and the client-side wire
    /// deserialization (mirrors <c>BasisNetworkHandleVoice.HandleAudioUpdate</c> for VoiceChannel).
    /// </summary>
    sealed class VoiceSimReceiverRig : IDisposable
    {
        public readonly BasisAudioReceiver Receiver = new BasisAudioReceiver();
        public double CurrentTime { get; private set; }
        int _virtualTickMs;

        public VoiceSimReceiverRig(int outputSampleRate)
        {
            Receiver.InitializeWithOutputRate(null, outputSampleRate);
            BasisAudioReceiver.outputSampleRate = outputSampleRate;
            Receiver.VoiceBuffer.TickCountSource = () => _virtualTickMs;
            Receiver.HasAudioSource = true;
            Receiver.InitializeForPlayback();
        }

        public void SetTime(double seconds)
        {
            CurrentTime = seconds;
            _virtualTickMs = (int)(seconds * 1000.0);
        }

        public void DeliverWire(byte[] data, int length)
        {
            var reader = new NetDataReader(data, 0, length);
            ServerAudioSegmentMessage msg = default;
            msg.Deserialize(reader, false);
            Receiver.Insert(msg.audioSegmentData);
        }

        public void Dispose() => Receiver.OnDestroy();
    }

    /// <summary>
    /// The server hop + downlink. Send() first performs the REAL relay wire work
    /// (deserialize the client packet, wrap in ServerAudioSegmentMessage, serialize once —
    /// mirroring <c>BasisServerHandleEvents.HandleVoiceMessage</c>/<c>SendVoiceMessageToClients</c>),
    /// then applies seeded downlink impairments. The uplink leg is kept clean so stated
    /// loss/jitter numbers act on exactly one leg.
    /// </summary>
    sealed class VoiceSimNetwork
    {
        const ushort SimPlayerId = 42;

        struct Flight
        {
            public double Arrival;
            public int Index;
            public byte[] Data;
            public int Length;
        }

        readonly BasisVoiceNetProfile _p;
        readonly Random _rng;
        readonly List<Flight> _inflight = new List<Flight>();
        readonly NetDataWriter _relayWriter = new NetDataWriter();
        double _nextBurstAt;
        int _burstRemaining;
        double _nextLateSpikeAt;
        int _lateSpikeRemaining;
        int _sendIndex;

        public int Dropped, Duped, Delivered;

        public VoiceSimNetwork(BasisVoiceNetProfile profile, Random rng)
        {
            _p = profile;
            _rng = rng;
            _nextBurstAt = profile.BurstIntervalSeconds > 0f && profile.BurstLossPackets > 0
                ? profile.BurstIntervalSeconds
                : double.MaxValue;
            _nextLateSpikeAt = profile.LateSpikeIntervalSeconds > 0f && profile.LateSpikePackets > 0
                ? profile.LateSpikeIntervalSeconds
                : double.MaxValue;
        }

        public void Send(byte[] clientWire, int length, double now)
        {
            var reader = new NetDataReader(clientWire, 0, length);
            AudioSegmentDataMessage seg = default;
            seg.Deserialize(reader);
            ServerAudioSegmentMessage relayed = default;
            relayed.playerIdMessage.playerID = SimPlayerId;
            relayed.audioSegmentData = seg;
            _relayWriter.Reset();
            relayed.Serialize(_relayWriter, false);
            byte[] bytes = new byte[_relayWriter.Length];
            Array.Copy(_relayWriter.Data, bytes, _relayWriter.Length);

            int index = _sendIndex++;

            if (now >= _nextBurstAt && _burstRemaining == 0)
            {
                _burstRemaining = _p.BurstLossPackets;
                _nextBurstAt += _p.BurstIntervalSeconds;
            }
            if (_burstRemaining > 0)
            {
                _burstRemaining--;
                Dropped++;
                return;
            }
            if (_p.LossChance > 0f && _rng.NextDouble() < _p.LossChance)
            {
                Dropped++;
                return;
            }

            Enqueue(bytes, bytes.Length, now, index);
            if (_p.DupChance > 0f && _rng.NextDouble() < _p.DupChance)
            {
                Duped++;
                Enqueue(bytes, bytes.Length, now, index);
            }
        }

        void Enqueue(byte[] data, int length, double now, int index)
        {
            double jitter = _p.JitterMs > 0f ? (_rng.NextDouble() * 2.0 - 1.0) * _p.JitterMs / 1000.0 : 0.0;
            double arrival = now + _p.LatencyMs / 1000.0 + jitter;

            // Congestion swell: extra transit ramps up to the peak and back down over
            // the window, keyed on SEND time (queueing happens on entry).
            if (_p.LatencyRampPeakMs > 0f && _p.LatencyRampDurationSeconds > 0f)
            {
                double u = (now - _p.LatencyRampAtSeconds) / _p.LatencyRampDurationSeconds;
                if (u > 0.0 && u < 1.0)
                {
                    double tri = u < 0.5 ? u * 2.0 : (1.0 - u) * 2.0;
                    arrival += tri * _p.LatencyRampPeakMs / 1000.0;
                }
            }

            // Correlated lateness burst: N consecutive packets all take the slow path.
            if (now >= _nextLateSpikeAt && _lateSpikeRemaining == 0)
            {
                _lateSpikeRemaining = _p.LateSpikePackets;
                _nextLateSpikeAt += _p.LateSpikeIntervalSeconds;
            }
            if (_lateSpikeRemaining > 0)
            {
                _lateSpikeRemaining--;
                arrival += _p.LateSpikeDelayMs / 1000.0;
            }

            // Independent straggler: this one packet takes a slow path and arrives
            // behind packets sent after it.
            if (_p.ReorderChance > 0f && _p.ReorderDelayMs > 0f && _rng.NextDouble() < _p.ReorderChance)
                arrival += _p.ReorderDelayMs / 1000.0;

            // Delivery batching: quantize arrivals up to a batch grid.
            if (_p.EarlyCoalescePackets > 1)
            {
                double period = _p.EarlyCoalescePackets * 0.02;
                arrival = Math.Ceiling(arrival / period) * period;
            }

            if (arrival < now) arrival = now;
            if (_p.StallDurationMs > 0f)
            {
                double stallEnd = _p.StallAtSeconds + _p.StallDurationMs / 1000.0;
                if (arrival >= _p.StallAtSeconds && arrival < stallEnd)
                    arrival = stallEnd + index * 1e-7;
            }
            _inflight.Add(new Flight { Arrival = arrival, Index = index, Data = data, Length = length });
        }

        public void DeliverDue(double now, Action<byte[], int> deliver)
        {
            if (_inflight.Count == 0) return;
            _inflight.Sort((a, b) =>
            {
                int c = a.Arrival.CompareTo(b.Arrival);
                return c != 0 ? c : a.Index.CompareTo(b.Index);
            });
            int i = 0;
            while (i < _inflight.Count && _inflight[i].Arrival <= now)
            {
                deliver(_inflight[i].Data, _inflight[i].Length);
                Delivered++;
                i++;
            }
            if (i > 0) _inflight.RemoveRange(0, i);
        }
    }
}
