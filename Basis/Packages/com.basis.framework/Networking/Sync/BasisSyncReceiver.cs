using System.Collections.Generic;
using Unity.Mathematics;

namespace Basis.Scripts.Networking.Sync
{
    /// <summary>
    /// Per-object playback buffer. Decodes keyframe/delta packets into a rolling baseline, stages
    /// timestamped frames, and interpolates a Current→Next window with an adaptive playback rate,
    /// mirroring the remote-avatar receiver. Main-thread only (packets dispatch on the main thread).
    /// </summary>
    public sealed class BasisSyncReceiver
    {
        private const int MaxStage = 32;
        private const int MinJitterDepth = 1;
        private const int MaxJitterDepth = 4;
        private const double DefaultInterval = 0.05;
        private const float CatchupDeadband = 0.5f;
        private const float CatchupGain = 0.5f;
        private const float MinPlaybackRate = 0.85f;
        private const float MaxPlaybackRate = 1.35f;
        // Widest Current->Next window playback will interpolate across in real time. Anything wider is a
        // resume after an idle gap (or an owner hitch), not motion — it gets crossed at the nominal packet
        // pace instead of being replayed, so a pickup grabbed after sitting still doesn't spend seconds
        // sliding out of its stale timeline. Kept above the server's slowest distance-reduced send rate
        // (2.55 s stock) so legitimately slow far-object streams still interpolate smoothly.
        private const double MaxPlayableWindowSeconds = 3.0;

        private sealed class SyncFrame
        {
            public BasisSyncValues Values;
            public byte Seq;
            public double ServerTime;
            public double Interval;
        }

        private sealed class RawPacket
        {
            public byte[] Bytes;
            public int Len;
            public byte Seq;
        }

        private readonly BasisSyncSchema _schema;
        private readonly BasisSyncValues _baseline;
        private readonly int _maxPacket;

        private readonly Stack<SyncFrame> _freeFrames = new Stack<SyncFrame>();
        private readonly Queue<SyncFrame> _staged = new Queue<SyncFrame>();
        private readonly Stack<RawPacket> _freeRaw = new Stack<RawPacket>();
        private readonly List<RawPacket> _arrived = new List<RawPacket>();
        private readonly System.Comparison<RawPacket> _seqComparer;

        private SyncFrame _current;
        private SyncFrame _next;
        private double _interpTime;

        private byte _highestSeq;
        private byte _sortRef;
        private int _seenPackets;
        private bool _hasKeyframe;
        private double _serverClock;
        private bool _serverClockSeeded;
        private float _dynamicDepth = 2f;
        // Steady-state staged-frame target (the buffer's latency floor, in send intervals). Lower = less
        // latency, less tolerance to jitter/loss before a hitch. Configurable per object; default keeps the
        // historical depth of 2. _dynamicDepth still climbs above this on underruns and decays back to it.
        private float _depthFloor = MinJitterDepth + 1f;
        private bool _valuesDirty = true;
        private bool _snapRequested;
        private bool _windowStatic;
        private bool _windowStaticValid;

        private bool _extrapolate;
        private double _maxExtrapSeconds;
        private bool _teleportEnabled;
        private float _teleportThresholdSq;
        private int _teleportStart;
        private int _teleportCount;
        private bool _verifyChecksum;
        private readonly float[] _lastCont;
        private bool _haveLastCont;

        private const double BandwidthWindow = 0.5;
        private int _bwBytes;
        private int _bwPackets;
        private double _bwTime;
        private float _bytesPerSecond;
        private float _packetsPerSecond;

        public BasisSyncReceiver(BasisSyncSchema schema)
        {
            _schema = schema;
            _baseline = new BasisSyncValues();
            _baseline.Allocate(schema);
            _maxPacket = BasisSyncCodec.MaxSerializedSize(schema);
            _seqComparer = CompareBySeq;
            _lastCont = schema.ContCount > 0 ? new float[schema.ContCount] : System.Array.Empty<float>();
        }

        public void Configure(bool extrapolate, double maxExtrapSeconds, bool teleport, float teleportThresholdSq, int teleportStart, int teleportCount, bool verifyChecksum = false, float bufferDepth = MinJitterDepth + 1f)
        {
            _extrapolate = extrapolate;
            _maxExtrapSeconds = maxExtrapSeconds;
            _teleportEnabled = teleport;
            _teleportThresholdSq = teleportThresholdSq;
            _teleportStart = teleportStart;
            _teleportCount = teleportCount;
            _verifyChecksum = verifyChecksum;
            _depthFloor = math.clamp(bufferDepth, MinJitterDepth, MaxJitterDepth);
            // Fresh object: start playback at the target floor. Mid-stream change: don't stomp a value that
            // has climbed above the floor under jitter — let it decay back down on its own.
            if (!_serverClockSeeded) _dynamicDepth = _depthFloor;
            else if (_dynamicDepth < _depthFloor) _dynamicDepth = _depthFloor;
        }

        public bool HasData => _current != null;
        public float InterpTime => _next != null ? (float)_interpTime : 0f;
        public BasisSyncValues CurrentValues => _current != null ? _current.Values : null;
        public BasisSyncValues NextValues => _next != null ? _next.Values : (_current != null ? _current.Values : null);
        public int BufferedFrameCount => _staged.Count;
        public float DynamicDepth => _dynamicDepth;
        public float BytesPerSecond => _bytesPerSecond;
        public float PacketsPerSecond => _packetsPerSecond;

        /// <summary>
        /// True if Current/Next changed since the last call (a new frame was staged or the window advanced), then
        /// clears. Lets the driver skip re-copying unchanged values into the interpolation pools every frame —
        /// only the interp fraction needs updating between frame advances.
        /// </summary>
        public bool ConsumeValuesDirty()
        {
            bool d = _valuesDirty;
            _valuesDirty = false;
            return d;
        }

        public void OnPacket(byte[] payload, int length)
        {
            if (payload == null || length < BasisSyncCodec.HeaderSize || length > payload.Length || length > _maxPacket) return;
            // Bytes on the wire for this object — counted before any drop, so it reflects what arrived.
            _bwBytes += length;
            _bwPackets++;
            // Drop corrupted packets before they touch the sequence/baseline, so a corrupted sequence number
            // can't poison the high-water-mark and a corrupted value is never applied.
            if (_verifyChecksum && !BasisSyncCodec.VerifyChecksum(payload, length)) return;
            RawPacket rp = RentRaw();
            if (rp.Bytes.Length < length) rp.Bytes = new byte[length];
            System.Array.Copy(payload, 0, rp.Bytes, 0, length);
            rp.Len = length;
            rp.Seq = payload[0];
            _arrived.Add(rp);
        }

        public void Advance(double dt)
        {
            _bwTime += dt;
            if (_bwTime >= BandwidthWindow)
            {
                float inv = (float)(1.0 / _bwTime);
                _bytesPerSecond = _bwBytes * inv;
                _packetsPerSecond = _bwPackets * inv;
                _bwBytes = 0;
                _bwPackets = 0;
                _bwTime = 0.0;
            }

            if (_arrived.Count > 0)
            {
                _sortRef = _highestSeq;
                if (_arrived.Count > 1) _arrived.Sort(_seqComparer);

                for (int i = 0; i < _arrived.Count; i++)
                {
                    RawPacket rp = _arrived[i];

                    if (_seenPackets != 0)
                    {
                        byte fwd = (byte)(rp.Seq - _highestSeq);
                        if (fwd == 0 || fwd >= 128)
                        {
                            ReturnRaw(rp);
                            continue;
                        }
                    }

                    if (!BasisSyncCodec.Deserialize(_schema, rp.Bytes, rp.Len, _baseline, out byte seq, out bool keyframe, out ushort intervalMs))
                    {
                        ReturnRaw(rp);
                        continue;
                    }

                    if (keyframe) _hasKeyframe = true;
                    else if (!_hasKeyframe)
                    {
                        ReturnRaw(rp);
                        continue;
                    }

                    _highestSeq = seq;
                    _seenPackets = 1;

                    double interval = intervalMs > 0 ? intervalMs / 1000.0 : DefaultInterval;
                    if (!_serverClockSeeded)
                    {
                        _serverClock = 0.0;
                        _serverClockSeeded = true;
                    }
                    else
                    {
                        _serverClock += interval;
                    }

                    SyncFrame frame = RentFrame();
                    frame.Values.CopyFrom(_baseline);
                    frame.Seq = seq;
                    frame.Interval = interval;
                    frame.ServerTime = _serverClock;

                    if (_teleportEnabled && _haveLastCont && _teleportCount > 0 && ContJumpSq(frame.Values.Cont) > _teleportThresholdSq)
                    {
                        SnapTo(frame);
                    }
                    else
                    {
                        Stage(frame);
                    }
                    UpdateLastCont(frame.Values.Cont);

                    ReturnRaw(rp);
                }
                _arrived.Clear();
            }

            if (_snapRequested)
            {
                _snapRequested = false;
                // Collapse the buffer to the freshest frame we have so playback jumps straight to it
                // instead of interpolating across the discontinuity that prompted the request.
                SyncFrame fresh = null;
                while (_staged.Count > 0)
                {
                    if (fresh != null) ReturnFrame(fresh);
                    fresh = _staged.Dequeue();
                }
                if (fresh == null) fresh = _next ?? _current;
                if (fresh != null)
                {
                    if (_current != null && _current != fresh) ReturnFrame(_current);
                    if (_next != null && _next != fresh) ReturnFrame(_next);
                    _current = fresh;
                    _next = null;
                    _interpTime = 0.0;
                    _valuesDirty = true;
                    _windowStaticValid = false;
                }
            }

            if (_current == null && _staged.Count > 0) { _current = _staged.Dequeue(); _valuesDirty = true; _windowStaticValid = false; }
            if (_next == null && _staged.Count > 0) { _next = _staged.Dequeue(); _valuesDirty = true; _windowStaticValid = false; }

            // Windows whose endpoints are identical (an idle sender's keyframe refreshes) carry no motion;
            // crossing them in real time only delays whatever is queued behind them. Skip straight through.
            while (_next != null && _staged.Count > 0 && WindowIsStatic())
            {
                ReturnFrame(_current);
                _current = _next;
                _next = _staged.Dequeue();
                _interpTime = 0.0;
                _valuesDirty = true;
                _windowStaticValid = false;
            }

            if (_current != null && _next != null)
            {
                if (WindowIsStatic())
                {
                    // Static window with nothing queued behind it: pin to the newest values. Starving here is
                    // the sender being idle, not network trouble, so the depth target must not ratchet up.
                    _interpTime = 1.0;
                }
                else
                {
                    double window = _next.ServerTime - _current.ServerTime;
                    bool gap = window > MaxPlayableWindowSeconds;
                    if (window <= 1e-6 || gap) window = DefaultInterval;

                    float diff = _staged.Count - _dynamicDepth;
                    float rate;
                    if (diff > CatchupDeadband) rate = 1f + CatchupGain * (diff - CatchupDeadband);
                    else if (diff < 0f) rate = 1f + CatchupGain * diff;
                    else rate = 1f;
                    rate = math.clamp(rate, MinPlaybackRate, MaxPlaybackRate);

                    _interpTime += (dt / window) * rate;

                    while (_interpTime >= 1.0 && _staged.Count > 0)
                    {
                        ReturnFrame(_current);
                        _current = _next;
                        _next = _staged.Dequeue();
                        _interpTime -= 1.0;
                        _valuesDirty = true;
                        _windowStaticValid = false;
                    }

                    if (_interpTime >= 1.0)
                    {
                        double cap = 1.0;
                        // A collapsed gap window has a garbage slope (real distance over a nominal interval)
                        // — extrapolating along it would overshoot hard, so hold at the window end instead.
                        if (_extrapolate && !gap && window > 1e-6) cap = 1.0 + _maxExtrapSeconds / window;
                        if (_interpTime > cap) _interpTime = cap;
                        _dynamicDepth = math.min(_dynamicDepth + 0.25f, MaxJitterDepth);
                    }
                    else if (_interpTime < 0.0)
                    {
                        _interpTime = 0.0;
                    }
                }

                _dynamicDepth = math.max(_dynamicDepth - (float)(dt * 0.5), _depthFloor);
            }
            else
            {
                _interpTime = 0.0;
            }
        }

        /// <summary>
        /// Request that playback collapse to the freshest received frame on the next <see cref="Advance"/>,
        /// skipping interpolation. Use after a discontinuity in what the synced values mean (teleport, or a
        /// pickup switching between world-space and hand-relative encoding) so the remote copy snaps rather
        /// than sliding across the jump.
        /// </summary>
        public void ForceSnap() => _snapRequested = true;

        public void Reset()
        {
            if (_current != null) { ReturnFrame(_current); _current = null; }
            if (_next != null) { ReturnFrame(_next); _next = null; }
            while (_staged.Count > 0) ReturnFrame(_staged.Dequeue());
            for (int i = 0; i < _arrived.Count; i++) ReturnRaw(_arrived[i]);
            _arrived.Clear();
            _interpTime = 0.0;
            _windowStaticValid = false;
            _seenPackets = 0;
            _hasKeyframe = false;
            _serverClockSeeded = false;
            _serverClock = 0.0;
            _dynamicDepth = _depthFloor;
            _haveLastCont = false;
            _bwBytes = 0;
            _bwPackets = 0;
            _bwTime = 0.0;
            _bytesPerSecond = 0f;
            _packetsPerSecond = 0f;
        }

        private void SnapTo(SyncFrame frame)
        {
            if (_current != null) { ReturnFrame(_current); _current = null; }
            if (_next != null) { ReturnFrame(_next); _next = null; }
            while (_staged.Count > 0) ReturnFrame(_staged.Dequeue());
            _interpTime = 0.0;
            _current = frame;
            _valuesDirty = true;
            _windowStaticValid = false;
        }

        private bool WindowIsStatic()
        {
            if (!_windowStaticValid)
            {
                _windowStatic = ValuesEqual(_current.Values, _next.Values);
                _windowStaticValid = true;
            }
            return _windowStatic;
        }

        // Exact comparison is intentional: an unchanged field decodes to bit-identical values (deltas carry
        // the baseline forward, keyframes re-quantize the same source), so "identical" reliably means the
        // sender had nothing new. Any real change — or NaN garbage — compares unequal and interpolates.
        private static bool ValuesEqual(BasisSyncValues a, BasisSyncValues b)
        {
            float[] ac = a.Cont, bc = b.Cont;
            for (int i = 0; i < ac.Length; i++)
            {
                if (ac[i] != bc[i]) return false;
            }
            quaternion[] ar = a.Rot, br = b.Rot;
            for (int i = 0; i < ar.Length; i++)
            {
                float4 qa = ar[i].value, qb = br[i].value;
                if (qa.x != qb.x || qa.y != qb.y || qa.z != qb.z || qa.w != qb.w) return false;
            }
            int[] ad = a.Disc, bd = b.Disc;
            for (int i = 0; i < ad.Length; i++)
            {
                if (ad[i] != bd[i]) return false;
            }
            return true;
        }

        private float ContJumpSq(float[] cont)
        {
            int end = _teleportStart + _teleportCount;
            if (end > cont.Length) end = cont.Length;
            if (end > _lastCont.Length) end = _lastCont.Length;
            float sum = 0f;
            for (int i = _teleportStart; i < end; i++)
            {
                float d = cont[i] - _lastCont[i];
                sum += d * d;
            }
            return sum;
        }

        private void UpdateLastCont(float[] cont)
        {
            int n = cont.Length < _lastCont.Length ? cont.Length : _lastCont.Length;
            for (int i = 0; i < n; i++) _lastCont[i] = cont[i];
            _haveLastCont = true;
        }

        private void Stage(SyncFrame f)
        {
            if (_staged.Count >= MaxStage) ReturnFrame(_staged.Dequeue());
            _staged.Enqueue(f);
        }

        private int CompareBySeq(RawPacket a, RawPacket b)
        {
            byte fa = (byte)(a.Seq - _sortRef);
            byte fb = (byte)(b.Seq - _sortRef);
            return fa.CompareTo(fb);
        }

        private SyncFrame RentFrame()
        {
            if (_freeFrames.Count > 0) return _freeFrames.Pop();
            var f = new SyncFrame { Values = new BasisSyncValues() };
            f.Values.Allocate(_schema);
            return f;
        }

        private void ReturnFrame(SyncFrame f)
        {
            if (f != null) _freeFrames.Push(f);
        }

        private RawPacket RentRaw()
        {
            if (_freeRaw.Count > 0) return _freeRaw.Pop();
            return new RawPacket { Bytes = new byte[_maxPacket] };
        }

        private void ReturnRaw(RawPacket rp)
        {
            if (rp != null) _freeRaw.Push(rp);
        }
    }
}
