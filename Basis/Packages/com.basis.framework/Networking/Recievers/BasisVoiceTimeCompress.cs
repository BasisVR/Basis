using System;

/// <summary>
/// Time-scale compression for the voice catch-up drain: removes one pitch period
/// from a decoded frame by overlap-adding the frame onto itself at its strongest
/// autocorrelation lag. Periodic (voiced) audio shortens transparently — the
/// waveform stays phase-continuous — while unvoiced/transient content reports "no
/// confident period" and is left untouched. Pure math, no Unity dependencies, so
/// it is directly unit-testable.
/// </summary>
public static class BasisVoiceTimeCompress
{
    /// <summary>Minimum normalized autocorrelation to treat the lag as a real pitch
    /// period. Below this the frame is left unmodified (transients, noise).</summary>
    public const float MinPeriodCorrelation = 0.55f;

    /// <summary>
    /// Try to shorten <paramref name="pcm"/>[0..<paramref name="length"/>) in place by
    /// one pitch period. Returns the new length; <paramref name="savedSamples"/> is 0
    /// when no confident period was found.
    /// </summary>
    public static int AccelerateInPlace(float[] pcm, int length, int sampleRate, out int savedSamples)
    {
        savedSamples = 0;
        int minLag = sampleRate / 400;         // 2.5 ms — above typical voice f0=400 Hz
        int maxLag = sampleRate / 80;          // 12.5 ms — below f0=80 Hz
        if (maxLag > length / 3) maxLag = length / 3;
        if (maxLag <= minLag || length < minLag * 3) return length;

        // Correlation window: as long as possible while x[i+lag] stays in bounds.
        int window = length - maxLag;
        if (window > maxLag * 2) window = maxLag * 2;
        if (window < minLag) return length;

        // Coarse search (every 2nd sample in the window) for the best-normalized lag.
        int bestLag = -1;
        float bestScore = 0f;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double dot = 0, e0 = 0, e1 = 0;
            for (int i = 0; i < window; i += 2)
            {
                float a = pcm[i];
                float b = pcm[i + lag];
                dot += a * b;
                e0 += a * a;
                e1 += b * b;
            }
            double denom = Math.Sqrt(e0 * e1);
            if (denom < 1e-9) continue;
            float score = (float)(dot / denom);
            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }
        if (bestLag < 0 || bestScore < MinPeriodCorrelation) return length;

        // Overlap-add: crossfade the head onto the same waveform one period later,
        // then keep the remainder. Reads stay ahead of writes (bestLag >= minLag > 0).
        int fade = Math.Min(96, bestLag);
        float invFade = 1f / fade;
        for (int i = 0; i < fade; i++)
        {
            float w = i * invFade;
            pcm[i] = pcm[i] * (1f - w) + pcm[bestLag + i] * w;
        }
        int tail = length - (bestLag + fade);
        if (tail > 0)
            Array.Copy(pcm, bestLag + fade, pcm, fade, tail);

        savedSamples = bestLag;
        return length - bestLag;
    }
}
