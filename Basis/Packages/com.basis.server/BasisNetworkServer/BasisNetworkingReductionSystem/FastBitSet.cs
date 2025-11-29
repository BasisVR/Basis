using System;
using System.Threading;

namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    public partial class BasisServerReductionSystemEvents
    {
        /// <summary>
        /// Lock-free (for normal ops) bitset backed by 32-bit words.
        /// - Per-bit Set/Clear uses CAS on int words.
        /// - Reads use Volatile.Read.
        /// - Resizes are protected by a lock (rare).
        /// Length is the number of addressable bits (>= highest set index + 1 after EnsureCapacity).
        /// </summary>
        public sealed class FastBitSet
        {
            private const int BitsPerElement = 32;

            // Backing storage (each int is 32 bits).
            private int[] _words;

            // Protects only array resizing (rare path).
            private readonly object _resizeLock = new();

            /// <summary>Number of bits that can be addressed without resize.</summary>
            public int Length { get; private set; }

            public FastBitSet(int initialBitCount)
            {
                if (initialBitCount < 0) throw new ArgumentOutOfRangeException(nameof(initialBitCount));
                var wordCount = (initialBitCount + BitsPerElement - 1) / BitsPerElement;
                _words = new int[wordCount];
                Length = wordCount * BitsPerElement;
            }

            /// <summary>
            /// Ensures the bit at <paramref name="index"/> can be addressed.
            /// Resizes the underlying array if needed (synchronized).
            /// </summary>
            private void EnsureCapacity(int index)
            {
                if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
                if (index < Length) return;

                lock (_resizeLock)
                {
                    if (index < Length) return;

                    int requiredBits = index + 1;
                    int requiredWords = (requiredBits + BitsPerElement - 1) / BitsPerElement;

                    if (requiredWords > _words.Length)
                    {
                        // Grow by 1.5x to reduce future resizes.
                        int newWords = Math.Max(requiredWords, _words.Length + (_words.Length >> 1) + 1);
                        var newArr = new int[newWords];
                        Array.Copy(_words, newArr, _words.Length);
                        _words = newArr;
                    }

                    Length = _words.Length * BitsPerElement;
                }
            }

            /// <summary>
            /// Atomically sets or clears the bit at <paramref name="index"/>.
            /// </summary>
            public void Set(int index, bool value)
            {
                EnsureCapacity(index);

                int elem = index / BitsPerElement;
                int bit = index % BitsPerElement;

                uint mask = 1U << bit;
                ref int wordRef = ref _words[elem];

                while (true)
                {
                    int oldVal = Volatile.Read(ref wordRef);
                    uint uOld = unchecked((uint)oldVal);
                    uint uNew = value ? (uOld | mask) : (uOld & ~mask);

                    // If nothing changes, we’re done (avoids unnecessary CAS).
                    if (uNew == uOld) return;

                    int newVal = unchecked((int)uNew);
                    if (Interlocked.CompareExchange(ref wordRef, newVal, oldVal) == oldVal)
                        return; // success
                    // else: lost the race, retry
                }
            }

            /// <summary>
            /// Atomically tests the bit and clears it if it was set.
            /// Returns true iff the bit was previously set.
            /// </summary>
            public bool TestAndClear(int index)
            {
                EnsureCapacity(index);

                int elem = index / BitsPerElement;
                int bit = index % BitsPerElement;

                uint mask = 1U << bit;
                ref int wordRef = ref _words[elem];

                while (true)
                {
                    int oldVal = Volatile.Read(ref wordRef);
                    uint uOld = unchecked((uint)oldVal);

                    if ((uOld & mask) == 0)
                        return false; // already clear; no write needed

                    uint uNew = uOld & ~mask;
                    int newVal = unchecked((int)uNew);

                    if (Interlocked.CompareExchange(ref wordRef, newVal, oldVal) == oldVal)
                        return true; // we cleared it
                    // else: retry
                }
            }

            /// <summary>
            /// Returns true if the bit at <paramref name="index"/> is set.
            /// </summary>
            public bool Get(int index)
            {
                if (index < 0) return false;
                if (index >= Length) return false;

                int elem = index / BitsPerElement;
                int bit = index % BitsPerElement;

                uint mask = 1U << bit;
                uint word = unchecked((uint)Volatile.Read(ref _words[elem]));
                return (word & mask) != 0;
            }

            /// <summary>
            /// Sets all addressable bits to the given value.
            /// Bulk write under resize lock (simple and safe).
            /// </summary>
            public void SetAll(bool value)
            {
                int fill = value ? unchecked((int)0xFFFFFFFFu) : 0;
                lock (_resizeLock)
                {
                    for (int i = 0; i < _words.Length; i++)
                        _words[i] = fill;
                }
            }

            /// <summary>Clears all bits.</summary>
            public void Clear()
            {
                lock (_resizeLock)
                {
                    Array.Clear(_words, 0, _words.Length);
                }
            }

            /// <summary>Returns true if any bit is set.</summary>
            public bool AnyTrue()
            {
                // Safe without the resize lock: worst case we miss a concurrent grow,
                // but then the new words are zero-initialized anyway.
                for (int i = 0; i < _words.Length; i++)
                {
                    if (Volatile.Read(ref _words[i]) != 0)
                        return true;
                }
                return false;
            }
        }
    }
}
