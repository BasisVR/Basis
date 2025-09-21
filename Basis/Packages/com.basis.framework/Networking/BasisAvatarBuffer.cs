using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;

namespace Basis.Scripts.Networking.NetworkedAvatar
{
    [System.Serializable]
    public class BasisAvatarBuffer
    {
        public quaternion rotation = new quaternion(0, 0, 0, 1);
        public float3 Scale = new float3(1, 1, 1);
        public float3 Position = new float3(0, 0, 0);
        public NativeArray<float> Muscles;
        public double SecondsInterval = 0.01;

        // Initialize method for pooling
        public void Initialize()
        {
            if (Muscles == null || Muscles.IsCreated == false || Muscles.Length != 95)
            {
                if (Muscles != null && Muscles.IsCreated)
                {
                    Muscles.Dispose();
                }
                Muscles = new NativeArray<float>(95, Allocator.Persistent);
            }
            rotation = quaternion.identity;
            Scale = new float3(1f, 1f, 1f);
            Position = new float3(0f, 0f, 0f);
            SecondsInterval = 0.01;
        }
    }

    // Simple pool for BasisAvatarBuffer structs
    public static class BasisAvatarBufferPool
    {
        private static readonly Stack<BasisAvatarBuffer> _pool = new Stack<BasisAvatarBuffer>();

        public static BasisAvatarBuffer Get()
        {
            if (_pool.Count > 0)
            {
                var item = _pool.Pop();
                item.Initialize();
                return item;
            }

            var newItem = new BasisAvatarBuffer();
            newItem.Initialize();
            return newItem;
        }

        public static void Release(ref BasisAvatarBuffer item)
        {
            item.Initialize(); // reset before pooling
            _pool.Push(item);
            item = default; // optional: avoid accidental use after release
        }
        public static void DeInitalize()
        {
            while (_pool.Count > 0)
            {
                var dataset = _pool.Pop();
                if (dataset != null && dataset.Muscles != null)
                {
                    if (dataset.Muscles.IsCreated)
                    {
                        dataset.Muscles.Dispose();
                    }
                }
            }
        }
    }
}
