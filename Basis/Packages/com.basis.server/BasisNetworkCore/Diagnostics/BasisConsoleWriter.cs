using System;
using System.Collections.Concurrent;
using System.Threading;

public static class BasisConsoleWriter
{
    public const int MaxQueuedLines = 4096;
    private static readonly ConcurrentQueue<Action> lines = new ConcurrentQueue<Action>();
    private static readonly SemaphoreSlim signal = new SemaphoreSlim(0);
    private static readonly object startGate = new object();
    private static Thread worker;
    private static int queued;
    private static long dropped;
    public static long Dropped => Interlocked.Read(ref dropped);
    public static void Enqueue(Action write)
    {
        if (write == null) return;
        if (Interlocked.Increment(ref queued) > MaxQueuedLines)
        {
            Interlocked.Decrement(ref queued);
            Interlocked.Increment(ref dropped);
            return;
        }
        EnsureStarted();
        lines.Enqueue(write);
        signal.Release();
    }
    public static void Flush(int timeoutMs = 2000)
    {
        long deadline = Environment.TickCount + timeoutMs;
        while (Volatile.Read(ref queued) > 0 && Environment.TickCount < deadline) Thread.Sleep(5);
    }
    private static void EnsureStarted()
    {
        if (worker != null) return;
        lock (startGate)
        {
            if (worker != null) return;
            worker = new Thread(Run) { IsBackground = true, Name = "Basis console writer" };
            worker.Start();
        }
    }
    private static void Run()
    {
        long reported = 0;
        while (true)
        {
            signal.Wait();
            if (!lines.TryDequeue(out Action write)) continue;
            try
            {
                write();
                long lost = Interlocked.Read(ref dropped);
                if (lost != reported && Volatile.Read(ref queued) <= 1)
                {
                    Console.WriteLine($"[console] {lost - reported} log lines were not shown because output could not keep up.");
                    reported = lost;
                }
            }
            catch
            {
            }
            Interlocked.Decrement(ref queued);
        }
    }
}
