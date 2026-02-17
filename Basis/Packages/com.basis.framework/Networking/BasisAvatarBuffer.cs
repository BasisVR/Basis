using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Assertions;
namespace Basis.Scripts.Networking.NetworkedAvatar
{
    [Serializable]
    public class BasisAvatarBuffer : IDisposable
    {
        public const int MuscleCount = 95;
        public double ServerTimeSeconds;
        public quaternion Rotation = quaternion.identity;
        public float3 Scale = new float3(1f, 1f, 1f);
        public float3 Position = new float3(0f, 0f, 0f);
        public NativeArray<float> Muscles;
        public double SecondsInterval = 0.01;

        public bool IsDisposed = false;

        // Pool internals (intrusive lock-free stack)
        internal BasisAvatarBuffer NextInPool;
        internal int PooledFlag; // 0 = not in pool, 1 = in pool

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureAllocated()
        {
            if (!Muscles.IsCreated || Muscles.Length != MuscleCount)
            {
                if (Muscles.IsCreated)
                    Muscles.Dispose();

                Muscles = new NativeArray<float>(MuscleCount, Allocator.Persistent);
            }
        }

        /// <summary>
        /// Called when the buffer is checked OUT of the pool.
        /// Does defaults + ensures muscles exist.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ResetForReuse()
        {
            if (IsDisposed)
                throw new ObjectDisposedException(nameof(BasisAvatarBuffer));

            EnsureAllocated();

            Rotation = quaternion.identity;
            Scale = new float3(1f, 1f, 1f);
            Position = new float3(0f, 0f, 0f);
            SecondsInterval = 0.01;

            // IMPORTANT: do NOT clear muscles unless you actually need it.
            // If you need deterministic muscles, clear explicitly at the write site.
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
            // PooledFlag intentionally not reset; disposed objects should not be pooled.
        }
    }

    /// <summary>
    /// High-performance, lock-free, thread-safe pool for BasisAvatarBuffer.
    /// - Single reset per round-trip: buffers are reset on Get(), NOT on Release().
    /// - Editor/Dev-only invariants enforced with UnityEngine.Assertions.
    /// </summary>
    public static class BasisAvatarBufferPool
    {
        // Intrusive lock-free stack head.
        private static BasisAvatarBuffer _head;

        // Use Unity's assertion stripping (enabled in Editor/Development when UNITY_ASSERTIONS is defined).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void PoolAssert(bool condition, string message)
        {
#if UNITY_ASSERTIONS
            Assert.IsTrue(condition, message);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void PoolAssertNotNull(object obj, string message)
        {
#if UNITY_ASSERTIONS
            Assert.IsNotNull(obj, message);
#endif
        }

        /// <summary>
        /// Get a buffer from the pool or create a new one.
        /// Lock-free pop via CAS on the head pointer.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static BasisAvatarBuffer Get()
        {
            while (true)
            {
                var head = _head;

                if (head == null)
                {
                    var fresh = new BasisAvatarBuffer();
                    // Fresh buffers are not in the pool; PooledFlag default is 0.
                    fresh.ResetForReuse();
                    return fresh;
                }

                var next = head.NextInPool;

                // Try to pop: if _head == head, set it to next.
                if (Interlocked.CompareExchange(ref _head, next, head) == head)
                {
                    // Successfully popped. Detach from list.
                    head.NextInPool = null;

                    // Mark as out-of-pool.
                    Interlocked.Exchange(ref head.PooledFlag, 0);

                    // --- DEV/EDITOR invariants ---
                    PoolAssert(!head.IsDisposed, "Pool returned a disposed BasisAvatarBuffer. Disposed buffers must never be pooled.");
                    PoolAssert(head.NextInPool == null, "Popped BasisAvatarBuffer still has NextInPool set. Pool list corruption.");
                    PoolAssert(head.PooledFlag == 0, "Popped BasisAvatarBuffer still marked as pooled (PooledFlag != 0).");

                    // Single reset per round-trip happens here.
                    head.ResetForReuse();
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
            // --- DEV/EDITOR invariants ---
            PoolAssertNotNull(item, "Attempted to release a null BasisAvatarBuffer.");
#if !UNITY_ASSERTIONS
            // In non-assert builds, still avoid NRE.
            if (item == null) return;
#endif

            PoolAssert(!item.IsDisposed, "Attempted to release a disposed BasisAvatarBuffer. Do not pool disposed objects.");
            PoolAssert(item.NextInPool == null, "Releasing BasisAvatarBuffer with NextInPool already set. Possible double-release or corruption.");

            // Double-release detection: if it was already 1, it's already in the pool.
            if (Interlocked.Exchange(ref item.PooledFlag, 1) == 1)
            {
#if UNITY_ASSERTIONS
                UnityEngine.Debug.LogError("Double release detected for BasisAvatarBuffer (PooledFlag was already 1).");
#endif
                return;
            }

            // IMPORTANT:
            // Do NOT call item.Reset/EnsureAllocated here.
            // Reset happens once when checked OUT (Get), keeping Release cheap and avoiding "allocate on release".

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
            var head = Interlocked.Exchange(ref _head, null);

            while (head != null)
            {
                var next = head.NextInPool;
                head.NextInPool = null;

                // --- DEV/EDITOR invariants ---
                PoolAssert(head.PooledFlag == 1, "Deinitializing pool found a buffer not marked as pooled (PooledFlag != 1).");
                PoolAssert(!head.IsDisposed, "Deinitializing pool found a disposed buffer in the pool list.");

                head.Dispose();
                head = next;
            }
        }
    }
}
