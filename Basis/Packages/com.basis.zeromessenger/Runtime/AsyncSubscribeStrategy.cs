using System.Threading;
using System.Threading.Tasks;

namespace Basis.ZeroMessenger
{
    public enum AsyncSubscribeStrategy
    {
        Sequential,
        Parallel,
        Switch,
        Drop,
    }

    internal sealed class SequentialAsyncMessageHandler<T> : AsyncMessageHandler<T>
    {
        readonly AsyncMessageHandler<T> handler;
        readonly SemaphoreSlim publishLock = new(1, 1);

        public SequentialAsyncMessageHandler(AsyncMessageHandler<T> handler)
        {
            this.handler = handler;
        }

        protected override async ValueTask HandleAsyncCore(T message, CancellationToken cancellationToken = default)
        {
            await publishLock.WaitAsync(cancellationToken);
            try
            {
                await handler.HandleAsync(message, cancellationToken);
            }
            finally
            {
                // Only release if not disposed, to avoid ObjectDisposedException
                // when fire-and-forget tasks complete after handler is disposed
                if (!IsDisposed)
                {
                    publishLock.Release();
                }
            }
        }

        protected override void DisposeCore()
        {
            publishLock.Dispose();
        }
    }

    internal sealed class DropAsyncMessageHandler<T> : AsyncMessageHandler<T>
    {
        readonly AsyncMessageHandler<T> handler;
        int flag;

        public DropAsyncMessageHandler(AsyncMessageHandler<T> handler)
        {
            this.handler = handler;
        }

        protected override async ValueTask HandleAsyncCore(T message, CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref flag, 1, 0) == 0)
            {
                try
                {
                    await handler.HandleAsync(message, cancellationToken);
                }
                finally
                {
                    Interlocked.Exchange(ref flag, 0);
                }
            }
        }
    }

    internal sealed class SwitchAsyncMessageHandler<T> : AsyncMessageHandler<T>
    {
        readonly AsyncMessageHandler<T> handler;
        CancellationTokenSource? cts;

        public SwitchAsyncMessageHandler(AsyncMessageHandler<T> handler)
        {
            this.handler = handler;
        }

        protected override ValueTask HandleAsyncCore(T message, CancellationToken cancellationToken = default)
        {
            if (cts != null)
            {
                cts.Cancel();
                cts.Dispose();
            }

            cts = new CancellationTokenSource();
            var ct = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken).Token;

            return handler.HandleAsync(message, ct);
        }
    }
}