using System.Collections.Generic;
using static Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkSendBase;

public static class BasisAvatarBufferPool
{
    // Internal pool to hold reusable AvatarBuffer objects
    public static Stack<AvatarBuffer> pool;
    public static bool Initialized = false;
    // Constructor to initialize the pool with a given capacity
    public static void AvatarBufferPool(int initialCapacity = 10)
    {
        if (Initialized == false)
        {
            pool = new Stack<AvatarBuffer>(initialCapacity);
            Initialized = true;
            // Prepopulate the pool with default AvatarBuffer objects
            for (int i = 0; i < initialCapacity; i++)
            {
                pool.Push(CreateDefaultAvatarBuffer());
            }
        }
    }

    // Method to fetch an AvatarBuffer from the pool
    public static AvatarBuffer Rent()
    {
        if (pool.Count > 0)
        {
            return pool.Pop();
        }

        // If the pool is empty, create a new default AvatarBuffer
        return CreateDefaultAvatarBuffer();
    }

    // Method to return an AvatarBuffer back to the pool
    public static void Return(AvatarBuffer avatarBuffer)
    {
        pool.Push(avatarBuffer);
    }

    // Helper method to create a default AvatarBuffer
    private static AvatarBuffer CreateDefaultAvatarBuffer()
    {
        return new AvatarBuffer();
    }

    // Method to clear the pool if needed
    public static void Clear()
    {
        pool.Clear();
    }

    // Property to get the current count of items in the pool
    public static int Count => pool.Count;
}