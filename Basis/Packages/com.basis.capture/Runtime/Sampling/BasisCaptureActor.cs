using GatorDragonGames.JigglePhysics;
using System;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Jobs;
namespace Basis.Capture
{
    public sealed class BasisCaptureActor : IDisposable
    {
        public sealed class RendererInfo
        {
            public Renderer renderer;
            public SkinnedMeshRenderer skinned;
            public int transformIndex, meshId = -1, rootBone = -1, shapeCount, shapeOffset;
            public int[] materials = Array.Empty<int>(), bones = Array.Empty<int>();
            public string[] shapeNames = Array.Empty<string>();
            public bool staticBatched, isSkinned;
        }
        public int id, startFrame, endFrame = -1;
        public string kind, uuid, displayName, avatarUrl, avatarVersion, avatarName;
        public ushort netId;
        public bool isLocal, humanoid;
        public Transform root;
        public UnityEngine.Object owner;
        public Transform[] bones = Array.Empty<Transform>();
        public int[] parents = Array.Empty<int>();
        public string[] paths = Array.Empty<string>();
        public float[] rest = Array.Empty<float>();
        public int[] humanBones = Array.Empty<int>();
        public List<int> jiggleBones = new List<int>();
        public List<RendererInfo> renderers = new List<RendererInfo>();
        public int hierarchyCount;
        private TransformAccessArray access;
        private NativeArray<float> current;
        private float[] previous = Array.Empty<float>(), shapes = Array.Empty<float>(), previousShapes = Array.Empty<float>();
        private bool[] enabled = Array.Empty<bool>();
        private Dictionary<Transform, int> lookup = new Dictionary<Transform, int>();
        public HashSet<Renderer> hiddenRenderers = new HashSet<Renderer>();
        private bool havePrevious;
        public bool Alive => root != null && owner != null;
        public static bool Skip(Transform t, UnityEngine.Object ownerContent)
        {
            if (t.name.EndsWith("_ShadowOnly", StringComparison.Ordinal)) return true;
            return t.TryGetComponent(out BasisContentBase content) && !ReferenceEquals(content, ownerContent);
        }
        public void Build(Transform actorRoot, UnityEngine.Object ownerContent, Animator animator, IList<JiggleRig> jiggleRigs, BasisCaptureAssets assets, Func<Transform, bool> exclude = null)
        {
            root = actorRoot;
            owner = ownerContent != null ? ownerContent : actorRoot;
            hierarchyCount = actorRoot.hierarchyCount;
            HashSet<Transform> wanted = new HashSet<Transform>();
            List<Renderer> found = new List<Renderer>();
            Stack<Transform> stack = new Stack<Transform>();
            stack.Push(actorRoot);
            while (stack.Count > 0)
            {
                Transform t = stack.Pop();
                if (t != actorRoot && (Skip(t, ownerContent) || (exclude != null && exclude(t)))) continue;
                if (t.TryGetComponent(out Renderer r) && (r is SkinnedMeshRenderer || r is MeshRenderer)) found.Add(r);
                for (int i = t.childCount - 1; i >= 0; i--) stack.Push(t.GetChild(i));
            }
            for (int i = 0; i < found.Count; i++)
            {
                Renderer r = found[i];
                AddChain(r.transform, wanted);
                if (r is SkinnedMeshRenderer smr)
                {
                    Transform[] b = smr.bones;
                    for (int k = 0; k < b.Length; k++) AddChain(b[k], wanted);
                    AddChain(smr.rootBone, wanted);
                }
            }
            humanoid = animator != null && animator.avatar != null && animator.isHuman;
            if (humanoid) for (int i = 0; i < (int)HumanBodyBones.LastBone; i++) AddChain(animator.GetBoneTransform((HumanBodyBones)i), wanted);
            HashSet<Transform> jiggle = new HashSet<Transform>();
            for (int i = 0; jiggleRigs != null && i < jiggleRigs.Count; i++)
            {
                Transform rigRoot = jiggleRigs[i] != null ? jiggleRigs[i].GetJiggleRigData().rootBone : null;
                if (rigRoot == null || !rigRoot.IsChildOf(actorRoot)) continue;
                foreach (Transform t in rigRoot.GetComponentsInChildren<Transform>(true)) { AddChain(t, wanted); jiggle.Add(t); }
            }
            List<Transform> ordered = new List<Transform>(wanted.Count);
            stack.Push(actorRoot);
            while (stack.Count > 0)
            {
                Transform t = stack.Pop();
                if (t != actorRoot && wanted.Contains(t)) ordered.Add(t);
                for (int i = t.childCount - 1; i >= 0; i--) stack.Push(t.GetChild(i));
            }
            bones = ordered.ToArray();
            Dictionary<Transform, int> index = new Dictionary<Transform, int>(bones.Length);
            for (int i = 0; i < bones.Length; i++) index[bones[i]] = i;
            lookup = index;
            parents = new int[bones.Length];
            paths = new string[bones.Length];
            rest = new float[bones.Length * BasisCaptureFormat.BoneFloats];
            for (int i = 0; i < bones.Length; i++)
            {
                Transform t = bones[i];
                parents[i] = t.parent != null && index.TryGetValue(t.parent, out int p) ? p : -1;
                paths[i] = PathOf(actorRoot, t);
                t.GetLocalPositionAndRotation(out Vector3 lp, out Quaternion lr);
                Vector3 ls = t.localScale;
                int o = i * BasisCaptureFormat.BoneFloats;
                rest[o] = lp.x; rest[o + 1] = lp.y; rest[o + 2] = lp.z; rest[o + 3] = lr.x; rest[o + 4] = lr.y; rest[o + 5] = lr.z; rest[o + 6] = lr.w; rest[o + 7] = ls.x; rest[o + 8] = ls.y; rest[o + 9] = ls.z;
                if (jiggle.Contains(t)) jiggleBones.Add(i);
            }
            if (humanoid)
            {
                humanBones = new int[(int)HumanBodyBones.LastBone];
                for (int i = 0; i < humanBones.Length; i++) { Transform t = animator.GetBoneTransform((HumanBodyBones)i); humanBones[i] = t != null && index.TryGetValue(t, out int b) ? b : -1; }
            }
            int shapeTotal = 0;
            for (int i = 0; i < found.Count; i++)
            {
                Renderer r = found[i];
                RendererInfo info = new RendererInfo { renderer = r, skinned = r as SkinnedMeshRenderer, isSkinned = r is SkinnedMeshRenderer, transformIndex = index.TryGetValue(r.transform, out int ti) ? ti : -1, staticBatched = r.isPartOfStaticBatch };
                if (info.skinned != null)
                {
                    Transform[] b = info.skinned.bones;
                    info.bones = new int[b.Length];
                    for (int k = 0; k < b.Length; k++) info.bones[k] = b[k] != null && index.TryGetValue(b[k], out int bi) ? bi : -1;
                    info.rootBone = info.skinned.rootBone != null && index.TryGetValue(info.skinned.rootBone, out int rb) ? rb : -1;
                    Mesh m = info.skinned.sharedMesh;
                    info.shapeCount = m != null ? m.blendShapeCount : 0;
                    info.shapeNames = new string[info.shapeCount];
                    for (int k = 0; k < info.shapeCount; k++) info.shapeNames[k] = m.GetBlendShapeName(k);
                    info.shapeOffset = shapeTotal;
                    shapeTotal += info.shapeCount;
                }
                if (assets != null)
                {
                    info.meshId = assets.MeshFor(r);
                    Material[] mats = r.sharedMaterials;
                    info.materials = new int[mats.Length];
                    for (int k = 0; k < mats.Length; k++) info.materials[k] = assets.MaterialFor(mats[k]);
                }
                renderers.Add(info);
            }
            shapes = new float[shapeTotal];
            previousShapes = new float[shapeTotal];
            previous = new float[bones.Length * BasisCaptureFormat.BoneFloats];
            enabled = new bool[renderers.Count];
            access = new TransformAccessArray(bones);
            current = new NativeArray<float>(bones.Length * BasisCaptureFormat.BoneFloats, Allocator.Persistent);
        }
        private void AddChain(Transform t, HashSet<Transform> into)
        {
            for (; t != null && t != root && t.IsChildOf(root) && into.Add(t); t = t.parent) { }
        }
        public static string PathOf(Transform root, Transform t)
        {
            StringBuilder sb = new StringBuilder(t.name);
            for (Transform p = t.parent; p != null && p != root; p = p.parent) sb.Insert(0, '/').Insert(0, p.name);
            return sb.ToString();
        }
        public void OverrideScale(Transform t, Vector3 scale)
        {
            if (t == null || !lookup.TryGetValue(t, out int i)) return;
            int o = i * BasisCaptureFormat.BoneFloats + 7;
            current[o] = scale.x; current[o + 1] = scale.y; current[o + 2] = scale.z;
        }
        public JobHandle Schedule() => bones.Length == 0 ? default : new BasisCaptureLocalReadJob { output = current }.ScheduleReadOnly(access, 32);
        public void Write(BasisCaptureBuffer buffer)
        {
            for (int r = 0; r < renderers.Count; r++)
            {
                RendererInfo info = renderers[r];
                Renderer rr = info.renderer;
                enabled[r] = rr != null && rr.enabled && rr.gameObject.activeInHierarchy && !hiddenRenderers.Contains(rr);
                if (info.shapeCount == 0 || info.skinned == null) continue;
                for (int k = 0; k < info.shapeCount; k++) shapes[info.shapeOffset + k] = info.skinned.GetBlendShapeWeight(k);
            }
            WritePose(buffer, id, root.gameObject.activeInHierarchy, root.localToWorldMatrix, current.AsReadOnlySpan(), previous, shapes, previousShapes, enabled, ref havePrevious);
        }
        public static void WritePose(BasisCaptureBuffer buffer, int id, bool visible, Matrix4x4 rootMatrix, ReadOnlySpan<float> current, float[] previous, float[] shapes, float[] previousShapes, bool[] rendererEnabled, ref bool havePrevious)
        {
            int start = buffer.BeginRecord(BasisCaptureFormat.RecordActor, id);
            buffer.Byte(visible ? BasisCaptureFormat.ActorVisible : (byte)0);
            buffer.Matrix(rootMatrix);
            int n = previous.Length / BasisCaptureFormat.BoneFloats;
            buffer.Int(n);
            int mask = buffer.Reserve((n + 7) >> 3);
            for (int i = 0; i < n; i++)
            {
                int o = i * BasisCaptureFormat.BoneFloats;
                bool changed = !havePrevious;
                for (int k = 0; k < BasisCaptureFormat.BoneFloats && !changed; k++) changed = current[o + k] != previous[o + k];
                if (!changed) continue;
                buffer.Data[mask + (i >> 3)] |= (byte)(1 << (i & 7));
                for (int k = 0; k < BasisCaptureFormat.BoneFloats; k++) { float v = current[o + k]; previous[o + k] = v; buffer.Float(v); }
            }
            buffer.Int(shapes.Length);
            mask = buffer.Reserve((shapes.Length + 7) >> 3);
            for (int i = 0; i < shapes.Length; i++)
            {
                float v = shapes[i];
                if (havePrevious && v == previousShapes[i]) continue;
                buffer.Data[mask + (i >> 3)] |= (byte)(1 << (i & 7));
                previousShapes[i] = v;
                buffer.Float(v);
            }
            buffer.Int(rendererEnabled.Length);
            mask = buffer.Reserve((rendererEnabled.Length + 7) >> 3);
            for (int i = 0; i < rendererEnabled.Length; i++) if (rendererEnabled[i]) buffer.Data[mask + (i >> 3)] |= (byte)(1 << (i & 7));
            havePrevious = true;
            buffer.EndRecord(start);
        }
        public void WriteManifest(BasisCaptureJson j)
        {
            j.BeginObject().Prop("id", id).Prop("kind", kind).Prop("uuid", uuid).Prop("displayName", displayName).Prop("netId", (int)netId).Prop("isLocal", isLocal).Prop("name", root != null ? root.name : avatarName).Prop("avatarName", avatarName).Prop("avatarUrl", avatarUrl).Prop("avatarVersion", avatarVersion).Prop("humanoid", humanoid).Prop("startFrame", startFrame).Prop("endFrame", endFrame);
            j.BeginArray("bones");
            for (int i = 0; i < bones.Length; i++)
            {
                int o = i * BasisCaptureFormat.BoneFloats;
                j.BeginObject().Prop("path", paths[i]).Prop("name", bones[i] != null ? bones[i].name : LastSegment(paths[i])).Prop("parent", parents[i]);
                j.Name("rest").Floats(rest[o], rest[o + 1], rest[o + 2], rest[o + 3], rest[o + 4], rest[o + 5], rest[o + 6], rest[o + 7], rest[o + 8], rest[o + 9]);
                j.EndObject();
            }
            j.EndArray();
            j.Name("humanBones").Ints(humanBones);
            j.Name("jiggleBones").Ints(jiggleBones);
            j.BeginArray("renderers");
            for (int i = 0; i < renderers.Count; i++)
            {
                RendererInfo r = renderers[i];
                j.BeginObject().Prop("name", r.renderer != null ? r.renderer.name : "").Prop("skinned", r.isSkinned).Prop("transform", r.transformIndex).Prop("mesh", r.meshId).Prop("rootBone", r.rootBone).Prop("staticBatched", r.staticBatched).Prop("shapeOffset", r.shapeOffset);
                j.Name("materials").Ints(r.materials);
                j.Name("bones").Ints(r.bones);
                j.Name("shapes").Strings(r.shapeNames);
                j.EndObject();
            }
            j.EndArray();
            j.EndObject();
        }
        private static string LastSegment(string path)
        {
            int i = path.LastIndexOf('/');
            return i < 0 ? path : path.Substring(i + 1);
        }
        public void Dispose()
        {
            if (access.isCreated) access.Dispose();
            if (current.IsCreated) current.Dispose();
        }
    }
}
