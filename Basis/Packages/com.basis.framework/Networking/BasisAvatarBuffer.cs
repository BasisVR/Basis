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
        public const int FingerCount = BasisBoneRotationCompression.FingerChannelCount; // 10 curl/splay pairs
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
        // Hips rotation away from the sender's TPose hips, in the RIG-NEUTRAL generic space.
        // Hips isn't in the bone-rotations packet (BONE_WRITE_ORDER excludes it),
        // so this carries hips orientation independent of root.
        // Applied as hips.localRotation = decodePre × this × decodePost on the receiver, with the
        // pair built from the RECEIVING avatar's own rest pose — see BasisGenericBoneRotation.
        public quaternion HipsLocalRotation = quaternion.identity;
        /// <summary>
        /// 51 bone rotations in the RIG-NEUTRAL generic space: each joint's rotation away from its
        /// own rest pose, expressed in the CHARACTER's axes rather than in the sending rig's bone
        /// axes. That is what makes them genuinely avatar-agnostic — the receiver rebuilds its own
        /// rig's local rotations from them using only its own rest data, so a pose can be replayed
        /// on an avatar other than the one that produced it. See BasisGenericBoneRotation.
        /// Indexed by slot in BasisBoneRotationCompression.BONE_WRITE_ORDER.
        /// </summary>
        [System.NonSerialized] public NativeArray<quaternion> BoneRotations;

        /// <summary>
        /// Ten curl/splay pairs, ordered L thumb→little then R thumb→little — the twenty scalars
        /// that replaced slots 21..50 in v47. Filled by the decompressor on whichever thread the
        /// packet arrived on; the grid expansion into BoneRotations[21..50] happens later, on the
        /// frame path, because the pose grid belongs to the receiving avatar and its lifetime is
        /// only safe to touch there.
        /// </summary>
        [System.NonSerialized] public NativeArray<float2> FingerPercentages;

        public double SecondsInterval = 0.01;

        // End-effector anchoring (High quality only). Mask bit i => effector i is world-anchored
        // (0=LHand 1=RHand 2=LFoot 3=RFoot). Pos = hips-local target offset, Rot = tip world rotation.
        // Interpolated alongside the hips and applied by two-bone IK, which takes its pole from the FK
        // joint position rather than a networked swivel angle.
        public byte EffectorMask;
        public readonly float3[] EffectorPos = new float3[BasisAvatarEndEffectors.EffectorCount];
        public readonly quaternion[] EffectorRot = new quaternion[BasisAvatarEndEffectors.EffectorCount];

        /// <summary>
        /// Which avatar generation the finger slots of <see cref="BoneRotations"/> were expanded
        /// against. 0 = not expanded. Compared with BasisRemoteAvatarDriver.HandGridGeneration so a
        /// buffer that survives an avatar swap re-expands through the new rig's grid rather than
        /// keeping rotations proportioned for the old one.
        /// </summary>
        [System.NonSerialized] public int FingerExpansionGeneration;

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

            if (!FingerPercentages.IsCreated || FingerPercentages.Length != FingerCount)
            {
                if (FingerPercentages.IsCreated)
                    FingerPercentages.Dispose();

                FingerPercentages = new NativeArray<float2>(FingerCount, Allocator.Persistent);
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
            FingerExpansionGeneration = 0;
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

            if (FingerPercentages.IsCreated)
            {
                FingerPercentages.Dispose();
                FingerPercentages = default;
            }

            IsDisposed = true;
            NextInPool = null;
            // PooledFlag intentionally not reset; disposed objects should not be pooled.
        }
    }

    /// <summary>
    /// Thread-safe pool for BasisAvatarBuffer.
    /// - Single reset per round-trip: buffers are reset on Get(), NOT on Release().
    /// - Editor/Dev-only invariants enforced with UnityEngine.Assertions.
    /// - The list is guarded by a monitor rather than a CAS on the head. The lock-free form was a
    ///   Treiber stack with no version tag: a pop that read head/head.NextInPool and was then
    ///   preempted while another thread popped both and pushed the first back had its CAS succeed
    ///   against the recycled head and installed a CHECKED-OUT buffer as the list head. That hands
    ///   one buffer to two receivers, orphans the rest of the free list, and lets Deinitialize
    ///   Dispose() the persistent NativeArrays of a buffer a Burst job still reads.
    /// </summary>
    public static class BasisAvatarBufferPool
    {
        // Intrusive stack head; only ever read or written under _gate.
        private static BasisAvatarBuffer _head;
        private static readonly object _gate = new object();

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
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static BasisAvatarBuffer Get()
        {
            BasisAvatarBuffer head;
            lock (_gate)
            {
                head = _head;
                if (head != null)
                {
                    _head = head.NextInPool;
                    head.NextInPool = null;
                    Interlocked.Exchange(ref head.PooledFlag, 0);
                }
            }

            if (head == null)
            {
                var fresh = new BasisAvatarBuffer();
                // Fresh buffers are not in the pool; PooledFlag default is 0.
                fresh.ResetForReuse();
                return fresh;
            }

            // --- DEV/EDITOR invariants ---
            PoolAssert(!head.IsDisposed, "Pool returned a disposed BasisAvatarBuffer. Disposed buffers must never be pooled.");
            PoolAssert(head.NextInPool == null, "Popped BasisAvatarBuffer still has NextInPool set. Pool list corruption.");
            PoolAssert(head.PooledFlag == 0, "Popped BasisAvatarBuffer still marked as pooled (PooledFlag != 0).");

            // Single reset per round-trip happens here. Outside the lock: the buffer is detached and
            // exclusively owned by this caller, and ResetForReuse can allocate.
            head.ResetForReuse();
            return head;
        }

        /// <summary>
        /// Return a buffer to the pool.
        /// Double-release detection via PooledFlag.
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

            lock (_gate)
            {
                item.NextInPool = _head;
                _head = item;
            }
        }

        /// <summary>
        /// Dispose all buffers in the pool and clear it.
        /// Caller must ensure no concurrent Get/Release while deinitializing.
        /// </summary>
        public static void Deinitialize()
        {
            BasisAvatarBuffer head;
            lock (_gate)
            {
                head = _head;
                _head = null;
            }

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
