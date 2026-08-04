using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Basis.Scripts.Networking.Voice.Testing
{
    /// <summary>
    /// Scenario matrix for <see cref="BasisVoiceSim"/>: network profiles ranging from clean
    /// to hostile, sender/receiver timing faults, codec variants, and the signals that best
    /// expose each failure mode. Quick = smoke set; Full = the whole grid.
    /// </summary>
    public static class BasisVoiceSimMatrix
    {
        public static BasisVoiceNetProfile Perfect() => new BasisVoiceNetProfile { Name = "perfect", LatencyMs = 40f };
        public static BasisVoiceNetProfile Lan() => new BasisVoiceNetProfile { Name = "lan", LatencyMs = 5f };
        public static BasisVoiceNetProfile Jitter30() => new BasisVoiceNetProfile { Name = "jitter30", LatencyMs = 40f, JitterMs = 30f };
        public static BasisVoiceNetProfile Loss5() => new BasisVoiceNetProfile { Name = "loss5", LatencyMs = 40f, LossChance = 0.05f };
        public static BasisVoiceNetProfile Loss15() => new BasisVoiceNetProfile { Name = "loss15", LatencyMs = 40f, LossChance = 0.15f };
        public static BasisVoiceNetProfile JitterLoss() => new BasisVoiceNetProfile { Name = "jitterloss", LatencyMs = 40f, JitterMs = 25f, LossChance = 0.05f };
        public static BasisVoiceNetProfile Burst160() => new BasisVoiceNetProfile { Name = "burst160ms", LatencyMs = 40f, BurstIntervalSeconds = 2f, BurstLossPackets = 8 };
        /// <summary>40 consecutive losses &gt; the 32-packet ring lookahead — exercises the resync path.</summary>
        public static BasisVoiceNetProfile Burst800() => new BasisVoiceNetProfile { Name = "burst800ms", LatencyMs = 40f, BurstIntervalSeconds = 3f, BurstLossPackets = 40 };
        public static BasisVoiceNetProfile Stall600() => new BasisVoiceNetProfile { Name = "stall600ms", LatencyMs = 40f, StallAtSeconds = 2.5f, StallDurationMs = 600f };
        /// <summary>10% of packets take an +80 ms slow path — heavy reordering, nothing lost.</summary>
        public static BasisVoiceNetProfile Reorder10() => new BasisVoiceNetProfile { Name = "reorder10", LatencyMs = 40f, ReorderChance = 0.10f, ReorderDelayMs = 80f };
        /// <summary>Every 2 s, 6 consecutive packets arrive +150 ms late together (wifi clump).</summary>
        public static BasisVoiceNetProfile LateSpike() => new BasisVoiceNetProfile { Name = "latespike", LatencyMs = 40f, LateSpikeIntervalSeconds = 2f, LateSpikePackets = 6, LateSpikeDelayMs = 150f };
        /// <summary>Delivery batched in 5-packet groups: queue depth pulses +5/-5 around target.</summary>
        public static BasisVoiceNetProfile EarlyBurst() => new BasisVoiceNetProfile { Name = "earlyburst", LatencyMs = 40f, EarlyCoalescePackets = 5 };
        /// <summary>Congestion swell: +250 ms transit at the midpoint of a 1.5 s window.</summary>
        public static BasisVoiceNetProfile Congestion() => new BasisVoiceNetProfile { Name = "congestion", LatencyMs = 40f, LatencyRampAtSeconds = 2f, LatencyRampPeakMs = 250f, LatencyRampDurationSeconds = 1.5f };
        public static BasisVoiceNetProfile Chaos() => new BasisVoiceNetProfile
        {
            Name = "chaos",
            LatencyMs = 80f,
            JitterMs = 50f,
            LossChance = 0.10f,
            DupChance = 0.02f,
            StallAtSeconds = 3f,
            StallDurationMs = 400f,
        };

        public static List<BasisVoiceScenario> Enumerate(bool full, int seeds)
        {
            var list = new List<BasisVoiceScenario>();
            for (int seedIndex = 0; seedIndex < seeds; seedIndex++)
            {
                int seed = 1234 + seedIndex * 811;

                Add(list, "speech/perfect", BasisVoiceSignal.SpeechLike, Perfect(), seed);
                Add(list, "speech/jitter30", BasisVoiceSignal.SpeechLike, Jitter30(), seed);
                Add(list, "speech/loss5", BasisVoiceSignal.SpeechLike, Loss5(), seed);
                Add(list, "speech/burst160ms", BasisVoiceSignal.SpeechLike, Burst160(), seed);
                Add(list, "speech/stall600ms", BasisVoiceSignal.SpeechLike, Stall600(), seed);
                Add(list, "sine/perfect", BasisVoiceSignal.Sine, Perfect(), seed);
                Add(list, "impulse/perfect", BasisVoiceSignal.ImpulseTrain, Perfect(), seed);

                var hang = Add(list, "speech/receiver-hang500ms", BasisVoiceSignal.SpeechLike, Perfect(), seed);
                hang.ReceiverHangAtSeconds = 2.5f;
                hang.ReceiverHangDurationMs = 500f;

                var out44 = Add(list, "sweep/out44100", BasisVoiceSignal.Sweep, Perfect(), seed);
                out44.OutputSampleRate = 44100;

                if (!full) continue;

                Add(list, "speech/lan", BasisVoiceSignal.SpeechLike, Lan(), seed);
                Add(list, "speech/reorder10", BasisVoiceSignal.SpeechLike, Reorder10(), seed);
                Add(list, "speech/latespike", BasisVoiceSignal.SpeechLike, LateSpike(), seed);
                Add(list, "speech/earlyburst", BasisVoiceSignal.SpeechLike, EarlyBurst(), seed);
                Add(list, "impulse/reorder10", BasisVoiceSignal.ImpulseTrain, Reorder10(), seed);
                Add(list, "impulse/latespike", BasisVoiceSignal.ImpulseTrain, LateSpike(), seed);
                Add(list, "impulse/earlyburst", BasisVoiceSignal.ImpulseTrain, EarlyBurst(), seed);
                Add(list, "impulse/congestion", BasisVoiceSignal.ImpulseTrain, Congestion(), seed);
                Add(list, "impulse/stall600ms", BasisVoiceSignal.ImpulseTrain, Stall600(), seed);
                Add(list, "speech/loss15", BasisVoiceSignal.SpeechLike, Loss15(), seed);
                Add(list, "speech/jitterloss", BasisVoiceSignal.SpeechLike, JitterLoss(), seed);
                Add(list, "speech/burst800ms-resync", BasisVoiceSignal.SpeechLike, Burst800(), seed);
                Add(list, "speech/chaos", BasisVoiceSignal.SpeechLike, Chaos(), seed);
                Add(list, "sine/jitter30", BasisVoiceSignal.Sine, Jitter30(), seed);
                Add(list, "sine/loss5", BasisVoiceSignal.Sine, Loss5(), seed);
                Add(list, "sine/loss15", BasisVoiceSignal.Sine, Loss15(), seed);
                Add(list, "sweep/perfect", BasisVoiceSignal.Sweep, Perfect(), seed);
                Add(list, "impulse/lan", BasisVoiceSignal.ImpulseTrain, Lan(), seed);
                Add(list, "impulse/jitter30", BasisVoiceSignal.ImpulseTrain, Jitter30(), seed);

                var hitch = Add(list, "speech/sender-hitch300ms", BasisVoiceSignal.SpeechLike, Perfect(), seed);
                hitch.SenderHitchAtSeconds = 2f;
                hitch.SenderHitchDurationMs = 300f;

                var hangLossy = Add(list, "speech/hang500ms+loss5", BasisVoiceSignal.SpeechLike, Loss5(), seed);
                hangLossy.ReceiverHangAtSeconds = 2.5f;
                hangLossy.ReceiverHangDurationMs = 500f;

                var frame40 = Add(list, "speech/frame40ms", BasisVoiceSignal.SpeechLike, Perfect(), seed);
                frame40.FrameDurationSeconds = 0.04f;
                var frame40Loss = Add(list, "speech/frame40ms+loss5", BasisVoiceSignal.SpeechLike, Loss5(), seed);
                frame40Loss.FrameDurationSeconds = 0.04f;

                var br8 = Add(list, "sine/bitrate8k", BasisVoiceSignal.Sine, Perfect(), seed);
                br8.Bitrate = 8000;
                var br96 = Add(list, "sine/bitrate96k", BasisVoiceSignal.Sine, Perfect(), seed);
                br96.Bitrate = 96000;

                var floor2 = Add(list, "speech/floor2", BasisVoiceSignal.SpeechLike, Perfect(), seed);
                floor2.JitterBufferFloor = 2;
                var floor2Jitter = Add(list, "speech/floor2+jitter30", BasisVoiceSignal.SpeechLike, Jitter30(), seed);
                floor2Jitter.JitterBufferFloor = 2;

                var speech44 = Add(list, "speech/out44100", BasisVoiceSignal.SpeechLike, Perfect(), seed);
                speech44.OutputSampleRate = 44100;
                var speech44Loss = Add(list, "speech/out44100+loss5", BasisVoiceSignal.SpeechLike, Loss5(), seed);
                speech44Loss.OutputSampleRate = 44100;
            }
            return list;
        }

        static BasisVoiceScenario Add(List<BasisVoiceScenario> list, string name, BasisVoiceSignal signal, BasisVoiceNetProfile profile, int seed)
        {
            var s = new BasisVoiceScenario
            {
                Name = name,
                Signal = signal,
                Profile = profile,
                Seed = seed,
                KeepAudio = false,
            };
            list.Add(s);
            return s;
        }

        public static string ToCsv(List<BasisVoiceSimResult> results)
        {
            var sb = new StringBuilder();
            sb.AppendLine("scenario,profile,signal,seed,sent,dropped,duped,delivered,silentTicks,plc,fec,silenceInjected,genuineUnderruns,rearms,finalDepth,floor,recvLossPct,latencyMs,latStartMs,latMaxMs,latEndMs,standingMax,standingEnd,starvePlc,trimmed,accel,accelMs,flushed,salvaged,expanded,medianSnrDb,notches,notchMs,droppedAudioMs,outputSecs,pass,failure,error");
            foreach (var r in results)
            {
                sb.Append(Csv(r.ScenarioName)).Append(',');
                sb.Append(Csv(r.ProfileName)).Append(',');
                sb.Append(r.Signal).Append(',');
                sb.Append(r.Seed).Append(',');
                sb.Append(r.PacketsSent).Append(',');
                sb.Append(r.PacketsDropped).Append(',');
                sb.Append(r.PacketsDuped).Append(',');
                sb.Append(r.PacketsDelivered).Append(',');
                sb.Append(r.SilentMicTicks).Append(',');
                sb.Append(r.PlcCount).Append(',');
                sb.Append(r.FecRecoveredCount).Append(',');
                sb.Append(r.SilenceInjectedCount).Append(',');
                sb.Append(r.GenuineUnderruns).Append(',');
                sb.Append(r.RearmCount).Append(',');
                sb.Append(r.FinalPrerollDepth).Append(',');
                sb.Append(r.PrerollFloor).Append(',');
                sb.Append((r.ReceiverLossPercent01 * 100f).ToString("F1", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(r.LatencyMs.ToString("F0", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(r.LatencyStartMs.ToString("F0", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(r.LatencyMaxMs.ToString("F0", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(r.LatencyEndMs.ToString("F0", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(r.StandingFramesMax).Append(',');
                sb.Append(r.StandingFramesEnd).Append(',');
                sb.Append(r.StarvePlcCount).Append(',');
                sb.Append(r.TrimmedQuietFrames).Append(',');
                sb.Append(r.AcceleratedFrames).Append(',');
                sb.Append(r.AcceleratedSavedMs.ToString("F0", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(r.FlushedPackets).Append(',');
                sb.Append(r.LateSalvagedCount).Append(',');
                sb.Append(r.ExpandInsertedFrames).Append(',');
                sb.Append(double.IsNaN(r.MedianSegSnrDb) ? "" : r.MedianSegSnrDb.ToString("F1", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(r.NotchCount).Append(',');
                sb.Append(r.NotchTotalMs.ToString("F1", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(r.DroppedAudioMs.ToString("F0", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(r.OutputSeconds.ToString("F2", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(r.Passed ? "PASS" : "FAIL").Append(',');
                sb.Append(Csv(r.Failure)).Append(',');
                sb.Append(Csv(r.Error));
                sb.AppendLine();
            }
            return sb.ToString();
        }

        static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Contains(",") || s.Contains("\""))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
    }
}
