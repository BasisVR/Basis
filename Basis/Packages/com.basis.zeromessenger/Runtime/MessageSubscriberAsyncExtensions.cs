#if NET5_0_OR_GREATER
using System.Threading.Channels;
#endif

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.ZeroMessenger
{
    public static class MessageSubscriberAsyncExtensions
    {
        public static Task<T> FirstAsync<T>(this IMessageSubscriber<T> subscriber, CancellationToken cancellationToken = default)
        {
            var handler = new FirstAsyncMessageHandler<T>(subscriber, cancellationToken);
            subscriber.Subscribe(handler);
            return handler.Task;
        }

#if NET5_0_OR_GREATER
        public static IAsyncEnumerable<T> ToAsyncEnumerable<T>(this IMessageSubscriber<T> subscriber, CancellationToken cancellationToken = default)
        {
            var channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions
            {
                SingleWriter = true,
                SingleReader = true,
                AllowSynchronousContinuations = true
            });

            var handler = new AsyncEnumerableMessageHandler<T>(channel.Writer);
            var disposable = subscriber.Subscribe(handler);

            if (cancellationToken.CanBeCanceled)
            {
                handler.registration = cancellationToken.Register(state =>
                {
                    ((IDisposable)state!).Dispose();
                }, disposable);
            }

            return channel.Reader.ReadAllAsync(cancellationToken);
        }
#endif
    }

    internal sealed class FirstAsyncMessageHandler<T> : MessageHandler<T>
    {
        int handleCalled = 0;
        CancellationToken cancellationToken;
        CancellationTokenRegistration cancellationTokenRegistration;
        TaskCompletionSource<T> source = new();

        public Task<T> Task => source.Task;

        public FirstAsyncMessageHandler(IMessageSubscriber<T> subscriber, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                source.TrySetException(new OperationCanceledException(cancellationToken));
                return;
            }

            try
            {
                subscriber.Subscribe(this);
            }
            catch (Exception ex)
            {
                source.TrySetException(ex);
                return;
            }

            if (handleCalled != 0)
            {
                Dispose();
                return;
            }

            if (cancellationToken.CanBeCanceled)
            {
                this.cancellationToken = cancellationToken;

                cancellationTokenRegistration = cancellationToken.Register(static state =>
                {
                    var s = (FirstAsyncMessageHandler<T>)state!;
                    s.Dispose();
                    s.source.TrySetCanceled(s.cancellationToken);
                }, this);
            }
        }

        protected override void HandleCore(T message)
        {
            if (Interlocked.Increment(ref handleCalled) == 1)
            {
                try
                {
                    source.TrySetResult(message);
                }
                finally
                {
                    cancellationTokenRegistration.Dispose();
                    Dispose();
                }
            }
        }

#if NET5_0_OR_GREATER

    internal class AsyncEnumerableMessageHandler<T> : MessageHandler<T>
    {
        readonly ChannelWriter<T> writer;
        public CancellationTokenRegistration registration;

        public AsyncEnumerableMessageHandler(ChannelWriter<T> writer)
        {
            this.writer = writer;
        }

        protected override void HandleCore(T message)
        {
            writer.TryWrite(message);
        }

        protected override void DisposeCore()
        {
            registration.Dispose();
        }
    }
    
#endif
    }
}