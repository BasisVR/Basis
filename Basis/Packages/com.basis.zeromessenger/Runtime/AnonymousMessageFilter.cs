using System;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.ZeroMessenger
{
    internal sealed class AnonymousMessageFilter<T> : IMessageFilter<T>
    {
        private readonly Func<T, CancellationToken, Func<T, CancellationToken, ValueTask>, ValueTask> _filter;

        public AnonymousMessageFilter(Func<T, CancellationToken, Func<T, CancellationToken, ValueTask>, ValueTask> filter)
        {
            _filter = filter;
        }

        public ValueTask InvokeAsync(T message, CancellationToken cancellationToken, Func<T, CancellationToken, ValueTask> next)
        {
            return _filter(message, cancellationToken, next);
        }
    }
}