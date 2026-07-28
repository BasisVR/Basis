using System.Collections.Concurrent;
using Basis.Network.Core.Compression;
using BasisNetworkCore.Pooling;
using Xunit;

namespace BasisServerTests;

/// <summary>
/// BasisObjectPool&lt;T&gt;: lock-guarded Stack pool with a caller-supplied factory.
/// Contract under test: Get pops (LIFO) or creates via the factory, Return pushes,
/// returned instances are not reset, and null factory/item are rejected.
/// </summary>
public class BasisObjectPoolTests
{
    private sealed class PooledNode { }

    [Fact]
    public void Ctor_NullFactory_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new BasisObjectPool<object>(null!));
    }

    [Fact]
    public void Get_EmptyPool_CreatesDistinctInstancesViaFactory()
    {
        int created = 0;
        var pool = new BasisObjectPool<PooledNode>(() => { created++; return new PooledNode(); });

        PooledNode first = pool.Get();
        PooledNode second = pool.Get();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.Equal(2, created);
    }

    [Fact]
    public void ReturnThenGet_ReusesInstance_WithoutInvokingFactory()
    {
        int created = 0;
        var pool = new BasisObjectPool<PooledNode>(() => { created++; return new PooledNode(); });

        PooledNode item = pool.Get();
        pool.Return(item);

        Assert.Same(item, pool.Get());
        Assert.Equal(1, created);
    }

    [Fact]
    public void Get_DrainsReturnedItemsInLifoOrder()
    {
        var pool = new BasisObjectPool<PooledNode>(() => new PooledNode());
        PooledNode a = pool.Get();
        PooledNode b = pool.Get();

        pool.Return(a);
        pool.Return(b);

        Assert.Same(b, pool.Get());
        Assert.Same(a, pool.Get());
    }

    [Fact]
    public void Return_Null_Throws()
    {
        var pool = new BasisObjectPool<object>(() => new object());
        Assert.Throws<ArgumentNullException>(() => pool.Return(null!));
    }

    [Fact]
    public void Return_DoesNotResetInstances_CallerMustClearState()
    {
        var pool = new BasisObjectPool<List<int>>(() => new List<int>());

        List<int> list = pool.Get();
        list.Add(42);
        pool.Return(list);

        List<int> reused = pool.Get();
        Assert.Same(list, reused);
        int survivor = Assert.Single(reused);
        Assert.Equal(42, survivor);
    }

    [Fact]
    public void ConcurrentGetReturn_NeverHandsSameInstanceToTwoHolders()
    {
        const int ThreadCount = 8;
        const int Iterations = 10_000;
        var pool = new BasisObjectPool<PooledNode>(() => new PooledNode());
        var outstanding = new ConcurrentDictionary<PooledNode, byte>();
        int duplicates = 0;

        PoolConcurrency.Run(ThreadCount, () =>
        {
            for (int i = 0; i < Iterations; i++)
            {
                PooledNode node = pool.Get();
                if (!outstanding.TryAdd(node, 0))
                {
                    Interlocked.Increment(ref duplicates);
                }
                outstanding.TryRemove(node, out _);
                pool.Return(node);
            }
        });

        Assert.Equal(0, duplicates);
        Assert.Empty(outstanding);
    }
}

/// <summary>
/// BasisByteArrayPooling: process-wide static pool with one FIFO bucket per exact
/// array length. Rent returns an array of exactly the requested size; buffers are
/// never cleared, so callers must treat rented arrays as uninitialized.
/// No other test in this assembly touches this pool and xunit never parallelizes
/// methods within one class, so the reuse assertions here are deterministic.
/// </summary>
public class BasisByteArrayPoolingTests
{
    [Fact]
    public void Rent_ReturnsWritableArrayOfExactRequestedLength()
    {
        foreach (int size in new[] { 1, 2, 16, 255, 1024, 65_536 })
        {
            byte[] array = BasisByteArrayPooling.Rent(size);
            Assert.NotNull(array);
            Assert.Equal(size, array.Length);
            array[0] = 0x5A;
            array[size - 1] = 0xA5;
        }
    }

    [Fact]
    public void Rent_ZeroSize_ReturnsEmptyArray_AndPoolsIt()
    {
        BasisByteArrayPooling.Clear();

        byte[] empty = BasisByteArrayPooling.Rent(0);
        Assert.NotNull(empty);
        Assert.Empty(empty);

        BasisByteArrayPooling.Return(empty);
        Assert.Same(empty, BasisByteArrayPooling.Rent(0));
    }

    [Fact]
    public void Rent_LargeSize_HonorsLength_AndReturnThenRentReuses()
    {
        const int Size = (1 << 20) + 17;
        BasisByteArrayPooling.Clear();

        byte[] large = BasisByteArrayPooling.Rent(Size);
        Assert.Equal(Size, large.Length);
        large[Size - 1] = 0x7F;

        BasisByteArrayPooling.Return(large);
        Assert.Same(large, BasisByteArrayPooling.Rent(Size));

        BasisByteArrayPooling.Clear();
    }

    [Fact]
    public void ReturnThenRent_ReusesBuffer_WithoutClearingContents()
    {
        const int Size = 7717;
        BasisByteArrayPooling.Clear();

        byte[] array = BasisByteArrayPooling.Rent(Size);
        array[0] = 0xAB;
        array[Size - 1] = 0xCD;
        BasisByteArrayPooling.Return(array);

        byte[] reused = BasisByteArrayPooling.Rent(Size);
        Assert.Same(array, reused);
        // The pool does not zero buffers; stale-data hygiene is on the caller.
        Assert.Equal((byte)0xAB, reused[0]);
        Assert.Equal((byte)0xCD, reused[Size - 1]);
    }

    [Fact]
    public void Rent_UsesExactSizeBuckets_NeverBorrowsOtherSizes()
    {
        BasisByteArrayPooling.Clear();

        byte[] pooled = BasisByteArrayPooling.Rent(4099);
        BasisByteArrayPooling.Return(pooled);

        byte[] other = BasisByteArrayPooling.Rent(4100);
        Assert.Equal(4100, other.Length);

        Assert.Same(pooled, BasisByteArrayPooling.Rent(4099));
    }

    [Fact]
    public void Return_Null_IsANoOp()
    {
        BasisByteArrayPooling.Return(null!);

        byte[] array = BasisByteArrayPooling.Rent(31);
        Assert.NotNull(array);
        Assert.Equal(31, array.Length);
    }

    [Fact]
    public void Clear_DropsPooledBuffers()
    {
        BasisByteArrayPooling.Clear();

        byte[] array = BasisByteArrayPooling.Rent(6151);
        BasisByteArrayPooling.Return(array);
        BasisByteArrayPooling.Clear();

        Assert.NotSame(array, BasisByteArrayPooling.Rent(6151));
    }

    [Fact]
    public void ConcurrentRentals_NeverAliasTheSameArray()
    {
        const int Rentals = 8_000;
        const int Size = 5081;
        var rented = new ConcurrentDictionary<byte[], byte>(); // arrays compare by reference
        int aliased = 0;
        int wrongLength = 0;

        Parallel.For(0, Rentals, _ =>
        {
            byte[] array = BasisByteArrayPooling.Rent(Size);
            if (array.Length != Size)
            {
                Interlocked.Increment(ref wrongLength);
            }
            if (!rented.TryAdd(array, 0))
            {
                Interlocked.Increment(ref aliased);
            }
        });

        Assert.Equal(0, wrongLength);
        Assert.Equal(0, aliased);
        Assert.Equal(Rentals, rented.Count);
    }

    [Fact]
    public void ConcurrentRentReturn_Storm_KeepsBuffersExclusive()
    {
        const int ThreadCount = 8;
        const int Iterations = 10_000;
        const int Size = 3371;
        var outstanding = new ConcurrentDictionary<byte[], byte>();
        int duplicates = 0;
        int wrongLength = 0;

        PoolConcurrency.Run(ThreadCount, () =>
        {
            for (int i = 0; i < Iterations; i++)
            {
                byte[] array = BasisByteArrayPooling.Rent(Size);
                if (array.Length != Size)
                {
                    Interlocked.Increment(ref wrongLength);
                }
                if (!outstanding.TryAdd(array, 0))
                {
                    Interlocked.Increment(ref duplicates);
                }
                array[i % Size] = (byte)i;
                outstanding.TryRemove(array, out _);
                BasisByteArrayPooling.Return(array);
            }
        });

        Assert.Equal(0, wrongLength);
        Assert.Equal(0, duplicates);
        Assert.Empty(outstanding);
    }
}

/// <summary>
/// ThreadSafeMessagePool&lt;T&gt;: process-wide static ConcurrentQueue pool, soft-capped
/// at 500 retained instances per closed generic type. Each test below uses its own
/// private message type, so every test observes a virgin pool even when other test
/// classes run in parallel.
/// </summary>
public class ThreadSafeMessagePoolTests
{
    private sealed class FreshMessage { }
    private sealed class ReusedMessage { }
    private sealed class StatefulMessage { public int Value; }
    private sealed class CappedMessage { }
    private sealed class StormMessage { }

    [Fact]
    public void Rent_EmptyPool_CreatesDistinctInstances()
    {
        FreshMessage first = ThreadSafeMessagePool<FreshMessage>.Rent();
        FreshMessage second = ThreadSafeMessagePool<FreshMessage>.Rent();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void ReturnThenRent_ReusesInstance()
    {
        ReusedMessage message = ThreadSafeMessagePool<ReusedMessage>.Rent();
        ThreadSafeMessagePool<ReusedMessage>.Return(message);

        Assert.Same(message, ThreadSafeMessagePool<ReusedMessage>.Rent());
    }

    [Fact]
    public void Return_DoesNotResetState_CallerMustOverwriteOnRent()
    {
        StatefulMessage message = ThreadSafeMessagePool<StatefulMessage>.Rent();
        message.Value = 42;
        ThreadSafeMessagePool<StatefulMessage>.Return(message);

        StatefulMessage reused = ThreadSafeMessagePool<StatefulMessage>.Rent();
        Assert.Same(message, reused);
        // Matches production usage: HandleVoiceMessage deserializes over the rented
        // instance, overwriting whatever the previous user left behind.
        Assert.Equal(42, reused.Value);
    }

    [Fact]
    public void Return_BeyondCap_DoesNotThrow_AndRetainsExactlyMaxPoolSize()
    {
        const int MaxPoolSize = 500; // mirrors ThreadSafeMessagePool<T>.MaxPoolSize
        const int OverReturn = 600;

        var returned = new HashSet<CappedMessage>(); // reference equality
        for (int i = 0; i < OverReturn; i++)
        {
            var message = new CappedMessage();
            returned.Add(message);
            ThreadSafeMessagePool<CappedMessage>.Return(message);
        }

        var rented = new HashSet<CappedMessage>();
        for (int i = 0; i < MaxPoolSize; i++)
        {
            CappedMessage message = ThreadSafeMessagePool<CappedMessage>.Rent();
            Assert.True(rented.Add(message), $"rent {i} produced an instance already handed out");
            Assert.Contains(message, returned);
        }

        // Retention stopped at the cap, so the pool is now drained and the next
        // rent must allocate a brand-new instance.
        CappedMessage fresh = ThreadSafeMessagePool<CappedMessage>.Rent();
        Assert.DoesNotContain(fresh, returned);
    }

    [Fact]
    public void ConcurrentRentReturn_Storm_NoDuplicateOutstandingInstances()
    {
        const int ThreadCount = 8;
        const int Iterations = 10_000;
        var outstanding = new ConcurrentDictionary<StormMessage, byte>();
        int duplicates = 0;

        PoolConcurrency.Run(ThreadCount, () =>
        {
            for (int i = 0; i < Iterations; i++)
            {
                StormMessage message = ThreadSafeMessagePool<StormMessage>.Rent();
                if (!outstanding.TryAdd(message, 0))
                {
                    Interlocked.Increment(ref duplicates);
                }
                outstanding.TryRemove(message, out _);
                ThreadSafeMessagePool<StormMessage>.Return(message);
            }
        });

        Assert.Equal(0, duplicates);
        Assert.Empty(outstanding);
    }
}

/// <summary>
/// Runs a worker on dedicated threads released together, then blocks with a bounded
/// wait so a deadlocked pool fails the test instead of hanging the run.
/// </summary>
file static class PoolConcurrency
{
    public static void Run(int threadCount, Action worker)
    {
        using ManualResetEventSlim startGate = new(false);
        var tasks = new Task[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            tasks[t] = Task.Factory.StartNew(() =>
            {
                startGate.Wait();
                worker();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        startGate.Set();
        Assert.True(Task.WaitAll(tasks, TimeSpan.FromSeconds(60)), "Pool workers did not finish within 60 seconds.");
    }
}
