using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Basis.ZeroMessenger.Internal;

namespace Basis.ZeroMessenger
{

    [UnityEngine.Scripting.Preserve]
    public class MessageBroker<T> : IMessagePublisher<T>, IMessageSubscriber<T>, IDisposable
    {
        public static readonly MessageBroker<T> Default = new();

        readonly object gate;
        readonly MessageHandlerList<T> syncHandlers;
        readonly MessageHandlerList<T> asyncHandlers;

        bool isDisposed;
        FastListCore<IMessageFilter<T>> globalFilters;

        public bool IsDisposed => isDisposed;

        [UnityEngine.Scripting.Preserve]
        public MessageBroker(MessageFilterProvider<T>? filterProvider = null)
        {
            gate = new();
            syncHandlers = new(gate);
            asyncHandlers = new(gate);

            if (filterProvider != null)
            {
                foreach (var filter in filterProvider.GetGlobalFilters())
                {
                    globalFilters.Add(filter);
                }
            }
        }

        public void Publish(T message, CancellationToken cancellationToken = default)
        {
            {
                var node = syncHandlers.Root;
                var version = syncHandlers.GetVersion();

                while (node != null)
                {
                    if (node.Version > version) break;
                    Unsafe.As<MessageHandler<T>>(node)!.Handle(message);
                    node = node.NextNode;
                }
            }

            {
                var node = asyncHandlers.Root;
                var version = asyncHandlers.GetVersion();

                while (node != null)
                {
                    if (node.Version > version) break;
                    Unsafe.As<AsyncMessageHandler<T>>(node)!.HandleAsync(message, cancellationToken).Forget();
                    node = node.NextNode;
                }
            }
        }

        public async ValueTask PublishAsync(T message, AsyncPublishStrategy publishStrategy = AsyncPublishStrategy.Parallel, CancellationToken cancellationToken = default)
        {
            {
                var node = syncHandlers.Root;
                var version = syncHandlers.GetVersion();

                while (node != null)
                {
                    if (node.Version > version) break;
                    Unsafe.As<MessageHandler<T>>(node)!.Handle(message);
                    node = node.NextNode;
                }
            }

            switch (publishStrategy)
            {
                case AsyncPublishStrategy.Parallel:
                    {
                        var list = new PooledList<AsyncMessageHandler<T>>(8);
                        AsyncMessageHandler<T>[]? result = null;

                        try
                        {
                            var node = asyncHandlers.Root;
                            var version = asyncHandlers.GetVersion();

                            while (node != null)
                            {
                                if (node.Version > version) break;
                                list.Add(Unsafe.As<AsyncMessageHandler<T>>(node));
                                node = node.NextNode;
                            }

                            if (list.Count > 0)
                            {
                                result = list.AsSpan().ToArray();
                            }
                        }
                        finally
                        {
                            list.Dispose();
                        }

                        if (result != null)
                        {
                            await new ValueTaskWhenAll<T>(result, message, cancellationToken);
                        }
                    }
                    break;
                case AsyncPublishStrategy.Sequential:
                    {
                        var node = asyncHandlers.Root;
                        var version = asyncHandlers.GetVersion();

                        while (node != null)
                        {
                            if (node.Version > version) break;
                            await Unsafe.As<AsyncMessageHandler<T>>(node)!.HandleAsync(message, cancellationToken);
                            node = node.NextNode;
                        }
                    }
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IDisposable Subscribe(MessageHandler<T> handler)
        {
            ThrowHelper.ThrowArgumentNullIfNull(handler, nameof(handler));
            ThrowHelper.ThrowObjectDisposedIf(handler.IsDisposed, typeof(AsyncMessageHandler<T>));
            ThrowHelper.ThrowIfMessageHandlerIsAssigned(handler);

            if (globalFilters.Length > 0)
            {
                return SubscribeAwaitCore(new FilteredMessageHandler<T>(handler, globalFilters.AsSpan().ToArray()), AsyncSubscribeStrategy.Sequential);
            }

            return SubscribeCore(handler);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IDisposable SubscribeAwait(AsyncMessageHandler<T> handler, AsyncSubscribeStrategy subscribeStrategy = AsyncSubscribeStrategy.Sequential)
        {
            ThrowHelper.ThrowArgumentNullIfNull(handler, nameof(handler));
            ThrowHelper.ThrowObjectDisposedIf(handler.IsDisposed, typeof(AsyncMessageHandler<T>));
            ThrowHelper.ThrowIfMessageHandlerIsAssigned(handler);

            if (globalFilters.Length > 0)
            {
                return SubscribeAwaitCore(new FilteredAsyncMessageHandler<T>(handler, globalFilters.AsSpan().ToArray()), AsyncSubscribeStrategy.Sequential);
            }

            return SubscribeAwaitCore(handler, subscribeStrategy);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        MessageHandler<T> SubscribeCore(MessageHandler<T> handler)
        {
            syncHandlers.Add(handler);
            return handler;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        AsyncMessageHandler<T> SubscribeAwaitCore(AsyncMessageHandler<T> handler, AsyncSubscribeStrategy subscribeStrategy)
        {
            handler = subscribeStrategy switch
            {
                AsyncSubscribeStrategy.Sequential => new SequentialAsyncMessageHandler<T>(handler),
                AsyncSubscribeStrategy.Switch => new SwitchAsyncMessageHandler<T>(handler),
                AsyncSubscribeStrategy.Drop => new DropAsyncMessageHandler<T>(handler),
                _ => handler,
            };

            asyncHandlers.Add(handler);
            return handler;
        }

        public void AddFilter<TFilter>(TFilter filter) where TFilter : IMessageFilter<T>
        {
            globalFilters.Add(filter);
        }

        public void AddFilter<TFilter>() where TFilter : IMessageFilter<T>, new()
        {
            globalFilters.Add(new TFilter());
        }

        public void AddFilter(Predicate<T> predicate)
        {
            globalFilters.Add(new PredicateFilter<T>(predicate));
        }

        public void AddFilter(Func<T, CancellationToken, Func<T, CancellationToken, ValueTask>, ValueTask> filter)
        {
            globalFilters.Add(new AnonymousMessageFilter<T>(filter));
        }

        public void Dispose()
        {
            lock (gate)
            {
                ThrowHelper.ThrowObjectDisposedIf(IsDisposed, typeof(MessageBroker<T>));
                isDisposed = true;
            }

            syncHandlers.Dispose();
            asyncHandlers.Dispose();
        }
    }
}