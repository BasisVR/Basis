using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Basis.ZeroMessenger.Internal
{
    internal interface ILinkedPoolNode<T>
        where T : class
    {
        ref T NextNode { get; }
    }

    [StructLayout(LayoutKind.Auto)]
    internal struct LinkedPool<T> where T : class, ILinkedPoolNode<T>
{
    int gate;
    int size;
    T root;

    public int Size => size;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPop([MaybeNullWhen(false)] out T result)
    {
        if (Interlocked.CompareExchange(ref gate, 1, 0) == 0)
        {
            var v = root;
            if (v is not null)
            {
                ref var nextNode = ref v.NextNode;
                root = nextNode;
                nextNode = null;
                size--;
                result = v;
                Volatile.Write(ref gate, 0);
                return true;
            }

            Volatile.Write(ref gate, 0);
        }

        result = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPush(T item)
    {
        if (Interlocked.CompareExchange(ref gate, 1, 0) == 0)
        {
            item.NextNode = root;
            root = item;
            size++;
            Volatile.Write(ref gate, 0);
            return true;
        }
        return false;
    }
}
}