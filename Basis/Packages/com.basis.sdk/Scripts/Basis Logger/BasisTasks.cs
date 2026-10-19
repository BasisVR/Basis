using Unity.Scripting.LifecycleManagement;
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

[NoAutoStaticsCleanup]
public static class BasisTasks
{
#if UNITY_WEBGL && !UNITY_EDITOR
    public static readonly bool ThreadsAvailable = false;
#else
    public static readonly bool ThreadsAvailable = true;
#endif

    public static Task Run(Action action)
    {
        if (ThreadsAvailable) return Task.Run(action);
        try
        {
            action();
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    public static Task Run(Action action, CancellationToken token)
    {
        if (ThreadsAvailable) return Task.Run(action, token);
        if (token.IsCancellationRequested) return Task.FromCanceled(token);
        return Run(action);
    }

    public static Task<T> Run<T>(Func<T> function)
    {
        if (ThreadsAvailable) return Task.Run(function);
        try
        {
            return Task.FromResult(function());
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }

    public static Task<T> Run<T>(Func<T> function, CancellationToken token)
    {
        if (ThreadsAvailable) return Task.Run(function, token);
        if (token.IsCancellationRequested) return Task.FromCanceled<T>(token);
        return Run(function);
    }

    public static Task Run(Func<Task> function)
    {
        if (ThreadsAvailable) return Task.Run(function);
        try
        {
            return function() ?? Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    public static Task Run(Func<Task> function, CancellationToken token)
    {
        if (ThreadsAvailable) return Task.Run(function, token);
        if (token.IsCancellationRequested) return Task.FromCanceled(token);
        return Run(function);
    }

    public static Task<T> Run<T>(Func<Task<T>> function)
    {
        if (ThreadsAvailable) return Task.Run(function);
        try
        {
            return function();
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }

    public static Task<T> Run<T>(Func<Task<T>> function, CancellationToken token)
    {
        if (ThreadsAvailable) return Task.Run(function, token);
        if (token.IsCancellationRequested) return Task.FromCanceled<T>(token);
        return Run(function);
    }

    public static Task Delay(int milliseconds, CancellationToken token = default)
    {
        if (ThreadsAvailable) return Task.Delay(milliseconds, token);
        return MainThreadDelay(milliseconds, token);
    }

    public static Task Delay(TimeSpan delay, CancellationToken token = default)
    {
        return Delay((int)Math.Min(int.MaxValue, Math.Max(0, delay.TotalMilliseconds)), token);
    }

    private static async Task MainThreadDelay(int milliseconds, CancellationToken token)
    {
        if (milliseconds <= 0)
        {
            await Awaitable.NextFrameAsync(token);
            return;
        }
        float until = Time.realtimeSinceStartup + milliseconds / 1000f;
        while (Time.realtimeSinceStartup < until)
        {
            await Awaitable.NextFrameAsync(token);
        }
    }

    public static void For(int fromInclusive, int toExclusive, Action<int> body)
    {
        if (ThreadsAvailable)
        {
            Parallel.For(fromInclusive, toExclusive, body);
            return;
        }
        for (int index = fromInclusive; index < toExclusive; index++)
        {
            body(index);
        }
    }

    public static void For(int fromInclusive, int toExclusive, ParallelOptions options, Action<int> body)
    {
        if (ThreadsAvailable)
        {
            Parallel.For(fromInclusive, toExclusive, options, body);
            return;
        }
        for (int index = fromInclusive; index < toExclusive; index++)
        {
            body(index);
        }
    }
}
