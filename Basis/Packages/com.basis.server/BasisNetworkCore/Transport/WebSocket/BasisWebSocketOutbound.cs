using System;
using System.Threading;

namespace Basis.Network.Core
{
    public sealed class BasisWebSocketOutbound
    {
        public enum AppendResult
        {
            Queued,
            QueuedStartFlush,
            Dropped,
            Overflow,
            Closed
        }

        private const int MinimumCapacity = 4096;
        private const int MaxRetainedCapacity = 1024 * 1024;
        private static readonly bool[] PriorityChannels = BuildPriority();
        private readonly object _gate = new object();
        private readonly int[] _queued = new int[BasisWebSocketProtocol.ChannelCount];
        private readonly int _maxBulkBytes;
        private readonly int _maxPriorityBytes;
        private readonly int _maxPendingBytes;
        private byte[] _pending;
        private byte[] _spare;
        private int _length;
        private int _bulkBytes;
        private int _priorityBytes;
        private bool _flushing;
        private bool _closed;
        private bool _closeAfterFlush;
        private bool _closeIssued;
        private long _dropped;
        private long _priorityDropped;

        public BasisWebSocketOutbound(int maxBulkBytes, int maxPriorityBytes, int maxPendingBytes)
        {
            _maxBulkBytes = Math.Max(MinimumCapacity, maxBulkBytes);
            _maxPriorityBytes = Math.Max(MinimumCapacity, maxPriorityBytes);
            _maxPendingBytes = Math.Max(MinimumCapacity, maxPendingBytes);
            _pending = new byte[MinimumCapacity];
        }

        public long Dropped => Interlocked.Read(ref _dropped);

        public long PriorityDropped => Interlocked.Read(ref _priorityDropped);

        public int PendingBytes
        {
            get
            {
                lock (_gate)
                {
                    return _length;
                }
            }
        }

        private static bool[] BuildPriority()
        {
            bool[] map = new bool[BasisWebSocketProtocol.ChannelCount];
            bool[] source = BasisNetworkCommons.BuildPriorityUnreliableChannelMap();
            Array.Copy(source, map, Math.Min(source.Length, map.Length));
            return map;
        }

        public int QueuedCount(byte channel)
        {
            return Volatile.Read(ref _queued[channel]);
        }

        public AppendResult AppendData(byte channel, DeliveryMethod method, byte[] data, int offset, int length, int patchOffset = -1, byte patchValue = 0)
        {
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            int bodyLength = BasisWebSocketProtocol.DataHeaderBytes + length;
            int frameSize = BasisWebSocketProtocol.FrameSize(bodyLength);
            bool droppable = BasisWebSocketProtocol.IsDroppable(method);
            bool priority = droppable && PriorityChannels[channel];
            lock (_gate)
            {
                if (_closed) return AppendResult.Closed;
                if (droppable)
                {
                    if (priority ? _priorityBytes + frameSize > _maxPriorityBytes : _bulkBytes + frameSize > _maxBulkBytes)
                    {
                        if (priority) _priorityDropped++;
                        else _dropped++;
                        return AppendResult.Dropped;
                    }
                }
                if (_length + frameSize > _maxPendingBytes)
                {
                    if (droppable)
                    {
                        if (priority) _priorityDropped++;
                        else _dropped++;
                        return AppendResult.Dropped;
                    }
                    return AppendResult.Overflow;
                }
                EnsureCapacity(_length + frameSize);
                int cursor = _length + BasisWebSocketProtocol.WriteFrameHeader(_pending, _length, BasisWebSocketProtocol.KindData, bodyLength);
                _pending[cursor++] = channel;
                _pending[cursor++] = (byte)method;
                if (length > 0)
                {
                    Buffer.BlockCopy(data, offset, _pending, cursor, length);
                    if (patchOffset >= 0 && patchOffset < length)
                    {
                        _pending[cursor + patchOffset] = patchValue;
                    }
                }
                _length += frameSize;
                if (droppable)
                {
                    _queued[channel]++;
                    if (priority) _priorityBytes += frameSize;
                    else _bulkBytes += frameSize;
                }
                return StartFlushIfIdle();
            }
        }

        public AppendResult AppendControl(byte kind, byte[] body, int offset, int length)
        {
            int frameSize = BasisWebSocketProtocol.FrameSize(length);
            lock (_gate)
            {
                if (_closed) return AppendResult.Closed;
                if (_length + frameSize > _maxPendingBytes && kind != BasisWebSocketProtocol.KindDisconnect && kind != BasisWebSocketProtocol.KindReject)
                {
                    return AppendResult.Overflow;
                }
                EnsureCapacity(_length + frameSize);
                int cursor = _length + BasisWebSocketProtocol.WriteFrameHeader(_pending, _length, kind, length);
                if (length > 0)
                {
                    Buffer.BlockCopy(body, offset, _pending, cursor, length);
                }
                _length += frameSize;
                return StartFlushIfIdle();
            }
        }

        private AppendResult StartFlushIfIdle()
        {
            if (_flushing) return AppendResult.Queued;
            _flushing = true;
            return AppendResult.QueuedStartFlush;
        }

        private void EnsureCapacity(int required)
        {
            if (required <= _pending.Length) return;
            int size = _pending.Length;
            while (size < required)
            {
                size = size > int.MaxValue / 2 ? required : size * 2;
            }
            Array.Resize(ref _pending, size);
        }

        public bool RequestCloseAfterFlush()
        {
            lock (_gate)
            {
                _closed = true;
                _closeAfterFlush = true;
                if (_flushing) return false;
                _flushing = true;
                return true;
            }
        }

        public bool TryTakeBatch(out byte[] batch, out int length, out bool closeNow)
        {
            lock (_gate)
            {
                closeNow = false;
                if (_length == 0)
                {
                    _flushing = false;
                    if (_closeAfterFlush && !_closeIssued)
                    {
                        _closeIssued = true;
                        closeNow = true;
                    }
                    batch = null;
                    length = 0;
                    return false;
                }
                batch = _pending;
                length = _length;
                _pending = _spare ?? new byte[MinimumCapacity];
                _spare = null;
                _length = 0;
                _bulkBytes = 0;
                _priorityBytes = 0;
                Array.Clear(_queued, 0, _queued.Length);
                return true;
            }
        }

        public void ReturnBatch(byte[] batch)
        {
            if (batch == null || batch.Length > MaxRetainedCapacity) return;
            lock (_gate)
            {
                if (_spare == null) _spare = batch;
            }
        }

        public void Close()
        {
            lock (_gate)
            {
                _closed = true;
            }
        }

        public bool IsClosed
        {
            get
            {
                lock (_gate)
                {
                    return _closed;
                }
            }
        }

        public bool IsDrained
        {
            get
            {
                lock (_gate)
                {
                    return _length == 0 && (!_closeAfterFlush || _closeIssued);
                }
            }
        }
    }
}
