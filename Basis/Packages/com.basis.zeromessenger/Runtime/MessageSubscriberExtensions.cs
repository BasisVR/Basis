using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.ZeroMessenger
{
    public static class MessageSubscriberExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IDisposable Subscribe<T>(this IMessageSubscriber<T> subscriber, Action<T> handler)
        {
            return subscriber.Subscribe(new AnonymousMessageHandler<T>(handler));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IDisposable Subscribe<T, TState>(this IMessageSubscriber<T> subscriber, TState state, Action<T, TState> handler)
        {
            return subscriber.Subscribe(new AnonymousMessageHandler<T, TState>(state, handler));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IDisposable SubscribeAwait<T>(this IMessageSubscriber<T> subscriber, Func<T, CancellationToken, ValueTask> handler, AsyncSubscribeStrategy subscribeStrategy = AsyncSubscribeStrategy.Sequential)
        {
            return subscriber.SubscribeAwait(new AnonymousAsyncMessageHandler<T>(handler), subscribeStrategy);
        }
    }
}