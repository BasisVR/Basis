using System;
using System.Collections.Generic;
using System.Threading;

public class BasisVoiceRingBuffer
{
    private readonly float[] buffer;
    private int head;
    private int tail;
    private int size;
    private readonly object bufferLock = new();
    public Queue<float[]> BufferedReturn = new Queue<float[]>();

    public BasisVoiceRingBuffer()
    {
        buffer = new float[RemoteOpusSettings.TotalFrameBufferSize];
        head = 0;
        tail = 0;
        size = 0;
    }

    public int Capacity => buffer.Length;
    public bool IsEmpty => Interlocked.CompareExchange(ref size, 0, 0) == 0;
    public bool IsFull => Interlocked.CompareExchange(ref size, 0, 0) == Capacity;

    public void Add(float[] segment, int length)
    {
        if (segment == null || segment.Length == 0)
        {
            throw new ArgumentNullException(nameof(segment));
        }
        if (length <= 0 || length > segment.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Length must be a positive number and within segment length.");
        }

        lock (bufferLock)
        {
            if (length > Capacity)
            {
                throw new InvalidOperationException("The segment is too large to fit into the buffer.");
            }

            int availableSpace = Capacity - Interlocked.CompareExchange(ref size, 0, 0);
            if (length > availableSpace)
            {
                int itemsToRemove = length - availableSpace;
                Interlocked.Add(ref tail, itemsToRemove);
                Interlocked.Add(ref size, -itemsToRemove);
                tail %= Capacity;
            //    BasisDebug.Log($"Overwriting {itemsToRemove} elements due to lack of space in the Audio buffer.");
            }

            int firstPart = Math.Min(length, Capacity - head);
            Array.Copy(segment, 0, buffer, head, firstPart);
            int remaining = length - firstPart;
            if (remaining > 0)
            {
                Array.Copy(segment, firstPart, buffer, 0, remaining);
            }

            Interlocked.Add(ref head, length);
            head %= Capacity;
            Interlocked.Add(ref size, length);
        }
    }

    public void Remove(int segmentSize, out float[] segment)
    {
        if (segmentSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(segmentSize));

        lock (BufferedReturn)
        {
            if (!BufferedReturn.TryDequeue(out segment) || segment.Length != segmentSize)
            {
                segment = new float[segmentSize];
            }
        }

        lock (bufferLock)
        {
            int currentSize = Interlocked.CompareExchange(ref size, 0, 0);
            int itemsToRemove = Math.Min(segmentSize, currentSize);

            int firstPart = Math.Min(itemsToRemove, Capacity - tail);
            Array.Copy(buffer, tail, segment, 0, firstPart);
            int remaining = itemsToRemove - firstPart;
            if (remaining > 0)
            {
                Array.Copy(buffer, 0, segment, firstPart, remaining);
            }

            Interlocked.Add(ref tail, itemsToRemove);
            tail %= Capacity;
            Interlocked.Add(ref size, -itemsToRemove);
        }
    }

    public bool NeedsMoreData(int requiredSize)
    {
        int currentSize = Interlocked.CompareExchange(ref size, 0, 0);
        if (currentSize < requiredSize)
        {
            BasisDebug.Log($"Warning: Requested {requiredSize} items, but only {currentSize} are available.");
            return true;
        }
        return false;
    }
    public int SamplesToMax()
    {
        return Capacity - Interlocked.CompareExchange(ref size, 0, 0);
    }
}
