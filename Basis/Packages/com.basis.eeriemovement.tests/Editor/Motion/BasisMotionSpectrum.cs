using System;
using UnityEngine;
namespace Basis.IK.Motion
{
    public static class BasisMotionSpectrum
    {
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
                        float xr = re[b] * cr - im[b] * ci, xi = re[b] * ci + im[b] * cr;
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
        public static float[] Power(float[] x, float dt, out float[] freqHz)
        {
            int n = x.Length, nfft = NextPow2(n);
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
        public static float DominantAbove(float[] x, float dt, float cutoffHz)
        {
            if (x == null || x.Length < 16) return float.NaN;
            float[] p = Power(x, dt, out float[] f);
            float best = -1f, at = float.NaN;
            for (int i = 0; i < p.Length; i++)
                if (f[i] > cutoffHz && p[i] > best) { best = p[i]; at = f[i]; }
            return at;
        }
        static readonly float[] bandEdgesHz = { 0.2f, 0.5f, 1f, 2f, 4f, 6f };
        public static float ShapeDistance(float[] a, float[] b, float dt)
        {
            if (a == null || b == null || a.Length < 16 || b.Length < 16) return float.NaN;
            int n = Mathf.Min(a.Length, b.Length);
            var aa = new float[n];
            var bb = new float[n];
            Array.Copy(a, aa, n);
            Array.Copy(b, bb, n);

            float[] pa = Power(aa, dt, out float[] f), pb = Power(bb, dt, out _);
            int nb = bandEdgesHz.Length - 1;
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
            for (int k = 0; k < bandEdgesHz.Length - 1; k++)
                if (hz >= bandEdgesHz[k] && hz < bandEdgesHz[k + 1])
                    return k;
            return -1;
        }
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

            float df = fs / nfft, span = (last - first) * df;
            if (span <= 0f) return float.NaN;

            double arc = 0;
            for (int i = first; i < last; i++)
            {
                double dfn = df / span, dm = mag[i + 1] - mag[i];
                arc += Math.Sqrt(dfn * dfn + dm * dm);
            }
            return (float)(-arc);
        }
    }
}
