using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
namespace Basis.Capture
{
    public sealed class BasisCaptureMeshReader
    {
        public readonly Mesh mesh;
        public readonly BasisCaptureMeshData data = new BasisCaptureMeshData();
        private volatile bool done;
        public bool Done { get => done; private set => done = value; }
        private int pending;
        private byte[][] streams;
        private byte[] indexBytes, shapeBytes, skinBytes;
        private VertexAttributeDescriptor[] attributes;
        private int[] strides;
        private IndexFormat indexFormat;
        private SubMeshDescriptor[] subs;
        private BlendShapeBufferRange[] ranges;
        private int[] shapeFrames;
        private float[][] shapeWeights;
        private int[] offsets;
        private string[] shapeNames;
        public BasisCaptureMeshReader(Mesh mesh) { this.mesh = mesh; }
        public void Begin()
        {
            data.name = mesh.name;
            data.vertexCount = mesh.vertexCount;
            if (mesh.isReadable) { ReadCpu(); Done = true; return; }
            try { BeginGpu(); }
            catch (Exception e) { data.warning = "gpu readback failed: " + e.Message; Done = true; }
        }
        private void ReadCpu()
        {
            int n = mesh.vertexCount;
            data.positions = mesh.vertices;
            if (mesh.HasVertexAttribute(VertexAttribute.Normal)) data.normals = mesh.normals;
            if (mesh.HasVertexAttribute(VertexAttribute.Tangent)) data.tangents = mesh.tangents;
            if (mesh.HasVertexAttribute(VertexAttribute.Color)) { Color[] c = mesh.colors; data.colors = new Vector4[n]; for (int i = 0; i < n; i++) data.colors[i] = c[i]; }
            List<Vector4> uv = new List<Vector4>(n);
            for (int u = 0; u < 8; u++)
            {
                VertexAttribute a = VertexAttribute.TexCoord0 + u;
                if (!mesh.HasVertexAttribute(a)) continue;
                mesh.GetUVs(u, uv);
                data.uvs[u] = uv.ToArray();
                data.uvDims[u] = mesh.GetVertexAttributeDimension(a);
            }
            for (int s = 0; s < mesh.subMeshCount; s++) data.submeshes.Add((mesh.GetTopology(s), mesh.GetIndices(s, true)));
            data.bindposes = mesh.bindposes;
            NativeArray<byte> perVertex = mesh.GetBonesPerVertex();
            if (perVertex.Length == n)
            {
                NativeArray<BoneWeight1> weights = mesh.GetAllBoneWeights();
                data.boneIndices = new int[n * 4];
                data.boneWeights = new float[n * 4];
                int cursor = 0;
                for (int v = 0; v < n; v++)
                {
                    int count = perVertex[v];
                    for (int k = 0; k < count; k++)
                    {
                        BoneWeight1 bw = weights[cursor + k];
                        int slot = -1;
                        for (int s = 0; s < 4; s++) if (slot < 0 || data.boneWeights[v * 4 + s] < data.boneWeights[v * 4 + slot]) slot = s;
                        if (bw.weight > data.boneWeights[v * 4 + slot]) { data.boneWeights[v * 4 + slot] = bw.weight; data.boneIndices[v * 4 + slot] = bw.boneIndex; }
                    }
                    cursor += count;
                }
            }
            Vector3[] dp = new Vector3[n], dn = new Vector3[n], dt = new Vector3[n];
            for (int s = 0; s < mesh.blendShapeCount; s++)
            {
                int frames = mesh.GetBlendShapeFrameCount(s);
                BasisCaptureMeshData.Shape shape = new BasisCaptureMeshData.Shape { name = mesh.GetBlendShapeName(s), weights = new float[frames], frames = new List<(int, Vector3, Vector3, Vector3)>[frames] };
                for (int f = 0; f < frames; f++)
                {
                    shape.weights[f] = mesh.GetBlendShapeFrameWeight(s, f);
                    mesh.GetBlendShapeFrameVertices(s, f, dp, dn, dt);
                    List<(int, Vector3, Vector3, Vector3)> list = new List<(int, Vector3, Vector3, Vector3)>();
                    for (int v = 0; v < n; v++) if (dp[v] != Vector3.zero || dn[v] != Vector3.zero || dt[v] != Vector3.zero) list.Add((v, dp[v], dn[v], dt[v]));
                    shape.frames[f] = list;
                }
                data.shapes.Add(shape);
            }
        }
        private void BeginGpu()
        {
            if ((mesh.vertexBufferTarget & GraphicsBuffer.Target.Raw) == 0) mesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
            if ((mesh.indexBufferTarget & GraphicsBuffer.Target.Raw) == 0) mesh.indexBufferTarget |= GraphicsBuffer.Target.Raw;
            attributes = mesh.GetVertexAttributes();
            offsets = new int[attributes.Length];
            for (int a = 0; a < attributes.Length; a++) offsets[a] = mesh.GetVertexAttributeOffset(attributes[a].attribute);
            int streamCount = mesh.vertexBufferCount;
            streams = new byte[streamCount][];
            strides = new int[streamCount];
            indexFormat = mesh.indexFormat;
            subs = new SubMeshDescriptor[mesh.subMeshCount];
            for (int s = 0; s < subs.Length; s++) subs[s] = mesh.GetSubMesh(s);
            try { data.bindposes = mesh.bindposes; } catch { data.bindposes = Array.Empty<Matrix4x4>(); }
            for (int s = 0; s < streamCount; s++)
            {
                strides[s] = mesh.GetVertexBufferStride(s);
                int slot = s;
                Request(mesh.GetVertexBuffer(slot), bytes => streams[slot] = bytes);
            }
            Request(mesh.GetIndexBuffer(), bytes => indexBytes = bytes);
            bool hasSkinAttributes = false;
            foreach (VertexAttributeDescriptor a in attributes) if (a.attribute == VertexAttribute.BlendIndices) hasSkinAttributes = true;
            if (!hasSkinAttributes && mesh.skinWeightBufferLayout != SkinWeights.None && SystemInfo.supportsComputeShaders) Request(mesh.GetBoneWeightBuffer(SkinWeights.FourBones), bytes => skinBytes = bytes);
            int shapes = mesh.blendShapeCount;
            if (shapes > 0 && SystemInfo.supportsComputeShaders)
            {
                ranges = new BlendShapeBufferRange[shapes];
                shapeFrames = new int[shapes];
                shapeWeights = new float[shapes][];
                shapeNames = new string[shapes];
                for (int s = 0; s < shapes; s++)
                {
                    ranges[s] = mesh.GetBlendShapeBufferRange(s);
                    shapeNames[s] = mesh.GetBlendShapeName(s);
                    shapeFrames[s] = mesh.GetBlendShapeFrameCount(s);
                    shapeWeights[s] = new float[shapeFrames[s]];
                    for (int f = 0; f < shapeFrames[s]; f++) shapeWeights[s][f] = mesh.GetBlendShapeFrameWeight(s, f);
                }
                Request(mesh.GetBlendShapeBuffer(BlendShapeBufferLayout.PerShape), bytes => shapeBytes = bytes);
            }
            if (pending == 0) Finish();
        }
        private void Request(GraphicsBuffer buffer, Action<byte[]> store)
        {
            if (buffer == null) return;
            pending++;
            AsyncGPUReadback.Request(buffer, request =>
            {
                try { if (!request.hasError) store(request.GetData<byte>().ToArray()); else data.warning = "readback error"; }
                catch (Exception e) { data.warning = e.Message; }
                buffer.Dispose();
                if (--pending == 0) Finish();
            });
        }
        private void Finish() => System.Threading.Tasks.Task.Run(() =>
        {
            try { Decode(); }
            catch (Exception e) { data.warning = "decode failed: " + e.Message; }
            Done = true;
        });
        private void Decode()
        {
            int n = data.vertexCount;
            for (int ai = 0; ai < attributes.Length; ai++)
            {
                VertexAttributeDescriptor a = attributes[ai];
                byte[] s = streams[a.stream];
                if (s == null) continue;
                int offset = offsets[ai], stride = strides[a.stream];
                switch (a.attribute)
                {
                    case VertexAttribute.Position: data.positions = new Vector3[n]; for (int v = 0; v < n; v++) data.positions[v] = Read(s, v * stride + offset, a.format, a.dimension); break;
                    case VertexAttribute.Normal: data.normals = new Vector3[n]; for (int v = 0; v < n; v++) data.normals[v] = Read(s, v * stride + offset, a.format, a.dimension); break;
                    case VertexAttribute.Tangent: data.tangents = new Vector4[n]; for (int v = 0; v < n; v++) data.tangents[v] = Read(s, v * stride + offset, a.format, a.dimension); break;
                    case VertexAttribute.Color: data.colors = new Vector4[n]; for (int v = 0; v < n; v++) data.colors[v] = Read(s, v * stride + offset, a.format, a.dimension, true); break;
                    case VertexAttribute.BlendIndices:
                        data.boneIndices ??= new int[n * 4];
                        for (int v = 0; v < n; v++) { Vector4 x = Read(s, v * stride + offset, a.format, a.dimension); for (int k = 0; k < a.dimension && k < 4; k++) data.boneIndices[v * 4 + k] = (int)x[k]; }
                        break;
                    case VertexAttribute.BlendWeight:
                        data.boneWeights ??= new float[n * 4];
                        for (int v = 0; v < n; v++) { Vector4 x = Read(s, v * stride + offset, a.format, a.dimension); for (int k = 0; k < a.dimension && k < 4; k++) data.boneWeights[v * 4 + k] = x[k]; }
                        break;
                    default:
                        if (a.attribute >= VertexAttribute.TexCoord0 && a.attribute <= VertexAttribute.TexCoord7)
                        {
                            int u = a.attribute - VertexAttribute.TexCoord0;
                            data.uvs[u] = new Vector4[n];
                            data.uvDims[u] = a.dimension;
                            for (int v = 0; v < n; v++) data.uvs[u][v] = Read(s, v * stride + offset, a.format, a.dimension);
                        }
                        break;
                }
            }
            if (data.boneIndices != null && data.boneWeights == null) { data.boneWeights = new float[n * 4]; for (int v = 0; v < n; v++) data.boneWeights[v * 4] = 1f; }
            if (data.boneWeights != null && data.boneIndices == null) data.boneIndices = new int[n * 4];
            if (data.boneIndices == null && skinBytes != null) DecodeSkinBuffer(n);
            if (indexBytes != null)
            {
                int size = indexFormat == IndexFormat.UInt16 ? 2 : 4;
                for (int s = 0; s < subs.Length; s++)
                {
                    SubMeshDescriptor d = subs[s];
                    int[] idx = new int[d.indexCount];
                    for (int i = 0; i < idx.Length; i++)
                    {
                        int at = (d.indexStart + i) * size;
                        if (at + size > indexBytes.Length) break;
                        idx[i] = (size == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(indexBytes.AsSpan(at)) : (int)BinaryPrimitives.ReadUInt32LittleEndian(indexBytes.AsSpan(at))) + d.baseVertex;
                    }
                    data.submeshes.Add((d.topology, idx));
                }
            }
            if (shapeBytes != null && ranges != null)
            {
                for (int s = 0; s < ranges.Length; s++)
                {
                    BasisCaptureMeshData.Shape shape = new BasisCaptureMeshData.Shape { name = shapeNames[s] };
                    int frames = Math.Max(1, shapeFrames[s]);
                    long start = ranges[s].startIndex, end = ranges[s].endIndex;
                    long total = end >= start ? end - start + 1 : 0;
                    long perFrame = frames > 1 && total % frames == 0 ? total / frames : total;
                    int usedFrames = perFrame == total ? 1 : frames;
                    shape.frames = new List<(int, Vector3, Vector3, Vector3)>[usedFrames];
                    shape.weights = new float[usedFrames];
                    for (int f = 0; f < usedFrames; f++)
                    {
                        shape.weights[f] = usedFrames == 1 ? shapeWeights[s][shapeWeights[s].Length - 1] : shapeWeights[s][f];
                        List<(int, Vector3, Vector3, Vector3)> list = new List<(int, Vector3, Vector3, Vector3)>((int)perFrame);
                        for (long e = start + f * perFrame; e < start + (f + 1) * perFrame; e++)
                        {
                            int at = (int)(e * 40);
                            if (at + 40 > shapeBytes.Length) break;
                            int v = BinaryPrimitives.ReadInt32LittleEndian(shapeBytes.AsSpan(at));
                            if (v < 0 || v >= n) continue;
                            list.Add((v, F3(shapeBytes, at + 4), F3(shapeBytes, at + 16), F3(shapeBytes, at + 28)));
                        }
                        shape.frames[f] = list;
                    }
                    data.shapes.Add(shape);
                }
            }
        }
        private void DecodeSkinBuffer(int n)
        {
            if (skinBytes.Length < n * 32) { data.warning = "skin buffer too small"; return; }
            int bindCount = Math.Max(1, data.bindposes.Length);
            for (int layout = 0; layout < 2; layout++)
            {
                int[] bi = new int[n * 4];
                float[] bw = new float[n * 4];
                bool valid = true;
                for (int v = 0; v < n && valid; v++)
                {
                    int at = v * 32;
                    float sum = 0f;
                    for (int k = 0; k < 4; k++)
                    {
                        float w = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(skinBytes.AsSpan(layout == 0 ? at + k * 4 : at + 16 + k * 4)));
                        int i = BinaryPrimitives.ReadInt32LittleEndian(skinBytes.AsSpan(layout == 0 ? at + 16 + k * 4 : at + k * 4));
                        if (!(w >= -1e-4f && w <= 1.0001f) || i < 0 || (w > 0f && i >= bindCount)) { valid = false; break; }
                        bw[v * 4 + k] = w; bi[v * 4 + k] = i; sum += w;
                    }
                    if (sum > 1e-4f && (sum < 0.98f || sum > 1.02f)) valid = false;
                }
                if (!valid) continue;
                data.boneIndices = bi;
                data.boneWeights = bw;
                return;
            }
            data.warning = "skin weights layout not recognised";
        }
        private static Vector3 F3(byte[] b, int at) => new Vector3(F(b, at), F(b, at + 4), F(b, at + 8));
        private static float F(byte[] b, int at) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at)));
        private static Vector4 Read(byte[] s, int at, VertexAttributeFormat format, int dim, bool colorDefault = false)
        {
            Vector4 r = colorDefault ? new Vector4(0, 0, 0, 1) : Vector4.zero;
            for (int k = 0; k < dim && k < 4; k++)
            {
                switch (format)
                {
                    case VertexAttributeFormat.Float32: r[k] = F(s, at + k * 4); break;
                    case VertexAttributeFormat.Float16: r[k] = Mathf.HalfToFloat(BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(at + k * 2))); break;
                    case VertexAttributeFormat.UNorm8: r[k] = s[at + k] / 255f; break;
                    case VertexAttributeFormat.SNorm8: r[k] = Mathf.Max((sbyte)s[at + k] / 127f, -1f); break;
                    case VertexAttributeFormat.UNorm16: r[k] = BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(at + k * 2)) / 65535f; break;
                    case VertexAttributeFormat.SNorm16: r[k] = Mathf.Max(BinaryPrimitives.ReadInt16LittleEndian(s.AsSpan(at + k * 2)) / 32767f, -1f); break;
                    case VertexAttributeFormat.UInt8: r[k] = s[at + k]; break;
                    case VertexAttributeFormat.SInt8: r[k] = (sbyte)s[at + k]; break;
                    case VertexAttributeFormat.UInt16: r[k] = BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(at + k * 2)); break;
                    case VertexAttributeFormat.SInt16: r[k] = BinaryPrimitives.ReadInt16LittleEndian(s.AsSpan(at + k * 2)); break;
                    case VertexAttributeFormat.UInt32: r[k] = BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(at + k * 4)); break;
                    case VertexAttributeFormat.SInt32: r[k] = BinaryPrimitives.ReadInt32LittleEndian(s.AsSpan(at + k * 4)); break;
                }
            }
            return r;
        }
    }
}
