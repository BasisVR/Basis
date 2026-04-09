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
        private readonly Complex[] _fftInput;
        private readonly Complex[] _fftOutput;
        private readonly float[,] _melFilterBank;
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
            _fftInput = new Complex[_nFft];
            _fftOutput = new Complex[_nFft];
            _windowBuffer = new float[_windowLength];
            _previousSamples = new float[_windowLength - _hopLength];
            _melFilterBank = CreateMelFilterBank();
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

            for (int i = 0; i < _windowLength; i++)
            {
                _windowBuffer[i] *= _window[i];
            }

            Array.Clear(_fftBuffer, 0, _fftBuffer.Length);
            _windowBuffer.AsSpan().CopyTo(_fftBuffer.AsSpan(0, _windowLength));

            for (int i = 0; i < _nFft; i++)
            {
                _fftInput[i] = new Complex(_fftBuffer[i], 0);
            }

            _fft.Forward(_fftInput, _fftOutput);

            for (int i = 0; i < _powerSpectrum.Length; i++)
            {
                _powerSpectrum[i] = (float)(_fftOutput[i].Magnitude * _fftOutput[i].Magnitude);
            }

            for (int mel = 0; mel < _nMels; mel++)
            {
                float sum = 0f;
                for (int bin = 0; bin < _powerSpectrum.Length; bin++)
                {
                    sum += _powerSpectrum[bin] * _melFilterBank[mel, bin];
                }
                _melSpectrum[mel] = sum;
            }

            for (int i = 0; i < _nMels; i++)
            {
                _melSpectrum[i] = 10f * MathF.Log10(MathF.Max(_melSpectrum[i], 1e-10f));
            }

            return _melSpectrum;
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

        private float[,] CreateMelFilterBank()
        {
            var filterBank = new float[_nMels, _nFft / 2 + 1];

            float melMin = HzToMel(_fMin);
            float melMax = HzToMel(_fMax);

            var melPoints = new float[_nMels + 2];
            for (int i = 0; i < melPoints.Length; i++)
            {
                melPoints[i] = melMin + (melMax - melMin) * i / (_nMels + 1);
            }

            var hzPoints = new float[melPoints.Length];
            for (int i = 0; i < hzPoints.Length; i++)
            {
                hzPoints[i] = MelToHz(melPoints[i]);
            }

            var binPoints = new float[hzPoints.Length];
            for (int i = 0; i < hzPoints.Length; i++)
            {
                binPoints[i] = (_nFft + 1) * hzPoints[i] / _sampleRate;
            }

            for (int mel = 0; mel < _nMels; mel++)
            {
                float left = binPoints[mel];
                float center = binPoints[mel + 1];
                float right = binPoints[mel + 2];

                for (int bin = 0; bin < _nFft / 2 + 1; bin++)
                {
                    if (bin >= left && bin <= center)
                    {
                        filterBank[mel, bin] = (bin - left) / (center - left);
                    }
                    else if (bin > center && bin <= right)
                    {
                        filterBank[mel, bin] = (right - bin) / (right - center);
                    }
                    else
                    {
                        filterBank[mel, bin] = 0f;
                    }
                }
            }

            return filterBank;
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
