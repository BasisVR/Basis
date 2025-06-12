using Basis.Scripts.Networking.NetworkedAvatar;
using System.Collections.Generic;

public static class BasisAvatarBufferPool
{
    // Internal pool to hold reusable AvatarBuffer objects
    private static Stack<BasisAvatarBuffer> pool;
    private static readonly object lockObject = new object();
    public static bool Initialized { get; private set; } = false;

    // Initialize the pool with a given capacity
    public static void AvatarBufferPool(int initialCapacity = 10)
    {
        if (!Initialized)
        {
            lock (lockObject)
            {
                if (!Initialized) // Double-check locking for thread safety
                {
                    pool = new Stack<BasisAvatarBuffer>(initialCapacity);
                    Initialized = true;
                    // Prepopulate the pool with default AvatarBuffer objects
                    for (int i = 0; i < initialCapacity; i++)
                    {
                        pool.Push(CreateDefaultAvatarBuffer());
                    }
                }
            }
        }
    }

    // Method to fetch an AvatarBuffer from the pool
    public static BasisAvatarBuffer Rent()
    {
        lock (lockObject)
        {
            if (pool.Count > 0)
            {
                return pool.Pop();
            }
        }

        // If the pool is empty, create a new default AvatarBuffer (outside the lock for better performance)
        return CreateDefaultAvatarBuffer();
    }

    // Method to return an AvatarBuffer back to the pool
    public static void Return(BasisAvatarBuffer avatarBuffer)
    {
        lock (lockObject)
        {
            pool.Push(avatarBuffer);
        }
    }

    // Helper method to create a default AvatarBuffer
    private static BasisAvatarBuffer CreateDefaultAvatarBuffer()
    {
        return new BasisAvatarBuffer();
    }

    // Method to clear the pool if needed
    public static void Clear()
    {
        lock (lockObject)
        {
            pool.Clear();
        }
    }

    // Property to get the current count of items in the pool
    public static int Count
    {
        get
        {
            lock (lockObject)
            {
                return pool.Count;
            }
        }
    }
}