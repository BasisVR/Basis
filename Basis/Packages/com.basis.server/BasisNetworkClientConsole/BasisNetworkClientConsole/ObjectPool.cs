namespace Basis
{
    partial class Program
    {
        // Object pool for byte arrays to avoid allocation during runtime
        private class ObjectPool<T>
        {
            private readonly Func<T> createFunc;
            private readonly Stack<T> pool;

            public ObjectPool(Func<T> createFunc)
            {
                this.createFunc = createFunc;
                this.pool = new Stack<T>();
            }

            public T Get()
            {
                return pool.Count > 0 ? pool.Pop() : createFunc();
            }

            public void Return(T item)
            {
                pool.Push(item);
            }
        }
    }
}
