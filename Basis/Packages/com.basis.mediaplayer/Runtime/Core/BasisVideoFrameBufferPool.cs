using System.Collections.Concurrent;
using System.Threading;

public static class BasisVideoFrameBufferPool
{
    private const int MaxPooledBuffers = 256;
    private static readonly ConcurrentBag<byte[]> bag = new ConcurrentBag<byte[]>();
    private static int pooledCount;

    public static byte[] Rent(int size)
    {
        while (bag.TryTake(out var buf))
        {
            Interlocked.Decrement(ref pooledCount);
            if (buf.Length == size) return buf;
        }
        return new byte[size];
    }

    public static void Return(byte[] buf)
    {
        if (buf == null) return;
        if (Interlocked.Increment(ref pooledCount) > MaxPooledBuffers)
        {
            Interlocked.Decrement(ref pooledCount);
            return;
        }
        bag.Add(buf);
    }

}
