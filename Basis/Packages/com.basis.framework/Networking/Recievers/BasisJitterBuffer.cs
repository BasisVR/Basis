using System;

[System.Serializable]
public class BasisJitterBuffer
{
    private struct Slot
    {
        public bool Occupied;
        public byte SequenceNumber;
        public byte[] EncodedData;
        public int EncodedLength;
        public byte SilenceUnits;
    }

    private const int BufferSize = 64;
    private const int BufferMask = BufferSize - 1;
    private const int MaxAheadDistance = BufferSize / 2;

    private readonly Slot[] _slots = new Slot[BufferSize];
    private readonly object _lock = new object();

    private byte _nextPlaybackSeq;
    private byte _highestReceivedSeq;
    private volatile bool _started;
    private bool _hasHighest;
    private int _bufferedCount;
    private int _receivedSinceStart;

    public int InitialBufferDepth => RemoteOpusSettings.JitterBufferSize;

    public void Insert(byte sequenceNumber, byte[] data, int length, byte silenceUnits)
    {
        lock (_lock)
        {
            if (!_started)
            {
                _nextPlaybackSeq = sequenceNumber;
                _started = true;
                _bufferedCount = 0;
                _receivedSinceStart = 0;
                _hasHighest = false;
            }
            int distance = SequenceDistance(sequenceNumber, _nextPlaybackSeq);
            if (distance < 0) return;
            if (distance >= MaxAheadDistance)
            {
                Reset();
                _nextPlaybackSeq = sequenceNumber;
                _started = true;
                _receivedSinceStart = 0;
                distance = 0;
            }
            if (!_hasHighest || SequenceDistance(sequenceNumber, _highestReceivedSeq) > 0)
            {
                _highestReceivedSeq = sequenceNumber;
                _hasHighest = true;
            }
            int slotIndex = sequenceNumber & BufferMask;
            if (!_slots[slotIndex].Occupied) _bufferedCount++;
            if (_slots[slotIndex].EncodedData == null || _slots[slotIndex].EncodedData.Length < length)
                _slots[slotIndex].EncodedData = new byte[length];
            Buffer.BlockCopy(data, 0, _slots[slotIndex].EncodedData, 0, length);
            _slots[slotIndex].EncodedLength = length;
            _slots[slotIndex].SequenceNumber = sequenceNumber;
            _slots[slotIndex].SilenceUnits = silenceUnits;
            _slots[slotIndex].Occupied = true;
            _receivedSinceStart++;
        }
    }

    public bool TryConsume(out byte[] data, out int length, out byte silenceUnits, out bool isMissing)
    {
        data = null; length = 0; silenceUnits = 0; isMissing = false;
        // Lock-free fast path: most players have no buffered audio, skip lock entirely
        if (!_started) return false;
        lock (_lock)
        {
            if (!_started) return false;
            if (_receivedSinceStart < InitialBufferDepth) return false;
            int slotIndex = _nextPlaybackSeq & BufferMask;
            if (_slots[slotIndex].Occupied && _slots[slotIndex].SequenceNumber == _nextPlaybackSeq)
            {
                data = _slots[slotIndex].EncodedData;
                length = _slots[slotIndex].EncodedLength;
                silenceUnits = _slots[slotIndex].SilenceUnits;
                isMissing = false;
                _slots[slotIndex].Occupied = false;
                _bufferedCount--;
                _nextPlaybackSeq++;
                return true;
            }
            else
            {
                if (_hasHighest && SequenceDistance(_nextPlaybackSeq, _highestReceivedSeq) < 0)
                {
                    isMissing = true;
                    _nextPlaybackSeq++;
                    return true;
                }
                return false;
            }
        }
    }

    private static int SequenceDistance(byte target, byte baseSeq)
    {
        return (sbyte)(target - baseSeq);
    }

    public void Reset()
    {
        lock (_lock)
        {
            for (int i = 0; i < BufferSize; i++) _slots[i].Occupied = false;
            _started = false;
            _hasHighest = false;
            _bufferedCount = 0;
            _receivedSinceStart = 0;
        }
    }
}