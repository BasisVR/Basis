using System;

namespace OpenLipSync.Inference.Audio
{
    public sealed class AudioRingBuffer : IDisposable
    {
        private readonly float[] _buffer;
        private readonly int _capacity;
        private volatile int _writeIndex;
        private volatile int _readIndex;
        private volatile int _availableSamples;
        private readonly object _lock = new object();
        private bool _disposed;

        public AudioRingBuffer(int capacitySamples)
        {
            if (capacitySamples <= 0)
                throw new ArgumentException("Capacity must be positive", nameof(capacitySamples));

            _capacity = NextPowerOfTwo(capacitySamples);
            _buffer = new float[_capacity];
            _writeIndex = 0;
            _readIndex = 0;
            _availableSamples = 0;
        }

        public int Capacity => _capacity;
        public int AvailableSamples => _availableSamples;

        public int Write(ReadOnlySpan<float> samples)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AudioRingBuffer));
            if (samples.IsEmpty) return 0;

            lock (_lock)
            {
                int samplesToWrite = Math.Min(samples.Length, _capacity);
                int written = 0;

                while (written < samplesToWrite)
                {
                    int contiguousSpace = Math.Min(
                        samplesToWrite - written,
                        _capacity - (_writeIndex & (_capacity - 1))
                    );

                    samples.Slice(written, contiguousSpace)
                        .CopyTo(_buffer.AsSpan((_writeIndex & (_capacity - 1)), contiguousSpace));

                    written += contiguousSpace;
                    _writeIndex = (_writeIndex + contiguousSpace) & (_capacity - 1);
                }

                _availableSamples = Math.Min(_availableSamples + samplesToWrite, _capacity);

                if (_availableSamples == _capacity && samplesToWrite > 0)
                {
                    _readIndex = _writeIndex;
                }

                return samplesToWrite;
            }
        }

        public int Read(Span<float> output, int count)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AudioRingBuffer));
            if (count <= 0) return 0;

            lock (_lock)
            {
                int samplesToRead = Math.Min(count, Math.Min(_availableSamples, output.Length));
                int read = 0;

                while (read < samplesToRead)
                {
                    int contiguousData = Math.Min(
                        samplesToRead - read,
                        _capacity - (_readIndex & (_capacity - 1))
                    );

                    _buffer.AsSpan((_readIndex & (_capacity - 1)), contiguousData)
                        .CopyTo(output.Slice(read, contiguousData));

                    read += contiguousData;
                    _readIndex = (_readIndex + contiguousData) & (_capacity - 1);
                }

                _availableSamples -= samplesToRead;
                return samplesToRead;
            }
        }

        public void Clear()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AudioRingBuffer));

            lock (_lock)
            {
                _writeIndex = 0;
                _readIndex = 0;
                _availableSamples = 0;
                Array.Clear(_buffer, 0, _buffer.Length);
            }
        }

        private static int NextPowerOfTwo(int value)
        {
            if (value <= 1) return 1;
            value--;
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            return value + 1;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                lock (_lock)
                {
                    Array.Clear(_buffer, 0, _buffer.Length);
                    _disposed = true;
                }
            }
        }
    }
}
