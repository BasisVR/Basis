using System.Collections.Generic;
using Unity.Mathematics;

namespace Basis.Scripts.Networking.NetworkedAvatar
{
    [System.Serializable]
    public struct BasisAvatarBuffer
    {
        public quaternion rotation;
        public float3 Scale;
        public float3 Position;
        public float[] Muscles; // structs can't have default initialization for arrays
        public double SecondsInterval;

        // Initialize method for pooling
        public void Initialize()
        {
            if (Muscles == null || Muscles.Length != 95)
            {
                Muscles = new float[95];
            }
            rotation = quaternion.identity;
            Scale = new float3(1f, 1f, 1f);
            Position = new float3(0f, 0f, 0f);
            SecondsInterval = 0;
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
    }
}
