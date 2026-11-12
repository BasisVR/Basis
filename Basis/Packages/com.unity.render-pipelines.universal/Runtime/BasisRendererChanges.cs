using System;
using System.Collections.Generic;
using Unity.Collections;
using ObjectDispatcher = UnityEngine.InternalBridge.ObjectDispatcher;

namespace UnityEngine.Rendering.Universal
{
    public sealed class BasisRendererChanges : IDisposable
    {
        readonly ObjectDispatcher dispatcher = new ObjectDispatcher();

        public BasisRendererChanges()
        {
            dispatcher.maxDispatchHistoryFramesCount = int.MaxValue;
            dispatcher.EnableTypeTracking<MeshRenderer>(ObjectDispatcher.TypeTrackingFlags.SceneObjects);
            dispatcher.EnableTypeTracking<SkinnedMeshRenderer>(ObjectDispatcher.TypeTrackingFlags.SceneObjects);
            dispatcher.EnableTypeTracking<Material>(ObjectDispatcher.TypeTrackingFlags.SceneObjects | ObjectDispatcher.TypeTrackingFlags.Assets);
            dispatcher.EnableTypeTracking<Light>(ObjectDispatcher.TypeTrackingFlags.SceneObjects);
        }

        public void Collect(List<Renderer> renderers, List<Material> materials, List<Light> lights)
        {
            Drain<MeshRenderer, Renderer>(renderers);
            Drain<SkinnedMeshRenderer, Renderer>(renderers);
            Drain<Material, Material>(materials);
            Drain<Light, Light>(lights);
        }

        void Drain<T, U>(List<U> into) where T : Object where U : Object
        {
            using var data = dispatcher.GetTypeChangesAndClear<T>(Allocator.Temp);
            Object[] changed = data.changed;
            if (changed == null) { return; }
            foreach (Object o in changed) { if (o is U u) { into.Add(u); } }
        }

        public void Dispose() => dispatcher.Dispose();
    }
}
