using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Scripting;

namespace Basis.ZeroMessenger.Internal
{
    [Preserve]
    public sealed class MessageBrokerPublisher<T> : IMessagePublisher<T>
    {
        readonly MessageBroker<T> messageBroker;

        [Preserve]
        public MessageBrokerPublisher(MessageBroker<T> messageBroker)
        {
            this.messageBroker = messageBroker;
        }

        public void Publish(T message, CancellationToken cancellationToken = default)
        {
            messageBroker.Publish(message, cancellationToken);
        }

        public ValueTask PublishAsync(T message, AsyncPublishStrategy publishStrategy = AsyncPublishStrategy.Parallel, CancellationToken cancellationToken = default)
        {
            return messageBroker.PublishAsync(message, publishStrategy, cancellationToken);
        }
    }

    [Preserve]
    public sealed class MessageBrokerSubscriber<T> : IMessageSubscriber<T>
    {
        readonly MessageBroker<T> messageBroker;

        [Preserve]
        public MessageBrokerSubscriber(MessageBroker<T> messageBroker)
        {
            this.messageBroker = messageBroker;
        }

        public IDisposable Subscribe(MessageHandler<T> handler)
        {
            return messageBroker.Subscribe(handler);
        }

        public IDisposable SubscribeAwait(AsyncMessageHandler<T> handler, AsyncSubscribeStrategy subscribeStrategy = AsyncSubscribeStrategy.Sequential)
        {
            return messageBroker.SubscribeAwait(handler, subscribeStrategy);
        }
    }
}