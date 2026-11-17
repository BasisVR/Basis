using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;
using UnityEngine.SceneManagement;
namespace Basis.Capture
{
    public sealed class BasisCaptureScene : IDisposable
    {
        public sealed class Item
        {
            public Transform transform;
            public Renderer renderer;
            public Light light;
            public string path, source;
            public int meshId = -1, firstFrame;
            public int[] materials = Array.Empty<int>();
            public bool staticBatched;
            public float4x4 last;
            public byte lastActive = 255;
            public float[] lastLight;
            public Matrix4x4 initial;
        }
        public readonly List<Item> items = new List<Item>();
        public readonly List<(Transform root, Animator animator, string source)> skinnedRoots = new List<(Transform, Animator, string)>();
        private readonly HashSet<GameObject> scannedRoots = new HashSet<GameObject>();
        private readonly HashSet<Transform> claimed = new HashSet<Transform>();
        private TransformAccessArray access;
        private NativeArray<float4x4> matrices;
        private bool dirty = true;
        public bool RegistryChanged;
        public int Scan(BasisCaptureAssets assets, int frame)
        {
            int added = 0;
            foreach (KeyValuePair<string, Scene> pair in BasisRuntimeSpawnRegistry.SpawnedScenes)
            {
                Scene scene = pair.Value;
                if (!scene.IsValid() || !scene.isLoaded) continue;
                foreach (GameObject go in scene.GetRootGameObjects()) added += ScanRoot(go, "scene:" + scene.name, assets, frame);
            }
            foreach (KeyValuePair<string, GameObject> pair in BasisRuntimeSpawnRegistry.SpawnedGameobjects) if (pair.Value != null) added += ScanRoot(pair.Value, "prop:" + pair.Key, assets, frame);
            if (added > 0) dirty = true;
            RegistryChanged = false;
            return added;
        }
        private int ScanRoot(GameObject rootObject, string source, BasisCaptureAssets assets, int frame)
        {
            if (!scannedRoots.Add(rootObject)) return 0;
            int added = 0;
            Transform root = rootObject.transform;
            foreach (SkinnedMeshRenderer smr in rootObject.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Animator animator = smr.GetComponentInParent<Animator>(true);
                Transform group = animator != null && animator.transform.IsChildOf(root) ? animator.transform : CommonRoot(smr, root);
                if (claimed.Contains(group)) continue;
                bool nested = false;
                foreach (Transform c in claimed) if (group.IsChildOf(c)) { nested = true; break; }
                if (nested) continue;
                claimed.Add(group);
                skinnedRoots.Add((group, animator != null && animator.transform == group ? animator : null, source));
            }
            Stack<Transform> stack = new Stack<Transform>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                Transform t = stack.Pop();
                if (claimed.Contains(t)) continue;
                if (t.TryGetComponent(out MeshRenderer mr) && t.TryGetComponent(out MeshFilter mf) && mf.sharedMesh != null)
                {
                    Item item = new Item { transform = t, renderer = mr, path = BasisCaptureActor.PathOf(root, t), source = source, staticBatched = mr.isPartOfStaticBatch, firstFrame = frame, initial = t.localToWorldMatrix };
                    item.meshId = assets.MeshFor(mr);
                    Material[] mats = mr.sharedMaterials;
                    item.materials = new int[mats.Length];
                    for (int i = 0; i < mats.Length; i++) item.materials[i] = assets.MaterialFor(mats[i]);
                    items.Add(item);
                    added++;
                }
                if (t.TryGetComponent(out Light light))
                {
                    items.Add(new Item { transform = t, light = light, path = BasisCaptureActor.PathOf(root, t), source = source, firstFrame = frame, initial = t.localToWorldMatrix });
                    added++;
                }
                for (int i = t.childCount - 1; i >= 0; i--) stack.Push(t.GetChild(i));
            }
            return added;
        }
        private static Transform CommonRoot(SkinnedMeshRenderer smr, Transform root)
        {
            Transform candidate = smr.transform.parent != null ? smr.transform.parent : smr.transform;
            Transform[] bones = smr.bones;
            foreach (Transform b in bones)
            {
                if (b == null) continue;
                while (candidate != root && !b.IsChildOf(candidate)) candidate = candidate.parent;
            }
            return candidate;
        }
        public JobHandle Schedule()
        {
            if (dirty)
            {
                if (access.isCreated) access.Dispose();
                if (matrices.IsCreated) matrices.Dispose();
                Transform[] ts = new Transform[items.Count];
                for (int i = 0; i < ts.Length; i++) ts[i] = items[i].transform;
                access = new TransformAccessArray(ts);
                matrices = new NativeArray<float4x4>(ts.Length, Allocator.Persistent);
                dirty = false;
            }
            return items.Count == 0 ? default : new BasisCaptureWorldReadJob { output = matrices }.ScheduleReadOnly(access, 64);
        }
        public void Write(BasisCaptureBuffer buffer, int frame)
        {
            int start = buffer.BeginRecord(BasisCaptureFormat.RecordSceneTransforms, 0);
            int countAt = buffer.Reserve(4);
            int count = 0;
            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i];
                byte active;
                if (item.transform == null) active = 0;
                else if (item.renderer != null) active = (byte)(item.renderer != null && item.renderer.enabled && item.transform.gameObject.activeInHierarchy ? 1 : 0);
                else active = (byte)(item.light != null && item.light.enabled && item.transform.gameObject.activeInHierarchy ? 1 : 0);
                float4x4 m = item.transform != null ? matrices[i] : item.last;
                bool moved = item.lastActive == 255 || !m.Equals(item.last);
                if (!moved && active == item.lastActive) continue;
                item.last = m;
                item.lastActive = active;
                buffer.Int(i);
                buffer.Byte(active);
                for (int c = 0; c < 4; c++) { float4 col = m[c]; buffer.Float(col.x); buffer.Float(col.y); buffer.Float(col.z); buffer.Float(col.w); }
                count++;
            }
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer.Data.AsSpan(countAt), count);
            buffer.EndRecord(start);
            start = buffer.BeginRecord(BasisCaptureFormat.RecordLights, 0);
            countAt = buffer.Reserve(4);
            count = 0;
            for (int i = 0; i < items.Count; i++)
            {
                Light l = items[i].light;
                if (l == null) continue;
                Color c = l.color;
                float[] v = { l.enabled ? 1f : 0f, c.r, c.g, c.b, l.intensity, l.range, l.spotAngle, l.innerSpotAngle, l.colorTemperature, l.useColorTemperature ? 1f : 0f, (float)l.shadows, l.bounceIntensity };
                float[] p = items[i].lastLight;
                bool changed = p == null;
                for (int k = 0; !changed && k < v.Length; k++) changed = v[k] != p[k];
                if (!changed) continue;
                items[i].lastLight = v;
                buffer.Int(i);
                buffer.Int(v.Length);
                for (int k = 0; k < v.Length; k++) buffer.Float(v[k]);
                count++;
            }
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer.Data.AsSpan(countAt), count);
            buffer.EndRecord(start);
        }
        public void WriteManifest(BasisCaptureJson j)
        {
            j.BeginArray("sceneObjects");
            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i];
                j.BeginObject().Prop("index", i).Prop("name", item.transform != null ? item.transform.name : item.path).Prop("path", item.path).Prop("source", item.source).Prop("firstFrame", item.firstFrame).Prop("initialMatrix", item.initial);
                if (item.renderer != null || item.meshId >= 0)
                {
                    j.Prop("kind", "mesh").Prop("mesh", item.meshId).Prop("staticBatched", item.staticBatched).Name("materials").Ints(item.materials);
                    if (item.renderer != null) j.Prop("castShadows", item.renderer.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off).Prop("lightmapIndex", item.renderer.lightmapIndex).Prop("lightmapScaleOffset", item.renderer.lightmapScaleOffset);
                }
                else if (item.light != null)
                {
                    Light l = item.light;
                    j.Prop("kind", "light").Prop("lightType", l.type.ToString()).Prop("color", l.color).Prop("intensity", l.intensity).Prop("range", l.range).Prop("spotAngle", l.spotAngle).Prop("innerSpotAngle", l.innerSpotAngle).Prop("shadows", l.shadows.ToString()).Prop("useColorTemperature", l.useColorTemperature).Prop("colorTemperature", l.colorTemperature).Prop("bakeType", l.bakingOutput.isBaked ? l.bakingOutput.lightmapBakeType.ToString() : "Realtime");
                }
                else j.Prop("kind", "empty");
                j.EndObject();
            }
            j.EndArray();
        }
        public void Dispose()
        {
            if (access.isCreated) access.Dispose();
            if (matrices.IsCreated) matrices.Dispose();
        }
    }
}
