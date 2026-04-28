using System;
using System.Numerics;

namespace OpenLipSync.Inference.Audio
{
    public sealed class MelSpectrogramProcessor : IDisposable
    {
        private readonly int _sampleRate;
        private readonly int _hopLength;
        private readonly int _windowLength;
        private readonly int _nFft;
        private readonly int _nMels;
        private readonly float _fMin;
        private readonly float _fMax;

        private readonly float[] _window;
        private readonly float[] _fftBuffer;

        // Sparse mel filter bank. Each mel is a triangular filter that touches only
        // ~5–15 contiguous bins out of (nFft/2 + 1). The dense float[,] version paid
        // for nMels × (nFft/2 + 1) multiplies per hop AND used the slow 2D-array
        // indexer; this stores only the nonzero range per mel.
        //   _melBinStart[m] = index of first nonzero bin
        //   _melWeights[m]  = contiguous nonzero weights, _melWeights[m].Length bins
        private readonly int[] _melBinStart;
        private readonly float[][] _melWeights;

        private readonly float[] _powerSpectrum;
        private readonly float[] _melSpectrum;
        private readonly float[] _windowBuffer;
        private readonly FFTProcessor _fft;

        private readonly float[] _previousSamples;
        private bool _disposed;

        public MelSpectrogramProcessor(AudioProcessingConfig config)
        {
            _sampleRate = config.SampleRate;
            _hopLength = config.HopLengthSamples;
            _windowLength = config.WindowLengthSamples;
            _nFft = config.NFft;
            _nMels = config.NMels;
            _fMin = config.FMin;
            _fMax = config.FMax;

            _window = CreateHannWindow(_windowLength);
            _fftBuffer = new float[_nFft];
            _windowBuffer = new float[_windowLength];
            _previousSamples = new float[_windowLength - _hopLength];
            (_melBinStart, _melWeights) = CreateSparseMelFilterBank();
            _powerSpectrum = new float[_nFft / 2 + 1];
            _melSpectrum = new float[_nMels];
            _fft = new FFTProcessor(_nFft);
        }

        public int SampleRate => _sampleRate;
        public int HopLength => _hopLength;
        public int WindowLength => _windowLength;
        public int MelBands => _nMels;

        public float[] ProcessHop(ReadOnlySpan<float> hopSamples)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MelSpectrogramProcessor));
            if (hopSamples.Length != _hopLength)
                throw new ArgumentException($"Expected {_hopLength} samples, got {hopSamples.Length}", nameof(hopSamples));

            _previousSamples.AsSpan().CopyTo(_windowBuffer.AsSpan(0, _previousSamples.Length));
            hopSamples.CopyTo(_windowBuffer.AsSpan(_previousSamples.Length, _hopLength));
            _windowBuffer.AsSpan(_hopLength, _previousSamples.Length).CopyTo(_previousSamples.AsSpan());

            ApplyWindow(_windowBuffer, _window, _windowLength);

            Array.Clear(_fftBuffer, 0, _fftBuffer.Length);
            _windowBuffer.AsSpan().CopyTo(_fftBuffer.AsSpan(0, _windowLength));

            // Real-input FFT writes |X[k]|² directly: skips Complex packing on the way in
            // and the per-bin sqrt-then-square on the way out, and runs an N/2 FFT internally.
            _fft.ForwardPower(_fftBuffer, _powerSpectrum);

            // Sparse mel multiply — visit only the nonzero range per filter.
            float[] power = _powerSpectrum;
            int[] starts = _melBinStart;
            float[][] weights = _melWeights;
            for (int mel = 0; mel < _nMels; mel++)
            {
                int start = starts[mel];
                float[] w = weights[mel];
                int len = w.Length;
                float sum = 0f;
                for (int i = 0; i < len; i++)
                {
                    sum += power[start + i] * w[i];
                }
                _melSpectrum[mel] = sum;
            }

            for (int i = 0; i < _nMels; i++)
            {
                _melSpectrum[i] = 10f * MathF.Log10(MathF.Max(_melSpectrum[i], 1e-10f));
            }

            return _melSpectrum;
        }

        private static void ApplyWindow(float[] buffer, float[] window, int length)
        {
            int simd = Vector<float>.Count;
            int i = 0;
            if (simd > 1 && length >= simd)
            {
                int vEnd = length - simd;
                for (; i <= vEnd; i += simd)
                {
                    var vb = new Vector<float>(buffer, i);
                    var vw = new Vector<float>(window, i);
                    (vb * vw).CopyTo(buffer, i);
                }
            }
            for (; i < length; i++)
            {
                buffer[i] *= window[i];
            }
        }

        public bool CanProcessHop(AudioRingBuffer ringBuffer)
        {
            return ringBuffer.AvailableSamples >= _hopLength;
        }

        public bool TryProcessNextHop(AudioRingBuffer ringBuffer, out float[] melFeatures)
        {
            if (!CanProcessHop(ringBuffer))
            {
                melFeatures = Array.Empty<float>();
                return false;
            }

            Span<float> hopBuffer = stackalloc float[_hopLength];
            int read = ringBuffer.Read(hopBuffer, _hopLength);

            if (read == _hopLength)
            {
                melFeatures = ProcessHop(hopBuffer);
                return true;
            }

            melFeatures = Array.Empty<float>();
            return false;
        }

        private static float[] CreateHannWindow(int length)
        {
            var window = new float[length];
            for (int i = 0; i < length; i++)
            {
                window[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (length - 1)));
            }
            return window;
        }

        private (int[] starts, float[][] weights) CreateSparseMelFilterBank()
        {
            int numBins = _nFft / 2 + 1;
            var starts = new int[_nMels];
            var weights = new float[_nMels][];

            float melMin = HzToMel(_fMin);
            float melMax = HzToMel(_fMax);

            var melPoints = new float[_nMels + 2];
            for (int i = 0; i < melPoints.Length; i++)
            {
                melPoints[i] = melMin + (melMax - melMin) * i / (_nMels + 1);
            }

            var binPoints = new float[melPoints.Length];
            for (int i = 0; i < binPoints.Length; i++)
            {
                binPoints[i] = (_nFft + 1) * MelToHz(melPoints[i]) / _sampleRate;
            }

            for (int mel = 0; mel < _nMels; mel++)
            {
                float left = binPoints[mel];
                float center = binPoints[mel + 1];
                float right = binPoints[mel + 2];

                // First nonzero bin: smallest int strictly greater than left and <= right.
                // Match the original predicates: rising edge `bin >= left && bin <= center`,
                // falling edge `bin > center && bin <= right`. So bin == left contributes 0
                // (numerator is 0), but we keep it in the range for exact equivalence.
                int binLo = (int)Math.Ceiling(left);
                int binHi = (int)Math.Floor(right);

                if (binLo < 0) binLo = 0;
                if (binHi > numBins - 1) binHi = numBins - 1;

                if (binHi < binLo)
                {
                    starts[mel] = 0;
                    weights[mel] = Array.Empty<float>();
                    continue;
                }

                int len = binHi - binLo + 1;
                var w = new float[len];
                for (int k = 0; k < len; k++)
                {
                    int bin = binLo + k;
                    if (bin >= left && bin <= center)
                        w[k] = (bin - left) / (center - left);
                    else if (bin > center && bin <= right)
                        w[k] = (right - bin) / (right - center);
                    else
                        w[k] = 0f;
                }

                starts[mel] = binLo;
                weights[mel] = w;
            }

            return (starts, weights);
        }

        private static float HzToMel(float hz)
        {
            return 2595f * MathF.Log10(1f + hz / 700f);
        }

        private static float MelToHz(float mel)
        {
            return 700f * (MathF.Pow(10f, mel / 2595f) - 1f);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _fft?.Dispose();
                _disposed = true;
            }
        }
    }
}
