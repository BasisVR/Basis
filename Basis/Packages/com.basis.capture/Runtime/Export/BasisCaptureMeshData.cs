using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
namespace Basis.Capture
{
    public sealed class BasisCaptureMeshData
    {
        public sealed class Shape
        {
            public string name;
            public float[] weights = Array.Empty<float>();
            public List<(int vertex, Vector3 dp, Vector3 dn, Vector3 dt)>[] frames = Array.Empty<List<(int, Vector3, Vector3, Vector3)>>();
        }
        public const int FlagNormals = 1, FlagTangents = 2, FlagColors = 4, FlagSkin = 8, FlagWorldSpace = 1 << 16;
        public string name;
        public int vertexCount;
        public Vector3[] positions, normals;
        public Vector4[] tangents, colors;
        public Vector4[][] uvs = new Vector4[8][];
        public int[] uvDims = new int[8];
        public List<(MeshTopology topology, int[] indices)> submeshes = new List<(MeshTopology, int[])>();
        public Matrix4x4[] bindposes = Array.Empty<Matrix4x4>();
        public int[] boneIndices;
        public float[] boneWeights;
        public List<Shape> shapes = new List<Shape>();
        public bool worldSpace;
        public string warning;
        public void Compact(int subStart, int subCount)
        {
            if (subStart == 0 && subCount >= submeshes.Count) return;
            List<(MeshTopology, int[])> kept = submeshes.GetRange(Math.Min(subStart, submeshes.Count), Math.Max(0, Math.Min(subCount, submeshes.Count - subStart)));
            int[] remap = new int[vertexCount];
            for (int i = 0; i < remap.Length; i++) remap[i] = -1;
            List<int> order = new List<int>();
            foreach ((MeshTopology _, int[] idx) in kept) for (int i = 0; i < idx.Length; i++) if (remap[idx[i]] < 0) { remap[idx[i]] = order.Count; order.Add(idx[i]); }
            for (int s = 0; s < kept.Count; s++) { int[] idx = kept[s].Item2; int[] n = new int[idx.Length]; for (int i = 0; i < idx.Length; i++) n[i] = remap[idx[i]]; kept[s] = (kept[s].Item1, n); }
            submeshes = kept;
            positions = Pick(positions, order);
            normals = Pick(normals, order);
            tangents = Pick(tangents, order);
            colors = Pick(colors, order);
            for (int u = 0; u < uvs.Length; u++) uvs[u] = Pick(uvs[u], order);
            if (boneIndices != null) { int[] bi = new int[order.Count * 4]; float[] bw = new float[order.Count * 4]; for (int i = 0; i < order.Count; i++) { Array.Copy(boneIndices, order[i] * 4, bi, i * 4, 4); Array.Copy(boneWeights, order[i] * 4, bw, i * 4, 4); } boneIndices = bi; boneWeights = bw; }
            foreach (Shape s in shapes) for (int f = 0; f < s.frames.Length; f++) { List<(int, Vector3, Vector3, Vector3)> n = new List<(int, Vector3, Vector3, Vector3)>(); foreach ((int v, Vector3 a, Vector3 b, Vector3 c) in s.frames[f]) if (v < remap.Length && remap[v] >= 0) n.Add((remap[v], a, b, c)); s.frames[f] = n; }
            vertexCount = order.Count;
        }
        private static T[] Pick<T>(T[] source, List<int> order)
        {
            if (source == null) return null;
            T[] result = new T[order.Count];
            for (int i = 0; i < order.Count; i++) result[i] = source[order[i]];
            return result;
        }
        public void Write(string path)
        {
            using FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            using BinaryWriter w = new BinaryWriter(fs, Encoding.UTF8);
            w.Write(Encoding.ASCII.GetBytes(BasisCaptureFormat.MeshMagic));
            w.Write(1);
            w.Write(vertexCount);
            int flags = 0;
            if (normals != null) flags |= FlagNormals;
            if (tangents != null) flags |= FlagTangents;
            if (colors != null) flags |= FlagColors;
            if (boneIndices != null) flags |= FlagSkin;
            if (worldSpace) flags |= FlagWorldSpace;
            for (int u = 0; u < 8; u++) if (uvs[u] != null) flags |= 16 << u;
            w.Write(flags);
            for (int i = 0; i < vertexCount; i++) { Vector3 p = positions[i]; w.Write(p.x); w.Write(p.y); w.Write(p.z); }
            if (normals != null) for (int i = 0; i < vertexCount; i++) { Vector3 n = normals[i]; w.Write(n.x); w.Write(n.y); w.Write(n.z); }
            if (tangents != null) for (int i = 0; i < vertexCount; i++) { Vector4 t = tangents[i]; w.Write(t.x); w.Write(t.y); w.Write(t.z); w.Write(t.w); }
            if (colors != null) for (int i = 0; i < vertexCount; i++) { Vector4 c = colors[i]; w.Write(c.x); w.Write(c.y); w.Write(c.z); w.Write(c.w); }
            for (int u = 0; u < 8; u++)
            {
                if (uvs[u] == null) continue;
                int d = Mathf.Clamp(uvDims[u], 2, 4);
                w.Write(d);
                for (int i = 0; i < vertexCount; i++) { Vector4 v = uvs[u][i]; w.Write(v.x); w.Write(v.y); if (d > 2) w.Write(v.z); if (d > 3) w.Write(v.w); }
            }
            w.Write(submeshes.Count);
            foreach ((MeshTopology topology, int[] indices) in submeshes) { w.Write((int)topology); w.Write(indices.Length); for (int i = 0; i < indices.Length; i++) w.Write(indices[i]); }
            if (boneIndices != null)
            {
                w.Write(bindposes.Length);
                for (int b = 0; b < bindposes.Length; b++) for (int c = 0; c < 4; c++) for (int r = 0; r < 4; r++) w.Write(bindposes[b][r, c]);
                for (int i = 0; i < vertexCount * 4; i++) w.Write(boneIndices[i]);
                for (int i = 0; i < vertexCount * 4; i++) w.Write(boneWeights[i]);
            }
            w.Write(shapes.Count);
            foreach (Shape s in shapes)
            {
                byte[] nameBytes = Encoding.UTF8.GetBytes(s.name ?? "");
                w.Write(nameBytes.Length);
                w.Write(nameBytes);
                w.Write(s.frames.Length);
                for (int f = 0; f < s.frames.Length; f++)
                {
                    w.Write(f < s.weights.Length ? s.weights[f] : 100f);
                    List<(int vertex, Vector3 dp, Vector3 dn, Vector3 dt)> list = s.frames[f];
                    w.Write(list.Count);
                    foreach ((int v, Vector3 dp, Vector3 dn, Vector3 dt) in list) { w.Write(v); w.Write(dp.x); w.Write(dp.y); w.Write(dp.z); w.Write(dn.x); w.Write(dn.y); w.Write(dn.z); w.Write(dt.x); w.Write(dt.y); w.Write(dt.z); }
                }
            }
        }
    }
}
