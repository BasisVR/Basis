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

    // App-layer voice packet loss tracking. LiteNetLib does not track loss on
    // DeliveryMethod.Unreliable (which voice uses), so NetStatistics.PacketLoss
    // is useless here. Instead we count incoming packets vs. detected missing
    // sequences, halving the counters every DecayIntervalMs to keep the ratio
    // weighted toward recent history.
    private long _recentReceived;
    private long _recentMissing;
    private int _lastDecayTick;
    private const int DecayIntervalMs = 5000; // ~5 s half-life

    // ── Adaptive jitter depth (per stream) ──
    // RemoteOpusSettings.JitterBufferSize is the user-set FLOOR. We grow the effective
    // depth for THIS stream when packets are seen arriving too late for the current
    // depth, and relax back toward the floor when the stream is clean. Lateness is
    // measured clock-free (a packet whose playback slot already passed, counted in
    // InsertEncoded), so this needs no high-resolution timer. The depth drives both the
    // initial pre-roll and the grace window before a gap is declared lost.
    /// <summary>
    /// Millisecond clock feeding the adaptive-depth and loss-decay intervals.
    /// Defaults to wall time; the offline voice harness swaps in a virtual clock
    /// so faster-than-real-time simulation still exercises depth adaptation.
    /// </summary>
    [NonSerialized] public Func<int> TickCountSource = () => Environment.TickCount;

    private int _adaptiveExtraDepth;
    private long _arrivalsSinceAdjust;
    private long _lateSinceAdjust;
    private int _lastDepthAdjustTick;
    private const int DepthAdjustIntervalMs = 1500;
    private bool _underrunPending;
    private int _underrunsSinceAdjust;
    private int _cleanIntervals;
    private const int CleanIntervalsBeforeShrink = 4;
    public int GenuineUnderruns;
    // Pre-roll cap (packets). 10 ≈ 200 ms max startup buffering on a bad network.
    private const int MaxPrerollDepth = 10;

    // ── Arrival-gap tracker ──
    // The two largest wall-clock gaps between consecutive VOICE arrivals over the
    // last few seconds (decayed in MaybeAdjustDepthLocked). Unlike the reactive
    // late/underrun rules, this raises the target depth BEFORE damage: delivery
    // batching, wifi clumping, or congestion spacing shows up here on the first bad
    // packet, not after the queue has already gone dry. The depth uses the SECOND
    // largest gap: a repeating pattern registers on it almost as fast, while a
    // one-off outage (a 500 ms stall) doesn't get to demand 500 ms of standing
    // buffer — one-offs are what the PLC bridge and the flush are for. Gaps
    // following a sender mute are excluded — the resume packet's silenceUnits>0
    // identifies them — so intentional silence never inflates the depth.
    private int _lastArrivalTick;
    private bool _haveLastArrival;
    private int _peakGapMs;
    private int _peakGap2Ms;
    private const int GapTrackCapMs = 500;

    // How many frames of decoded runway must remain when a gap at the playback
    // cursor is finally declared lost. Holding while more runway than this exists
    // costs nothing (playback continues off the decoded queue) and gives a
    // reordered/late packet its maximum possible time to land — the effective
    // late-tolerance becomes the standing buffer depth instead of a fixed grace.
    private const int PlcReserveFrames = 2;

    /// <summary>Holes at the cursor that were resolved by the late packet actually
    /// arriving during the deadline-hold — audio that older builds would have
    /// concealed with FEC/PLC and then discarded on arrival.</summary>
    public int LateSalvagedCount;
    private byte _holdingSeq;
    private bool _holdingForSeq;

    /// <summary>Per-stream pre-roll depth: user floor, grown by observed lateness, capped.</summary>
    public int InitialBufferDepth
    {
        get { lock (_encodedLock) return TargetDepthLocked(); }
    }

    // Caller must hold _encodedLock.
    private int TargetDepthLocked()
    {
        int depth = RemoteOpusSettings.JitterBufferSize + _adaptiveExtraDepth;

        // Proactive component: enough packets to ride out the worst REPEATING
        // arrival gap seen recently (second-largest, see tracker comment), plus
        // one. Decays as the network calms.
        if (_peakGap2Ms > 0)
        {
            int frameMs = RemoteOpusSettings.FrameSize * 1000 / RemoteOpusSettings.NetworkSampleRate;
            if (frameMs < 1) frameMs = 20;
            int needed = _peakGap2Ms / frameMs + 1;
            if (needed > depth) depth = needed;
        }

        if (depth < 1) depth = 1;
        else if (depth > MaxPrerollDepth) depth = MaxPrerollDepth;
        return depth;
    }

    // Caller must hold _encodedLock. Periodically nudges _adaptiveExtraDepth from the
    // recent late-arrival ratio.
    private void MaybeAdjustDepthLocked(int now)
    {
        int elapsed = unchecked(now - _lastDepthAdjustTick);
        if (elapsed >= 0 && elapsed < DepthAdjustIntervalMs) return;
        _lastDepthAdjustTick = now;

        bool enoughTraffic = _arrivalsSinceAdjust >= 20;
        if (enoughTraffic || _underrunsSinceAdjust > 0)
        {
            float lateRatio = _arrivalsSinceAdjust > 0 ? (float)_lateSinceAdjust / _arrivalsSinceAdjust : 0f;

            int grow = 0;
            if (enoughTraffic && lateRatio > 0.05f) grow = 2;
            else if (enoughTraffic && lateRatio > 0.01f) grow = 1;
            if (_underrunsSinceAdjust >= 2 && grow < 2) grow = 2;
            else if (_underrunsSinceAdjust >= 1 && grow < 1) grow = 1;

            if (grow > 0)
            {
                _adaptiveExtraDepth += grow;
                _cleanIntervals = 0;
            }
            else if (enoughTraffic && lateRatio < 0.002f)
            {
                if (++_cleanIntervals >= CleanIntervalsBeforeShrink)
                {
                    _cleanIntervals = 0;
                    _adaptiveExtraDepth -= 1;
                }
            }

            int maxExtra = MaxPrerollDepth - RemoteOpusSettings.JitterBufferSize;
            if (maxExtra < 0) maxExtra = 0;
            if (_adaptiveExtraDepth < 0) _adaptiveExtraDepth = 0;
            else if (_adaptiveExtraDepth > maxExtra) _adaptiveExtraDepth = maxExtra;
        }
        _arrivalsSinceAdjust = 0;
        _lateSinceAdjust = 0;
        _underrunsSinceAdjust = 0;

        // Let the tracked worst arrival gaps fade: ~×0.7 per interval halves them in
        // two, so a one-off spike stops holding the depth up after a few seconds
        // while sustained batching keeps re-feeding them.
        _peakGapMs = _peakGapMs * 7 / 10;
        _peakGap2Ms = _peakGap2Ms * 7 / 10;
    }

    public void NoteUnderrun()
    {
        lock (_encodedLock)
        {
            if (_started && _receivedSinceStart >= TargetDepthLocked())
            {
                _underrunPending = true;
                // Mid-stream dry-out with nothing buffered: require a short refill
                // (mirrors the resync path's TargetDepth-2) before releasing more
                // audio. Without this, playback resumes the instant one packet lands
                // and rides a 0-1 packet queue, underrunning at the callback/packet
                // beat — an audible bubble on every resume.
                if (_encodedCount == 0)
                    _receivedSinceStart = Math.Max(0, TargetDepthLocked() - 2);
            }
        }
    }

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

    /// <summary>
    /// Rolling packet-loss ratio for incoming voice in the [0..1] range, weighted
    /// toward the last ~5 s of traffic. 0 means no losses seen; 0.1 means ~10% of
    /// expected packets were missing. Useful as an indicator that the network is
    /// degraded enough to warrant bumping <see cref="LocalOpusSettings.PacketLossPercent"/>
    /// (which drives Opus FEC aggressiveness on the encoder side).
    /// </summary>
    public float LossPercent01
    {
        get
        {
            long r, m;
            lock (_encodedLock)
            {
                r = _recentReceived;
                m = _recentMissing;
            }
            long total = r + m;
            return total == 0 ? 0f : (float)m / total;
        }
    }

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
            _decoded[i] = new float[RemoteOpusSettings.MaxFrameSize];
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

            if (_underrunPending)
            {
                _underrunPending = false;
                if (silenceUnits == 0)
                {
                    _underrunsSinceAdjust++;
                    GenuineUnderruns++;
                }
            }

            int now = TickCountSource();

            // Arrival-gap tracker (see field comment). silenceUnits > 0 marks the
            // first packet after an intentional sender mute — that gap is not
            // network behavior, skip it.
            if (_haveLastArrival && silenceUnits == 0)
            {
                int gap = unchecked(now - _lastArrivalTick);
                if (gap > GapTrackCapMs) gap = GapTrackCapMs;
                if (gap > _peakGapMs)
                {
                    _peakGap2Ms = _peakGapMs;
                    _peakGapMs = gap;
                }
                else if (gap > _peakGap2Ms)
                {
                    _peakGap2Ms = gap;
                }
            }
            _lastArrivalTick = now;
            _haveLastArrival = true;

            // Adaptive-depth telemetry: every packet is an arrival; a negative distance
            // means its playback slot already passed, i.e. it arrived too late for the
            // current depth. A rising late ratio grows the buffer (MaybeAdjustDepthLocked).
            _arrivalsSinceAdjust++;
            if (distance < 0) _lateSinceAdjust++;
            MaybeAdjustDepthLocked(now);

            if (distance < 0) return; // old packet, discard

            if (distance >= MaxAheadDistance)
            {
                // Forward jump too wide for the 64-slot ring to represent (heavy loss
                // burst or a sender restart). We must drop the stale slots, but DON'T
                // force a full re-pre-roll: the decoded PCM queue is still draining real
                // audio and the underrun fade covers the seam, so a transient burst
                // shouldn't add the entire jitter pre-roll as extra silence on top of the
                // gap. Resync and require only a short re-fill before playback resumes.
                ClearEncoded();
                _nextPlaybackSeq = sequenceNumber;
                _started = true;
                _receivedSinceStart = Math.Max(0, TargetDepthLocked() - 2);
                distance = 0;
            }

            if (!_hasHighest || SeqDist(sequenceNumber, _highestReceivedSeq) > 0)
            {
                _highestReceivedSeq = sequenceNumber;
                _hasHighest = true;
            }

            int slot = sequenceNumber & EncodedSlotMask;
            bool isNew = !_encoded[slot].Occupied || _encoded[slot].SequenceNumber != sequenceNumber;
            if (!_encoded[slot].Occupied) _encodedCount++;
            if (_encoded[slot].Data == null || _encoded[slot].Data.Length < length)
                _encoded[slot].Data = new byte[length];
            Buffer.BlockCopy(data, 0, _encoded[slot].Data, 0, length);
            _encoded[slot].Length = length;
            _encoded[slot].SequenceNumber = sequenceNumber;
            _encoded[slot].SilenceUnits = silenceUnits;
            _encoded[slot].Occupied = true;
            // A duplicate delivery must not advance the pre-roll gate or the loss
            // stats — it adds no new audio.
            if (isNew)
            {
                _receivedSinceStart++;
                DecayRecentIfDue();
                _recentReceived++;
            }
        }
    }

    /// <summary>
    /// Peek the packet that <see cref="TryConsumeEncoded"/> would next return WITHOUT
    /// consuming or advancing state. After TryConsumeEncoded reports isMissing=true,
    /// _nextPlaybackSeq already points at the packet AFTER the missing one; if that
    /// packet is present, its FEC data can reconstruct the lost frame via
    /// decoder.Decode(..., decode_fec:true).
    /// </summary>
    public bool TryPeekNextEncoded(out byte[] data, out int length)
    {
        data = null; length = 0;
        if (!_started) return false;
        lock (_encodedLock)
        {
            if (!_started) return false;
            int slot = _nextPlaybackSeq & EncodedSlotMask;
            if (_encoded[slot].Occupied && _encoded[slot].SequenceNumber == _nextPlaybackSeq)
            {
                data = _encoded[slot].Data;
                length = _encoded[slot].Length;
                return true;
            }
            return false;
        }
    }

    // Caller must hold _encodedLock. Halves the rolling counters roughly every
    // DecayIntervalMs so LossPercent01 tracks recent traffic instead of the full
    // lifetime of the receiver.
    private void DecayRecentIfDue()
    {
        int now = TickCountSource();
        int elapsed = unchecked(now - _lastDecayTick);
        if (elapsed >= DecayIntervalMs || elapsed < 0)
        {
            _recentReceived >>= 1;
            _recentMissing >>= 1;
            _lastDecayTick = now;
        }
    }

    /// <summary>
    /// Consume the next in-order encoded packet. Called from the drain (frame pass or
    /// audio thread) under the receiver's decode gate.
    /// Returns false when nothing is ready (not started, still filling, caught up, or
    /// deliberately HOLDING a gap open for a late packet while decoded runway lasts).
    /// </summary>
    public bool TryConsumeEncoded(out byte[] data, out int length, out byte silenceUnits, out bool isMissing)
    {
        data = null; length = 0; silenceUnits = 0; isMissing = false;
        if (!_started) return false;

        lock (_encodedLock)
        {
            if (!_started) return false;
            if (_receivedSinceStart < TargetDepthLocked()) return false;

            int slot = _nextPlaybackSeq & EncodedSlotMask;
            if (_encoded[slot].Occupied && _encoded[slot].SequenceNumber == _nextPlaybackSeq)
            {
                if (_holdingForSeq && _holdingSeq == _nextPlaybackSeq)
                {
                    // The gap we were holding open got filled by the late packet —
                    // full-quality audio instead of a concealment.
                    LateSalvagedCount++;
                }
                _holdingForSeq = false;
                data = _encoded[slot].Data;
                length = _encoded[slot].Length;
                silenceUnits = _encoded[slot].SilenceUnits;
                isMissing = false;
                _encoded[slot].Occupied = false;
                _encodedCount--;
                _nextPlaybackSeq++;
                return true;
            }

            if (_hasHighest)
            {
                // Slot empty. How far is playback behind the newest arrival?
                int lead = -SeqDist(_nextPlaybackSeq, _highestReceivedSeq); // >0 when behind
                if (lead > 0)
                {
                    // Deadline hold: newer packets exist but this slot hasn't arrived.
                    // As long as the decoded queue still has runway beyond the PLC
                    // reserve, HOLD — playback continues off the queue, and a
                    // reordered/late packet gets its maximum possible time to land.
                    // The effective late-tolerance is therefore the standing buffer
                    // depth itself, not a fixed grace constant; deeper adaptive
                    // depths automatically wait longer. Only once runway is nearly
                    // gone is the gap declared lost (FEC via the next packet, then
                    // PLC), because from that point every held frame would become an
                    // audible underrun instead.
                    if (Volatile.Read(ref _frameCount) > PlcReserveFrames)
                    {
                        _holdingForSeq = true;
                        _holdingSeq = _nextPlaybackSeq;
                        return false;
                    }
                    _holdingForSeq = false;
                    isMissing = true;
                    _nextPlaybackSeq++;
                    DecayRecentIfDue();
                    _recentMissing++;
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// True when the consume cursor is parked at a missing slot with newer packets
    /// buffered behind it (the deadline-hold state). Used by the drain to tell a
    /// held gap apart from a genuine starve (nothing newer has arrived at all).
    /// </summary>
    public bool IsHoldingForLatePacket
    {
        get { lock (_encodedLock) return _holdingForSeq && _started; }
    }

    /// <summary>
    /// Milliseconds since the last packet arrival (any packet, including mute
    /// resumes). int.MaxValue before the first arrival. The starve bridge keys on
    /// this: an empty ring moments after an arrival is the normal cadence of a
    /// shallow queue, not a stall.
    /// </summary>
    public int MsSinceLastArrival()
    {
        lock (_encodedLock)
        {
            if (!_haveLastArrival) return int.MaxValue;
            int elapsed = unchecked(TickCountSource() - _lastArrivalTick);
            return elapsed < 0 ? 0 : elapsed;
        }
    }

    /// <summary>
    /// Packets of standing buffer between the playback cursor and the newest
    /// arrival, inclusive, plus decoded-but-unplayed frames. This is the
    /// content-time the listener is behind the newest received audio — the
    /// standing latency the catch-up drain works to shrink. Holes count: they will
    /// take playback time as FEC/PLC/silence when reached.
    /// </summary>
    public int StandingBufferedFrames
    {
        get
        {
            int encodedSpan = 0;
            lock (_encodedLock)
            {
                if (_started && _hasHighest)
                {
                    int span = SeqDist(_highestReceivedSeq, _nextPlaybackSeq) + 1;
                    if (span > 0) encodedSpan = span;
                }
            }
            return encodedSpan + Volatile.Read(ref _frameCount);
        }
    }

    /// <summary>
    /// Emergency catch-up: advance the playback cursor forward, discarding the
    /// oldest <paramref name="packets"/> packets of buffered content (played
    /// content-time, not just occupied slots — holes are skipped with the rest).
    /// The caller owns the audible seam: reset the Opus decoder (the skipped
    /// content is gone from its history) and fade the join. Returns packets of
    /// content actually skipped.
    /// </summary>
    public int SkipForwardEncoded(int packets)
    {
        if (packets <= 0) return 0;
        lock (_encodedLock)
        {
            if (!_started || !_hasHighest) return 0;
            int span = SeqDist(_highestReceivedSeq, _nextPlaybackSeq) + 1;
            if (span <= 0) return 0;
            if (packets > span) packets = span;
            for (int i = 0; i < packets; i++)
            {
                int slot = _nextPlaybackSeq & EncodedSlotMask;
                if (_encoded[slot].Occupied && _encoded[slot].SequenceNumber == _nextPlaybackSeq)
                {
                    _encoded[slot].Occupied = false;
                    _encodedCount--;
                }
                _nextPlaybackSeq++;
            }
            _holdingForSeq = false;
            return packets;
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
    /// Fade the tail of the newest decoded-but-unplayed frame to zero over
    /// <paramref name="fadeSamples"/>. Called before a catch-up flush so the jump to
    /// newer content starts from silence instead of a mid-waveform cut. No-op when
    /// the newest frame is already being read (the seam then rides the underrun fade).
    /// </summary>
    public void FadeOutNewestDecodedTail(int fadeSamples)
    {
        lock (_decodedLock)
        {
            if (_frameCount == 0) return;
            int newest = (_writePos - 1 + MaxDecodedFrames) % MaxDecodedFrames;
            if (_frameCount == 1 && _readOffset > 0) return; // partially consumed
            int len = _decodedLengths[newest];
            int fade = fadeSamples < len ? fadeSamples : len;
            float[] buf = _decoded[newest];
            for (int i = 0; i < fade; i++)
            {
                float w = (float)(fade - 1 - i) / fade;
                buf[len - fade + i] *= w;
            }
        }
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

    /// <summary>
    /// Re-arm the initial-fill gate so <see cref="TryConsumeEncoded"/> waits for
    /// <see cref="InitialBufferDepth"/> packets again before releasing playback.
    /// Called by the receiver when it detects the sender has been idle long enough
    /// (e.g. mic muted) that the pipeline has fully drained — without this, the
    /// first post-resume packet would be released with only 20 ms of audio queued
    /// and any arrival jitter on the next packet would underrun.
    /// Keeps <c>_started</c> and <c>_nextPlaybackSeq</c> as-is: the sender's
    /// sequence number is continuous across its mute, so playback resumes from
    /// where it left off.
    /// </summary>
    public void RearmInitialBuffer()
    {
        lock (_encodedLock)
        {
            _receivedSinceStart = 0;
            // _underrunPending deliberately survives the rearm: the packet that resumes
            // the stream resolves it, and its silenceUnits field discriminates a sender
            // mute (>0, discarded) from a network stall long enough to trip the idle
            // reset (0, counted genuine so the adaptive depth can react).
        }
    }

    private void ClearEncoded()
    {
        for (int i = 0; i < EncodedSlotCount; i++)
            _encoded[i].Occupied = false;
        _started = false;
        _hasHighest = false;
        _encodedCount = 0;
        _receivedSinceStart = 0;
        _underrunPending = false;
        _holdingForSeq = false;
        _haveLastArrival = false;
    }

    private static int SeqDist(byte target, byte baseSeq) => (sbyte)(target - baseSeq);
}
