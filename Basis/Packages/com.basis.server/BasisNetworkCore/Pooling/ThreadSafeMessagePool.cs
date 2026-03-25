using System.Collections.Concurrent;

namespace BasisNetworkCore.Pooling
{
    public static class ThreadSafeMessagePool<T> where T : new()
    {
        private static readonly ConcurrentQueue<T> pool = new();
        private const int MaxPoolSize = 500;

        public static T Rent()
        {
            return pool.TryDequeue(out T obj) ? obj : new T();
        }

        public static void Return(T obj)
        {
            if (pool.Count < MaxPoolSize)
            {
                pool.Enqueue(obj);
            }
        }
    }
}
