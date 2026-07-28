using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Basis.Network.Core.Compression;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Assertions;
namespace Basis.Scripts.Networking.NetworkedAvatar
{
    [Serializable]
    public class BasisAvatarBuffer : IDisposable
    {
        public const int BoneCount = BasisBoneRotationCompression.SyncBoneCount; // 51 (excludes Hips, Eyes, Jaw)
        public byte Sequence;
        public double ServerTimeSeconds;
        // Position/Rotation carry the HIPS world pose (sent in the high-precision
        // 12+7 byte slots). Root world is derived on the receiver from this pose
        // plus the local deltas below — so the visually anchored bone gets the
        // best precision and the server reduction system reads hips for distance.
        public quaternion Rotation = quaternion.identity;
        public float3 Scale = new float3(1f, 1f, 1f);
        public float3 Position = new float3(0f, 0f, 0f);
        // Hips local-position delta vs the avatar's TPose hips local position.
        // Combined with the network hips world pose, lets the receiver derive
        // both the root world transform and the hips bone's local transform.
        public float3 HipsLocalDelta = float3.zero;
        // Hips local-rotation delta vs the avatar's TPose hips local rotation.
        // Hips isn't in the bone-rotations packet (BONE_WRITE_ORDER excludes it),
        // so this carries hips orientation independent of root.
        // Encoded as inverse(tposeLocalRot) × currentLocalRot — applied as
        // hips.localRotation = tposeLocalRot × delta on the receiver.
        public quaternion HipsLocalRotation = quaternion.identity;
        /// <summary>
        /// 54 bone delta rotations (T-pose-relative, avatar-agnostic).
        /// Indexed by slot in BasisBoneRotationCompression.BONE_WRITE_ORDER.
        /// </summary>
        [System.NonSerialized] public NativeArray<quaternion> BoneRotations;
        public double SecondsInterval = 0.01;

        // End-effector anchoring (High quality only). Mask bit i => effector i is world-anchored
        // (0=LHand 1=RHand 2=LFoot 3=RFoot). Pos = hips-local target offset, Rot = tip world rotation.
        // Interpolated alongside the hips and applied by two-bone IK, which takes its pole from the FK
        // joint position rather than a networked swivel angle.
        public byte EffectorMask;
        public readonly float3[] EffectorPos = new float3[BasisAvatarEndEffectors.EffectorCount];
        public readonly quaternion[] EffectorRot = new quaternion[BasisAvatarEndEffectors.EffectorCount];

        public bool IsDisposed = false;

        // Pool internals (intrusive lock-free stack)
        internal BasisAvatarBuffer NextInPool;
        internal int PooledFlag; // 0 = not in pool, 1 = in pool

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureAllocated()
        {
            if (!BoneRotations.IsCreated || BoneRotations.Length != BoneCount)
            {
                if (BoneRotations.IsCreated)
                    BoneRotations.Dispose();

                BoneRotations = new NativeArray<quaternion>(BoneCount, Allocator.Persistent);
            }
        }

        /// <summary>
        /// Called when the buffer is checked OUT of the pool.
        /// Does defaults + ensures bone rotation array exists.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ResetForReuse()
        {
            if (IsDisposed)
                throw new ObjectDisposedException(nameof(BasisAvatarBuffer));

            EnsureAllocated();

            Sequence = 0;
            Rotation = quaternion.identity;
            Scale = new float3(1f, 1f, 1f);
            Position = new float3(0f, 0f, 0f);
            HipsLocalDelta = float3.zero;
            HipsLocalRotation = quaternion.identity;
            SecondsInterval = 0.01;
            EffectorMask = 0;
        }

        public void Dispose()
        {
            if (IsDisposed)
                return;

            if (BoneRotations.IsCreated)
            {
                BoneRotations.Dispose();
                BoneRotations = default;
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

            // Double-release detection via PooledFlag.
#if UNITY_ASSERTIONS
            if (Interlocked.Exchange(ref item.PooledFlag, 1) == 1)
            {
                UnityEngine.Debug.LogError("Double release detected for BasisAvatarBuffer (PooledFlag was already 1).");
                return;
            }
#else
            if (Interlocked.Exchange(ref item.PooledFlag, 1) == 1)
            {
                return;
            }
#endif

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
