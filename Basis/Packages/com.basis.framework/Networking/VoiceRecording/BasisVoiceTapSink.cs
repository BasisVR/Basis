namespace Basis.Scripts.Networking.VoiceRecording
{
    /// <summary>
    /// Thread-safe ring for decoded mono PCM feeding the recording path (NOT the playback
    /// path). Decode threads call <see cref="Write"/>; the main thread drains with
    /// <see cref="Drain"/>. A player can be decoded by both a normal and a shout receiver on
    /// independent audio threads during a mode transition, so this must tolerate multiple
    /// producers — a short lock guards the cursors and slots. The lock is only ever taken by
    /// the recording side-tap, never by voice playback, so it cannot degrade audio output.
    /// On overflow (the consumer stalled) the newest samples are dropped and counted.
    /// </summary>
    public sealed class BasisVoiceTapSink
    {
        private readonly float[] _ring;
        private readonly int _capacity;
        private readonly object _gate = new object();
        private long _writePos;
        private long _readPos;

        public long DroppedSamples { get; private set; }

        public BasisVoiceTapSink(int capacity)
        {
            _capacity = capacity < 1 ? 1 : capacity;
            _ring = new float[_capacity];
        }

        public void Write(float[] pcm, int count)
        {
            if (pcm == null || count <= 0)
            {
                return;
            }
            lock (_gate)
            {
                int free = (int)(_capacity - (_writePos - _readPos));
                if (free <= 0)
                {
                    DroppedSamples += count;
                    return;
                }
                int toWrite = count < free ? count : free;
                for (int i = 0; i < toWrite; i++)
                {
                    _ring[(int)((_writePos + i) % _capacity)] = pcm[i];
                }
                _writePos += toWrite;
                if (toWrite < count)
                {
                    DroppedSamples += count - toWrite;
                }
            }
        }

        public int Drain(float[] output, int max)
        {
            lock (_gate)
            {
                int available = (int)(_writePos - _readPos);
                if (available <= 0)
                {
                    return 0;
                }
                int toRead = available < max ? available : max;
                for (int i = 0; i < toRead; i++)
                {
                    output[i] = _ring[(int)((_readPos + i) % _capacity)];
                }
                _readPos += toRead;
                return toRead;
            }
        }

        public int Available
        {
            get
            {
                lock (_gate)
                {
                    return (int)(_writePos - _readPos);
                }
            }
        }
    }
}
