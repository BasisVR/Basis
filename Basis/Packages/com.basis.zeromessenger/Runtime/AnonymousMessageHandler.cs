using System;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.ZeroMessenger
{
    internal sealed class AnonymousMessageHandler<T> : MessageHandler<T>
    {
        private readonly Action<T> handler;

        public AnonymousMessageHandler(Action<T> handler)
        {
            this.handler = handler;
        }

        protected override void HandleCore(T message)
        {
            handler(message);
        }
    }
    internal sealed class AnonymousMessageHandler<T, TState> : MessageHandler<T>
    {
        private readonly TState state;
        private readonly Action<T, TState> handler;

        public AnonymousMessageHandler(TState state, Action<T, TState> handler)
        {
            this.state = state;
            this.handler = handler;
        }

        protected override void HandleCore(T message)
        {
            handler(message, state);
        }
    }

    internal sealed class AnonymousAsyncMessageHandler<T> : AsyncMessageHandler<T>
    {
        private readonly Func<T, CancellationToken, ValueTask> handler;

        public AnonymousAsyncMessageHandler(Func<T, CancellationToken, ValueTask> handler)
        {
            this.handler = handler;
        }

        protected override ValueTask HandleAsyncCore(T message, CancellationToken cancellationToken = default)
        {
            return handler(message, cancellationToken);
        }
    }
}