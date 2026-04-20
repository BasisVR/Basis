using System;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Basis.ZeroMessenger.Internal
{
    internal static class ContinuationSentinel
    {
        public static readonly Action AvailableContinuation = () => { };
        public static readonly Action CompletedContinuation = () => { };
    }

    internal partial class ValueTaskWhenAll<T> : ICriticalNotifyCompletion
    {
        internal class AwaiterNode : ILinkedPoolNode<AwaiterNode>
        {
            static LinkedPool<AwaiterNode> pool;

            AwaiterNode nextNode = default!;
            public ref AwaiterNode NextNode => ref nextNode;

            ValueTaskWhenAll<T> parent = default!;
            ValueTaskAwaiter awaiter;

            readonly Action continuation;

            public AwaiterNode()
            {
                continuation = OnCompleted;
            }

            public static void RegisterUnsafeOnCompleted(ValueTaskWhenAll<T> parent, ValueTaskAwaiter awaiter)
            {
                if (!pool.TryPop(out var result))
                {
                    result = new AwaiterNode();
                }
                result.parent = parent;
                result.awaiter = awaiter;

                result.awaiter.UnsafeOnCompleted(result.continuation);
            }

            void OnCompleted()
            {
                var p = parent;
                var a = awaiter;
                parent = null!;
                awaiter = default;

                pool.TryPush(this);

                try
                {
                    a.GetResult();
                }
                catch (Exception ex)
                {
                    p.exception = ExceptionDispatchInfo.Capture(ex);
                    p.TryInvokeContinuation();
                    return;
                }

                p.IncrementSuccessfully();
            }
        }

        readonly int taskCount = 0;

        int completedCount = 0;
        ExceptionDispatchInfo? exception;
        Action continuation = ContinuationSentinel.AvailableContinuation;

        public ValueTaskWhenAll(AsyncMessageHandler<T>?[] handlers, T message, CancellationToken cancellationtoken)
        {
            taskCount = handlers.Length;

            foreach (var item in handlers)
            {
                if (item == null)
                {
                    IncrementSuccessfully();
                }
                else
                {
                    try
                    {
                        var awaiter = item.HandleAsync(message, cancellationtoken).GetAwaiter();
                        if (awaiter.IsCompleted)
                        {
                            awaiter.GetResult();
                            goto SUCCESSFULLY;
                        }
                        else
                        {
                            AwaiterNode.RegisterUnsafeOnCompleted(this, awaiter);
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        exception = ExceptionDispatchInfo.Capture(ex);
                        TryInvokeContinuation();
                        return;
                    }

                SUCCESSFULLY:
                    IncrementSuccessfully();
                }
            }
        }

        void IncrementSuccessfully()
        {
            if (Interlocked.Increment(ref completedCount) == taskCount)
            {
                TryInvokeContinuation();
            }
        }

        void TryInvokeContinuation()
        {
            var c = Interlocked.Exchange(ref continuation, ContinuationSentinel.CompletedContinuation); // register completed.
            if (c != ContinuationSentinel.AvailableContinuation && c != ContinuationSentinel.CompletedContinuation)
            {
                c();
            }
        }

        public ValueTaskWhenAll<T> GetAwaiter()
        {
            return this;
        }

        public bool IsCompleted => exception != null || completedCount == taskCount;

        public void GetResult()
        {
            exception?.Throw();
        }

        public void OnCompleted(Action continuation)
        {
            UnsafeOnCompleted(continuation);
        }

        public void UnsafeOnCompleted(Action continuation)
        {
            var c = Interlocked.CompareExchange(ref this.continuation, continuation, ContinuationSentinel.AvailableContinuation);
            if (c == ContinuationSentinel.CompletedContinuation) // registered TryInvokeContinuation first.
            {
                continuation();
                return;
            }
        }
    }
}