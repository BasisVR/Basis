using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine; // only for optional Debug.LogWarning

namespace Basis.Scripts.Networking.NetworkedAvatar
{
    [Serializable]
    public class BasisAvatarBuffer : IDisposable
    {
        public const int MuscleCount = 95;

        public quaternion Rotation = quaternion.identity;
        public float3 Scale = new float3(1, 1, 1);
        public float3 Position = new float3(0, 0, 0);
        public NativeArray<float> Muscles;
        public double SecondsInterval = 0.01;

        private bool _disposed;

        /// <summary>Ensure NativeArray is allocated correctly; does not clear existing values.</summary>
        public void EnsureAllocated()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BasisAvatarBuffer));

            if (!Muscles.IsCreated || Muscles.Length != MuscleCount)
            {
                if (Muscles.IsCreated) Muscles.Dispose();
                Muscles = new NativeArray<float>(MuscleCount, Allocator.Persistent); // values are zeroed by default
            }
        }

        /// <summary>Reset fields to defaults. Optionally zero the muscle array.</summary>
        public void Reset(bool clearMuscles = false)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BasisAvatarBuffer));

            EnsureAllocated();

            Rotation = quaternion.identity;
            Scale = new float3(1f, 1f, 1f);
            Position = new float3(0f, 0f, 0f);
            SecondsInterval = 0.01;

            if (clearMuscles)
            {
                // Simple safe clear; if you need speed, switch to UnsafeUtility.MemClear.
                for (int i = 0; i < Muscles.Length; i++) Muscles[i] = 0f;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (Muscles.IsCreated) Muscles.Dispose();
            _disposed = true;
        }
    }

    /// <summary>Thread-safe pool for BasisAvatarBuffer (class). Guards null + double-release.</summary>
    public static class BasisAvatarBufferPool
    {
        private static readonly Stack<BasisAvatarBuffer> _pool = new Stack<BasisAvatarBuffer>();
        private static readonly HashSet<BasisAvatarBuffer> _inPool = new HashSet<BasisAvatarBuffer>(); // detect double-release
        private static readonly object _lock = new object();

        public static BasisAvatarBuffer Get()
        {
            lock (_lock)
            {
                while (_pool.Count > 0)
                {
                    var item = _pool.Pop();
                    if (item == null) continue;               // skip any stray nulls
                    _inPool.Remove(item);                      // now “checked out”
                    item.Reset(clearMuscles: false);           // re-init cheap stuff; skip clearing muscles unless you need to
                    return item;
                }
            }

            // Nothing available; create fresh.
            var fresh = new BasisAvatarBuffer();
            fresh.Reset(clearMuscles: false);
            return fresh;
        }
        public static void Release(BasisAvatarBuffer item)
        {
            if (item == null)
            {
                BasisDebug.LogError("released Avatar Buffer was null");
                return; // don’t push nulls
            }
            lock (_lock)
            {
                if (_inPool.Contains(item))
                {
                    // Optional: log once to help catch double-release bugs.
                    // Debug.LogWarning("BasisAvatarBuffer double-release detected.");
                    return;
                }

                // Reset lightweight fields; keep the persistent Muscles allocation.
                item.Reset(clearMuscles: false);

                _pool.Push(item);
                _inPool.Add(item);
            }
        }

        public static void Deinitialize()
        {
            lock (_lock)
            {
                while (_pool.Count > 0)
                {
                    var item = _pool.Pop();
                    if (item != null) item.Dispose();
                }
                _inPool.Clear();
            }
        }
    }
}
