using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Rendering;
namespace Basis.Scripts.BasisSdk.Interactions
{
    public struct BasisRendererGpuRay
    {
        public float3 origin;
        public float maxDistance;
        public float3 direction;
        public float pad;
    }
    public struct BasisRendererGpuHit
    {
        public float3 point;
        public float distance;
        public float3 normal;
        public int renderer, triangle, hit;
    }
    public struct BasisRendererGpuPart
    {
        public int boneOffset, boneCount, rendererMatrix, influences, shapeOffset, vertexOffset, meshVertexOffset;
    }
    public struct BasisRendererGpuNode
    {
        public uint lo01, lo2hi0, hi12;
        public int link, extra;
    }
    [NoAutoStaticsCleanup]
    public sealed class BasisRendererRayBatchGpu : IDisposable
    {
        public const string ShaderResource = "BasisRendererRays";
        public const int RayStride = 32, HitStride = 40, NodeStride = 20, GroupSize = 64, MaxGroups = 65535;
        public delegate GraphicsBuffer BufferAllocator(GraphicsBuffer.Target target, int count, int stride);
        public static bool Supported => SystemInfo.supportsComputeShaders;
        private static readonly int baseVerticesId = Shader.PropertyToID("_BaseVertices"), vertexPartId = Shader.PropertyToID("_VertexPart"), weightRangeId = Shader.PropertyToID("_WeightRange"), weightBoneId = Shader.PropertyToID("_WeightBone"), weightValueId = Shader.PropertyToID("_WeightValue"), blendRangeId = Shader.PropertyToID("_BlendRange"), blendShapeId = Shader.PropertyToID("_BlendShape"), blendDeltaId = Shader.PropertyToID("_BlendDelta"), shapeFactorsId = Shader.PropertyToID("_ShapeFactors"), matricesId = Shader.PropertyToID("_Matrices"), partsId = Shader.PropertyToID("_Parts"), partChangedId = Shader.PropertyToID("_PartChanged"), worldId = Shader.PropertyToID("_World"), vertexCountId = Shader.PropertyToID("_VertexCount");
        private static readonly int worldInId = Shader.PropertyToID("_WorldIn"), triangleIndicesId = Shader.PropertyToID("_TriangleIndices"), groupChangedId = Shader.PropertyToID("_GroupChanged"), nodeLevelId = Shader.PropertyToID("_NodeLevel"), nodeGroupId = Shader.PropertyToID("_NodeGroup"), groupOriginInId = Shader.PropertyToID("_GroupOrigin"), nodesOutId = Shader.PropertyToID("_NodesOut"), levelStartId = Shader.PropertyToID("_LevelStart"), levelCountId = Shader.PropertyToID("_LevelCount");
        private static readonly int raysId = Shader.PropertyToID("_Rays"), hitsId = Shader.PropertyToID("_Hits"), rayCountId = Shader.PropertyToID("_RayCount"), rayOffsetId = Shader.PropertyToID("_RayOffset"), probeOutId = Shader.PropertyToID("_ProbeOut");
        private static readonly int nodesId = Shader.PropertyToID("_BasisRayNodes"), indicesId = Shader.PropertyToID("_BasisRayIndices"), verticesId = Shader.PropertyToID("_BasisRayVertices"), trianglePartId = Shader.PropertyToID("_BasisRayTrianglePart"), triangleLocalId = Shader.PropertyToID("_BasisRayTriangleLocal"), groupsId = Shader.PropertyToID("_BasisRayGroups"), groupNodeCountId = Shader.PropertyToID("_BasisRayGroupNodeCount"), groupOriginId = Shader.PropertyToID("_BasisRayGroupOrigin"), groupCountId = Shader.PropertyToID("_BasisRayGroupCount");
        public readonly BasisRendererRayBatch Cpu = new BasisRendererRayBatch { RebuildInterval = 0 };
        private readonly ComputeShader shader;
        private readonly BufferAllocator allocate;
        private readonly int skinKernel, refitNodesKernel, traceKernel, probeKernel;
        private readonly List<int2> levels = new List<int2>();
        private readonly List<GraphicsBuffer> owned = new List<GraphicsBuffer>();
        private readonly List<IDisposable> temps = new List<IDisposable>();
        private GraphicsBuffer baseVertices, vertexPart, weightRange, weightBone, weightValue, blendRange, blendShape, blendDelta, shapeFactors, matrices, partsBuffer, partChanged, world, triangleIndices, groupChanged, trianglePart, triangleLocal, nodeLevel, nodeGroup, nodes, groups, groupNodeCount, groupOrigin, rayScratch, hitScratch;
        private NativeArray<float> factors;
        private NativeArray<int> changedAll;
        private NativeArray<float3> origins;
        private int[] shapeOffsets = new int[0], groupMatrix = new int[0];
        private int uploadedVersion = -1, shapeTotal, probe = -1;
        private bool forceAll = true;
        public long GpuBytes { get; private set; }
        public long GeometryBytes => GpuBytes - Size(rayScratch) - Size(hitScratch);
        public int VertexCount => Cpu.VertexCount;
        public int TriangleCount => Cpu.TriangleCount;
        public int RendererCount => Cpu.RendererCount;
        public int UniqueMeshes { get; private set; }
        public bool Ready => uploadedVersion >= 0;
        public bool CompiledWithDxc
        {
            get
            {
                bool unused = NearestFirstTraversal;
                return (probe & 2) != 0;
            }
        }
        public BasisRendererRayBatchGpu(ComputeShader computeShader = null, BufferAllocator allocator = null)
        {
            shader = computeShader != null ? computeShader : Resources.Load<ComputeShader>(ShaderResource);
            if (shader == null)
            {
                throw new InvalidOperationException("The " + ShaderResource + " compute shader is missing from Resources.");
            }
            allocate = allocator ?? ((target, count, stride) => new GraphicsBuffer(target, count, stride));
            skinKernel = shader.FindKernel("Skin");
            refitNodesKernel = shader.FindKernel("RefitNodes");
            traceKernel = shader.FindKernel("Trace");
            probeKernel = shader.FindKernel("Probe");
        }
        private static long Size(GraphicsBuffer b) => b != null && b.IsValid() ? (long)b.count * b.stride : 0;
        public bool Add(Renderer renderer) => Cpu.Add(renderer);
        public bool Remove(Renderer renderer) => Cpu.Remove(renderer);
        public void Clear() => Cpu.Clear();
        public Renderer GetRenderer(int index) => Cpu.GetRenderer(index);
        public bool NearestFirstTraversal
        {
            get
            {
                if (probe >= 0)
                {
                    return (probe & 1) == 1;
                }
                using GraphicsBuffer output = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4);
                shader.SetBuffer(probeKernel, probeOutId, output);
                shader.Dispatch(probeKernel, 1, 1, 1);
                int[] value = new int[1];
                output.GetData(value);
                probe = value[0];
                return (probe & 1) == 1;
            }
        }
        private GraphicsBuffer Buffer(GraphicsBuffer current, int count, int stride)
        {
            count = math.max(1, count);
            if (current != null && current.IsValid() && current.count >= count && current.stride == stride)
            {
                return current;
            }
            if (current != null)
            {
                owned.Remove(current);
                GpuBytes -= Size(current);
                current.Dispose();
            }
            GraphicsBuffer created = allocate(GraphicsBuffer.Target.Structured, count, stride);
            owned.Add(created);
            GpuBytes += (long)count * stride;
            return created;
        }
        private T Temp<T>(T array) where T : struct, IDisposable
        {
            temps.Add(array);
            return array;
        }
        private static int Influences(BasisRendererRayBatch.Part p)
        {
            if (p.skinned == null)
            {
                return 255;
            }
            int influences = p.skinned.quality == SkinQuality.Auto ? (int)QualitySettings.skinWeights : (int)p.skinned.quality;
            return influences <= 0 ? 255 : influences;
        }
        private void Upload()
        {
            try
            {
                UploadData();
            }
            finally
            {
                for (int i = 0; i < temps.Count; i++)
                {
                    temps[i].Dispose();
                }
                temps.Clear();
            }
        }
        private void UploadData()
        {
            BasisRendererRayBatch c = Cpu;
            List<BasisRendererRayBatch.Part> parts = c.parts;
            int v = c.vertexCount, t = c.triangleCount, m = c.matrixCount, g = c.groupCount;
            Dictionary<BasisRendererRayBatch.MeshEntry, int> meshOffset = new Dictionary<BasisRendererRayBatch.MeshEntry, int>();
            List<BasisRendererRayBatch.MeshEntry> unique = new List<BasisRendererRayBatch.MeshEntry>();
            int meshVertices = 0, weightTotal = 0, blendTotal = 0;
            shapeTotal = 0;
            shapeOffsets = new int[parts.Count];
            for (int i = 0; i < parts.Count; i++)
            {
                BasisRendererRayBatch.MeshEntry mesh = parts[i].mesh;
                shapeOffsets[i] = shapeTotal;
                shapeTotal += mesh.shapes;
                if (meshOffset.ContainsKey(mesh))
                {
                    continue;
                }
                meshOffset.Add(mesh, meshVertices);
                unique.Add(mesh);
                meshVertices += mesh.vertices.Length;
                if (mesh.weighted)
                {
                    weightTotal += mesh.weightValue.Length;
                }
                if (mesh.shapes > 0)
                {
                    blendTotal += mesh.deltaIndex.Length;
                }
            }
            UniqueMeshes = unique.Count;
            NativeArray<float3> baseData = Temp(new NativeArray<float3>(math.max(1, meshVertices), Allocator.Temp));
            NativeArray<int2> weightRangeData = Temp(new NativeArray<int2>(math.max(1, meshVertices), Allocator.Temp));
            NativeArray<int> weightBoneData = Temp(new NativeArray<int>(math.max(1, weightTotal), Allocator.Temp));
            NativeArray<float> weightValueData = Temp(new NativeArray<float>(math.max(1, weightTotal), Allocator.Temp));
            NativeArray<int2> blendRangeData = Temp(new NativeArray<int2>(math.max(1, meshVertices), Allocator.Temp));
            NativeArray<int> blendShapeData = Temp(new NativeArray<int>(math.max(1, blendTotal), Allocator.Temp));
            NativeArray<float3> blendDeltaData = Temp(new NativeArray<float3>(math.max(1, blendTotal), Allocator.Temp));
            int weightAt = 0, blendAt = 0;
            foreach (BasisRendererRayBatch.MeshEntry mesh in unique)
            {
                int offset = meshOffset[mesh], n = mesh.vertices.Length;
                NativeArray<float3>.Copy(mesh.vertices, 0, baseData, offset, n);
                if (mesh.weighted)
                {
                    for (int k = 0; k < n; k++)
                    {
                        weightRangeData[offset + k] = new int2(weightAt + mesh.weightStart[k], mesh.weightCount[k]);
                    }
                    NativeArray<int>.Copy(mesh.weightBone, 0, weightBoneData, weightAt, mesh.weightBone.Length);
                    NativeArray<float>.Copy(mesh.weightValue, 0, weightValueData, weightAt, mesh.weightValue.Length);
                    weightAt += mesh.weightValue.Length;
                }
                if (mesh.shapes > 0)
                {
                    int[] count = new int[n];
                    for (int s = 0; s < mesh.shapes; s++)
                    {
                        for (int j = mesh.shapeStart[s]; j < mesh.shapeStart[s] + mesh.shapeCount[s]; j++)
                        {
                            count[mesh.deltaIndex[j]]++;
                        }
                    }
                    int[] cursor = new int[n];
                    int run = blendAt;
                    for (int k = 0; k < n; k++)
                    {
                        blendRangeData[offset + k] = new int2(run, count[k]);
                        cursor[k] = run;
                        run += count[k];
                    }
                    for (int s = 0; s < mesh.shapes; s++)
                    {
                        for (int j = mesh.shapeStart[s]; j < mesh.shapeStart[s] + mesh.shapeCount[s]; j++)
                        {
                            int at = cursor[mesh.deltaIndex[j]]++;
                            blendShapeData[at] = s;
                            blendDeltaData[at] = mesh.delta[j];
                        }
                    }
                    blendAt = run;
                }
            }
            NativeArray<int> partData = Temp(new NativeArray<int>(math.max(1, v), Allocator.Temp));
            NativeArray<BasisRendererGpuPart> partInfo = Temp(new NativeArray<BasisRendererGpuPart>(math.max(1, parts.Count), Allocator.Temp));
            for (int i = 0; i < parts.Count; i++)
            {
                BasisRendererRayBatch.Part p = parts[i];
                for (int k = 0; k < p.mesh.vertices.Length; k++)
                {
                    partData[p.vertexOffset + k] = i;
                }
                partInfo[i] = new BasisRendererGpuPart { boneOffset = p.boneOffset, boneCount = p.boneCount, rendererMatrix = p.boneOffset + p.boneCount, influences = Influences(p), shapeOffset = shapeOffsets[i], vertexOffset = p.vertexOffset, meshVertexOffset = meshOffset[p.mesh] };
            }
            int gpuNodes = 0;
            int[] gpuStart = new int[g];
            for (int gi = 0; gi < g; gi++)
            {
                gpuStart[gi] = gpuNodes;
                gpuNodes += c.groupNodeCount[gi];
            }
            NativeArray<BasisRendererGpuNode> nodeData = Temp(new NativeArray<BasisRendererGpuNode>(math.max(1, gpuNodes), Allocator.Temp));
            NativeArray<int> nodeGroupData = Temp(new NativeArray<int>(math.max(1, gpuNodes), Allocator.Temp));
            NativeArray<BasisRayTreeGroup> groupData = Temp(new NativeArray<BasisRayTreeGroup>(math.max(1, g), Allocator.Temp));
            List<int2> depthOfNode = new List<int2>(gpuNodes);
            groupMatrix = new int[g];
            for (int gi = 0; gi < g; gi++)
            {
                BasisRayTreeGroup group = c.groups[gi];
                int count = c.groupNodeCount[gi], nodeStart = group.nodeStart, delta = gpuStart[gi] - nodeStart;
                BasisRendererRayBatch.Part first = parts[group.partStart];
                groupMatrix[gi] = first.boneOffset + first.boneCount;
                BasisRayTreeGroup remapped = group;
                remapped.nodeStart = gpuStart[gi];
                groupData[gi] = remapped;
                if (count == 0)
                {
                    continue;
                }
                int[] skip = new int[count];
                for (int n = nodeStart + count - 1; n >= nodeStart; n--)
                {
                    BasisRayBvhNode node = c.nodes[n];
                    skip[n - nodeStart] = node.right >= 0 ? skip[node.right - nodeStart] : n + 1;
                }
                for (int n = nodeStart; n < nodeStart + count; n++)
                {
                    BasisRayBvhNode node = c.nodes[n];
                    int gpu = n + delta;
                    nodeGroupData[gpu] = gi;
                    nodeData[gpu] = node.right >= 0 ? new BasisRendererGpuNode { lo01 = 0xfc00fc00u, lo2hi0 = 0x7c00fc00u, hi12 = 0x7c007c00u, link = node.right + delta, extra = skip[n - nodeStart] + delta } : new BasisRendererGpuNode { lo01 = 0xfc00fc00u, lo2hi0 = 0x7c00fc00u, hi12 = 0x7c007c00u, link = -(node.start + 1), extra = node.count };
                }
                Stack<int2> stack = new Stack<int2>();
                stack.Push(new int2(nodeStart, 0));
                while (stack.Count > 0)
                {
                    int2 e = stack.Pop();
                    depthOfNode.Add(new int2(e.x + delta, e.y));
                    BasisRayBvhNode node = c.nodes[e.x];
                    if (node.right < 0)
                    {
                        continue;
                    }
                    stack.Push(new int2(node.right, e.y + 1));
                    stack.Push(new int2(e.x + 1, e.y + 1));
                }
            }
            depthOfNode.Sort((a, b) => b.y.CompareTo(a.y));
            levels.Clear();
            NativeArray<int> levelData = Temp(new NativeArray<int>(math.max(1, depthOfNode.Count), Allocator.Temp));
            for (int k = 0; k < depthOfNode.Count; k++)
            {
                levelData[k] = depthOfNode[k].x;
                if (k == 0 || depthOfNode[k].y != depthOfNode[k - 1].y)
                {
                    levels.Add(new int2(k, 0));
                }
                int2 last = levels[levels.Count - 1];
                levels[levels.Count - 1] = new int2(last.x, last.y + 1);
            }
            NativeArray<int> groupNodeData = Temp(new NativeArray<int>(math.max(1, g), Allocator.Temp));
            for (int gi = 0; gi < g; gi++)
            {
                groupNodeData[gi] = c.groupNodeCount[gi];
            }
            baseVertices = Buffer(baseVertices, meshVertices, 12);
            weightRange = Buffer(weightRange, meshVertices, 8);
            weightBone = Buffer(weightBone, weightTotal, 4);
            weightValue = Buffer(weightValue, weightTotal, 4);
            blendRange = Buffer(blendRange, meshVertices, 8);
            blendShape = Buffer(blendShape, blendTotal, 4);
            blendDelta = Buffer(blendDelta, blendTotal, 12);
            shapeFactors = Buffer(shapeFactors, shapeTotal, 4);
            vertexPart = Buffer(vertexPart, v, 4);
            matrices = Buffer(matrices, m, 64);
            partsBuffer = Buffer(partsBuffer, parts.Count, 28);
            partChanged = Buffer(partChanged, parts.Count, 4);
            world = Buffer(world, v, 12);
            triangleIndices = Buffer(triangleIndices, t, 12);
            trianglePart = Buffer(trianglePart, t, 4);
            triangleLocal = Buffer(triangleLocal, t, 4);
            groupChanged = Buffer(groupChanged, g, 4);
            groupOrigin = Buffer(groupOrigin, g, 12);
            nodeLevel = Buffer(nodeLevel, depthOfNode.Count, 4);
            nodeGroup = Buffer(nodeGroup, gpuNodes, 4);
            nodes = Buffer(nodes, gpuNodes, NodeStride);
            groups = Buffer(groups, g, 20);
            groupNodeCount = Buffer(groupNodeCount, g, 4);
            baseVertices.SetData(baseData);
            weightRange.SetData(weightRangeData);
            weightBone.SetData(weightBoneData);
            weightValue.SetData(weightValueData);
            blendRange.SetData(blendRangeData);
            blendShape.SetData(blendShapeData);
            blendDelta.SetData(blendDeltaData);
            vertexPart.SetData(partData);
            partsBuffer.SetData(partInfo);
            if (t > 0)
            {
                triangleIndices.SetData(c.sortedIndex, 0, 0, t);
                trianglePart.SetData(c.sortedPart, 0, 0, t);
                triangleLocal.SetData(c.sortedLocal, 0, 0, t);
            }
            nodeLevel.SetData(levelData);
            nodeGroup.SetData(nodeGroupData);
            nodes.SetData(nodeData);
            groups.SetData(groupData);
            groupNodeCount.SetData(groupNodeData);
            if (factors.IsCreated)
            {
                factors.Dispose();
            }
            factors = new NativeArray<float>(math.max(1, shapeTotal), Allocator.Persistent);
            if (origins.IsCreated)
            {
                origins.Dispose();
            }
            origins = new NativeArray<float3>(math.max(1, g), Allocator.Persistent);
            if (changedAll.IsCreated)
            {
                changedAll.Dispose();
            }
            changedAll = new NativeArray<int>(math.max(1, math.max(parts.Count, g)), Allocator.Persistent);
            for (int k = 0; k < changedAll.Length; k++)
            {
                changedAll[k] = 1;
            }
            uploadedVersion = c.TopologyVersion;
            forceAll = true;
        }
        public void Update(CommandBuffer cmd)
        {
            BasisRendererRayBatch c = Cpu;
            if (c.Prune())
            {
                c.Build(default, false).Complete();
            }
            else
            {
                c.Prepare(true).Complete();
            }
            if (uploadedVersion != c.TopologyVersion || !Ready)
            {
                Upload();
            }
            List<BasisRendererRayBatch.Part> parts = c.parts;
            if (parts.Count == 0 || c.vertexCount == 0)
            {
                return;
            }
            for (int i = 0; i < parts.Count; i++)
            {
                float[] f = parts[i].factors;
                for (int s = 0; s < f.Length; s++)
                {
                    factors[shapeOffsets[i] + s] = f[s];
                }
            }
            for (int gi = 0; gi < c.groupCount; gi++)
            {
                origins[gi] = c.matrices[groupMatrix[gi]].c3.xyz;
            }
            cmd.SetBufferData(matrices, c.matrices, 0, 0, c.matrixCount);
            cmd.SetBufferData(partChanged, forceAll ? changedAll : c.partChanged, 0, 0, parts.Count);
            cmd.SetBufferData(groupChanged, forceAll ? changedAll : c.groupChanged, 0, 0, c.groupCount);
            cmd.SetBufferData(groupOrigin, origins, 0, 0, c.groupCount);
            if (shapeTotal > 0)
            {
                cmd.SetBufferData(shapeFactors, factors, 0, 0, shapeTotal);
            }
            forceAll = false;
            cmd.SetComputeBufferParam(shader, skinKernel, baseVerticesId, baseVertices);
            cmd.SetComputeBufferParam(shader, skinKernel, vertexPartId, vertexPart);
            cmd.SetComputeBufferParam(shader, skinKernel, weightRangeId, weightRange);
            cmd.SetComputeBufferParam(shader, skinKernel, weightBoneId, weightBone);
            cmd.SetComputeBufferParam(shader, skinKernel, weightValueId, weightValue);
            cmd.SetComputeBufferParam(shader, skinKernel, blendRangeId, blendRange);
            cmd.SetComputeBufferParam(shader, skinKernel, blendShapeId, blendShape);
            cmd.SetComputeBufferParam(shader, skinKernel, blendDeltaId, blendDelta);
            cmd.SetComputeBufferParam(shader, skinKernel, shapeFactorsId, shapeFactors);
            cmd.SetComputeBufferParam(shader, skinKernel, matricesId, matrices);
            cmd.SetComputeBufferParam(shader, skinKernel, partsId, partsBuffer);
            cmd.SetComputeBufferParam(shader, skinKernel, partChangedId, partChanged);
            cmd.SetComputeBufferParam(shader, skinKernel, worldId, world);
            cmd.SetComputeIntParam(shader, vertexCountId, c.vertexCount);
            Dispatch(cmd, skinKernel, c.vertexCount);
            if (c.triangleCount == 0)
            {
                return;
            }
            cmd.SetComputeBufferParam(shader, refitNodesKernel, worldInId, world);
            cmd.SetComputeBufferParam(shader, refitNodesKernel, triangleIndicesId, triangleIndices);
            cmd.SetComputeBufferParam(shader, refitNodesKernel, groupChangedId, groupChanged);
            cmd.SetComputeBufferParam(shader, refitNodesKernel, nodeLevelId, nodeLevel);
            cmd.SetComputeBufferParam(shader, refitNodesKernel, nodeGroupId, nodeGroup);
            cmd.SetComputeBufferParam(shader, refitNodesKernel, groupOriginInId, groupOrigin);
            cmd.SetComputeBufferParam(shader, refitNodesKernel, nodesOutId, nodes);
            for (int l = 0; l < levels.Count; l++)
            {
                cmd.SetComputeIntParam(shader, levelStartId, levels[l].x);
                cmd.SetComputeIntParam(shader, levelCountId, levels[l].y);
                Dispatch(cmd, refitNodesKernel, levels[l].y);
            }
        }
        public void Update()
        {
            using CommandBuffer cmd = new CommandBuffer { name = "BasisRendererRays.Update" };
            Update(cmd);
            Graphics.ExecuteCommandBuffer(cmd);
        }
        private void Dispatch(CommandBuffer cmd, int kernel, int count)
        {
            int groupsNeeded = (count + GroupSize - 1) / GroupSize;
            if (groupsNeeded == 0)
            {
                return;
            }
            if (groupsNeeded > MaxGroups)
            {
                throw new InvalidOperationException("BasisRendererRayBatchGpu handles at most " + (MaxGroups * GroupSize) + " vertices or nodes per batch.");
            }
            cmd.DispatchCompute(shader, kernel, groupsNeeded, 1, 1);
        }
        public void Bind(CommandBuffer cmd, ComputeShader target, int kernel)
        {
            cmd.SetComputeBufferParam(target, kernel, nodesId, nodes);
            cmd.SetComputeBufferParam(target, kernel, indicesId, triangleIndices);
            cmd.SetComputeBufferParam(target, kernel, verticesId, world);
            cmd.SetComputeBufferParam(target, kernel, trianglePartId, trianglePart);
            cmd.SetComputeBufferParam(target, kernel, triangleLocalId, triangleLocal);
            cmd.SetComputeBufferParam(target, kernel, groupsId, groups);
            cmd.SetComputeBufferParam(target, kernel, groupNodeCountId, groupNodeCount);
            cmd.SetComputeBufferParam(target, kernel, groupOriginId, groupOrigin);
            cmd.SetComputeIntParam(target, groupCountId, Ready ? Cpu.groupCount : 0);
        }
        public void Bind(ComputeShader target, int kernel)
        {
            target.SetBuffer(kernel, nodesId, nodes);
            target.SetBuffer(kernel, indicesId, triangleIndices);
            target.SetBuffer(kernel, verticesId, world);
            target.SetBuffer(kernel, trianglePartId, trianglePart);
            target.SetBuffer(kernel, triangleLocalId, triangleLocal);
            target.SetBuffer(kernel, groupsId, groups);
            target.SetBuffer(kernel, groupNodeCountId, groupNodeCount);
            target.SetBuffer(kernel, groupOriginId, groupOrigin);
            target.SetInt(groupCountId, Ready ? Cpu.groupCount : 0);
        }
        public void Raycast(CommandBuffer cmd, GraphicsBuffer rays, GraphicsBuffer hits, int count)
        {
            if (!Ready || rays == null || hits == null)
            {
                return;
            }
            count = math.min(count, math.min(rays.count, hits.count));
            if (count <= 0)
            {
                return;
            }
            Bind(cmd, shader, traceKernel);
            cmd.SetComputeBufferParam(shader, traceKernel, raysId, rays);
            cmd.SetComputeBufferParam(shader, traceKernel, hitsId, hits);
            cmd.SetComputeIntParam(shader, rayCountId, count);
            int chunk = MaxGroups * GroupSize;
            for (int offset = 0; offset < count; offset += chunk)
            {
                cmd.SetComputeIntParam(shader, rayOffsetId, offset);
                cmd.DispatchCompute(shader, traceKernel, (math.min(chunk, count - offset) + GroupSize - 1) / GroupSize, 1, 1);
            }
        }
        private void Scratch(int count)
        {
            rayScratch = Buffer(rayScratch, count, RayStride);
            hitScratch = Buffer(hitScratch, count, HitStride);
        }
        public void RaycastImmediate(NativeArray<BasisRendererGpuRay> rays, NativeArray<BasisRendererGpuHit> hits)
        {
            int count = math.min(rays.Length, hits.Length);
            if (count == 0 || !Ready)
            {
                return;
            }
            Scratch(count);
            using CommandBuffer cmd = new CommandBuffer { name = "BasisRendererRays.Raycast" };
            cmd.SetBufferData(rayScratch, rays, 0, 0, count);
            Raycast(cmd, rayScratch, hitScratch, count);
            Graphics.ExecuteCommandBuffer(cmd);
            AsyncGPUReadback.RequestIntoNativeArray(ref hits, hitScratch, count * HitStride, 0).WaitForCompletion();
        }
        public void RaycastAsync(NativeArray<BasisRendererGpuRay> rays, Action<AsyncGPUReadbackRequest> done)
        {
            int count = rays.Length;
            if (count == 0 || !Ready)
            {
                return;
            }
            Scratch(count);
            using CommandBuffer cmd = new CommandBuffer { name = "BasisRendererRays.RaycastAsync" };
            cmd.SetBufferData(rayScratch, rays, 0, 0, count);
            Raycast(cmd, rayScratch, hitScratch, count);
            cmd.RequestAsyncReadback(hitScratch, count * HitStride, 0, done);
            Graphics.ExecuteCommandBuffer(cmd);
        }
        public void ReadWorldVertices(NativeArray<float3> output)
        {
            if (world != null && output.Length > 0)
            {
                AsyncGPUReadback.RequestIntoNativeArray(ref output, world, math.min(output.Length, Cpu.vertexCount) * 12, 0).WaitForCompletion();
            }
        }
        public void Dispose()
        {
            Cpu.Dispose();
            for (int i = 0; i < owned.Count; i++)
            {
                owned[i]?.Dispose();
            }
            owned.Clear();
            GpuBytes = 0;
            if (factors.IsCreated)
            {
                factors.Dispose();
            }
            if (changedAll.IsCreated)
            {
                changedAll.Dispose();
            }
            if (origins.IsCreated)
            {
                origins.Dispose();
            }
        }
    }
}
