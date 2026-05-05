using System;
using System.Numerics;

namespace OpenLipSync.Inference.Audio
{
    internal sealed class FFTProcessor : IDisposable
    {
        private readonly int _size;

        // br[i] = bit-reverse(i) over log2(size) bits — for the full-size complex FFT.
        // Folded into the input copy so the original explicit swap loop is gone.
        private readonly int[] _br;

        // brHalf[i] = bit-reverse(i) over log2(size/2) bits — for the half-size FFT
        // used by ForwardPower. Null if size < 2.
        private readonly int[] _brHalf;

        // Twiddle tables, packed by stage. For stage of length `len`,
        // twiddle k=0..len/2-1 lives at index (len/2 + k).
        // The len=size entries (indices [size/2 .. size-1]) double as the
        // recombination twiddles e^(-i·2π·k/size) used by ForwardPower.
        private readonly float[] _twC;
        private readonly float[] _twS;

        // Split-form working buffers. Avoids:
        //   - System.Numerics.Complex.operator* (IEEE-conformant, NaN/inf branches)
        //   - double overhead (Complex = 2× double = 16 bytes per element)
        // Mel spectrogram caller does not need double precision.
        private readonly float[] _re;
        private readonly float[] _im;

        public FFTProcessor(int size)
        {
            if (size <= 0 || (size & (size - 1)) != 0)
                throw new ArgumentException("FFT size must be a power of 2", nameof(size));

            _size = size;
            _br = BuildBitReverseTable(size);
            _brHalf = size >= 2 ? BuildBitReverseTable(size / 2) : null;
            (_twC, _twS) = BuildTwiddleTables(size);
            _re = new float[size];
            _im = new float[size];
        }

        public void Forward(ReadOnlySpan<Complex> input, Span<Complex> output)
        {
            if (input.Length != _size || output.Length != _size)
                throw new ArgumentException("Input and output must match FFT size");

            int n = _size;
            float[] re = _re;
            float[] im = _im;
            int[] br = _br;

            for (int i = 0; i < n; i++)
            {
                Complex c = input[br[i]];
                re[i] = (float)c.Real;
                im[i] = (float)c.Imaginary;
            }

            Butterflies(re, im, n);

            for (int i = 0; i < n; i++)
            {
                output[i] = new Complex(re[i], im[i]);
            }
        }

        /// <summary>
        /// Real-input forward FFT that writes the power spectrum |X[k]|² for k=0..N/2 directly.
        /// Internally runs a length-N/2 complex FFT (half the work of the full-size path) and
        /// recombines, skipping both the Complex(re, 0) packing and the per-bin magnitude
        /// the caller would otherwise pay.
        /// </summary>
        /// <param name="realInput">Length-N real signal.</param>
        /// <param name="powerOut">Length-(N/2 + 1) destination for |X[k]|².</param>
        public void ForwardPower(ReadOnlySpan<float> realInput, Span<float> powerOut)
        {
            int n = _size;
            int halfN = n >> 1;

            if (n < 2)
                throw new InvalidOperationException("ForwardPower requires FFT size >= 2");
            if (realInput.Length != n)
                throw new ArgumentException($"Input length must be {n}", nameof(realInput));
            if (powerOut.Length != halfN + 1)
                throw new ArgumentException($"Power output length must be {halfN + 1}", nameof(powerOut));

            float[] re = _re;
            float[] im = _im;
            int[] br = _brHalf;

            // Pack length-N real signal as length-(N/2) complex z[k] = x[2k] + i·x[2k+1],
            // with bit-reverse permutation baked into the placement.
            for (int i = 0; i < halfN; i++)
            {
                int j = br[i];
                int j2 = j << 1;
                re[i] = realInput[j2];
                im[i] = realInput[j2 + 1];
            }

            // Length-N/2 in-place FFT. Reuses the same twiddle table — stages 2..N/2 live
            // at indices 1..N/2-1 of the table, which is what Butterflies indexes.
            Butterflies(re, im, halfN);

            // After the FFT, re[k]/im[k] hold Z[k] for k=0..N/2-1.
            // Recombine into |X[k]|² for the original length-N DFT.
            //
            // Identities (E = DFT of even-indexed samples, O = DFT of odd-indexed):
            //   E[k] =  (Z[k] + conj(Z[N/2-k])) / 2
            //   O[k] = -i·(Z[k] - conj(Z[N/2-k])) / 2
            //   X[k] = E[k] + e^(-i·2πk/N) · O[k]
            //
            // k=0 and k=N/2 are real-valued specials:
            //   X[0]   = E[0] + O[0] = Re(Z[0]) + Im(Z[0])
            //   X[N/2] = E[0] - O[0] = Re(Z[0]) - Im(Z[0])
            float z0r = re[0];
            float z0i = im[0];
            float dcSum  = z0r + z0i;
            float dcDiff = z0r - z0i;
            powerOut[0]     = dcSum  * dcSum;
            powerOut[halfN] = dcDiff * dcDiff;

            // Recombination twiddles e^(-i·2πk/N) live at the len=N stage of the table,
            // i.e. at indices [N/2 .. N-1]. Twiddle k (k=0..N/2-1) is at index N/2 + k.
            float[] twC = _twC;
            float[] twS = _twS;
            int twBase = halfN;

            for (int k = 1; k < halfN; k++)
            {
                float a = re[k];
                float b = im[k];
                float c = re[halfN - k];
                float d = im[halfN - k];

                // wRe = cos(2πk/N), wIm = -sin(2πk/N)  (forward FFT sign)
                float wRe = twC[twBase + k];
                float wIm = twS[twBase + k];

                // E[k].real = (a+c)/2,  E[k].imag = (b-d)/2
                // O[k].real = (b+d)/2,  O[k].imag = (c-a)/2
                // X[k] = E[k] + (wRe + wIm·i)·O[k]
                float halfApC = 0.5f * (a + c);
                float halfBmD = 0.5f * (b - d);
                float halfBpD = 0.5f * (b + d);
                float halfCmA = 0.5f * (c - a);

                float xRe = halfApC + wRe * halfBpD - wIm * halfCmA;
                float xIm = halfBmD + wRe * halfCmA + wIm * halfBpD;

                powerOut[k] = xRe * xRe + xIm * xIm;
            }
        }

        private void Butterflies(float[] re, float[] im, int n)
        {
            float[] twC = _twC;
            float[] twS = _twS;

            for (int len = 2; len <= n; len <<= 1)
            {
                int half = len >> 1;
                int twBase = half;

                for (int i = 0; i < n; i += len)
                {
                    for (int j = 0; j < half; j++)
                    {
                        float wRe = twC[twBase + j];
                        float wIm = twS[twBase + j];

                        int p = i + j;
                        int q = p + half;

                        float uRe = re[p];
                        float uIm = im[p];
                        float vRawRe = re[q];
                        float vRawIm = im[q];

                        // v = vRaw * w  — manual complex multiply, no IEEE guard rails.
                        float vRe = vRawRe * wRe - vRawIm * wIm;
                        float vIm = vRawRe * wIm + vRawIm * wRe;

                        re[p] = uRe + vRe;
                        im[p] = uIm + vIm;
                        re[q] = uRe - vRe;
                        im[q] = uIm - vIm;
                    }
                }
            }
        }

        private static int[] BuildBitReverseTable(int n)
        {
            int logN = 0;
            while ((1 << logN) < n) logN++;

            var tbl = new int[n];
            for (int i = 0; i < n; i++)
            {
                int j = 0, x = i;
                for (int k = 0; k < logN; k++)
                {
                    j = (j << 1) | (x & 1);
                    x >>= 1;
                }
                tbl[i] = j;
            }
            return tbl;
        }

        private static (float[] cos, float[] sin) BuildTwiddleTables(int n)
        {
            // Forward FFT: w_k = e^(-i·2π·k/len)
            var c = new float[n];
            var s = new float[n];
            for (int len = 2; len <= n; len <<= 1)
            {
                int half = len >> 1;
                for (int k = 0; k < half; k++)
                {
                    double angle = -2.0 * Math.PI * k / len;
                    c[half + k] = (float)Math.Cos(angle);
                    s[half + k] = (float)Math.Sin(angle);
                }
            }
            return (c, s);
        }

        public void Dispose() { }
    }
}
