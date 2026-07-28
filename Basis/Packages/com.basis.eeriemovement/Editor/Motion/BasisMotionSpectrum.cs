using System;
using UnityEngine;

namespace Basis.IK.Motion
{
    /// <summary>
    /// Frequency-domain metrics for the IK motion-quality harness.
    ///
    /// There IS an FFT in the repo already (com.basis.openlipsync FFTProcessor) but it is `internal`
    /// to an assembly the IK tests do not reference, so reaching it would mean an InternalsVisibleTo
    /// plus a new asmdef edge to a lipsync package. Not worth the coupling for 60 lines of radix-2.
    ///
    /// Why the spectrum earns its place, when jerk and SPARC already exist: they are SCALARS, and a
    /// scalar cannot tell you WHERE the badness is. The spectrum can. A peak parked at Nyquist means
    /// a two-frame limit cycle (a feedback loop oscillating against itself); a peak at the step
    /// frequency means the stepper; broadband hash means the input. Same "the elbow is jittery"
    /// complaint, three completely different bugs, and the dominant frequency tells them apart.
    /// </summary>
    public static class BasisMotionSpectrum
    {
        /// <summary>In-place iterative radix-2 Cooley-Tukey. Length MUST be a power of two.</summary>
        public static void Fft(float[] re, float[] im)
        {
            int n = re.Length;
            if (n <= 1) return;
            if ((n & (n - 1)) != 0) throw new ArgumentException("FFT length must be a power of two, got " + n);

            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }

            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2.0 * Math.PI / len;
                float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    float cr = 1f, ci = 0f;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = i + k + len / 2;
                        float xr = re[b] * cr - im[b] * ci;
                        float xi = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - xr; im[b] = im[a] - xi;
                        re[a] += xr; im[a] += xi;
                        float ncr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = ncr;
                    }
                }
            }
        }

        static int NextPow2(int n)
        {
            int p = 1;
            while (p < n) p <<= 1;
            return p;
        }

        /// <summary>One-sided power spectrum of a real signal, Hann-windowed and mean-removed.
        ///
        /// The window is not optional. Without it a non-periodic signal (every joint path -- it does
        /// not start and end in the same place) produces a step discontinuity at the DFT wrap-around,
        /// and the leakage from that step lands in EVERY bin, including the high ones we are about to
        /// call "jitter". First attempt at this measured the leakage and called it a defect.</summary>
        public static float[] Power(float[] x, float dt, out float[] freqHz)
        {
            int n = x.Length;
            int nfft = NextPow2(n);
            var re = new float[nfft];
            var im = new float[nfft];

            double mean = 0;
            for (int i = 0; i < n; i++) mean += x[i];
            mean /= Math.Max(1, n);

            for (int i = 0; i < n; i++)
            {
                float w = 0.5f * (1f - Mathf.Cos(2f * Mathf.PI * i / Mathf.Max(1, n - 1)));
                re[i] = (float)(x[i] - mean) * w;
            }

            Fft(re, im);

            int half = nfft / 2 + 1;
            var p = new float[half];
            freqHz = new float[half];
            float fs = 1f / dt;
            for (int i = 0; i < half; i++)
            {
                p[i] = re[i] * re[i] + im[i] * im[i];
                freqHz[i] = i * fs / nfft;
            }
            return p;
        }

        /// <summary>Fraction of the signal's power sitting above cutoffHz. Run it on VELOCITY, not
        /// position: a position path's power is overwhelmingly DC and low-frequency, which drowns the
        /// ratio and makes a badly-buzzing joint look clean.</summary>
        public static float HighBandRatio(float[] x, float dt, float cutoffHz)
        {
            if (x == null || x.Length < 16) return float.NaN;
            float[] p = Power(x, dt, out float[] f);
            double tot = 0, hi = 0;
            for (int i = 0; i < p.Length; i++)
            {
                tot += p[i];
                if (f[i] > cutoffHz) hi += p[i];
            }
            return tot <= 0 ? float.NaN : (float)(hi / tot);
        }

        /// <summary>Frequency of the biggest peak above cutoffHz. Diagnostic, never a gate -- it tells
        /// you WHICH bug you have, not whether you have one.</summary>
        public static float DominantAbove(float[] x, float dt, float cutoffHz)
        {
            if (x == null || x.Length < 16) return float.NaN;
            float[] p = Power(x, dt, out float[] f);
            float best = -1f, at = float.NaN;
            for (int i = 0; i < p.Length; i++)
                if (f[i] > cutoffHz && p[i] > best) { best = p[i]; at = f[i]; }
            return at;
        }

        /// <summary>
        /// Octave-ish bands spanning the voluntary-motion band. Everything above ~6 Hz is stripped by
        /// BasisMotionSignal.LowPass before this ever runs (that content is the JITTER metric's job), so
        /// there is nothing to compare up there.
        /// </summary>
        static readonly float[] k_BandEdgesHz = { 0.2f, 0.5f, 1f, 2f, 4f, 6f };

        /// <summary>
        /// Total-variation distance between two signals' spectra, compared BAND BY BAND and each
        /// normalised to unit total energy first -- so this measures SHAPE (where the energy lives),
        /// not amplitude. 0 = identical distribution across frequency; 1 = no overlap at all.
        ///
        /// This is the metric that catches the failures every SMOOTHNESS metric rewards. Over-smoothed,
        /// plateaued and linearised ("robotic") motion all score BETTER than a real human on jerk and
        /// SPARC, because they genuinely are smoother -- that is the whole problem with them. But they
        /// put their energy in the wrong bands, and this sees that immediately.
        ///
        /// ⚠ IT COMPARES BANDS, NOT FFT BINS, AND THAT IS LOAD-BEARING. The first version summed the
        /// TV distance over raw bins and it was unusable as a cross-trajectory gate: a short clip has
        /// only ~60 bins in the band of interest, real motion concentrates its power into a handful of
        /// them, and TV distance between two spiky spectra is then dominated by bin-level noise rather
        /// than by any real difference in where the energy sits. It scored a solved elbow sitting 2 cm
        /// from the truth at 0.69 -- worse than a crude lookup guess, which is impossible. The
        /// HintSources_Compared test exists precisely to catch that, and it did.
        ///
        /// Bands are what the metric was always FOR: the failure it names is "the energy is in the
        /// wrong frequency bands".
        /// </summary>
        public static float ShapeDistance(float[] a, float[] b, float dt)
        {
            if (a == null || b == null || a.Length < 16 || b.Length < 16) return float.NaN;
            int n = Mathf.Min(a.Length, b.Length);
            var aa = new float[n];
            var bb = new float[n];
            Array.Copy(a, aa, n);
            Array.Copy(b, bb, n);

            float[] pa = Power(aa, dt, out float[] f);
            float[] pb = Power(bb, dt, out _);

            int nb = k_BandEdgesHz.Length - 1;
            var ea = new double[nb];
            var eb = new double[nb];
            for (int i = 0; i < pa.Length; i++)
            {
                int band = BandOf(f[i]);
                if (band < 0) continue;
                ea[band] += pa[i];
                eb[band] += pb[i];
            }

            double sa = 0, sb = 0;
            for (int k = 0; k < nb; k++) { sa += ea[k]; sb += eb[k]; }
            if (sa <= 0 || sb <= 0) return float.NaN;

            double tv = 0;
            for (int k = 0; k < nb; k++) tv += Math.Abs(ea[k] / sa - eb[k] / sb);
            return (float)(0.5 * tv);
        }

        static int BandOf(float hz)
        {
            for (int k = 0; k < k_BandEdgesHz.Length - 1; k++)
                if (hz >= k_BandEdgesHz[k] && hz < k_BandEdgesHz[k + 1])
                    return k;
            return -1;
        }

        /// <summary>
        /// Spectral arc length (Balasubramanian et al. 2015, J NeuroEng Rehabil 12:112) of a speed
        /// profile. Dimensionless, and invariant to BOTH movement amplitude and duration -- which is
        /// what lets it be compared across avatar scales and across clips of different length, and is
        /// why it beats a raw jerk integral as a portable smoothness number.
        ///
        /// Calibration standard: a minimum-jerk reach scores -1.40 (we measure -1.397). More negative
        /// = less smooth. Guarded by BasisMotionAnalyzerTests -- if that number moves, this is broken.
        /// </summary>
        public static float Sparc(float[] speed, float dt, float fcHz = 10f, float ampThreshold = 0.05f)
        {
            if (speed == null || speed.Length < 8) return float.NaN;

            // Pad well past the next power of two: SPARC integrates along the spectrum's arc, so it
            // needs fine frequency resolution or the arc is measured on a handful of coarse steps.
            int nfft = NextPow2(speed.Length) * 16;
            var re = new float[nfft];
            var im = new float[nfft];
            Array.Copy(speed, re, speed.Length);
            Fft(re, im);

            int half = nfft / 2 + 1;
            var mag = new float[half];
            float peak = 0f;
            for (int i = 0; i < half; i++)
            {
                mag[i] = Mathf.Sqrt(re[i] * re[i] + im[i] * im[i]);
                if (mag[i] > peak) peak = mag[i];
            }
            if (peak <= 0f) return float.NaN;

            float fs = 1f / dt;
            int last = -1, first = -1;
            int cut = Mathf.Min(half - 1, Mathf.FloorToInt(fcHz * nfft / fs));
            for (int i = 0; i <= cut; i++)
            {
                mag[i] /= peak;
                if (mag[i] >= ampThreshold)
                {
                    if (first < 0) first = i;
                    last = i;
                }
            }
            if (first < 0 || last <= first) return float.NaN;

            float df = fs / nfft;
            float span = (last - first) * df;
            if (span <= 0f) return float.NaN;

            double arc = 0;
            for (int i = first; i < last; i++)
            {
                double dfn = df / span;
                double dm = mag[i + 1] - mag[i];
                arc += Math.Sqrt(dfn * dfn + dm * dm);
            }
            return (float)(-arc);
        }
    }
}
