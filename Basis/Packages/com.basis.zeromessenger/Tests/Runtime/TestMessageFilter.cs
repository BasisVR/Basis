
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.ZeroMessenger.Tests
{
    public class TestMessageFilter<T> : IMessageFilter<T>
    {
        private readonly Func<T, T> preprocess;

        public TestMessageFilter(Func<T, T> preprocess)
        {
            this.preprocess = preprocess;
        }

        public async ValueTask InvokeAsync(T message, CancellationToken cancellationToken, Func<T, CancellationToken, ValueTask> next)
        {
            message = preprocess(message);
            await next(message, cancellationToken);
        }
    }

    public class IgnoreMessageFilter<T> : IMessageFilter<T>
    {
        public ValueTask InvokeAsync(T message, CancellationToken cancellationToken, Func<T, CancellationToken, ValueTask> next)
        {
            // ignore message
            return default;
        }
    }
    
}
