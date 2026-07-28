using System;
using System.Collections.Generic;
using System.IO;

namespace Basis.Scripts.Networking.Voice.Testing
{
    /// <summary>
    /// Pure-PCM quality analysis shared by the offline voice sim and any captured-WAV
    /// comparison (e.g. a live client recording vs. the reference it was sent).
    /// All inputs are mono float arrays; nothing here touches Unity or the network.
    /// </summary>
    public static class BasisVoiceQualityAnalysis
    {
        /// <summary>
        /// A short dip to (near) digital silence punched into otherwise-loud audio —
        /// the audible "bubbling" signature produced by underrun fade-outs.
        /// </summary>
        public struct Notch
        {
            public double StartMs;
            public double DurationMs;
        }

        // ==================== Envelope ====================

        /// <summary>
        /// Peak-abs envelope binned by time so signals at different sample rates
        /// compare directly. binMs of 1.0 for alignment, 0.25 for notch detection.
        /// </summary>
        public static float[] PeakEnvelope(float[] mono, int sampleRate, double binMs)
        {
            int samplesPerBin = Math.Max(1, (int)Math.Round(sampleRate * binMs / 1000.0));
            int bins = (mono.Length + samplesPerBin - 1) / samplesPerBin;
            float[] env = new float[bins];
            for (int b = 0; b < bins; b++)
            {
                int start = b * samplesPerBin;
                int end = Math.Min(start + samplesPerBin, mono.Length);
                float peak = 0f;
                for (int i = start; i < end; i++)
                {
                    float a = mono[i] < 0f ? -mono[i] : mono[i];
                    if (a > peak) peak = a;
                }
                env[b] = peak;
            }
            return env;
        }

        // ==================== Alignment / latency ====================

        /// <summary>
        /// Estimates how many ms <paramref name="candidate"/> lags behind
        /// <paramref name="reference"/> via normalized cross-correlation of their 1 ms
        /// peak envelopes. Rate-independent. Returns -1 when either side has no energy.
        /// </summary>
        public static double EstimateLagMs(float[] reference, int refRate, float[] candidate, int candRate, double maxLagMs)
        {
            const double binMs = 1.0;
            float[] er = PeakEnvelope(reference, refRate, binMs);
            float[] ec = PeakEnvelope(candidate, candRate, binMs);
            if (!HasEnergy(er) || !HasEnergy(ec)) return -1.0;
            // A flat envelope (continuous tone/sweep) gives cross-correlation no peak to
            // lock onto — any lag scores the same. Report unmeasurable instead of noise.
            if (!HasStructure(er) || !HasStructure(ec)) return -1.0;

            int maxLag = (int)(maxLagMs / binMs);
            int n = Math.Min(er.Length, ec.Length);
            double best = double.MinValue;
            int bestLag = 0;
            for (int lag = 0; lag <= maxLag; lag++)
            {
                double dot = 0, nr = 0, nc = 0;
                int count = Math.Min(n - lag, er.Length);
                if (count < 50) break;
                for (int i = 0; i < count; i++)
                {
                    if (i + lag >= ec.Length) break;
                    double r = er[i];
                    double c = ec[i + lag];
                    dot += r * c;
                    nr += r * r;
                    nc += c * c;
                }
                double denom = Math.Sqrt(nr * nc);
                double score = denom > 1e-12 ? dot / denom : 0.0;
                if (score > best)
                {
                    best = score;
                    bestLag = lag;
                }
            }
            return bestLag * binMs;
        }

        static bool HasEnergy(float[] env)
        {
            for (int i = 0; i < env.Length; i++)
                if (env[i] > 0.01f) return true;
            return false;
        }

        static bool HasStructure(float[] env)
        {
            double sum = 0;
            for (int i = 0; i < env.Length; i++) sum += env[i];
            double mean = sum / env.Length;
            if (mean <= 0) return false;
            double var = 0;
            for (int i = 0; i < env.Length; i++)
            {
                double d = env[i] - mean;
                var += d * d;
            }
            double cv = Math.Sqrt(var / env.Length) / mean;
            return cv > 0.25;
        }

        // ==================== Notch (bubble) detection ====================

        /// <summary>
        /// Finds underrun-style notches: runs of near-silence between
        /// <paramref name="minMs"/> and <paramref name="maxMs"/> long whose flanks
        /// (within <paramref name="flankMs"/> on both sides) carry real signal.
        /// Long gaps (word pauses, mutes) are ignored by the max-length bound.
        /// </summary>
        public static List<Notch> FindNotches(
            float[] mono, int sampleRate,
            double minMs = 0.4, double maxMs = 25.0, double flankMs = 8.0,
            float silenceFloor = 0.004f, float flankLevel = 0.03f)
        {
            const double binMs = 0.25;
            float[] env = PeakEnvelope(mono, sampleRate, binMs);
            var notches = new List<Notch>();
            int flankBins = (int)(flankMs / binMs);

            int i = 0;
            while (i < env.Length)
            {
                if (env[i] >= silenceFloor) { i++; continue; }
                int start = i;
                while (i < env.Length && env[i] < silenceFloor) i++;
                int end = i; // exclusive
                double durMs = (end - start) * binMs;
                if (durMs < minMs || durMs > maxMs) continue;

                bool leftLoud = false, rightLoud = false;
                for (int k = Math.Max(0, start - flankBins); k < start; k++)
                    if (env[k] >= flankLevel) { leftLoud = true; break; }
                for (int k = end; k < Math.Min(env.Length, end + flankBins); k++)
                    if (env[k] >= flankLevel) { rightLoud = true; break; }

                if (leftLoud && rightLoud)
                    notches.Add(new Notch { StartMs = start * binMs, DurationMs = durMs });
            }
            return notches;
        }

        // ==================== Fidelity vs. a same-rate baseline ====================

        /// <summary>
        /// Median segmental SNR (dB) of <paramref name="output"/> against a same-rate
        /// <paramref name="baseline"/>, after removing the given sample lag. Segments
        /// where the baseline is quiet are skipped; per-segment SNR is clamped to
        /// [-10, 60] so identical signals report 60 rather than infinity.
        /// </summary>
        public static double MedianSegmentalSnrDb(float[] baseline, float[] output, int sampleRate, int lagSamples,
            double segMs = 20.0, float baselineActiveRms = 0.01f)
        {
            int segLen = Math.Max(32, (int)(sampleRate * segMs / 1000.0));
            var snrs = new List<double>();
            for (int start = 0; start + segLen <= baseline.Length; start += segLen)
            {
                int outStart = start + lagSamples;
                if (outStart < 0 || outStart + segLen > output.Length) continue;

                double refPow = 0, errPow = 0;
                for (int k = 0; k < segLen; k++)
                {
                    double b = baseline[start + k];
                    double o = output[outStart + k];
                    refPow += b * b;
                    double e = b - o;
                    errPow += e * e;
                }
                double rms = Math.Sqrt(refPow / segLen);
                if (rms < baselineActiveRms) continue;

                double snr = errPow < 1e-20 ? 60.0 : 10.0 * Math.Log10(refPow / errPow);
                if (snr > 60.0) snr = 60.0;
                else if (snr < -10.0) snr = -10.0;
                snrs.Add(snr);
            }
            if (snrs.Count == 0) return double.NaN;
            snrs.Sort();
            return snrs[snrs.Count / 2];
        }

        /// <summary>
        /// Best sample lag between two same-rate signals: coarse 1 ms envelope search
        /// refined to the sample level over a ±2 ms window. Returns 0 when either
        /// side has no energy.
        /// </summary>
        public static int SampleAlign(float[] baseline, float[] output, int sampleRate, double maxLagMs = 250.0)
        {
            double coarseMs = EstimateLagMs(baseline, sampleRate, output, sampleRate, maxLagMs);
            if (coarseMs < 0) return 0;
            int coarse = (int)(coarseMs * sampleRate / 1000.0);
            int window = sampleRate / 500; // ±2 ms
            int n = Math.Min(baseline.Length, sampleRate * 2); // correlate up to 2 s
            double best = double.MinValue;
            int bestLag = coarse;
            for (int lag = coarse - window; lag <= coarse + window; lag++)
            {
                if (lag < 0) continue;
                double dot = 0;
                int count = Math.Min(n, output.Length - lag);
                if (count < 256) continue;
                for (int i = 0; i < count; i++)
                    dot += baseline[i] * output[i + lag];
                if (dot > best)
                {
                    best = dot;
                    bestLag = lag;
                }
            }
            return bestLag;
        }

        /// <summary>
        /// Total ms where the baseline carries clear audio but the output is (near)
        /// silent, i.e. audio that was lost outright rather than degraded. Evaluated
        /// in 5 ms windows after removing <paramref name="lagSamples"/>.
        /// </summary>
        public static double DroppedAudioMs(float[] baseline, float[] output, int sampleRate, int lagSamples,
            float baselineActiveRms = 0.02f, float droppedRatio = 0.1f)
        {
            int win = Math.Max(16, sampleRate * 5 / 1000);
            double dropped = 0;
            for (int start = 0; start + win <= baseline.Length; start += win)
            {
                int outStart = start + lagSamples;
                if (outStart < 0 || outStart + win > output.Length) continue;
                double bp = 0, op = 0;
                for (int k = 0; k < win; k++)
                {
                    bp += baseline[start + k] * baseline[start + k];
                    op += output[outStart + k] * output[outStart + k];
                }
                double bRms = Math.Sqrt(bp / win);
                double oRms = Math.Sqrt(op / win);
                if (bRms > baselineActiveRms && oRms < bRms * droppedRatio)
                    dropped += 5.0;
            }
            return dropped;
        }

        // ==================== WAV I/O ====================

        /// <summary>Writes mono float PCM as a 16-bit WAV.</summary>
        public static void WriteWav(string path, float[] mono, int sampleRate)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var w = new BinaryWriter(fs);
            int dataBytes = mono.Length * 2;
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + dataBytes);
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            w.Write(16);
            w.Write((short)1);           // PCM
            w.Write((short)1);           // mono
            w.Write(sampleRate);
            w.Write(sampleRate * 2);     // byte rate
            w.Write((short)2);           // block align
            w.Write((short)16);          // bits
            w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            w.Write(dataBytes);
            for (int i = 0; i < mono.Length; i++)
            {
                float v = mono[i];
                if (v > 1f) v = 1f;
                else if (v < -1f) v = -1f;
                w.Write((short)Math.Round(v * 32767f));
            }
        }

        /// <summary>
        /// Reads a WAV (16-bit PCM or 32-bit float, any channel count) as mono float.
        /// Multi-channel input is averaged down.
        /// </summary>
        public static float[] ReadWavMono(string path, out int sampleRate)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var r = new BinaryReader(fs);
            if (new string(r.ReadChars(4)) != "RIFF") throw new InvalidDataException("Not a RIFF file");
            r.ReadInt32();
            if (new string(r.ReadChars(4)) != "WAVE") throw new InvalidDataException("Not a WAVE file");

            short format = 0, channels = 0, bits = 0;
            sampleRate = 0;
            while (fs.Position + 8 <= fs.Length)
            {
                string chunk = new string(r.ReadChars(4));
                int size = r.ReadInt32();
                if (chunk == "fmt ")
                {
                    format = r.ReadInt16();
                    channels = r.ReadInt16();
                    sampleRate = r.ReadInt32();
                    r.ReadInt32();
                    r.ReadInt16();
                    bits = r.ReadInt16();
                    if (size > 16) r.ReadBytes(size - 16);
                }
                else if (chunk == "data")
                {
                    if (channels <= 0 || sampleRate <= 0) throw new InvalidDataException("data chunk before fmt");
                    int bytesPerSample = bits / 8;
                    int frames = size / (bytesPerSample * channels);
                    float[] mono = new float[frames];
                    for (int f = 0; f < frames; f++)
                    {
                        float sum = 0f;
                        for (int c = 0; c < channels; c++)
                        {
                            if (format == 3 && bits == 32) sum += r.ReadSingle();
                            else if (bits == 16) sum += r.ReadInt16() / 32768f;
                            else throw new InvalidDataException($"Unsupported WAV format {format}/{bits}bit");
                        }
                        mono[f] = sum / channels;
                    }
                    return mono;
                }
                else
                {
                    r.ReadBytes(size);
                }
            }
            throw new InvalidDataException("No data chunk found");
        }
    }
}
