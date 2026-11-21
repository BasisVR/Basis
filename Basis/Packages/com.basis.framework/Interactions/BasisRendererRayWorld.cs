using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
namespace Basis.Scripts.BasisSdk.Interactions
{
    [NoAutoStaticsCleanup]
    public static class BasisRendererRayWorld
    {
        private static readonly Dictionary<Renderer, int> references = new Dictionary<Renderer, int>();
        private static readonly List<Renderer> dead = new List<Renderer>();
        private static BasisRendererRayBatch batch;
        private static bool hooked;
        private static int builtFrame = -1;
        public static BasisRendererRayBatch Batch => batch;
        public static int RendererCount => references.Count;
        public static int References(Renderer renderer) => renderer != null && references.TryGetValue(renderer, out int count) ? count : 0;
        public static bool Acquire(Renderer renderer)
        {
            if (renderer == null)
            {
                return false;
            }
            if (references.TryGetValue(renderer, out int count))
            {
                references[renderer] = count + 1;
                return true;
            }
            batch ??= new BasisRendererRayBatch();
            if (!batch.Add(renderer))
            {
                if (references.Count == 0)
                {
                    Shutdown();
                }
                return false;
            }
            references[renderer] = 1;
            if (!hooked)
            {
                hooked = true;
                Application.quitting += Shutdown;
            }
            return true;
        }
        public static void Release(Renderer renderer)
        {
            if (ReferenceEquals(renderer, null) || !references.TryGetValue(renderer, out int count))
            {
                return;
            }
            if (count > 1)
            {
                references[renderer] = count - 1;
                return;
            }
            references.Remove(renderer);
            batch?.Remove(renderer);
            if (references.Count == 0)
            {
                Shutdown();
            }
        }
        public static JobHandle Build(bool allowRefit = true)
        {
            if (batch == null)
            {
                return default;
            }
            Sweep();
            builtFrame = Time.frameCount;
            return batch.Build(default, allowRefit);
        }
        public static JobHandle BuildOncePerFrame() => builtFrame == Time.frameCount || batch == null ? default : Build();
        public static JobHandle BuildFor(NativeArray<float3> origins, NativeArray<float3> directions, int count, float maxDistance)
        {
            if (batch == null)
            {
                return default;
            }
            Sweep();
            return batch.Build(default, true, origins, directions, count, maxDistance);
        }
        public static JobHandle BuildFor(NativeArray<BasisRendererRay> rays)
        {
            if (batch == null)
            {
                return default;
            }
            Sweep();
            return batch.Build(default, true, rays);
        }
        private static void Sweep()
        {
            foreach (KeyValuePair<Renderer, int> pair in references)
            {
                if (pair.Key == null)
                {
                    dead.Add(pair.Key);
                }
            }
            for (int i = 0; i < dead.Count; i++)
            {
                references.Remove(dead[i]);
            }
            dead.Clear();
        }
        private static void Shutdown()
        {
            batch?.Dispose();
            batch = null;
            references.Clear();
            builtFrame = -1;
            if (hooked)
            {
                hooked = false;
                Application.quitting -= Shutdown;
            }
        }
    }
}
