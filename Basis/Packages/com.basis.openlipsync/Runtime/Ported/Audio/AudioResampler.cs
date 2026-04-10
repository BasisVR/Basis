using System;
using System.Collections.Concurrent;
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

        // Cached output list and array to avoid per-call allocation.
        private readonly List<float> _outputList = new List<float>();
        private float[] _outputArray = Array.Empty<float>();

        // ── Coefficient table cache ──
        // The table depends only on (inputRate, outputRate, taps, phases, cutoffScale).
        // For OpenLipSync all contexts use the same rates, so this avoids rebuilding
        // the 49,152-float table (~196 KB) for every new context.
        private static readonly ConcurrentDictionary<long, float[]> _coeffCache = new ConcurrentDictionary<long, float[]>();

        private static long MakeCacheKey(int inputRate, int outputRate, int taps, int phases)
        {
            // Pack 4 ints into a single long key
            return ((long)inputRate << 48) | ((long)(outputRate & 0xFFFF) << 32) | ((long)(taps & 0xFFFF) << 16) | (long)(phases & 0xFFFF);
        }

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

            // Check cache before computing the expensive coefficient table
            long key = MakeCacheKey(inputSampleRate, outputSampleRate, filterTaps, numPhases);
            if (_coeffCache.TryGetValue(key, out float[] cached))
            {
                _coeffTable = cached;
            }
            else
            {
                _coeffTable = new float[_numPhases * _filterTaps];
                double fc = 0.5 * Math.Min(1.0, (double)_outputSampleRate / _inputSampleRate) * cutoffScale;
                BuildCoefficientTable(fc);
                _coeffCache.TryAdd(key, _coeffTable);
            }

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

            _outputList.Clear();

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
                _outputList.Add((float)sum);

                _time += _inputPerOutput;
            }

            int safeToRemove = (int)Math.Floor(_time) - (_halfTaps - 1);
            if (safeToRemove > 0)
            {
                _buffer.RemoveRange(0, Math.Min(safeToRemove, _buffer.Count));
                _time -= safeToRemove;
                if (_time < 0) _time = 0;
            }

            int count = _outputList.Count;
            if (count == 0) return Array.Empty<float>();

            // Reuse the cached array if it's the right size, otherwise resize once.
            if (_outputArray.Length != count)
                _outputArray = new float[count];
            _outputList.CopyTo(_outputArray);
            return _outputArray;
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
