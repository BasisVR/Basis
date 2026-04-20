using System;
using UnityEngine.Scripting;

namespace Basis.ZeroMessenger
{
    [Preserve]
    public interface IMessageSubscriber<T>
    {
        IDisposable Subscribe(MessageHandler<T> handler);
        IDisposable SubscribeAwait(AsyncMessageHandler<T> handler, AsyncSubscribeStrategy subscribeStrategy = AsyncSubscribeStrategy.Sequential);
    }
}