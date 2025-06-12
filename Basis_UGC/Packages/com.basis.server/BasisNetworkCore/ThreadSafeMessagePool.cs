using System.Collections.Concurrent;

namespace BasisNetworkCore
{
    public static class ThreadSafeMessagePool<T> where T : new()
    {
        private static readonly ConcurrentStack<T> pool = new ConcurrentStack<T>();
        private static readonly int maxPoolSize = 500; // Maximum allowed size of the pool

        public static T Rent()
        {
            if (pool.TryPop(out T obj))
            {
                return obj;
            }
            return new T();
        }

        public static void Return(T obj)
        {
            if (pool.Count < maxPoolSize) // Check if the pool size is within the limit
            {
                pool.Push(obj);
            }
        }
    }
}
