using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Unity.Collections;
using Unity.Mathematics;

namespace Basis.Scripts.Networking.NetworkedAvatar
{
    [Serializable]
    public class BasisAvatarBuffer : IDisposable
    {
        public const int MuscleCount = 95;

        public quaternion Rotation = quaternion.identity;
        public float3 Scale = new float3(1f, 1f, 1f);
        public float3 Position = new float3(0f, 0f, 0f);
        public NativeArray<float> Muscles;
        public double SecondsInterval = 0.01;

        public bool IsDisposed = false;

        // Pool internals (intrusive lock-free stack)
        internal BasisAvatarBuffer NextInPool;
        internal int PooledFlag; // 0 = not in pool, 1 = in pool

        /// <summary>
        /// Ensure NativeArray is allocated with correct size; does not clear existing values.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureAllocated()
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(BasisAvatarBuffer));
            }

            if (!Muscles.IsCreated || Muscles.Length != MuscleCount)
            {
                if (Muscles.IsCreated)
                {
                    Muscles.Dispose();
                }

                Muscles = new NativeArray<float>(MuscleCount, Allocator.Persistent);
            }
        }

        /// <summary>
        /// Reset fields to defaults. Optionally zero the muscle array.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Reset(bool clearMuscles = false)
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(BasisAvatarBuffer));
            }

            EnsureAllocated();

            Rotation = quaternion.identity;
            Scale = new float3(1f, 1f, 1f);
            Position = new float3(0f, 0f, 0f);
            SecondsInterval = 0.01;

            if (clearMuscles && Muscles.IsCreated)
            {
                // Simple safe clear; switch to UnsafeUtility.MemClear if you need even more speed.
                for (int i = 0; i < Muscles.Length; i++)
                {
                    Muscles[i] = 0f;
                }
            }
        }

        public void Dispose()
        {
            if (IsDisposed)
                return;

            if (Muscles.IsCreated)
            {
                Muscles.Dispose();
                Muscles = default;
            }

            IsDisposed = true;
            NextInPool = null;
            // PooledFlag intentionally not reset here; disposed objects shouldn't be pooled again.
        }
    }

    /// <summary>
    /// High-performance, lock-free, thread-safe pool for BasisAvatarBuffer.
    /// </summary>
    public static class BasisAvatarBufferPool
    {
        // Intrusive lock-free stack: head points directly to a BasisAvatarBuffer,
        // which contains NextInPool to form a singly-linked list.
        private static BasisAvatarBuffer _head;

        /// <summary>
        /// Get a buffer from the pool or create a new one.
        /// Lock-free using CAS on the head pointer.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static BasisAvatarBuffer Get()
        {
            while (true)
            {
                var head = _head;

                if (head == null)
                {
                    // Pool empty: allocate a fresh buffer.
                    var fresh = new BasisAvatarBuffer();
                    fresh.Reset(clearMuscles: false);
                    return fresh;
                }

                var next = head.NextInPool;

                // Try to pop: if _head == head, set it to next.
                if (Interlocked.CompareExchange(ref _head, next, head) == head)
                {
                    head.NextInPool = null;

                    // Mark as out of pool.
                    Interlocked.Exchange(ref head.PooledFlag, 0);

                    head.Reset(clearMuscles: false);
                    return head;
                }

                // CAS failed due to contention – brief spin.
                Thread.SpinWait(1);
            }
        }

        /// <summary>
        /// Return a buffer to the pool.
        /// Double-release detection via PooledFlag; lock-free push via CAS.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Release(BasisAvatarBuffer item)
        {
            if (item == null)
            {
                BasisDebug.LogError("Released BasisAvatarBuffer was null.");
                return;
            }

            if (item.IsDisposed)
            {
                BasisDebug.LogError("Attempted to release a disposed BasisAvatarBuffer.");
                return;
            }

            // Double-release detection: if it was already 1, it's already in the pool.
            if (Interlocked.Exchange(ref item.PooledFlag, 1) == 1)
            {
                BasisDebug.LogError("Double release detected for BasisAvatarBuffer.");
                return;
            }

            item.Reset(clearMuscles: false);

            while (true)
            {
                var head = _head;
                item.NextInPool = head;

                // Try to push: if _head == head, set it to item.
                if (Interlocked.CompareExchange(ref _head, item, head) == head)
                {
                    return;
                }

                // CAS failed – another thread changed the head; retry.
                Thread.SpinWait(1);
            }
        }

        /// <summary>
        /// Dispose all buffers in the pool and clear it.
        /// Caller must ensure no concurrent Get/Release while deinitializing.
        /// </summary>
        public static void Deinitialize()
        {
            // Detach the current stack in one atomic op so new Get/Release calls
            // see a null head (or you just don't call them anymore after this).
            var head = Interlocked.Exchange(ref _head, null);

            while (head != null)
            {
                var next = head.NextInPool;
                head.NextInPool = null;
                head.Dispose();
                head = next;
            }
        }
    }
}
