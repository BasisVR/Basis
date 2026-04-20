using System;
using System.Numerics;

namespace OpenLipSync.Inference.Audio
{
    internal sealed class FFTProcessor : IDisposable
    {
        private readonly int _size;
        private readonly Complex[] _tempBuffer;

        public FFTProcessor(int size)
        {
            if (size <= 0 || (size & (size - 1)) != 0)
                throw new ArgumentException("FFT size must be a power of 2", nameof(size));

            _size = size;
            _tempBuffer = new Complex[size];
        }

        public void Forward(ReadOnlySpan<Complex> input, Span<Complex> output)
        {
            if (input.Length != _size || output.Length != _size)
                throw new ArgumentException("Input and output must match FFT size");

            input.CopyTo(_tempBuffer);
            CooleyTukeyFFT(_tempBuffer, false);
            _tempBuffer.AsSpan().CopyTo(output);
        }

        private static void CooleyTukeyFFT(Span<Complex> data, bool inverse)
        {
            int n = data.Length;
            if (n <= 1) return;

            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1)
                {
                    j ^= bit;
                }
                j ^= bit;

                if (i < j)
                {
                    var tmp = data[i];
                    data[i] = data[j];
                    data[j] = tmp;
                }
            }

            for (int len = 2; len <= n; len <<= 1)
            {
                double angle = (inverse ? 1 : -1) * 2.0 * Math.PI / len;
                Complex wlen = new Complex(Math.Cos(angle), Math.Sin(angle));

                for (int i = 0; i < n; i += len)
                {
                    Complex w = 1;
                    for (int j = 0; j < len / 2; j++)
                    {
                        Complex u = data[i + j];
                        Complex v = data[i + j + len / 2] * w;
                        data[i + j] = u + v;
                        data[i + j + len / 2] = u - v;
                        w *= wlen;
                    }
                }
            }

            if (inverse)
            {
                for (int i = 0; i < n; i++)
                {
                    data[i] /= n;
                }
            }
        }

        public void Dispose()
        {
        }
    }
}
