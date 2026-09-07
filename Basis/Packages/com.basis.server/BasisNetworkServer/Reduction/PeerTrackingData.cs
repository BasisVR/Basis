namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    /// <summary>
    /// What one receiver remembers about one sender. There is one of these per ORDERED PAIR of players,
    /// so every byte here is paid N times per player and N squared across the instance: at 4000 players
    /// a 32 byte record is half a gigabyte, and the same record at the protocol's 65535 peer ceiling is
    /// 137. That is why the fields below are the widths they are, and why the hottest field of all does
    /// not live here at all - see PlayerState.PeerLastSeenGeneration.
    /// </summary>
    public struct PeerTrackingData
    {
        /// <summary>
        /// Stopwatch ticks, truncated to 32 bits, of the last send to this receiver from this sender.
        ///
        /// Only ever read as `(uint)now - LastSentTime` and compared against an interval, so unsigned
        /// wraparound is the arithmetic rather than a hazard: at a 10 MHz Stopwatch the counter wraps
        /// every ~430 seconds and every interval this is compared against is under a second. A pair that
        /// really has been idle for longer than a wrap gets a uniformly distributed elapsed value, which
        /// clears an interval of a few hundred milliseconds with overwhelming probability and re-rolls on
        /// the next tick if it does not.
        /// </summary>
        public uint LastSentTime;
        /// <summary>
        /// The sender keyframe generation this receiver holds, or <see cref="NoBaseline"/> for none.
        ///
        /// Compared for equality against `(uint)sender.KeyframeGen` and never ordered, so the low 32 bits
        /// are the whole of the question. Aliasing needs the sender to emit exactly 2^32 keyframes while
        /// this receiver holds a stale one - decades of continuous uptime - and costs a single delta
        /// applied to the wrong baseline if it ever happened.
        /// </summary>
        public uint BaselineKeyframeGen;
        /// <summary>Sentinel for "this receiver holds no keyframe of this sender".</summary>
        public const uint NoBaseline = uint.MaxValue;
        // Cached by the slow distance loop, read by the fast send loop (~250Hz). Eliminates per-pair
        // distance math from the hot path.
        //
        // The tick count that used to sit beside these is gone: it was exactly
        // _intervalTickTable[CachedIntervalByte] - the device scatter path wrote the two from one value -
        // so it was four bytes per pair storing a table lookup. The send loop does the lookup instead.
        public byte CachedQualityIndex;
        public byte CachedIntervalByte;
        public byte BaselineQuality;
        /// <summary>
        /// Whether the distance sweep has ever written the two cached fields above for this pair.
        ///
        /// It used to be implicit: the tick count was zero until the sweep filled it in, and the send
        /// loop read a zero as "no cache yet, use the base interval". With the tick count derived from
        /// the interval byte that tell is gone - byte zero is a legitimate encoding for the closest
        /// pairs - so the distinction is carried explicitly. It costs nothing: the record is four byte
        /// fields wide either way once the two uints are aligned.
        /// </summary>
        public bool HasDistanceCache;
    }
}
