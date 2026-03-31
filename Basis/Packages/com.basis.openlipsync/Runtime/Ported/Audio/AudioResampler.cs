using System;
using System.Collections.Generic;

namespace OpenLipSync.Inference.Audio
{
    public sealed class AudioResampler : IDisposable
    {
        private readonly int _inputSampleRate;
        private readonly int _outputSampleRate;
        private readonly double _inputPerOutput;
        private readonly double _ratio;

        private readonly int _filterTaps;
        private readonly int _halfTaps;
        private readonly int _numPhases;
        private readonly float[] _coeffTable;

        private readonly List<float> _buffer = new List<float>();
        private double _time;
        private bool _primed;
        private bool _disposed;

        public AudioResampler(int inputSampleRate, int outputSampleRate, int filterTaps = 48, int numPhases = 1024, double cutoffScale = 0.9)
        {
            if (inputSampleRate <= 0) throw new ArgumentException("Input sample rate must be positive", nameof(inputSampleRate));
            if (outputSampleRate <= 0) throw new ArgumentException("Output sample rate must be positive", nameof(outputSampleRate));
            if (filterTaps < 8 || filterTaps % 2 != 0) throw new ArgumentException("Filter taps must be even and >= 8", nameof(filterTaps));
            if (numPhases < 8) throw new ArgumentException("Number of phases must be >= 8", nameof(numPhases));
            if (cutoffScale <= 0 || cutoffScale >= 1) throw new ArgumentException("cutoffScale must be in (0,1)", nameof(cutoffScale));

            _inputSampleRate = inputSampleRate;
            _outputSampleRate = outputSampleRate;
            _inputPerOutput = (double)inputSampleRate / outputSampleRate;
            _ratio = _inputPerOutput;

            _filterTaps = filterTaps;
            _halfTaps = filterTaps / 2;
            _numPhases = numPhases;
            _coeffTable = new float[_numPhases * _filterTaps];

            double fc = 0.5 * Math.Min(1.0, (double)_outputSampleRate / _inputSampleRate) * cutoffScale;
            BuildCoefficientTable(fc);

            _time = 0.0;
            _primed = false;
            _disposed = false;
        }

        public int InputSampleRate => _inputSampleRate;
        public int OutputSampleRate => _outputSampleRate;
        public double ResampleRatio => _ratio;

        public float[] Resample(ReadOnlySpan<float> input)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AudioResampler));

            if (input.Length > 0)
            {
                if (!_primed)
                {
                    for (int i = 0; i < _halfTaps; i++) _buffer.Add(0f);
                    _time = _halfTaps - 1;
                    _primed = true;
                }

                for (int i = 0; i < input.Length; i++)
                {
                    _buffer.Add(input[i]);
                }
            }

            if (_buffer.Count < _filterTaps) return Array.Empty<float>();

            var output = new List<float>(input.Length > 0 ? (int)Math.Ceiling(input.Length / _inputPerOutput) + 8 : 0);

            while (true)
            {
                int center = (int)Math.Floor(_time);
                int leftIndex = center - (_halfTaps - 1);
                int rightIndex = center + _halfTaps;

                if (leftIndex < 0 || rightIndex >= _buffer.Count)
                {
                    break;
                }

                double frac = _time - center;
                int phaseIndex = (int)Math.Round(frac * _numPhases);
                if (phaseIndex == _numPhases) phaseIndex = 0;
                int coeffBase = phaseIndex * _filterTaps;

                double sum = 0.0;
                for (int t = 0; t < _filterTaps; t++)
                {
                    sum += _buffer[leftIndex + t] * _coeffTable[coeffBase + t];
                }
                output.Add((float)sum);

                _time += _inputPerOutput;
            }

            int safeToRemove = (int)Math.Floor(_time) - (_halfTaps - 1);
            if (safeToRemove > 0)
            {
                _buffer.RemoveRange(0, Math.Min(safeToRemove, _buffer.Count));
                _time -= safeToRemove;
                if (_time < 0) _time = 0;
            }

            return output.Count == 0 ? Array.Empty<float>() : output.ToArray();
        }

        private void BuildCoefficientTable(double fc)
        {
            for (int p = 0; p < _numPhases; p++)
            {
                double frac = (double)p / _numPhases;
                double sum = 0.0;

                for (int n = 0; n < _filterTaps; n++)
                {
                    double t = n - (_halfTaps - 1) - frac;
                    double sincArg = 2.0 * fc * t;
                    double sinc = Sinc(sincArg);

                    double w = 0.42
                             - 0.5 * Math.Cos((2.0 * Math.PI * n) / (_filterTaps - 1))
                             + 0.08 * Math.Cos((4.0 * Math.PI * n) / (_filterTaps - 1));

                    double h = 2.0 * fc * sinc * w;
                    _coeffTable[p * _filterTaps + n] = (float)h;
                    sum += h;
                }

                if (sum != 0)
                {
                    float norm = (float)(1.0 / sum);
                    for (int n = 0; n < _filterTaps; n++)
                    {
                        _coeffTable[p * _filterTaps + n] *= norm;
                    }
                }
            }
        }

        private static double Sinc(double x)
        {
            if (x == 0.0) return 1.0;
            double pix = Math.PI * x;
            return Math.Sin(pix) / pix;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
            }
        }
    }
}
