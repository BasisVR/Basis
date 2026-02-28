using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    public sealed class BasisShadowCloneBlendshapeSync : System.IDisposable
    {
        public SkinnedMeshRenderer Source;
        public SkinnedMeshRenderer Clone;

        public int Count;

        public NativeArray<float> Current;     // TempJob each frame OR Persistent
        public NativeArray<float> Previous;    // Persistent
        public NativeArray<byte> ChangedMask;  // Persistent

        public JobHandle Handle;

        public BasisShadowCloneBlendshapeSync(SkinnedMeshRenderer source, SkinnedMeshRenderer clone, int count)
        {
            Source = source;
            Clone = clone;
            Count = count;

            Previous = new NativeArray<float>(count, Allocator.Persistent);
            ChangedMask = new NativeArray<byte>(count, Allocator.Persistent);

            // Current can be TempJob per-frame if you prefer; I keep it persistent here to reduce alloc churn
            Current = new NativeArray<float>(count, Allocator.Persistent);

            // initialize Previous to current initial weights
            for (int i = 0; i < count; i++)
                Previous[i] = source.GetBlendShapeWeight(i);
        }

        public void Dispose()
        {
            if (Handle.IsCompleted == false) Handle.Complete();
            if (Current.IsCreated) Current.Dispose();
            if (Previous.IsCreated) Previous.Dispose();
            if (ChangedMask.IsCreated) ChangedMask.Dispose();
        }
    }
}
