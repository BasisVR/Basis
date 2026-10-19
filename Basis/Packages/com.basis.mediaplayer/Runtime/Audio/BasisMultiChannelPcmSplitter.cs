using System;
using System.Collections.Generic;

/// <summary>
/// Broadcasts a single interleaved-PCM source to several independent readers.
///
/// The engine exposes decoded audio as one interleaved float ring
/// (<see cref="IBasisPcmSource"/>) that can only be consumed once. A single
/// pump pulls from it and de-interleaves into a short rolling per-channel
/// window; every reader then reads that window at its own cursor. Because
/// reads don't consume, any number of outputs can draw from the same channel
/// (e.g. the centre channel played in two places), and each output mixes the
/// channels it wants via a tap matrix (one mono channel, or a stereo
/// downmix).
///
/// A single lock guards the pump and the window. Unity may invoke the audio
/// callbacks on more than one thread, so the lock keeps the shared source
/// ring and the window consistent; the critical sections only copy floats.
/// </summary>
public sealed class BasisMultiChannelPcmSplitter
{
    /// <summary>Routes one source channel into one output channel at a
    /// coefficient.</summary>
    public readonly struct Tap
    {
        public readonly int Source;
        public readonly int Out;
        public readonly float Coeff;
        public Tap(int source, int outChannel, float coeff) { Source = source; Out = outChannel; Coeff = coeff; }
    }

    /// <summary>Per-output cursor into the rolling window. <c>frac</c> is the
    /// sub-sample remainder of a resampling read (source rate != DSP output
    /// rate, or a sync trim either side of it).</summary>
    public sealed class Reader { internal long pos; internal double frac; }

    private readonly IBasisPcmSource source;
    private readonly int channelCount;
    private readonly int capacity;
    private readonly object gate = new object();

    // Rolling window: one ring per channel holding the last `capacity`
    // samples. writePos is the running total of samples written (shared
    // across channels).
    private readonly float[][] window;
    private long writePos;
    private readonly List<Reader> readers = new List<Reader>();

    // Interleaved read buffer reused across pulls, sized PullFrames * channels.
    private readonly float[] readBuf;
    private const int PullFrames = 1024;

    // The source can return a partial interleaved frame (its ring drains
    // sample by sample), so the sub-frame remainder is carried to the next
    // pull. Dropping it would shift every channel.
    private readonly float[] carry;
    private int carryLen;

    // Anti-alias low-pass applied as samples enter the window, while the
    // readers decimate. `history` holds each channel's last `lowPass.Length`
    // raw samples twice over, so a tap never wraps.
    private float[] lowPass;
    private float[][] history;
    private int historyPos;

    /// <summary>Delay the low-pass adds, in source frames (0 when it is
    /// off).</summary>
    public int FilterDelayFrames { get; private set; }

    public int ChannelCount => channelCount;

    public BasisMultiChannelPcmSplitter(IBasisPcmSource source, int channelCount, int windowSamples)
    {
        this.source = source;
        this.channelCount = Math.Max(1, channelCount);
        capacity = Math.Max(PullFrames * 2, windowSamples);
        window = new float[this.channelCount][];
        for (int c = 0; c < this.channelCount; c++) window[c] = new float[capacity];
        readBuf = new float[PullFrames * this.channelCount];
        carry = new float[this.channelCount];
    }

    /// <summary>
    /// Band-limit the window for readers stepping through it at
    /// <paramref name="sourceFramesPerOutputFrame"/>. Above 1 the readers
    /// decimate, and anything over the output's Nyquist frequency would fold
    /// back into the audible band (a 48 kHz source on Quest's 24 kHz DSP), so
    /// each channel is low-passed on the way in. At 1 or below the filter is
    /// off. Allocates: call from the main thread.
    /// </summary>
    public void SetDecimation(double sourceFramesPerOutputFrame)
    {
        float[] taps = DesignLowPass(sourceFramesPerOutputFrame);
        float[][] hist = null;
        if (taps != null)
        {
            hist = new float[channelCount][];
            for (int c = 0; c < channelCount; c++) hist[c] = new float[taps.Length * 2];
        }
        lock (gate)
        {
            lowPass = taps;
            history = hist;
            historyPos = 0;
            FilterDelayFrames = taps != null ? (taps.Length - 1) / 2 : 0;
        }
    }

    // Kaiser-windowed sinc, about 60 dB down from the output Nyquist frequency
    // up, passing everything below 80% of it. Null when nothing needs
    // removing.
    private static float[] DesignLowPass(double ratio)
    {
        if (!(ratio > 1.001)) return null;
        double nyquist = 0.5 / ratio;
        double transition = 0.2 * nyquist;
        double cutoff = nyquist - transition / 2;
        const double attenuationDb = 60;
        double beta = 0.1102 * (attenuationDb - 8.7);
        int n = (int)Math.Ceiling((attenuationDb - 7.95) / (2.285 * 2 * Math.PI * transition)) + 1;
        n = Math.Min(n | 1, 255);
        int mid = (n - 1) / 2;
        var taps = new float[n];
        double i0Beta = BesselI0(beta);
        double sum = 0;
        double[] h = new double[n];
        for (int i = 0; i < n; i++)
        {
            int t = i - mid;
            double sinc = t == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * t) / (Math.PI * t);
            double r = (double)t / mid;
            h[i] = sinc * BesselI0(beta * Math.Sqrt(1 - r * r)) / i0Beta;
            sum += h[i];
        }
        for (int i = 0; i < n; i++) taps[i] = (float)(h[i] / sum);
        return taps;
    }

    private static double BesselI0(double x)
    {
        double term = 1, sum = 1, q = x * x / 4;
        for (int k = 1; k < 50 && term > sum * 1e-12; k++)
        {
            term *= q / (k * k);
            sum += term;
        }
        return sum;
    }

    public Reader CreateReader()
    {
        lock (gate)
        {
            var r = new Reader { pos = writePos };
            readers.Add(r);
            return r;
        }
    }

    /// <summary>
    /// Produces <paramref name="frames"/> output frames (interleaved,
    /// <paramref name="outChannels"/> wide) for one reader by mixing source
    /// channels per <paramref name="taps"/>, applying
    /// <paramref name="gain"/>. Returns the frames produced; the caller
    /// zero-fills the rest. Safe on the audio thread.
    ///
    /// <paramref name="sourceStep"/> is source frames per output frame:
    /// the tap renders straight into DSP blocks, so both the device rate
    /// conversion (Quest runs the DSP at 24 kHz against 48 kHz sources,
    /// which served 1:1 would play at half speed) and the shared-playback rate
    /// trim happen here. The cursor and its sub-sample remainder live on the
    /// reader, so changing the step between calls slews the pull without
    /// resetting the interpolation. Non-unity steps use linear
    /// interpolation, over a window <see cref="SetDecimation"/> has already
    /// band-limited when the step decimates.
    /// </summary>
    public int ReadMixed(Reader reader, float[] dst, int frames, int outChannels, Tap[] taps, float gain, double sourceStep = 1.0)
    {
        if (reader == null || dst == null || taps == null || outChannels < 1) return 0;
        int maxFrames = dst.Length / outChannels;
        if (frames > maxFrames) frames = maxFrames;
        if (frames <= 0) return 0;

        lock (gate)
        {
            return ReadMixedLocked(reader, dst, frames, outChannels, taps, gain, sourceStep);
        }
    }

    // Non-blocking variant for main-thread callers. The blocking form can park the
    // main thread behind the audio thread's hold of the gate, a priority inversion
    // whose length the DSP callback dictates. Returns false, producing nothing, when
    // the gate is contended; the caller just tries again next frame.
    public bool TryReadMixed(Reader reader, float[] dst, int frames, int outChannels, Tap[] taps, float gain, out int produced, double sourceStep = 1.0)
    {
        produced = 0;
        if (reader == null || dst == null || taps == null || outChannels < 1) return true;
        int maxFrames = dst.Length / outChannels;
        if (frames > maxFrames) frames = maxFrames;
        if (frames <= 0) return true;

        if (!System.Threading.Monitor.TryEnter(gate)) return false;
        try
        {
            produced = ReadMixedLocked(reader, dst, frames, outChannels, taps, gain, sourceStep);
        }
        finally
        {
            System.Threading.Monitor.Exit(gate);
        }
        return true;
    }

    // Caller holds gate.
    private int ReadMixedLocked(Reader reader, float[] dst, int frames, int outChannels, Tap[] taps, float gain, double sourceStep)
    {
        int tapCount = taps.Length;
        int produced = 0;
        {
            // A reader that fell outside the retained window (its AudioSource
            // was paused) snaps to the live edge so it resumes in sync with
            // the rest.
            if (writePos - reader.pos > capacity) { reader.pos = writePos; reader.frac = 0; }

            // The whole-sample path only applies while the reader sits on a
            // sample boundary: a trim that lands back on 1.0 must resolve its
            // outstanding fraction here rather than have it discarded.
            if (sourceStep == 1.0 && reader.frac == 0)
            {
                while (produced < frames)
                {
                    if (reader.pos >= writePos && !Pump()) break;
                    if (reader.pos >= writePos) break;

                    long avail = writePos - reader.pos;
                    int take = (int)Math.Min(frames - produced, avail);
                    for (int k = 0; k < take; k++)
                    {
                        int outBase = (produced + k) * outChannels;
                        for (int oc = 0; oc < outChannels; oc++) dst[outBase + oc] = 0f;
                        int ringIdx = (int)((reader.pos + k) % capacity);
                        for (int t = 0; t < tapCount; t++)
                        {
                            Tap tap = taps[t];
                            if (tap.Source < 0 || tap.Source >= channelCount || tap.Out < 0 || tap.Out >= outChannels) continue;
                            dst[outBase + tap.Out] += window[tap.Source][ringIdx] * tap.Coeff;
                        }
                        if (gain != 1f)
                            for (int oc = 0; oc < outChannels; oc++) dst[outBase + oc] *= gain;
                    }
                    reader.pos += take;
                    produced += take;
                }
                return produced;
            }

            while (produced < frames)
            {
                // Interpolation needs the sample at pos and its successor.
                while (reader.pos + 1 >= writePos && Pump()) { }
                if (reader.pos >= writePos) break;
                bool haveNext = reader.pos + 1 < writePos;

                int outBase = produced * outChannels;
                for (int oc = 0; oc < outChannels; oc++) dst[outBase + oc] = 0f;
                int i0 = (int)(reader.pos % capacity);
                int i1 = haveNext ? (int)((reader.pos + 1) % capacity) : i0;
                float f1 = (float)reader.frac;
                float f0 = 1f - f1;
                for (int t = 0; t < tapCount; t++)
                {
                    Tap tap = taps[t];
                    if (tap.Source < 0 || tap.Source >= channelCount || tap.Out < 0 || tap.Out >= outChannels) continue;
                    float[] ch = window[tap.Source];
                    dst[outBase + tap.Out] += (ch[i0] * f0 + ch[i1] * f1) * tap.Coeff;
                }
                if (gain != 1f)
                    for (int oc = 0; oc < outChannels; oc++) dst[outBase + oc] *= gain;

                reader.frac += sourceStep;
                long adv = (long)reader.frac;
                reader.pos += adv;
                reader.frac -= adv;
                produced++;
            }
        }
        return produced;
    }

    // Pulls one interleaved chunk from the source and writes every whole
    // frame into the window. Returns false when no full frame is available
    // (treated as an underrun -> silence). Caller holds gate.
    private bool Pump()
    {
        // ReadPcm runs on Unity's audio thread under `gate`; IBasisPcmSource
        // implementations must be non-blocking and allocation-free here.
        int got = source != null ? source.ReadPcm(readBuf) : 0;
        if (got < 0) got = 0;
        int total = carryLen + got;
        int frames = total / channelCount;
        if (frames <= 0)
        {
            for (int i = 0; i < got; i++) carry[carryLen + i] = readBuf[i];
            carryLen = total;
            return false;
        }

        float[] taps = lowPass;
        int tapCount = taps != null ? taps.Length : 0;
        int mid = (tapCount - 1) / 2;
        for (int f = 0; f < frames; f++)
        {
            int ringIdx = (int)((writePos + f) % capacity);
            for (int c = 0; c < channelCount; c++)
            {
                int s = f * channelCount + c;
                float x = s < carryLen ? carry[s] : readBuf[s - carryLen];
                if (taps == null)
                {
                    window[c][ringIdx] = x;
                    continue;
                }
                // The newest sample lands at historyPos + tapCount, the
                // oldest at historyPos + 1; the taps are symmetric, so each
                // pair shares a multiply.
                float[] hist = history[c];
                hist[historyPos] = x;
                hist[historyPos + tapCount] = x;
                int oldest = historyPos + 1;
                int newest = historyPos + tapCount;
                float y = taps[mid] * hist[oldest + mid];
                for (int k = 0; k < mid; k++) y += taps[k] * (hist[oldest + k] + hist[newest - k]);
                window[c][ringIdx] = y;
            }
            if (taps != null && ++historyPos == tapCount) historyPos = 0;
        }
        writePos += frames;

        int usable = frames * channelCount;
        int leftover = total - usable;
        // carryLen is always a sub-frame remainder (< channelCount), and
        // frames >= 1 here, so usable >= channelCount > carryLen: the
        // leftover lies wholly within readBuf and the index is never
        // negative.
        for (int i = 0; i < leftover; i++) carry[i] = readBuf[usable + i - carryLen];
        carryLen = leftover;
        return true;
    }

    /// <summary>Snaps every reader to the live edge and drops the carry,
    /// discarding any buffered audio (used when restarting playback or
    /// landing a seek).</summary>
    public void Clear()
    {
        lock (gate)
        {
            carryLen = 0;
            foreach (var r in readers) { r.pos = writePos; r.frac = 0; }
            if (history != null)
                foreach (float[] h in history) Array.Clear(h, 0, h.Length);
            historyPos = 0;
        }
    }
}
