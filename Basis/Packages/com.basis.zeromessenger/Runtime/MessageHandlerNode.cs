using System;
using System.Threading;
using Basis.ZeroMessenger.Internal;

namespace Basis.ZeroMessenger
{
    public abstract class MessageHandlerNode<T> : IDisposable
    {
        internal MessageHandlerList<T> Parent = default!;
        internal MessageHandlerNode<T>? PreviousNode;
        internal MessageHandlerNode<T>? NextNode;
        internal ulong Version;

        bool disposed;
        public bool IsDisposed => disposed;

        public void Dispose()
        {
            ThrowHelper.ThrowObjectDisposedIf(IsDisposed, typeof(MessageHandlerNode<T>));

            if (Parent != null)
            {
                Parent.Remove(this);
                Volatile.Write(ref Parent!, null);
            }

            Volatile.Write(ref disposed, true);

            DisposeCore();
        }

        protected virtual void DisposeCore() { }
    }
}
