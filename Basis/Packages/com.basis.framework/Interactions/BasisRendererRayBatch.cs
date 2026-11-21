using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Jobs;
namespace Basis.Scripts.BasisSdk.Interactions
{
    public struct BasisRendererRay
    {
        public float3 origin, direction;
        public float maxDistance;
    }
    public struct BasisRendererBatchHit
    {
        public float3 point, normal;
        public float distance;
        public int renderer, triangle;
        public bool hit;
    }
    public struct BasisRayBvhNode
    {
        public float3 min, max;
        public int start, count, right;
    }
    public struct BasisRayTreeGroup
    {
        public int triangleStart, triangleCount, nodeStart, partStart, partEnd;
    }
    public struct BasisRayTrianglePack
    {
        public float4 v0x, v0y, v0z, e1x, e1y, e1z, e2x, e2y, e2z;
        public int4 triangle;
    }
    [NoAutoStaticsCleanup]
    public sealed class BasisRendererRayBatch : IDisposable
    {
        public const int MaxLeafSize = 8, Bins = 12, MaxTreesPerRay = 63;
        internal sealed class MeshEntry
        {
            public Mesh mesh;
            public int references;
            public NativeArray<float3> vertices, delta, boneOrigin;
            public NativeArray<int> indices, weightStart, weightBone, shapeStart, shapeCount, deltaIndex;
            public NativeArray<byte> weightCount;
            public NativeArray<float> weightValue, boneRadius;
            public float[] frameWeight;
            public Matrix4x4[] bindposes;
            public float3 boundsMin, boundsMax;
            public float blendPad;
            public int shapes;
            public bool weighted, unweighted;
            public void Dispose()
            {
                if (vertices.IsCreated)
                {
                    vertices.Dispose();
                }
                if (delta.IsCreated)
                {
                    delta.Dispose();
                }
                if (boneOrigin.IsCreated)
                {
                    boneOrigin.Dispose();
                }
                if (boneRadius.IsCreated)
                {
                    boneRadius.Dispose();
                }
                if (indices.IsCreated)
                {
                    indices.Dispose();
                }
                if (weightStart.IsCreated)
                {
                    weightStart.Dispose();
                }
                if (weightBone.IsCreated)
                {
                    weightBone.Dispose();
                }
                if (shapeStart.IsCreated)
                {
                    shapeStart.Dispose();
                }
                if (shapeCount.IsCreated)
                {
                    shapeCount.Dispose();
                }
                if (deltaIndex.IsCreated)
                {
                    deltaIndex.Dispose();
                }
                if (weightCount.IsCreated)
                {
                    weightCount.Dispose();
                }
                if (weightValue.IsCreated)
                {
                    weightValue.Dispose();
                }
            }
        }
        internal sealed class Part
        {
            public Renderer renderer;
            public SkinnedMeshRenderer skinned;
            public MeshEntry mesh;
            public Transform[] bones;
            public float[] factors;
            public int vertexOffset, triangleOffset, boneOffset, boneCount, shapeOffset, shapeCount;
        }
        private static readonly Dictionary<Mesh, MeshEntry> sharedMeshes = new Dictionary<Mesh, MeshEntry>();
        public static int SharedMeshCount => sharedMeshes.Count;
        internal readonly List<Part> parts = new List<Part>();
        private readonly Dictionary<Renderer, int> indexOf = new Dictionary<Renderer, int>();
        internal NativeArray<float3> world, scratch, triMin, triMax, packMin, packMax, partBoundsMin, partBoundsMax, meshBoundsMin, meshBoundsMax, boneOrigins;
        internal NativeArray<int> globalIndex, triPart, triLocal, order, sortedPart, sortedLocal, nodePack, partChanged, partForce, partStale, partTouched, groupChanged, groupStale, groupNodeCount, groupPackCount;
        internal NativeArray<int3> sortedIndex;
        internal NativeArray<int2> partMatrices;
        internal NativeArray<float> keys, boneRadii, partPad;
        internal NativeArray<float4x4> matrices, previous, bindposes;
        internal NativeArray<BasisRayBvhNode> nodes;
        internal NativeArray<BasisRayTreeGroup> groups;
        internal NativeArray<BasisRayTrianglePack> packs;
        private NativeArray<ulong> allMask;
        private NativeArray<BasisRendererRay> noRays;
        private NativeArray<float3> noVectors;
        private NativeList<int> activeShape;
        private NativeList<float> activeFactor;
        private TransformAccessArray transforms;
        private JobHandle buildHandle, readers;
        internal int vertexCount, triangleCount, matrixCount, groupCount, builds;
        private bool topologyDirty = true, treeValid, perRenderer = true;
        private bool[] groupRefit = new bool[0];
        public int RebuildInterval = 30;
        public int VertexCount => vertexCount;
        public int TriangleCount => triangleCount;
        public int RendererCount => parts.Count;
        public int TreeCount => groupCount;
        public int MaskLength => (parts.Count + 63) / 64;
        public bool LastBuildWasRefit { get; private set; }
        internal int TopologyVersion { get; private set; }
        public bool TreePerRenderer
        {
            get => perRenderer;
            set
            {
                if (perRenderer == value)
                {
                    return;
                }
                CompleteAll();
                perRenderer = value;
                topologyDirty = true;
            }
        }
        public BasisRendererRayBatch()
        {
            activeShape = new NativeList<int>(16, Allocator.Persistent);
            activeFactor = new NativeList<float>(16, Allocator.Persistent);
            noRays = new NativeArray<BasisRendererRay>(1, Allocator.Persistent);
            noVectors = new NativeArray<float3>(1, Allocator.Persistent);
        }
        public int NodeCount
        {
            get
            {
                buildHandle.Complete();
                int total = 0;
                for (int g = 0; g < groupCount; g++)
                {
                    total += groupNodeCount[g];
                }
                return total;
            }
        }
        private int CountFlags(NativeArray<int> flags)
        {
            buildHandle.Complete();
            int total = 0;
            if (flags.IsCreated)
            {
                for (int p = 0; p < parts.Count && p < flags.Length; p++)
                {
                    if (flags[p] != 0)
                    {
                        total++;
                    }
                }
            }
            return total;
        }
        public int ChangedRenderers => CountFlags(partChanged);
        public int StaleRenderers => CountFlags(partStale);
        public Renderer GetRenderer(int index) => index >= 0 && index < parts.Count ? parts[index].renderer : null;
        public int IndexOf(Renderer renderer)
        {
            if (topologyDirty)
            {
                return -1;
            }
            return renderer != null && indexOf.TryGetValue(renderer, out int index) ? index : -1;
        }
        public void FillMask(NativeArray<ulong> mask, IReadOnlyList<Renderer> renderers)
        {
            for (int i = 0; i < mask.Length; i++)
            {
                mask[i] = 0;
            }
            for (int i = 0; i < renderers.Count; i++)
            {
                int index = IndexOf(renderers[i]);
                if (index >= 0 && index / 64 < mask.Length)
                {
                    mask[index / 64] |= 1ul << (index & 63);
                }
            }
        }
        public NativeArray<float3> WorldVertices(int rendererIndex)
        {
            buildHandle.Complete();
            Part p = parts[rendererIndex];
            return world.GetSubArray(p.vertexOffset, p.mesh.vertices.Length);
        }
        public void Clear()
        {
            CompleteAll();
            for (int i = 0; i < parts.Count; i++)
            {
                ReleaseMesh(parts[i].mesh);
            }
            parts.Clear();
            topologyDirty = true;
        }
        public bool Add(Renderer renderer)
        {
            if (renderer == null)
            {
                return false;
            }
            SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
            Mesh mesh = skinned != null ? skinned.sharedMesh : renderer.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
            if (mesh == null || !BasisMeshGraspSurface.Readable(mesh))
            {
                return false;
            }
            MeshEntry entry = AcquireMesh(mesh);
            if (entry == null)
            {
                return false;
            }
            CompleteAll();
            Transform[] bones = skinned != null ? skinned.bones : null;
            if (bones != null && (bones.Length == 0 || !entry.weighted || entry.bindposes == null || entry.bindposes.Length < bones.Length))
            {
                bones = null;
            }
            parts.Add(new Part { renderer = renderer, skinned = skinned, mesh = entry, bones = bones, factors = new float[entry.shapes] });
            topologyDirty = true;
            return true;
        }
        public bool Remove(Renderer renderer)
        {
            CompleteAll();
            for (int i = 0; i < parts.Count; i++)
            {
                if (!ReferenceEquals(parts[i].renderer, renderer))
                {
                    continue;
                }
                ReleaseMesh(parts[i].mesh);
                parts.RemoveAt(i);
                topologyDirty = true;
                return true;
            }
            return false;
        }
        private static MeshEntry AcquireMesh(Mesh mesh)
        {
            if (!sharedMeshes.TryGetValue(mesh, out MeshEntry entry))
            {
                entry = Cache(mesh);
                if (entry == null)
                {
                    return null;
                }
                sharedMeshes.Add(mesh, entry);
            }
            entry.references++;
            return entry;
        }
        private static void ReleaseMesh(MeshEntry entry)
        {
            if (entry == null || --entry.references > 0)
            {
                return;
            }
            sharedMeshes.Remove(entry.mesh);
            entry.Dispose();
        }
        private static MeshEntry Cache(Mesh mesh)
        {
            MeshEntry e = new MeshEntry { mesh = mesh };
            try
            {
                using (Mesh.MeshDataArray array = Mesh.AcquireReadOnlyMeshData(mesh))
                {
                    Mesh.MeshData data = array[0];
                    e.vertices = new NativeArray<float3>(data.vertexCount, Allocator.Persistent);
                    data.GetVertices(e.vertices.Reinterpret<Vector3>());
                    int indexCount = 0;
                    for (int s = 0; s < data.subMeshCount; s++)
                    {
                        if (data.GetSubMesh(s).topology == MeshTopology.Triangles)
                        {
                            indexCount += data.GetSubMesh(s).indexCount;
                        }
                    }
                    e.indices = new NativeArray<int>(indexCount, Allocator.Persistent);
                    int written = 0;
                    for (int s = 0; s < data.subMeshCount; s++)
                    {
                        UnityEngine.Rendering.SubMeshDescriptor sub = data.GetSubMesh(s);
                        if (sub.topology != MeshTopology.Triangles || sub.indexCount == 0)
                        {
                            continue;
                        }
                        data.GetIndices(e.indices.GetSubArray(written, sub.indexCount), s);
                        written += sub.indexCount;
                    }
                }
                float3 lo = new float3(float.MaxValue), hi = new float3(float.MinValue);
                for (int i = 0; i < e.vertices.Length; i++)
                {
                    lo = math.min(lo, e.vertices[i]);
                    hi = math.max(hi, e.vertices[i]);
                }
                e.boundsMin = e.vertices.Length > 0 ? lo : float3.zero;
                e.boundsMax = e.vertices.Length > 0 ? hi : float3.zero;
                NativeArray<byte> perVertex = mesh.GetBonesPerVertex();
                NativeArray<BoneWeight1> weights = mesh.GetAllBoneWeights();
                e.weighted = perVertex.Length == e.vertices.Length && weights.Length > 0;
                if (e.weighted)
                {
                    e.weightCount = new NativeArray<byte>(perVertex, Allocator.Persistent);
                    e.weightStart = new NativeArray<int>(perVertex.Length, Allocator.Persistent);
                    e.weightBone = new NativeArray<int>(weights.Length, Allocator.Persistent);
                    e.weightValue = new NativeArray<float>(weights.Length, Allocator.Persistent);
                    int start = 0;
                    for (int i = 0; i < perVertex.Length; i++)
                    {
                        e.weightStart[i] = start;
                        start += perVertex[i];
                    }
                    for (int i = 0; i < weights.Length; i++)
                    {
                        e.weightBone[i] = weights[i].boneIndex;
                        e.weightValue[i] = weights[i].weight;
                    }
                    e.bindposes = mesh.bindposes;
                    int bones = e.bindposes.Length;
                    e.boneOrigin = new NativeArray<float3>(math.max(1, bones), Allocator.Persistent);
                    e.boneRadius = new NativeArray<float>(math.max(1, bones), Allocator.Persistent);
                    for (int b = 0; b < bones; b++)
                    {
                        e.boneOrigin[b] = ((float4x4)e.bindposes[b].inverse).c3.xyz;
                        e.boneRadius[b] = -1f;
                    }
                    for (int v = 0; v < e.vertices.Length; v++)
                    {
                        float total = 0f;
                        for (int k = 0; k < perVertex[v]; k++)
                        {
                            int bone = e.weightBone[e.weightStart[v] + k];
                            float w = e.weightValue[e.weightStart[v] + k];
                            if ((uint)bone >= (uint)bones || w <= 0f)
                            {
                                continue;
                            }
                            total += w;
                            e.boneRadius[bone] = math.max(e.boneRadius[bone], math.distance(e.vertices[v], e.boneOrigin[bone]));
                        }
                        if (total <= 1e-6f)
                        {
                            e.unweighted = true;
                        }
                    }
                }
                e.shapes = mesh.blendShapeCount;
                e.frameWeight = new float[e.shapes];
                e.shapeStart = new NativeArray<int>(math.max(1, e.shapes), Allocator.Persistent);
                e.shapeCount = new NativeArray<int>(math.max(1, e.shapes), Allocator.Persistent);
                List<int> index = new List<int>();
                List<float3> delta = new List<float3>();
                Vector3[] frame = e.shapes > 0 ? new Vector3[mesh.vertexCount] : null;
                for (int s = 0; s < e.shapes; s++)
                {
                    int last = mesh.GetBlendShapeFrameCount(s) - 1;
                    e.shapeStart[s] = index.Count;
                    if (last < 0)
                    {
                        continue;
                    }
                    e.frameWeight[s] = mesh.GetBlendShapeFrameWeight(s, last);
                    mesh.GetBlendShapeFrameVertices(s, last, frame, null, null);
                    float largest = 0f;
                    for (int v = 0; v < frame.Length; v++)
                    {
                        float m = frame[v].sqrMagnitude;
                        if (m < 1e-14f)
                        {
                            continue;
                        }
                        largest = math.max(largest, m);
                        index.Add(v);
                        delta.Add(frame[v]);
                    }
                    e.blendPad += math.sqrt(largest);
                    e.shapeCount[s] = index.Count - e.shapeStart[s];
                }
                e.deltaIndex = new NativeArray<int>(math.max(1, index.Count), Allocator.Persistent);
                e.delta = new NativeArray<float3>(math.max(1, delta.Count), Allocator.Persistent);
                for (int i = 0; i < index.Count; i++)
                {
                    e.deltaIndex[i] = index[i];
                    e.delta[i] = delta[i];
                }
                return e;
            }
            catch (Exception)
            {
                e.Dispose();
                return null;
            }
        }
        private void CompleteAll()
        {
            buildHandle.Complete();
            readers.Complete();
            readers = default;
        }
        private static void Ensure<T>(ref NativeArray<T> array, int length) where T : struct
        {
            length = math.max(1, length);
            if (array.IsCreated && array.Length >= length)
            {
                return;
            }
            if (array.IsCreated)
            {
                array.Dispose();
            }
            array = new NativeArray<T>(length, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        }
        private void RebuildTopology()
        {
            topologyDirty = false;
            treeValid = false;
            TopologyVersion++;
            vertexCount = triangleCount = matrixCount = 0;
            indexOf.Clear();
            List<Transform> transformList = new List<Transform>();
            for (int i = 0; i < parts.Count; i++)
            {
                Part p = parts[i];
                indexOf[p.renderer] = i;
                p.vertexOffset = vertexCount;
                p.triangleOffset = triangleCount;
                p.boneOffset = matrixCount;
                p.boneCount = p.bones != null ? p.bones.Length : 0;
                vertexCount += p.mesh.vertices.Length;
                triangleCount += p.mesh.indices.Length / 3;
                for (int b = 0; b < p.boneCount; b++)
                {
                    transformList.Add(p.bones[b] != null ? p.bones[b] : p.renderer.transform);
                }
                transformList.Add(p.renderer.transform);
                matrixCount += p.boneCount + 1;
                Array.Clear(p.factors, 0, p.factors.Length);
            }
            groupCount = perRenderer && parts.Count <= MaxTreesPerRay ? parts.Count : parts.Count > 0 ? 1 : 0;
            int nodeCapacity = 0;
            Ensure(ref groups, groupCount);
            for (int g = 0; g < groupCount; g++)
            {
                int partStart = groupCount == parts.Count ? g : 0, partEnd = groupCount == parts.Count ? g + 1 : parts.Count;
                int triangleStart = parts[partStart].triangleOffset, triangles = (partEnd < parts.Count ? parts[partEnd].triangleOffset : triangleCount) - triangleStart;
                groups[g] = new BasisRayTreeGroup { triangleStart = triangleStart, triangleCount = triangles, nodeStart = nodeCapacity, partStart = partStart, partEnd = partEnd };
                nodeCapacity += 2 * triangles + 1;
            }
            groupRefit = new bool[groupCount];
            Ensure(ref world, vertexCount);
            Ensure(ref scratch, vertexCount);
            Ensure(ref matrices, matrixCount);
            Ensure(ref previous, matrixCount);
            Ensure(ref bindposes, matrixCount);
            Ensure(ref boneOrigins, matrixCount);
            Ensure(ref boneRadii, matrixCount);
            Ensure(ref partMatrices, parts.Count);
            Ensure(ref partChanged, parts.Count);
            Ensure(ref partForce, parts.Count);
            Ensure(ref partStale, parts.Count);
            Ensure(ref partTouched, parts.Count);
            Ensure(ref partPad, parts.Count);
            Ensure(ref partBoundsMin, parts.Count);
            Ensure(ref partBoundsMax, parts.Count);
            Ensure(ref meshBoundsMin, parts.Count);
            Ensure(ref meshBoundsMax, parts.Count);
            Ensure(ref groupChanged, groupCount);
            Ensure(ref groupStale, groupCount);
            Ensure(ref groupNodeCount, groupCount);
            Ensure(ref groupPackCount, groupCount);
            Ensure(ref nodes, nodeCapacity);
            Ensure(ref nodePack, nodeCapacity);
            Ensure(ref globalIndex, triangleCount * 3);
            Ensure(ref triPart, triangleCount);
            Ensure(ref triLocal, triangleCount);
            Ensure(ref triMin, triangleCount);
            Ensure(ref triMax, triangleCount);
            Ensure(ref sortedIndex, triangleCount);
            Ensure(ref sortedPart, triangleCount);
            Ensure(ref sortedLocal, triangleCount);
            Ensure(ref packs, triangleCount);
            Ensure(ref packMin, triangleCount);
            Ensure(ref packMax, triangleCount);
            Ensure(ref order, triangleCount);
            Ensure(ref keys, triangleCount);
            Ensure(ref allMask, MaskLength);
            for (int i = 0; i < allMask.Length; i++)
            {
                allMask[i] = ulong.MaxValue;
            }
            for (int g = 0; g < groupCount; g++)
            {
                groupNodeCount[g] = groupPackCount[g] = groupStale[g] = 0;
            }
            int m = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                Part p = parts[i];
                partMatrices[i] = new int2(p.boneOffset, p.boneCount + 1);
                partStale[i] = 0;
                meshBoundsMin[i] = p.mesh.boundsMin;
                meshBoundsMax[i] = p.mesh.boundsMax;
                for (int b = 0; b < p.boneCount; b++)
                {
                    bool live = p.bones[b] != null;
                    bindposes[m] = live ? (float4x4)p.mesh.bindposes[b] : float4x4.identity;
                    boneOrigins[m] = live ? p.mesh.boneOrigin[b] : float3.zero;
                    boneRadii[m] = live ? p.mesh.boneRadius[b] : -1f;
                    m++;
                }
                bindposes[m] = float4x4.identity;
                boneOrigins[m] = float3.zero;
                boneRadii[m] = p.boneCount == 0 || p.mesh.unweighted ? 0f : -1f;
                m++;
            }
            if (transforms.isCreated)
            {
                transforms.Dispose();
            }
            transforms = new TransformAccessArray(transformList.ToArray());
            JobHandle h = default;
            for (int i = 0; i < parts.Count; i++)
            {
                Part p = parts[i];
                h = new TopologyJob { indices = p.mesh.indices, globalIndex = globalIndex, triPart = triPart, triLocal = triLocal, vertexOffset = p.vertexOffset, triangleOffset = p.triangleOffset, part = i }.Schedule(p.mesh.indices.Length / 3, 1024, h);
            }
            h.Complete();
        }
        internal bool Prune()
        {
            CompleteAll();
            for (int i = parts.Count - 1; i >= 0; i--)
            {
                if (parts[i].renderer != null)
                {
                    continue;
                }
                ReleaseMesh(parts[i].mesh);
                parts.RemoveAt(i);
                topologyDirty = true;
            }
            if (!topologyDirty && transforms.isCreated && transforms.length != matrixCount)
            {
                topologyDirty = true;
            }
            return topologyDirty || !treeValid;
        }
        public JobHandle Build(JobHandle dependsOn = default, bool allowRefit = true) => Build(dependsOn, allowRefit, default, default, default, 0, 0f);
        public JobHandle Build(JobHandle dependsOn, bool allowRefit, NativeArray<BasisRendererRay> onlyForRays) => Build(dependsOn, allowRefit, onlyForRays, default, default, onlyForRays.Length, 0f);
        public JobHandle Build(JobHandle dependsOn, bool allowRefit, NativeArray<float3> origins, NativeArray<float3> directions, int count, float maxDistance) => Build(dependsOn, allowRefit, default, origins, directions, count, maxDistance);
        private JobHandle Build(JobHandle dependsOn, bool allowRefit, NativeArray<BasisRendererRay> rays, NativeArray<float3> origins, NativeArray<float3> directions, int count, float maxDistance)
        {
            CompleteAll();
            dependsOn.Complete();
            Prune();
            bool lazy = (rays.IsCreated || (origins.IsCreated && directions.IsCreated)) && groupCount == parts.Count;
            JobHandle h = Prepare(allowRefit, lazy, rays, origins, directions, count, maxDistance);
            return Finish(h);
        }
        internal JobHandle Prepare(bool allowRefit) => Prepare(allowRefit, false, default, default, default, 0, 0f);
        private JobHandle Prepare(bool allowRefit, bool lazy, NativeArray<BasisRendererRay> rays, NativeArray<float3> origins, NativeArray<float3> directions, int count, float maxDistance)
        {
            if (topologyDirty)
            {
                RebuildTopology();
            }
            lazy &= groupCount == parts.Count && treeValid;
            bool forceAll = !treeValid;
            builds++;
            bool anyRefit = false;
            for (int g = 0; g < groupCount; g++)
            {
                groupRefit[g] = allowRefit && !forceAll && (RebuildInterval <= 0 || (builds + g) % RebuildInterval != 0);
                anyRefit |= groupRefit[g];
            }
            LastBuildWasRefit = anyRefit;
            treeValid = true;
            activeShape.Clear();
            activeFactor.Clear();
            for (int i = 0; i < parts.Count; i++)
            {
                Part p = parts[i];
                p.shapeOffset = activeShape.Length;
                bool blendChanged = false;
                float largest = 1f;
                if (p.skinned != null)
                {
                    MeshEntry m = p.mesh;
                    for (int s = 0; s < m.shapes; s++)
                    {
                        float factor = m.shapeCount[s] == 0 || m.frameWeight[s] == 0f ? 0f : p.skinned.GetBlendShapeWeight(s) / m.frameWeight[s];
                        if (factor != p.factors[s])
                        {
                            p.factors[s] = factor;
                            blendChanged = true;
                        }
                        if (math.abs(factor) < 1e-5f)
                        {
                            continue;
                        }
                        largest = math.max(largest, math.abs(factor));
                        activeShape.Add(s);
                        activeFactor.Add(factor);
                    }
                }
                p.shapeCount = activeShape.Length - p.shapeOffset;
                partPad[i] = p.mesh.blendPad * largest;
                partForce[i] = forceAll || blendChanged ? 1 : 0;
                partTouched[i] = lazy ? 0 : 1;
            }
            for (int g = 0; g < groupCount; g++)
            {
                if (groupRefit[g])
                {
                    continue;
                }
                for (int i = groups[g].partStart; i < groups[g].partEnd; i++)
                {
                    partForce[i] = 1;
                    partTouched[i] = 1;
                }
            }
            JobHandle h = new GatherJob { bindposes = bindposes, matrices = matrices }.ScheduleReadOnly(transforms, 64);
            if (lazy)
            {
                h = new PartBoundsJob { ranges = partMatrices, matrices = matrices, boneOrigins = boneOrigins, boneRadii = boneRadii, pad = partPad, meshMin = meshBoundsMin, meshMax = meshBoundsMax, boundsMin = partBoundsMin, boundsMax = partBoundsMax }.Schedule(parts.Count, 1, h);
                h = new CullJob { boundsMin = partBoundsMin, boundsMax = partBoundsMax, rays = rays.IsCreated ? rays : noRays, origins = origins.IsCreated ? origins : noVectors, directions = directions.IsCreated ? directions : noVectors, useRays = rays.IsCreated, count = count, maxDistance = maxDistance, touched = partTouched }.Schedule(parts.Count, 1, h);
            }
            h = new DetectJob { ranges = partMatrices, matrices = matrices, previous = previous, force = partForce, touched = partTouched, stale = partStale, changed = partChanged }.Schedule(parts.Count, 1, h);
            h = new GroupChangedJob { groups = groups, partChanged = partChanged, partStale = partStale, groupChanged = groupChanged, groupStale = groupStale }.Schedule(groupCount, 1, h);
            buildHandle = h;
            return h;
        }
        private JobHandle Finish(JobHandle h)
        {
            NativeArray<int> shapes = activeShape.AsArray();
            NativeArray<float> factors = activeFactor.AsArray();
            NativeArray<JobHandle> tails = new NativeArray<JobHandle>(math.max(1, groupCount), Allocator.Temp);
            for (int g = 0; g < groupCount; g++)
            {
                BasisRayTreeGroup group = groups[g];
                NativeArray<JobHandle> skins = new NativeArray<JobHandle>(group.partEnd - group.partStart, Allocator.Temp);
                for (int i = group.partStart; i < group.partEnd; i++)
                {
                    Part p = parts[i];
                    MeshEntry m = p.mesh;
                    int n = m.vertices.Length;
                    JobHandle s = h;
                    NativeArray<float3> source = m.vertices;
                    int sourceOffset = 0;
                    if (p.shapeCount > 0)
                    {
                        s = new BlendJob { source = m.vertices, target = scratch, offset = p.vertexOffset, shapes = shapes, factors = factors, first = p.shapeOffset, count = p.shapeCount, shapeStart = m.shapeStart, shapeCount = m.shapeCount, deltaIndex = m.deltaIndex, delta = m.delta, changed = partChanged, part = i }.Schedule(s);
                        source = scratch;
                        sourceOffset = p.vertexOffset;
                    }
                    int rendererMatrix = p.boneOffset + p.boneCount;
                    if (p.bones != null)
                    {
                        int influences = p.skinned.quality == SkinQuality.Auto ? (int)QualitySettings.skinWeights : (int)p.skinned.quality;
                        if (influences <= 0)
                        {
                            influences = 255;
                        }
                        s = new SkinJob { source = source, sourceOffset = sourceOffset, weightStart = m.weightStart, weightCount = m.weightCount, weightBone = m.weightBone, weightValue = m.weightValue, matrices = matrices, boneOffset = p.boneOffset, boneCount = p.boneCount, fallbackIndex = rendererMatrix, influences = influences, world = world, worldOffset = p.vertexOffset, changed = partChanged, part = i }.Schedule(n, 256, s);
                    }
                    else
                    {
                        s = new TransformJob { source = source, sourceOffset = sourceOffset, matrices = matrices, matrixIndex = rendererMatrix, world = world, worldOffset = p.vertexOffset, changed = partChanged, part = i }.Schedule(n, 256, s);
                    }
                    skins[i - group.partStart] = s;
                }
                JobHandle t = JobHandle.CombineDependencies(skins);
                skins.Dispose();
                if (group.triangleCount > 0)
                {
                    if (groupRefit[g])
                    {
                        t = new RefitPackJob { world = world, sortedIndex = sortedIndex, packs = packs, packMin = packMin, packMax = packMax, groupChanged = groupChanged, groupPackCount = groupPackCount, group = g, start = group.triangleStart }.Schedule(group.triangleCount, 256, t);
                        t = new RefitJob { nodes = nodes, nodePack = nodePack, packMin = packMin, packMax = packMax, groupChanged = groupChanged, groupNodeCount = groupNodeCount, group = g, nodeStart = group.nodeStart }.Schedule(t);
                    }
                    else
                    {
                        t = new TriangleBoundsJob { world = world, globalIndex = globalIndex, triMin = triMin, triMax = triMax, start = group.triangleStart }.Schedule(group.triangleCount, 512, t);
                        t = new BuildJob { triMin = triMin, triMax = triMax, order = order, keys = keys, nodes = nodes, groupNodeCount = groupNodeCount, group = g, start = group.triangleStart, count = group.triangleCount, nodeStart = group.nodeStart }.Schedule(t);
                        t = new ReorderJob { order = order, globalIndex = globalIndex, triPart = triPart, triLocal = triLocal, sortedIndex = sortedIndex, sortedPart = sortedPart, sortedLocal = sortedLocal, start = group.triangleStart }.Schedule(group.triangleCount, 512, t);
                        t = new PackJob { world = world, sortedIndex = sortedIndex, nodes = nodes, nodePack = nodePack, packs = packs, groupNodeCount = groupNodeCount, groupPackCount = groupPackCount, group = g, nodeStart = group.nodeStart, packStart = group.triangleStart }.Schedule(t);
                    }
                }
                tails[g] = t;
            }
            buildHandle = groupCount > 0 ? JobHandle.CombineDependencies(tails.GetSubArray(0, groupCount)) : h;
            tails.Dispose();
            JobHandle.ScheduleBatchedJobs();
            return buildHandle;
        }
        private BvhView View(NativeArray<ulong> mask) => new BvhView { nodes = nodes, nodePack = nodePack, packs = packs, groups = groups, groupNodeCount = groupNodeCount, groupStale = groupStale, sortedPart = sortedPart, sortedLocal = sortedLocal, mask = mask.IsCreated ? mask : allMask, filtered = mask.IsCreated, perRenderer = groupCount == parts.Count, groupCount = groupCount, empty = triangleCount == 0 || groupCount == 0 };
        private JobHandle Track(JobHandle h)
        {
            readers = JobHandle.CombineDependencies(readers, h);
            return h;
        }
        public JobHandle Raycast(NativeArray<BasisRendererRay> rays, NativeArray<BasisRendererBatchHit> hits, JobHandle dependsOn = default, int batchSize = 32) => Raycast(rays, hits, default, dependsOn, batchSize);
        public JobHandle Raycast(NativeArray<BasisRendererRay> rays, NativeArray<BasisRendererBatchHit> hits, NativeArray<ulong> rendererMask, JobHandle dependsOn = default, int batchSize = 32) => Track(new RaycastJob { bvh = View(rendererMask), rays = rays, hits = hits }.Schedule(rays.Length, batchSize, JobHandle.CombineDependencies(buildHandle, dependsOn)));
        public JobHandle Raycast(NativeArray<float3> origins, NativeArray<float3> directions, int count, float maxDistance, NativeArray<float> distances, NativeArray<float3> points, NativeArray<float3> normals, NativeArray<int> renderers, JobHandle dependsOn = default, int batchSize = 32) => Raycast(origins, directions, count, maxDistance, distances, points, normals, renderers, default, dependsOn, batchSize);
        public JobHandle Raycast(NativeArray<float3> origins, NativeArray<float3> directions, int count, float maxDistance, NativeArray<float> distances, NativeArray<float3> points, NativeArray<float3> normals, NativeArray<int> renderers, NativeArray<ulong> rendererMask, JobHandle dependsOn = default, int batchSize = 32) => Track(new RaycastStreamJob { bvh = View(rendererMask), origins = origins, directions = directions, maxDistance = maxDistance, distances = distances, points = points, normals = normals, renderers = renderers }.Schedule(count, batchSize, JobHandle.CombineDependencies(buildHandle, dependsOn)));
        public void RaycastOnThisThread(NativeArray<BasisRendererRay> rays, NativeArray<BasisRendererBatchHit> hits)
        {
            buildHandle.Complete();
            new RaycastJob { bvh = View(default), rays = rays, hits = hits }.Run(rays.Length);
        }
        public void Dispose()
        {
            CompleteAll();
            for (int i = 0; i < parts.Count; i++)
            {
                ReleaseMesh(parts[i].mesh);
            }
            parts.Clear();
            if (transforms.isCreated)
            {
                transforms.Dispose();
            }
            DisposeAll(world, scratch, triMin, triMax, packMin, packMax, partBoundsMin, partBoundsMax, meshBoundsMin, meshBoundsMax, boneOrigins);
            DisposeAll(globalIndex, triPart, triLocal, order, sortedPart, sortedLocal, nodePack, partChanged, partForce, partStale, partTouched, groupChanged, groupStale, groupNodeCount, groupPackCount);
            DisposeAll(keys, boneRadii, partPad);
            DisposeAll(matrices, previous, bindposes);
            if (sortedIndex.IsCreated)
            {
                sortedIndex.Dispose();
            }
            if (partMatrices.IsCreated)
            {
                partMatrices.Dispose();
            }
            if (nodes.IsCreated)
            {
                nodes.Dispose();
            }
            if (groups.IsCreated)
            {
                groups.Dispose();
            }
            if (packs.IsCreated)
            {
                packs.Dispose();
            }
            if (allMask.IsCreated)
            {
                allMask.Dispose();
            }
            if (noRays.IsCreated)
            {
                noRays.Dispose();
            }
            if (noVectors.IsCreated)
            {
                noVectors.Dispose();
            }
            if (activeShape.IsCreated)
            {
                activeShape.Dispose();
            }
            if (activeFactor.IsCreated)
            {
                activeFactor.Dispose();
            }
        }
        private static void DisposeAll<T>(params NativeArray<T>[] arrays) where T : struct
        {
            for (int i = 0; i < arrays.Length; i++)
            {
                if (arrays[i].IsCreated)
                {
                    arrays[i].Dispose();
                }
            }
        }
        [BurstCompile]
        private struct TopologyJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<int> indices;
            [NativeDisableParallelForRestriction] public NativeArray<int> globalIndex, triPart, triLocal;
            public int vertexOffset, triangleOffset, part;
            public void Execute(int i)
            {
                int t = triangleOffset + i;
                globalIndex[t * 3] = indices[i * 3] + vertexOffset;
                globalIndex[t * 3 + 1] = indices[i * 3 + 1] + vertexOffset;
                globalIndex[t * 3 + 2] = indices[i * 3 + 2] + vertexOffset;
                triPart[t] = part;
                triLocal[t] = i;
            }
        }
        [BurstCompile]
        private struct GatherJob : IJobParallelForTransform
        {
            [ReadOnly] public NativeArray<float4x4> bindposes;
            [WriteOnly] public NativeArray<float4x4> matrices;
            public void Execute(int index, TransformAccess transform) => matrices[index] = math.mul(transform.localToWorldMatrix, bindposes[index]);
        }
        [BurstCompile]
        private struct PartBoundsJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<int2> ranges;
            [ReadOnly] public NativeArray<float4x4> matrices;
            [ReadOnly] public NativeArray<float3> boneOrigins, meshMin, meshMax;
            [ReadOnly] public NativeArray<float> boneRadii, pad;
            [WriteOnly] public NativeArray<float3> boundsMin, boundsMax;
            public void Execute(int part)
            {
                int2 r = ranges[part];
                float3 lo = new float3(float.MaxValue), hi = new float3(float.MinValue);
                float padding = pad[part];
                for (int b = r.x; b < r.x + r.y - 1; b++)
                {
                    float radius = boneRadii[b];
                    if (radius < 0f)
                    {
                        continue;
                    }
                    float4x4 m = matrices[b];
                    float scale = math.max(math.length(m.c0.xyz), math.max(math.length(m.c1.xyz), math.length(m.c2.xyz)));
                    float3 center = math.mul(m, new float4(boneOrigins[b], 1f)).xyz;
                    float reach = (radius + padding) * scale;
                    lo = math.min(lo, center - reach);
                    hi = math.max(hi, center + reach);
                }
                int rendererMatrix = r.x + r.y - 1;
                if (boneRadii[rendererMatrix] >= 0f)
                {
                    float4x4 m = matrices[rendererMatrix];
                    float3 a = meshMin[part] - padding, c = meshMax[part] + padding;
                    for (int k = 0; k < 8; k++)
                    {
                        float3 corner = new float3((k & 1) != 0 ? c.x : a.x, (k & 2) != 0 ? c.y : a.y, (k & 4) != 0 ? c.z : a.z);
                        float3 w = math.mul(m, new float4(corner, 1f)).xyz;
                        lo = math.min(lo, w);
                        hi = math.max(hi, w);
                    }
                }
                boundsMin[part] = lo;
                boundsMax[part] = hi;
            }
        }
        [BurstCompile]
        private struct CullJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> boundsMin, boundsMax;
            [ReadOnly] public NativeArray<BasisRendererRay> rays;
            [ReadOnly] public NativeArray<float3> origins, directions;
            public NativeArray<int> touched;
            public bool useRays;
            public int count;
            public float maxDistance;
            public void Execute(int part)
            {
                if (touched[part] != 0)
                {
                    return;
                }
                float3 lo = boundsMin[part], hi = boundsMax[part];
                if (math.any(lo > hi))
                {
                    return;
                }
                for (int i = 0; i < count; i++)
                {
                    float3 o, d;
                    float max;
                    if (useRays)
                    {
                        o = rays[i].origin;
                        d = math.normalizesafe(rays[i].direction);
                        max = rays[i].maxDistance;
                    }
                    else
                    {
                        o = origins[i];
                        d = math.normalizesafe(directions[i]);
                        max = maxDistance;
                    }
                    float3 inverse = 1f / d, t1 = (lo - o) * inverse, t2 = (hi - o) * inverse;
                    float near = math.cmax(math.min(t1, t2)), far = math.cmin(math.max(t1, t2));
                    if (far >= math.max(near, 0f) && near <= max)
                    {
                        touched[part] = 1;
                        return;
                    }
                }
            }
        }
        [BurstCompile]
        private struct DetectJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<int2> ranges;
            [ReadOnly] public NativeArray<float4x4> matrices;
            [ReadOnly] public NativeArray<int> force, touched;
            [NativeDisableParallelForRestriction] public NativeArray<float4x4> previous;
            public NativeArray<int> stale;
            [WriteOnly] public NativeArray<int> changed;
            public void Execute(int part)
            {
                int2 r = ranges[part];
                bool dirty = force[part] != 0 || stale[part] != 0;
                for (int i = r.x; i < r.x + r.y; i++)
                {
                    float4x4 m = matrices[i], p = previous[i];
                    if (math.all(m.c0 == p.c0) && math.all(m.c1 == p.c1) && math.all(m.c2 == p.c2) && math.all(m.c3 == p.c3))
                    {
                        continue;
                    }
                    dirty = true;
                    previous[i] = m;
                }
                bool now = dirty && touched[part] != 0;
                stale[part] = dirty && !now ? 1 : 0;
                changed[part] = now ? 1 : 0;
            }
        }
        [BurstCompile]
        private struct GroupChangedJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<BasisRayTreeGroup> groups;
            [ReadOnly] public NativeArray<int> partChanged, partStale;
            [WriteOnly] public NativeArray<int> groupChanged, groupStale;
            public void Execute(int g)
            {
                BasisRayTreeGroup group = groups[g];
                int any = 0, old = 0;
                for (int i = group.partStart; i < group.partEnd; i++)
                {
                    any |= partChanged[i];
                    old |= partStale[i];
                }
                groupChanged[g] = any;
                groupStale[g] = old;
            }
        }
        [BurstCompile]
        private struct BlendJob : IJob
        {
            [ReadOnly] public NativeArray<float3> source, delta;
            [ReadOnly] public NativeArray<int> shapes, shapeStart, shapeCount, deltaIndex, changed;
            [ReadOnly] public NativeArray<float> factors;
            [NativeDisableContainerSafetyRestriction] public NativeArray<float3> target;
            public int offset, first, count, part;
            public void Execute()
            {
                if (changed[part] == 0)
                {
                    return;
                }
                NativeArray<float3>.Copy(source, 0, target, offset, source.Length);
                for (int a = first; a < first + count; a++)
                {
                    int s = shapes[a];
                    float f = factors[a];
                    for (int j = shapeStart[s], end = j + shapeCount[s]; j < end; j++)
                    {
                        target[offset + deltaIndex[j]] += delta[j] * f;
                    }
                }
            }
        }
        [BurstCompile]
        private struct SkinJob : IJobParallelFor
        {
            [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<float3> source;
            [ReadOnly] public NativeArray<int> weightStart, weightBone, changed;
            [ReadOnly] public NativeArray<byte> weightCount;
            [ReadOnly] public NativeArray<float> weightValue;
            [ReadOnly] public NativeArray<float4x4> matrices;
            [NativeDisableParallelForRestriction, NativeDisableContainerSafetyRestriction] public NativeArray<float3> world;
            public int sourceOffset, boneOffset, boneCount, fallbackIndex, influences, worldOffset, part;
            public void Execute(int i)
            {
                if (changed[part] == 0)
                {
                    return;
                }
                float4 p = new float4(source[sourceOffset + i], 1f);
                int start = weightStart[i], count = math.min(weightCount[i], influences);
                float3 sum = float3.zero;
                float total = 0f;
                for (int k = 0; k < count; k++)
                {
                    int bone = weightBone[start + k];
                    if ((uint)bone >= (uint)boneCount)
                    {
                        continue;
                    }
                    float w = weightValue[start + k];
                    sum += w * math.mul(matrices[boneOffset + bone], p).xyz;
                    total += w;
                }
                world[worldOffset + i] = total > 1e-6f ? sum / total : math.mul(matrices[fallbackIndex], p).xyz;
            }
        }
        [BurstCompile]
        private struct TransformJob : IJobParallelFor
        {
            [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<float3> source;
            [ReadOnly] public NativeArray<float4x4> matrices;
            [ReadOnly] public NativeArray<int> changed;
            [NativeDisableParallelForRestriction, NativeDisableContainerSafetyRestriction] public NativeArray<float3> world;
            public int sourceOffset, worldOffset, matrixIndex, part;
            public void Execute(int i)
            {
                if (changed[part] == 0)
                {
                    return;
                }
                world[worldOffset + i] = math.mul(matrices[matrixIndex], new float4(source[sourceOffset + i], 1f)).xyz;
            }
        }
        [BurstCompile]
        private struct TriangleBoundsJob : IJobParallelFor
        {
            [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<float3> world;
            [ReadOnly] public NativeArray<int> globalIndex;
            [NativeDisableParallelForRestriction, NativeDisableContainerSafetyRestriction] public NativeArray<float3> triMin, triMax;
            public int start;
            public void Execute(int k)
            {
                int i = start + k;
                float3 a = world[globalIndex[i * 3]], b = world[globalIndex[i * 3 + 1]], c = world[globalIndex[i * 3 + 2]];
                triMin[i] = math.min(a, math.min(b, c));
                triMax[i] = math.max(a, math.max(b, c));
            }
        }
        [BurstCompile]
        private struct BuildJob : IJob
        {
            [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<float3> triMin, triMax;
            [NativeDisableContainerSafetyRestriction] public NativeArray<int> order, groupNodeCount;
            [NativeDisableContainerSafetyRestriction] public NativeArray<float> keys;
            [NativeDisableContainerSafetyRestriction] public NativeArray<BasisRayBvhNode> nodes;
            public int group, start, count, nodeStart;
            private static float Area(float3 lo, float3 hi)
            {
                float3 e = math.max(hi - lo, 0f);
                return 2f * (e.x * e.y + e.y * e.z + e.z * e.x);
            }
            public void Execute()
            {
                for (int i = start; i < start + count; i++)
                {
                    order[i] = i;
                }
                NativeList<int3> stack = new NativeList<int3>(64, Allocator.Temp);
                NativeArray<int> binCount = new NativeArray<int>(Bins, Allocator.Temp);
                NativeArray<float3> binLo = new NativeArray<float3>(Bins, Allocator.Temp), binHi = new NativeArray<float3>(Bins, Allocator.Temp);
                NativeArray<float> rightArea = new NativeArray<float>(Bins, Allocator.Temp);
                NativeArray<int> rightCount = new NativeArray<int>(Bins, Allocator.Temp);
                stack.Add(new int3(start, count, -1));
                int used = 0;
                while (stack.Length > 0)
                {
                    int3 e = stack[stack.Length - 1];
                    stack.RemoveAt(stack.Length - 1);
                    int id = nodeStart + used++;
                    if (e.z >= 0)
                    {
                        BasisRayBvhNode parent = nodes[e.z];
                        parent.right = id;
                        nodes[e.z] = parent;
                    }
                    float3 lo = new float3(float.MaxValue), hi = new float3(float.MinValue), clo = lo, chi = hi;
                    for (int i = e.x; i < e.x + e.y; i++)
                    {
                        int t = order[i];
                        float3 mn = triMin[t], mx = triMax[t], c = (mn + mx) * 0.5f;
                        lo = math.min(lo, mn);
                        hi = math.max(hi, mx);
                        clo = math.min(clo, c);
                        chi = math.max(chi, c);
                    }
                    BasisRayBvhNode node = new BasisRayBvhNode { min = lo, max = hi, start = e.x, count = e.y, right = -1 };
                    if (e.y <= 4)
                    {
                        nodes[id] = node;
                        continue;
                    }
                    float3 extent = chi - clo;
                    int axis = extent.x >= extent.y && extent.x >= extent.z ? 0 : extent.y >= extent.z ? 1 : 2;
                    int mid = -1;
                    if (extent[axis] > 1e-9f)
                    {
                        float scale = Bins / extent[axis], origin = clo[axis];
                        for (int b = 0; b < Bins; b++)
                        {
                            binCount[b] = 0;
                            binLo[b] = new float3(float.MaxValue);
                            binHi[b] = new float3(float.MinValue);
                        }
                        for (int i = e.x; i < e.x + e.y; i++)
                        {
                            int t = order[i];
                            float3 mn = triMin[t], mx = triMax[t];
                            int b = math.min(Bins - 1, (int)(((mn[axis] + mx[axis]) * 0.5f - origin) * scale));
                            binCount[b]++;
                            binLo[b] = math.min(binLo[b], mn);
                            binHi[b] = math.max(binHi[b], mx);
                        }
                        float3 rlo = new float3(float.MaxValue), rhi = new float3(float.MinValue);
                        int rn = 0;
                        for (int b = Bins - 1; b > 0; b--)
                        {
                            rn += binCount[b];
                            rlo = math.min(rlo, binLo[b]);
                            rhi = math.max(rhi, binHi[b]);
                            rightCount[b] = rn;
                            rightArea[b] = rn > 0 ? Area(rlo, rhi) : 0f;
                        }
                        float3 llo = new float3(float.MaxValue), lhi = new float3(float.MinValue);
                        int ln = 0, bestBin = -1;
                        float best = float.MaxValue;
                        for (int b = 0; b < Bins - 1; b++)
                        {
                            ln += binCount[b];
                            llo = math.min(llo, binLo[b]);
                            lhi = math.max(lhi, binHi[b]);
                            if (ln == 0 || rightCount[b + 1] == 0)
                            {
                                continue;
                            }
                            float cost = Area(llo, lhi) * ((ln + 3) >> 2) + rightArea[b + 1] * ((rightCount[b + 1] + 3) >> 2);
                            if (cost < best)
                            {
                                best = cost;
                                bestBin = b;
                            }
                        }
                        float area = Area(lo, hi);
                        if (bestBin >= 0 && e.y <= MaxLeafSize && area * ((e.y + 3) >> 2) <= area + best)
                        {
                            nodes[id] = node;
                            continue;
                        }
                        if (bestBin >= 0)
                        {
                            int i = e.x, j = e.x + e.y - 1;
                            while (i <= j)
                            {
                                int t = order[i];
                                int b = math.min(Bins - 1, (int)(((triMin[t][axis] + triMax[t][axis]) * 0.5f - origin) * scale));
                                if (b <= bestBin)
                                {
                                    i++;
                                }
                                else
                                {
                                    order[i] = order[j];
                                    order[j] = t;
                                    j--;
                                }
                            }
                            mid = i - e.x;
                            if (mid <= 0 || mid >= e.y)
                            {
                                mid = -1;
                            }
                        }
                    }
                    if (mid < 0)
                    {
                        if (e.y <= MaxLeafSize / 2)
                        {
                            nodes[id] = node;
                            continue;
                        }
                        for (int i = e.x; i < e.x + e.y; i++)
                        {
                            int t = order[i];
                            keys[i] = (triMin[t][axis] + triMax[t][axis]) * 0.5f;
                        }
                        mid = e.y / 2;
                        Select(e.x, e.x + e.y - 1, e.x + mid);
                    }
                    node.count = 0;
                    nodes[id] = node;
                    stack.Add(new int3(e.x + mid, e.y - mid, id));
                    stack.Add(new int3(e.x, mid, -1));
                }
                stack.Dispose();
                binCount.Dispose();
                binLo.Dispose();
                binHi.Dispose();
                rightArea.Dispose();
                rightCount.Dispose();
                groupNodeCount[group] = used;
            }
            private void Select(int lo, int hi, int k)
            {
                while (hi > lo)
                {
                    float pivot = keys[(lo + hi) >> 1];
                    int i = lo, j = hi;
                    while (i <= j)
                    {
                        while (keys[i] < pivot)
                        {
                            i++;
                        }
                        while (keys[j] > pivot)
                        {
                            j--;
                        }
                        if (i <= j)
                        {
                            float tk = keys[i];
                            keys[i] = keys[j];
                            keys[j] = tk;
                            int to = order[i];
                            order[i] = order[j];
                            order[j] = to;
                            i++;
                            j--;
                        }
                    }
                    if (k <= j)
                    {
                        hi = j;
                    }
                    else if (k >= i)
                    {
                        lo = i;
                    }
                    else
                    {
                        return;
                    }
                }
            }
        }
        [BurstCompile]
        private struct ReorderJob : IJobParallelFor
        {
            [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<int> order;
            [ReadOnly] public NativeArray<int> globalIndex, triPart, triLocal;
            [NativeDisableParallelForRestriction, NativeDisableContainerSafetyRestriction] public NativeArray<int3> sortedIndex;
            [NativeDisableParallelForRestriction, NativeDisableContainerSafetyRestriction] public NativeArray<int> sortedPart, sortedLocal;
            public int start;
            public void Execute(int k)
            {
                int i = start + k, t = order[i];
                sortedIndex[i] = new int3(globalIndex[t * 3], globalIndex[t * 3 + 1], globalIndex[t * 3 + 2]);
                sortedPart[i] = triPart[t];
                sortedLocal[i] = triLocal[t];
            }
        }
        private static void Fill(ref BasisRayTrianglePack pack, int lane, float3 a, float3 b, float3 c)
        {
            float3 e1 = b - a, e2 = c - a;
            pack.v0x[lane] = a.x;
            pack.v0y[lane] = a.y;
            pack.v0z[lane] = a.z;
            pack.e1x[lane] = e1.x;
            pack.e1y[lane] = e1.y;
            pack.e1z[lane] = e1.z;
            pack.e2x[lane] = e2.x;
            pack.e2y[lane] = e2.y;
            pack.e2z[lane] = e2.z;
        }
        [BurstCompile]
        private struct PackJob : IJob
        {
            [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<float3> world;
            [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<int3> sortedIndex;
            [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<BasisRayBvhNode> nodes;
            [NativeDisableContainerSafetyRestriction] public NativeArray<int> nodePack, groupNodeCount, groupPackCount;
            [NativeDisableContainerSafetyRestriction] public NativeArray<BasisRayTrianglePack> packs;
            public int group, nodeStart, packStart;
            public void Execute()
            {
                int used = 0;
                for (int n = nodeStart; n < nodeStart + groupNodeCount[group]; n++)
                {
                    BasisRayBvhNode node = nodes[n];
                    if (node.right >= 0)
                    {
                        continue;
                    }
                    nodePack[n] = packStart + used;
                    for (int k = 0; k < node.count; k += 4)
                    {
                        BasisRayTrianglePack pack = default;
                        pack.triangle = new int4(-1);
                        for (int lane = 0; lane < 4; lane++)
                        {
                            int t = node.start + k + lane;
                            if (t >= node.start + node.count)
                            {
                                break;
                            }
                            int3 i = sortedIndex[t];
                            pack.triangle[lane] = t;
                            Fill(ref pack, lane, world[i.x], world[i.y], world[i.z]);
                        }
                        packs[packStart + used++] = pack;
                    }
                }
                groupPackCount[group] = used;
            }
        }
        [BurstCompile]
        private struct RefitPackJob : IJobParallelFor
        {
            [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<float3> world;
            [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<int3> sortedIndex;
            [ReadOnly] public NativeArray<int> groupChanged;
            [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<int> groupPackCount;
            [NativeDisableParallelForRestriction, NativeDisableContainerSafetyRestriction] public NativeArray<BasisRayTrianglePack> packs;
            [NativeDisableParallelForRestriction, NativeDisableContainerSafetyRestriction] public NativeArray<float3> packMin, packMax;
            public int group, start;
            public void Execute(int k)
            {
                if (groupChanged[group] == 0 || k >= groupPackCount[group])
                {
                    return;
                }
                BasisRayTrianglePack pack = packs[start + k];
                float3 lo = new float3(float.MaxValue), hi = new float3(float.MinValue);
                for (int lane = 0; lane < 4; lane++)
                {
                    int t = pack.triangle[lane];
                    if (t < 0)
                    {
                        break;
                    }
                    int3 i = sortedIndex[t];
                    float3 a = world[i.x], b = world[i.y], c = world[i.z];
                    Fill(ref pack, lane, a, b, c);
                    lo = math.min(lo, math.min(a, math.min(b, c)));
                    hi = math.max(hi, math.max(a, math.max(b, c)));
                }
                packs[start + k] = pack;
                packMin[start + k] = lo;
                packMax[start + k] = hi;
            }
        }
        [BurstCompile]
        private struct RefitJob : IJob
        {
            [NativeDisableContainerSafetyRestriction] public NativeArray<BasisRayBvhNode> nodes;
            [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<int> nodePack, groupNodeCount;
            [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeArray<float3> packMin, packMax;
            [ReadOnly] public NativeArray<int> groupChanged;
            public int group, nodeStart;
            public void Execute()
            {
                if (groupChanged[group] == 0)
                {
                    return;
                }
                for (int n = nodeStart + groupNodeCount[group] - 1; n >= nodeStart; n--)
                {
                    BasisRayBvhNode node = nodes[n];
                    if (node.right >= 0)
                    {
                        BasisRayBvhNode left = nodes[n + 1], right = nodes[node.right];
                        node.min = math.min(left.min, right.min);
                        node.max = math.max(left.max, right.max);
                    }
                    else
                    {
                        float3 lo = new float3(float.MaxValue), hi = new float3(float.MinValue);
                        int first = nodePack[n];
                        for (int k = first; k < first + ((node.count + 3) >> 2); k++)
                        {
                            lo = math.min(lo, packMin[k]);
                            hi = math.max(hi, packMax[k]);
                        }
                        node.min = lo;
                        node.max = hi;
                    }
                    nodes[n] = node;
                }
            }
        }
        private struct BvhView
        {
            [ReadOnly] public NativeArray<BasisRayBvhNode> nodes;
            [ReadOnly] public NativeArray<int> nodePack, groupNodeCount, groupStale, sortedPart, sortedLocal;
            [ReadOnly] public NativeArray<BasisRayTrianglePack> packs;
            [ReadOnly] public NativeArray<BasisRayTreeGroup> groups;
            [ReadOnly] public NativeArray<ulong> mask;
            public int groupCount;
            public bool empty, filtered, perRenderer;
            private bool Allowed(int part) => (mask[part >> 6] & (1ul << (part & 63))) != 0;
            private static bool Slab(in BasisRayBvhNode node, float3 origin, float3 inverse, float best, out float near)
            {
                float3 t1 = (node.min - origin) * inverse, t2 = (node.max - origin) * inverse;
                near = math.cmax(math.min(t1, t2));
                float far = math.cmin(math.max(t1, t2));
                return far >= math.max(near, 0f) && near <= best;
            }
            public bool Trace(float3 o, float3 direction, float maxDistance, out float best, out float3 d, out int foundPack, out int foundLane)
            {
                d = math.normalizesafe(direction);
                best = maxDistance;
                foundPack = foundLane = -1;
                if (empty || math.all(d == 0f) || !(maxDistance > 0f))
                {
                    return false;
                }
                float3 inverse = 1f / d;
                FixedList512Bytes<int2> roots = default;
                for (int g = 0; g < groupCount; g++)
                {
                    if (groupNodeCount[g] == 0 || groupStale[g] != 0)
                    {
                        continue;
                    }
                    if (filtered && perRenderer && !Allowed(groups[g].partStart))
                    {
                        continue;
                    }
                    int root = groups[g].nodeStart;
                    if (!Slab(nodes[root], o, inverse, best, out float near))
                    {
                        continue;
                    }
                    int2 entry = new int2(math.asint(math.max(near, 0f)), root);
                    int at = roots.Length;
                    roots.Add(entry);
                    while (at > 0 && math.asfloat(roots[at - 1].x) > math.asfloat(entry.x))
                    {
                        roots[at] = roots[at - 1];
                        at--;
                    }
                    roots[at] = entry;
                }
                bool laneFilter = filtered && !perRenderer;
                FixedList512Bytes<int> stack = default;
                for (int r = 0; r < roots.Length; r++)
                {
                    if (math.asfloat(roots[r].x) > best)
                    {
                        break;
                    }
                    stack.Add(roots[r].y);
                    while (stack.Length > 0)
                    {
                        int n = stack[stack.Length - 1];
                        stack.RemoveAt(stack.Length - 1);
                        BasisRayBvhNode node = nodes[n];
                        if (!Slab(node, o, inverse, best, out _))
                        {
                            continue;
                        }
                        if (node.right >= 0)
                        {
                            bool hitLeft = Slab(nodes[n + 1], o, inverse, best, out float nearLeft), hitRight = Slab(nodes[node.right], o, inverse, best, out float nearRight);
                            if (hitLeft && hitRight)
                            {
                                if (nearLeft <= nearRight)
                                {
                                    stack.Add(node.right);
                                    stack.Add(n + 1);
                                }
                                else
                                {
                                    stack.Add(n + 1);
                                    stack.Add(node.right);
                                }
                            }
                            else if (hitLeft)
                            {
                                stack.Add(n + 1);
                            }
                            else if (hitRight)
                            {
                                stack.Add(node.right);
                            }
                            continue;
                        }
                        int first = nodePack[n], last = first + ((node.count + 3) >> 2);
                        for (int k = first; k < last; k++)
                        {
                            BasisRayTrianglePack p = packs[k];
                            float4 px = d.y * p.e2z - d.z * p.e2y, py = d.z * p.e2x - d.x * p.e2z, pz = d.x * p.e2y - d.y * p.e2x;
                            float4 det = p.e1x * px + p.e1y * py + p.e1z * pz, inv = 1f / det;
                            float4 sx = o.x - p.v0x, sy = o.y - p.v0y, sz = o.z - p.v0z;
                            float4 u = (sx * px + sy * py + sz * pz) * inv;
                            float4 qx = sy * p.e1z - sz * p.e1y, qy = sz * p.e1x - sx * p.e1z, qz = sx * p.e1y - sy * p.e1x;
                            float4 v = (d.x * qx + d.y * qy + d.z * qz) * inv, t = (p.e2x * qx + p.e2y * qy + p.e2z * qz) * inv;
                            bool4 valid = (math.abs(det) >= 1e-12f) & (u >= 0f) & (u <= 1f) & (v >= 0f) & (u + v <= 1f) & (t >= 0f) & (t < best) & (p.triangle >= 0);
                            if (laneFilter)
                            {
                                for (int lane = 0; lane < 4; lane++)
                                {
                                    if (valid[lane] && !Allowed(sortedPart[p.triangle[lane]]))
                                    {
                                        valid[lane] = false;
                                    }
                                }
                            }
                            if (!math.any(valid))
                            {
                                continue;
                            }
                            float4 masked = math.select(new float4(float.MaxValue), t, valid);
                            float m = math.cmin(masked);
                            int lane0 = masked.x == m ? 0 : masked.y == m ? 1 : masked.z == m ? 2 : 3;
                            best = m;
                            foundPack = k;
                            foundLane = lane0;
                        }
                    }
                }
                return foundPack >= 0;
            }
            public float3 Normal(int pack, int lane, float3 d)
            {
                BasisRayTrianglePack p = packs[pack];
                float3 normal = math.normalizesafe(math.cross(new float3(p.e1x[lane], p.e1y[lane], p.e1z[lane]), new float3(p.e2x[lane], p.e2y[lane], p.e2z[lane])));
                return math.dot(normal, d) > 0f ? -normal : normal;
            }
            public int Triangle(int pack, int lane) => packs[pack].triangle[lane];
        }
        [BurstCompile]
        private struct RaycastJob : IJobParallelFor
        {
            public BvhView bvh;
            [ReadOnly] public NativeArray<BasisRendererRay> rays;
            [WriteOnly] public NativeArray<BasisRendererBatchHit> hits;
            public void Execute(int index)
            {
                BasisRendererRay ray = rays[index];
                if (!bvh.Trace(ray.origin, ray.direction, ray.maxDistance, out float best, out float3 d, out int pack, out int lane))
                {
                    hits[index] = default;
                    return;
                }
                int t = bvh.Triangle(pack, lane);
                hits[index] = new BasisRendererBatchHit { hit = true, distance = best, point = ray.origin + d * best, normal = bvh.Normal(pack, lane, d), renderer = bvh.sortedPart[t], triangle = bvh.sortedLocal[t] };
            }
        }
        [BurstCompile]
        private struct RaycastStreamJob : IJobParallelFor
        {
            public BvhView bvh;
            [ReadOnly] public NativeArray<float3> origins, directions;
            [WriteOnly] public NativeArray<float> distances;
            [WriteOnly] public NativeArray<float3> points, normals;
            [WriteOnly] public NativeArray<int> renderers;
            public float maxDistance;
            public void Execute(int index)
            {
                float3 o = origins[index];
                if (!bvh.Trace(o, directions[index], maxDistance, out float best, out float3 d, out int pack, out int lane))
                {
                    distances[index] = -1f;
                    points[index] = o;
                    normals[index] = float3.zero;
                    renderers[index] = -1;
                    return;
                }
                distances[index] = best;
                points[index] = o + d * best;
                normals[index] = bvh.Normal(pack, lane, d);
                renderers[index] = bvh.sortedPart[bvh.Triangle(pack, lane)];
            }
        }
    }
}
