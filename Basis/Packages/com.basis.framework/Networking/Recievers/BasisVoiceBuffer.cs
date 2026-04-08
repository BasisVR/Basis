using System;
using System.Threading;

/// <summary>
/// Single combined voice buffer that handles both:
/// 1. Encoded packet reordering (jitter buffer) — accepts out-of-order Opus packets
/// 2. Decoded PCM frame queue — serves OnAudioFilterRead with arbitrary sample counts
///
/// Replaces the separate BasisJitterBuffer + BasisVoiceRingBuffer pair.
/// Decoded frames are stored as discrete 20ms chunks (not a flat sample ring),
/// eliminating the overwrite-and-stall problem of the old ring buffer.
/// </summary>
[Serializable]
public class BasisVoiceBuffer
{
    // ==================== Encoded packet reordering ====================

    private struct EncodedSlot
    {
        public bool Occupied;
        public byte SequenceNumber;
        public byte[] Data;
        public int Length;
        public byte SilenceUnits;
    }

    private const int EncodedSlotCount = 64;
    private const int EncodedSlotMask = EncodedSlotCount - 1;
    private const int MaxAheadDistance = EncodedSlotCount / 2;

    private readonly EncodedSlot[] _encoded = new EncodedSlot[EncodedSlotCount];
    private readonly object _encodedLock = new();

    private byte _nextPlaybackSeq;
    private byte _highestReceivedSeq;
    private volatile bool _started;
    private bool _hasHighest;
    private int _encodedCount;
    private int _receivedSinceStart;

    public int InitialBufferDepth => RemoteOpusSettings.JitterBufferSize;

    // ==================== Decoded PCM frame queue ====================

    private const int MaxDecodedFrames = 8; // ~160ms at 20ms/frame

    private readonly float[][] _decoded;
    private readonly int[] _decodedLengths;
    private readonly bool[] _decodedIsReal;
    private int _writePos;      // next slot to write into
    private int _readPos;       // current slot being read from
    private int _frameCount;    // queued decoded frames
    private int _readOffset;    // sample offset within current read frame
    private int _realFrames;    // frames flagged as real audio
    private readonly object _decodedLock = new();

    // ==================== Public properties ====================

    // Encoded (jitter) diagnostics
    public bool Started => _started;
    public int EncodedBufferedCount { get { lock (_encodedLock) return _encodedCount; } }
    public int ReceivedSinceStart { get { lock (_encodedLock) return _receivedSinceStart; } }

    // Decoded (playback) state
    public bool IsEmpty
    {
        get
        {
            lock (_decodedLock)
                return _frameCount == 0;
        }
    }

    public bool HasRealAudio => Volatile.Read(ref _realFrames) > 0;

    public int DecodedFrameCount => Volatile.Read(ref _frameCount);
    public int DecodedFrameCapacity => MaxDecodedFrames;

    /// <summary>Approximate buffered sample count for diagnostics.</summary>
    public int SampleCount
    {
        get
        {
            lock (_decodedLock)
            {
                if (_frameCount == 0) return 0;
                // First frame may be partially consumed
                int first = _decodedLengths[_readPos] - _readOffset;
                // Remaining full frames
                int rest = 0;
                for (int i = 1; i < _frameCount; i++)
                {
                    int slot = (_readPos + i) % MaxDecodedFrames;
                    rest += _decodedLengths[slot];
                }
                return first + rest;
            }
        }
    }

    // ==================== Constructor ====================

    public BasisVoiceBuffer()
    {
        _decoded = new float[MaxDecodedFrames][];
        _decodedLengths = new int[MaxDecodedFrames];
        _decodedIsReal = new bool[MaxDecodedFrames];
        for (int i = 0; i < MaxDecodedFrames; i++)
            _decoded[i] = new float[RemoteOpusSettings.FrameSize];
    }

    // ==================== Encoded packet API ====================

    /// <summary>
    /// Insert an encoded Opus packet. Called from the network thread.
    /// Packets may arrive out of order; they are stored by sequence number.
    /// </summary>
    public void InsertEncoded(byte sequenceNumber, byte[] data, int length, byte silenceUnits)
    {
        lock (_encodedLock)
        {
            if (!_started)
            {
                _nextPlaybackSeq = sequenceNumber;
                _started = true;
                _encodedCount = 0;
                _receivedSinceStart = 0;
                _hasHighest = false;
            }

            int distance = SeqDist(sequenceNumber, _nextPlaybackSeq);
            if (distance < 0) return; // old packet, discard

            if (distance >= MaxAheadDistance)
            {
                // Huge jump — reset
                ClearEncoded();
                _nextPlaybackSeq = sequenceNumber;
                _started = true;
                _receivedSinceStart = 0;
                distance = 0;
            }

            if (!_hasHighest || SeqDist(sequenceNumber, _highestReceivedSeq) > 0)
            {
                _highestReceivedSeq = sequenceNumber;
                _hasHighest = true;
            }

            int slot = sequenceNumber & EncodedSlotMask;
            if (!_encoded[slot].Occupied) _encodedCount++;
            if (_encoded[slot].Data == null || _encoded[slot].Data.Length < length)
                _encoded[slot].Data = new byte[length];
            Buffer.BlockCopy(data, 0, _encoded[slot].Data, 0, length);
            _encoded[slot].Length = length;
            _encoded[slot].SequenceNumber = sequenceNumber;
            _encoded[slot].SilenceUnits = silenceUnits;
            _encoded[slot].Occupied = true;
            _receivedSinceStart++;
        }
    }

    /// <summary>
    /// Consume the next in-order encoded packet. Called from the main thread in DrainAndDecode.
    /// Returns false when nothing is ready (either not started, still filling, or caught up).
    /// </summary>
    public bool TryConsumeEncoded(out byte[] data, out int length, out byte silenceUnits, out bool isMissing)
    {
        data = null; length = 0; silenceUnits = 0; isMissing = false;
        if (!_started) return false;

        lock (_encodedLock)
        {
            if (!_started) return false;
            if (_receivedSinceStart < InitialBufferDepth) return false;

            int slot = _nextPlaybackSeq & EncodedSlotMask;
            if (_encoded[slot].Occupied && _encoded[slot].SequenceNumber == _nextPlaybackSeq)
            {
                data = _encoded[slot].Data;
                length = _encoded[slot].Length;
                silenceUnits = _encoded[slot].SilenceUnits;
                isMissing = false;
                _encoded[slot].Occupied = false;
                _encodedCount--;
                _nextPlaybackSeq++;
                return true;
            }

            if (_hasHighest && SeqDist(_nextPlaybackSeq, _highestReceivedSeq) < 0)
            {
                isMissing = true;
                _nextPlaybackSeq++;
                return true;
            }

            return false;
        }
    }

    // ==================== Decoded PCM API ====================

    /// <summary>
    /// Push a decoded PCM frame. Called from the main thread after Opus decode.
    /// If the queue is full, the oldest frame is silently dropped.
    /// </summary>
    public void PushDecoded(float[] pcm, int sampleCount, bool hasRealAudio)
    {
        lock (_decodedLock)
        {
            if (_frameCount >= MaxDecodedFrames)
            {
                // Drop oldest
                if (_decodedIsReal[_readPos])
                    Interlocked.Decrement(ref _realFrames);
                _readPos = (_readPos + 1) % MaxDecodedFrames;
                _frameCount--;
                _readOffset = 0;
            }

            int slot = _writePos;
            if (_decoded[slot].Length < sampleCount)
                _decoded[slot] = new float[sampleCount];
            Array.Copy(pcm, 0, _decoded[slot], 0, sampleCount);
            _decodedLengths[slot] = sampleCount;
            _decodedIsReal[slot] = hasRealAudio;
            if (hasRealAudio) Interlocked.Increment(ref _realFrames);

            _writePos = (_writePos + 1) % MaxDecodedFrames;
            _frameCount++;
        }
    }

    /// <summary>
    /// Read up to 'frames' mono PCM samples into output[0..frames-1].
    /// Handles partial frame reads across call boundaries.
    /// Returns number of samples written (caller should zero-fill the rest).
    /// Called from the audio thread.
    /// </summary>
    public int ReadPcm(float[] output, int frames)
    {
        int written = 0;

        lock (_decodedLock)
        {
            while (written < frames && _frameCount > 0)
            {
                int slot = _readPos;
                int available = _decodedLengths[slot] - _readOffset;
                int needed = frames - written;
                int toCopy = available < needed ? available : needed;

                Array.Copy(_decoded[slot], _readOffset, output, written, toCopy);
                written += toCopy;
                _readOffset += toCopy;

                if (_readOffset >= _decodedLengths[slot])
                {
                    // Frame fully consumed
                    if (_decodedIsReal[slot])
                        Interlocked.Decrement(ref _realFrames);
                    _readPos = (_readPos + 1) % MaxDecodedFrames;
                    _frameCount--;
                    _readOffset = 0;
                }
            }
        }

        return written;
    }

    /// <summary>
    /// Clear all decoded frames. Call on silence → voice transitions.
    /// </summary>
    public void ClearDecoded()
    {
        lock (_decodedLock)
        {
            _writePos = 0;
            _readPos = 0;
            _frameCount = 0;
            _readOffset = 0;
            Interlocked.Exchange(ref _realFrames, 0);
        }
    }

    /// <summary>Full reset of both encoded and decoded sides.</summary>
    public void Reset()
    {
        lock (_encodedLock) ClearEncoded();
        ClearDecoded();
    }

    private void ClearEncoded()
    {
        for (int i = 0; i < EncodedSlotCount; i++)
            _encoded[i].Occupied = false;
        _started = false;
        _hasHighest = false;
        _encodedCount = 0;
        _receivedSinceStart = 0;
    }

    private static int SeqDist(byte target, byte baseSeq) => (sbyte)(target - baseSeq);
}
