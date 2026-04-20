using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Scripting;

namespace Basis.ZeroMessenger
{
    [Preserve]
    public interface IMessagePublisher<T>
    {
        void Publish(T message, CancellationToken cancellationToken = default);
        ValueTask PublishAsync(T message, AsyncPublishStrategy publishStrategy = AsyncPublishStrategy.Parallel, CancellationToken cancellationToken = default);
    }
}
