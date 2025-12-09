using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Scripting;

namespace Basis.ZeroMessenger
{
    [Preserve]
    public sealed class PredicateFilter<T> : IMessageFilter<T>
    {
        private readonly Predicate<T> _predicate;

        public PredicateFilter(Predicate<T> predicate)
        {
            _predicate = predicate;
        }

        public ValueTask InvokeAsync(T message, CancellationToken cancellationToken, Func<T, CancellationToken, ValueTask> next)
        {
            if (_predicate(message))
            {
                return next(message, cancellationToken);
            }
            else
            {
                return default;
            }
        }
    }
}
