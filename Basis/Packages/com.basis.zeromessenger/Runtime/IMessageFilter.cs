using Basis.ZeroMessenger.Internal;
using UnityEngine.Scripting;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.ZeroMessenger
{
    public interface IMessageFilterBase
    {
    }

    [Preserve]
    public interface IMessageFilter<T> : IMessageFilterBase
    {
        ValueTask InvokeAsync(T message, CancellationToken cancellationToken, Func<T, CancellationToken, ValueTask> next);
    }
}