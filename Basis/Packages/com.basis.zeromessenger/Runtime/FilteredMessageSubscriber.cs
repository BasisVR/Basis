using System;

namespace Basis.ZeroMessenger
{
    internal sealed class FilteredMessageSubscriber<TMessage> : IMessageSubscriber<TMessage>
    {
        public IMessageSubscriber<TMessage> Subscriber { get; }
        public IMessageFilter<TMessage>[] Filters { get; }

        public FilteredMessageSubscriber(IMessageSubscriber<TMessage> subscriber, IMessageFilter<TMessage>[] filters)
        {
            Subscriber = subscriber;
            Filters = filters;
        }

        public IDisposable Subscribe(MessageHandler<TMessage> handler)
        {
            return Subscriber.SubscribeAwait(new FilteredMessageHandler<TMessage>(handler, Filters));
        }

        public IDisposable SubscribeAwait(AsyncMessageHandler<TMessage> handler, AsyncSubscribeStrategy subscribeStrategy = AsyncSubscribeStrategy.Sequential)
        {
            return Subscriber.SubscribeAwait(new FilteredAsyncMessageHandler<TMessage>(handler, Filters), subscribeStrategy);
        }
    }
}