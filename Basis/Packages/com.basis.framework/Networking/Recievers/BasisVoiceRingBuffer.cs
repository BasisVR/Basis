using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

public class BasisVoiceRingBuffer
{
    private readonly float[] buffer;
    private readonly bool[] realMask; // parallel mask of "real" (non-silent) samples
    private int head;
    private int tail;
    private int size;
    private int realCount; // number of real samples currently resident
    private readonly object bufferLock = new();
    public ConcurrentQueue<float[]> BufferedReturn = new ConcurrentQueue<float[]>();

    public BasisVoiceRingBuffer()
    {
        buffer = new float[RemoteOpusSettings.TotalFrameBufferSize];
        realMask = new bool[buffer.Length];
        head = 0;
        tail = 0;
        size = 0;
        realCount = 0;
    }

    public int Capacity => buffer.Length;
    public bool IsEmpty => Interlocked.CompareExchange(ref size, 0, 0) == 0;
    public bool IsFull => Interlocked.CompareExchange(ref size, 0, 0) == Capacity;

    // Computed from current contents instead of a sticky flag
    public bool HasRealAudio => Volatile.Read(ref realCount) > 0;

    public int Count => Interlocked.CompareExchange(ref size, 0, 0);
    /// <summary>
    /// Add 'length' samples from 'segment'. If hasActualAudio is true, those samples are flagged as real (non-silent).
    /// Older data is overwritten if needed (ring semantics).
    /// </summary>
    public void Add(float[] segment, int length, bool hasActualAudio)
    {
        if (segment == null || segment.Length == 0)
            throw new ArgumentNullException(nameof(segment));
        if (length <= 0 || length > segment.Length)
            throw new ArgumentOutOfRangeException(nameof(length), "Length must be a positive number and within segment length.");

        lock (bufferLock)
        {
            if (length > Capacity)
                throw new InvalidOperationException("The segment is too large to fit into the buffer.");

            int currentSize = Interlocked.CompareExchange(ref size, 0, 0);
            int availableSpace = Capacity - currentSize;

            // If not enough space, drop (overwrite) the oldest 'itemsToRemove' samples
            if (length > availableSpace)
            {
                int itemsToRemove = length - availableSpace;

                // Count and clear real flags in the range being dropped
                int removedReal = CountTrueAndClear(tail, itemsToRemove);
                if (removedReal != 0) Interlocked.Add(ref realCount, -removedReal);

                // advance tail/size
                tail = (tail + itemsToRemove) % Capacity;
                Interlocked.Add(ref size, -itemsToRemove);
                // BasisDebug.Log($"Overwriting {itemsToRemove} elements due to lack of space in the Audio buffer.");
            }

            // Write samples (may wrap)
            int firstPart = Math.Min(length, Capacity - head);
            Array.Copy(segment, 0, buffer, head, firstPart);
            int remaining = length - firstPart;
            if (remaining > 0)
            {
                Array.Copy(segment, firstPart, buffer, 0, remaining);
            }

            // Set real flags for written region and update realCount
            int newlyReal = hasActualAudio ? length : 0;
            SetMaskRange(head, length, hasActualAudio);
            if (newlyReal != 0) Interlocked.Add(ref realCount, newlyReal);

            // advance head/size
            head = (head + length) % Capacity;
            Interlocked.Add(ref size, length);
        }
    }

    /// <summary>
    /// Remove up to segmentSize samples from the buffer into 'segment'.
    /// If fewer exist, returns what is available (remaining padded with whatever is in 'segment' array).
    /// </summary>
    public void Remove(int segmentSize, out float[] segment)
    {
        if (segmentSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(segmentSize));

        lock (BufferedReturn)
        {
            if (!BufferedReturn.TryDequeue(out segment) || segment.Length != segmentSize)
                segment = new float[segmentSize];
            else
                Array.Clear(segment, 0, segmentSize);
        }

        lock (bufferLock)
        {
            int currentSize = Interlocked.CompareExchange(ref size, 0, 0);
            int itemsToRemove = Math.Min(segmentSize, currentSize);

            int firstPart = Math.Min(itemsToRemove, Capacity - tail);
            Array.Copy(buffer, tail, segment, 0, firstPart);

            int remaining = itemsToRemove - firstPart;
            if (remaining > 0)
                Array.Copy(buffer, 0, segment, firstPart, remaining);

            int removedReal = CountTrueAndClear(tail, itemsToRemove);
            if (removedReal != 0) Interlocked.Add(ref realCount, -removedReal);

            tail = (tail + itemsToRemove) % Capacity;
            Interlocked.Add(ref size, -itemsToRemove);

            // segment is already zero-filled from itemsToRemove..end
        }
    }

    private void SetMaskRange(int start, int count, bool value)
    {
        int first = Math.Min(count, Capacity - start);
        for (int i = 0; i < first; i++) realMask[start + i] = value;
        int remaining = count - first;
        for (int i = 0; i < remaining; i++) realMask[i] = value;
    }

    /// <summary>
    /// Count how many 'true' flags are in [start, start+count), clearing them to false as we go.
    /// Wraps around ring.
    /// </summary>
    private int CountTrueAndClear(int start, int count)
    {
        int total = 0;

        int first = Math.Min(count, Capacity - start);
        for (int i = 0; i < first; i++)
        {
            if (realMask[start + i]) { total++; realMask[start + i] = false; }
        }

        int remaining = count - first;
        for (int i = 0; i < remaining; i++)
        {
            if (realMask[i]) { total++; realMask[i] = false; }
        }

        return total;
    }

    // Min helper to avoid System.Math collision above
    private static class Math
    {
        public static int Min(int a, int b) => a < b ? a : b;
    }
}
