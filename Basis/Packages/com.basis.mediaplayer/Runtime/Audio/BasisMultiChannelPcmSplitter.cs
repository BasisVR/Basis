using System;
using System.Collections.Generic;

// Broadcasts a single interleaved-PCM source to several independent readers.
//
// The native decode engine exposes decoded audio as one interleaved float ring
// (IBasisPcmSource) that can only be consumed once. A single pump pulls from it
// and de-interleaves into a short rolling per-channel window; every reader then
// reads that window at its own cursor. Because reads don't consume, any number
// of outputs can draw from the same channel (e.g. the centre channel played in
// two places), and each output mixes the channels it wants via a tap matrix
// (one mono channel, or a stereo downmix).
//
// A single lock guards the pump and the window. Unity may invoke the per-clip
// PCM callbacks on more than one thread, so the lock keeps the shared source
// ring and the window consistent; the critical sections only copy floats.
public sealed class BasisMultiChannelPcmSplitter
{
    // Routes one source channel into one output channel at a coefficient.
    public readonly struct Tap
    {
        public readonly int Source;
        public readonly int Out;
        public readonly float Coeff;
        public Tap(int source, int outChannel, float coeff) { Source = source; Out = outChannel; Coeff = coeff; }
    }

    // Per-output cursor into the rolling window. `frac` is the sub-sample
    // remainder of a resampling read (source rate != DSP output rate).
    public sealed class Reader { internal long pos; internal double frac; }

    private readonly IBasisPcmSource source;
    private readonly int channelCount;
    private readonly int capacity;
    private readonly object gate = new object();

    // Rolling window: one ring per channel holding the last `capacity` samples.
    // writePos is the running total of samples written (shared across channels).
    private readonly float[][] window;
    private long writePos;
    private readonly List<Reader> readers = new List<Reader>();

    // Interleaved read buffer reused across pulls, sized PullFrames * channels.
    private readonly float[] readBuf;
    private const int PullFrames = 1024;

    // The source can return a partial interleaved frame (its ring drains sample
    // by sample), so the sub-frame remainder is carried to the next pull. This
    // keeps de-interleaving frame-exact — dropping it would shift every channel.
    private readonly float[] carry;
    private int carryLen;

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

    public Reader CreateReader()
    {
        lock (gate)
        {
            var r = new Reader { pos = writePos };
            readers.Add(r);
            return r;
        }
    }

    // Produces `frames` output frames (interleaved, `outChannels` wide) for one
    // reader by mixing source channels per `taps`, applying `gain`. Returns the
    // frames produced; the caller zero-fills the rest. Safe on the audio thread.
    //
    // `sourceStep` is source frames per output frame (source rate / DSP output
    // rate): the streaming AudioClip used to declare its frequency and Unity
    // resampled invisibly, but the tap path renders straight into DSP blocks,
    // so rate conversion must happen here (e.g. Quest runs the DSP at 24kHz
    // against 48kHz sources — served 1:1 that plays at half speed). Non-unity
    // steps use linear interpolation; decimation aliases content above the
    // output Nyquist, which the output device cannot render anyway.
    public int ReadMixed(Reader reader, float[] dst, int frames, int outChannels, Tap[] taps, float gain, double sourceStep = 1.0)
    {
        if (reader == null || dst == null || taps == null || outChannels < 1) return 0;
        int maxFrames = dst.Length / outChannels;
        if (frames > maxFrames) frames = maxFrames;
        if (frames <= 0) return 0;

        int tapCount = taps.Length;
        int produced = 0;
        lock (gate)
        {
            // A reader that fell outside the retained window (its AudioSource was
            // paused) snaps to the live edge so it resumes in sync with the rest.
            if (writePos - reader.pos > capacity) { reader.pos = writePos; reader.frac = 0; }

            if (sourceStep == 1.0)
            {
                reader.frac = 0;
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

    // Pulls one interleaved chunk from the source and writes every whole frame
    // into the window. Returns false when no full frame is available (treated as
    // an underrun -> silence). Caller holds gate.
    private bool Pump()
    {
        // ReadPcm runs on Unity's audio thread (via the clip PCM callback) under
        // `gate`; IBasisPcmSource implementations must be non-blocking and
        // allocation-free here.
        int got = source != null ? source.ReadPcm(readBuf) : 0;
        int total = carryLen + got;
        int frames = total / channelCount;
        if (frames <= 0)
        {
            for (int i = 0; i < got; i++) carry[carryLen + i] = readBuf[i];
            carryLen = total;
            return false;
        }

        for (int f = 0; f < frames; f++)
        {
            int ringIdx = (int)((writePos + f) % capacity);
            for (int c = 0; c < channelCount; c++)
            {
                int s = f * channelCount + c;
                window[c][ringIdx] = s < carryLen ? carry[s] : readBuf[s - carryLen];
            }
        }
        writePos += frames;

        int usable = frames * channelCount;
        int leftover = total - usable;
        // carryLen is always a sub-frame remainder (< channelCount), and frames
        // >= 1 here, so usable >= channelCount > carryLen: the leftover lies
        // wholly within readBuf and the index is never negative.
        for (int i = 0; i < leftover; i++) carry[i] = readBuf[usable + i - carryLen];
        carryLen = leftover;
        return true;
    }

    // Snaps every reader to the live edge and drops the carry, discarding any
    // buffered audio (used when restarting playback).
    public void Clear()
    {
        lock (gate)
        {
            carryLen = 0;
            foreach (var r in readers) { r.pos = writePos; r.frac = 0; }
        }
    }
}
