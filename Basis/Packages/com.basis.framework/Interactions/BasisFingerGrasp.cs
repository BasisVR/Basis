using System.Collections.Generic;
using Basis.Scripts.Drivers;
using Unity.Mathematics;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
namespace Basis.Scripts.BasisSdk.Interactions
{
    public enum BasisPickupFingerPose { None, FixedLocation, Automatic }
    public struct BasisFingerChain
    {
        public Vector3 proximalOffset, intermediateOffset, distalOffset, tipOffset;
        public Quaternion proximalParent, intermediateParent, distalParent;
        public float radius;
        public bool valid;
    }
    public abstract class BasisGraspSurface
    {
        public abstract float Depth(Vector3 a, Vector3 b, float radius);
        public virtual void Begin() { }
        public virtual void End() { }
        public virtual bool Focus(Vector3 center, float radius) => true;
        public virtual bool Exceeds(Vector3 a, Vector3 b, float radius, float threshold) => Depth(a, b, radius) > threshold;
    }
    [NoAutoStaticsCleanup]
    public sealed class BasisColliderGraspSurface : BasisGraspSurface
    {
        private static GameObject probeObject;
        private static CapsuleCollider probe;
        private static readonly List<Collider> found = new List<Collider>();
        public Collider[] Colliders = new Collider[4];
        public Vector3[] Positions = new Vector3[4];
        public Quaternion[] Rotations = new Quaternion[4];
        public int Count;
        public void Set(GameObject root) => Set(root, root != null ? root.transform.position : Vector3.zero, root != null ? root.transform.rotation : Quaternion.identity);
        public void Set(GameObject root, Vector3 rootPosition, Quaternion rootRotation)
        {
            Count = 0;
            if (root == null)
            {
                return;
            }
            root.GetComponentsInChildren(false, found);
            root.transform.GetPositionAndRotation(out Vector3 livePosition, out Quaternion liveRotation);
            Quaternion inverseLive = Quaternion.Inverse(liveRotation);
            for (int i = 0; i < found.Count; i++)
            {
                Collider c = found[i];
                if (c == null || !c.enabled || c.isTrigger)
                {
                    continue;
                }
                c.transform.GetPositionAndRotation(out Vector3 p, out Quaternion r);
                Add(c, rootPosition + rootRotation * (inverseLive * (p - livePosition)), rootRotation * (inverseLive * r));
            }
            found.Clear();
        }
        public void Add(Collider collider, Vector3 position, Quaternion rotation)
        {
            if (Count == Colliders.Length)
            {
                System.Array.Resize(ref Colliders, Count * 2);
                System.Array.Resize(ref Positions, Count * 2);
                System.Array.Resize(ref Rotations, Count * 2);
            }
            Colliders[Count] = collider;
            Positions[Count] = position;
            Rotations[Count++] = rotation;
        }
        public static CapsuleCollider Probe()
        {
            if (probe == null)
            {
                probeObject = new GameObject("BasisFingerGraspProbe") { hideFlags = HideFlags.HideAndDontSave };
                probe = probeObject.AddComponent<CapsuleCollider>();
                probe.direction = 2;
                probe.isTrigger = true;
                probe.enabled = false;
                probeObject.transform.position = new Vector3(0f, -100000f, 0f);
            }
            return probe;
        }
        public override void Begin() => Probe().enabled = true;
        public override void End()
        {
            if (probe != null)
            {
                probe.enabled = false;
            }
        }
        public override float Depth(Vector3 a, Vector3 b, float radius)
        {
            Vector3 axis = b - a;
            float length = axis.magnitude;
            if (length < 1e-6f || Count == 0)
            {
                return 0f;
            }
            CapsuleCollider p = Probe();
            p.radius = radius;
            p.height = length + 2f * radius;
            Vector3 mid = (a + b) * 0.5f;
            Quaternion rotation = Quaternion.LookRotation(axis / length);
            float depth = 0f;
            for (int i = 0; i < Count; i++)
            {
                Collider c = Colliders[i];
                if (c != null && Physics.ComputePenetration(p, mid, rotation, c, Positions[i], Rotations[i], out _, out float d) && d > depth)
                {
                    depth = d;
                }
            }
            return depth;
        }
        public override bool Exceeds(Vector3 a, Vector3 b, float radius, float threshold)
        {
            Vector3 axis = b - a;
            float length = axis.magnitude;
            if (length < 1e-6f || Count == 0)
            {
                return 0f > threshold;
            }
            CapsuleCollider p = Probe();
            p.radius = radius;
            p.height = length + 2f * radius;
            Vector3 mid = (a + b) * 0.5f;
            Quaternion rotation = Quaternion.LookRotation(axis / length);
            for (int i = 0; i < Count; i++)
            {
                Collider c = Colliders[i];
                if (c != null && Physics.ComputePenetration(p, mid, rotation, c, Positions[i], Rotations[i], out _, out float d) && d > threshold)
                {
                    return true;
                }
            }
            return 0f > threshold;
        }
    }
    [NoAutoStaticsCleanup]
    public sealed class BasisMeshGraspSurface : BasisGraspSurface
    {
        private static Mesh bakeMesh;
        private static readonly List<Vector3> vertices = new List<Vector3>();
        private static readonly List<int> triangles = new List<int>();
        public Vector3[] A = new Vector3[64], B = new Vector3[64], C = new Vector3[64], N = new Vector3[64], Min = new Vector3[64], Max = new Vector3[64], Center = new Vector3[64];
        public float[] Reach = new float[64];
        public int Count;
        public const int LeafSize = 4;
        private int[] order = new int[64], nodeStart = new int[32], nodeCount = new int[32], nodeRight = new int[32];
        private Vector3[] nodeMin = new Vector3[32], nodeMax = new Vector3[32];
        private float[] keys = new float[64];
        private readonly int[] stack = new int[64];
        private int nodes;
        private bool dirty = true;
        public int NodeCount => nodes;
        public void Clear()
        {
            Count = 0;
            dirty = true;
        }
        private void EnsureTree()
        {
            if (!dirty)
            {
                return;
            }
            dirty = false;
            nodes = 0;
            if (Count == 0)
            {
                return;
            }
            if (order.Length < Count)
            {
                order = new int[A.Length];
                keys = new float[A.Length];
            }
            for (int i = 0; i < Count; i++)
            {
                order[i] = i;
            }
            int capacity = 2 * Count + 1;
            if (nodeStart.Length < capacity)
            {
                nodeStart = new int[capacity];
                nodeCount = new int[capacity];
                nodeRight = new int[capacity];
                nodeMin = new Vector3[capacity];
                nodeMax = new Vector3[capacity];
            }
            BuildNode(0, Count, 0);
        }
        private int BuildNode(int start, int count, int depth)
        {
            int node = nodes++;
            Vector3 lo = Min[order[start]], hi = Max[order[start]], clo = (lo + hi) * 0.5f, chi = clo;
            for (int i = start + 1; i < start + count; i++)
            {
                int t = order[i];
                lo = Vector3.Min(lo, Min[t]);
                hi = Vector3.Max(hi, Max[t]);
                Vector3 c = (Min[t] + Max[t]) * 0.5f;
                clo = Vector3.Min(clo, c);
                chi = Vector3.Max(chi, c);
            }
            nodeMin[node] = lo;
            nodeMax[node] = hi;
            nodeStart[node] = start;
            if (count <= LeafSize || depth >= 60)
            {
                nodeCount[node] = count;
                nodeRight[node] = -1;
                return node;
            }
            Vector3 extent = chi - clo;
            int axis = extent.x >= extent.y && extent.x >= extent.z ? 0 : extent.y >= extent.z ? 1 : 2;
            for (int i = start; i < start + count; i++)
            {
                keys[i] = (Min[order[i]][axis] + Max[order[i]][axis]) * 0.5f;
            }
            System.Array.Sort(keys, order, start, count);
            int half = count / 2;
            nodeCount[node] = 0;
            BuildNode(start, half, depth + 1);
            nodeRight[node] = BuildNode(start + half, count - half, depth + 1);
            return node;
        }
        private static bool Overlaps(Vector3 mn, Vector3 mx, Vector3 lo, Vector3 hi) => !(mx.x < lo.x || mn.x > hi.x || mx.y < lo.y || mn.y > hi.y || mx.z < lo.z || mn.z > hi.z);
        public override bool Focus(Vector3 center, float radius)
        {
            EnsureTree();
            return nodes > 0 && (Vector3.Max(nodeMin[0], Vector3.Min(center, nodeMax[0])) - center).sqrMagnitude <= radius * radius;
        }
        public static bool Readable(Mesh mesh) => mesh != null && (mesh.isReadable || Application.isEditor);
        public bool Add(Renderer renderer, Vector3 cullCenter, float cullRadius) => Add(renderer, Matrix4x4.identity, cullCenter, cullRadius);
        public bool Add(Renderer renderer, Matrix4x4 after, Vector3 cullCenter, float cullRadius)
        {
            Mesh mesh;
            Matrix4x4 m;
            if (renderer is SkinnedMeshRenderer skinned)
            {
                if (!Readable(skinned.sharedMesh))
                {
                    return false;
                }
                if (bakeMesh == null)
                {
                    bakeMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                }
                skinned.BakeMesh(bakeMesh, true);
                mesh = bakeMesh;
                Transform t = skinned.transform;
                m = after * t.localToWorldMatrix;
            }
            else if (renderer != null && renderer.TryGetComponent(out MeshFilter filter) && Readable(filter.sharedMesh))
            {
                mesh = filter.sharedMesh;
                m = after * renderer.transform.localToWorldMatrix;
            }
            else
            {
                return false;
            }
            mesh.GetVertices(vertices);
            for (int i = 0; i < vertices.Count; i++)
            {
                vertices[i] = m.MultiplyPoint3x4(vertices[i]);
            }
            float cullSqr = cullRadius * cullRadius;
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                if (mesh.GetTopology(sub) != MeshTopology.Triangles)
                {
                    continue;
                }
                mesh.GetTriangles(triangles, sub);
                for (int i = 0; i + 2 < triangles.Count; i += 3)
                {
                    Vector3 a = vertices[triangles[i]], b = vertices[triangles[i + 1]], c = vertices[triangles[i + 2]];
                    Vector3 lo = Vector3.Min(a, Vector3.Min(b, c)), hi = Vector3.Max(a, Vector3.Max(b, c));
                    if (cullRadius > 0f && (Vector3.Max(lo, Vector3.Min(cullCenter, hi)) - cullCenter).sqrMagnitude > cullSqr)
                    {
                        continue;
                    }
                    Vector3 n = Vector3.Cross(b - a, c - a);
                    float area = n.magnitude;
                    if (area < 1e-12f)
                    {
                        continue;
                    }
                    if (Count == A.Length)
                    {
                        Grow();
                    }
                    A[Count] = a;
                    B[Count] = b;
                    C[Count] = c;
                    N[Count] = n / area;
                    Min[Count] = lo;
                    Vector3 centroid = (a + b + c) / 3f;
                    Center[Count] = centroid;
                    Reach[Count] = Mathf.Sqrt(Mathf.Max((a - centroid).sqrMagnitude, Mathf.Max((b - centroid).sqrMagnitude, (c - centroid).sqrMagnitude)));
                    Max[Count++] = hi;
                    dirty = true;
                }
            }
            vertices.Clear();
            triangles.Clear();
            return true;
        }
        private void Grow()
        {
            int size = A.Length * 2;
            System.Array.Resize(ref A, size);
            System.Array.Resize(ref B, size);
            System.Array.Resize(ref C, size);
            System.Array.Resize(ref N, size);
            System.Array.Resize(ref Min, size);
            System.Array.Resize(ref Max, size);
            System.Array.Resize(ref Center, size);
            System.Array.Resize(ref Reach, size);
        }
        public override float Depth(Vector3 a, Vector3 b, float radius)
        {
            EnsureTree();
            if (nodes == 0)
            {
                return 0f;
            }
            Vector3 pad = new Vector3(radius, radius, radius), lo = Vector3.Min(a, b) - pad, hi = Vector3.Max(a, b) + pad;
            Vector3 ab = b - a;
            float inverseLength = 1f / Mathf.Max(ab.sqrMagnitude, 1e-12f);
            float best = float.MaxValue;
            bool behind = false;
            int top = 0;
            stack[top++] = 0;
            while (top > 0)
            {
                int node = stack[--top];
                if (!Overlaps(nodeMin[node], nodeMax[node], lo, hi))
                {
                    continue;
                }
                if (nodeRight[node] >= 0)
                {
                    stack[top++] = nodeRight[node];
                    stack[top++] = node + 1;
                    continue;
                }
                for (int k = nodeStart[node], end = k + nodeCount[node]; k < end; k++)
                {
                    int i = order[k];
                    if (!Overlaps(Min[i], Max[i], lo, hi))
                    {
                        continue;
                    }
                    Vector3 toCenter = Center[i] - a, near = toCenter - ab * Mathf.Clamp01(Vector3.Dot(toCenter, ab) * inverseLength);
                    float limit = radius + Reach[i];
                    if (near.sqrMagnitude > limit * limit)
                    {
                        continue;
                    }
                    float d = SegmentTriangle(a, b, A[i], B[i], C[i], N[i], out Vector3 onSegment, out Vector3 onTriangle, out bool crosses, radius);
                    if (crosses)
                    {
                        return 2f * radius;
                    }
                    if (d < best && d <= radius)
                    {
                        best = d;
                        behind = Vector3.Dot(N[i], onSegment - onTriangle) < 0f;
                    }
                }
            }
            if (best == float.MaxValue)
            {
                return 0f;
            }
            return behind ? radius + best : radius - best;
        }
        public override bool Exceeds(Vector3 a, Vector3 b, float radius, float threshold)
        {
            EnsureTree();
            if (nodes == 0)
            {
                return 0f > threshold;
            }
            Vector3 pad = new Vector3(radius, radius, radius), lo = Vector3.Min(a, b) - pad, hi = Vector3.Max(a, b) + pad;
            Vector3 ab = b - a;
            float inverseLength = 1f / Mathf.Max(ab.sqrMagnitude, 1e-12f);
            float best = float.MaxValue, inside = radius - threshold;
            bool behind = false;
            int top = 0;
            stack[top++] = 0;
            while (top > 0)
            {
                int node = stack[--top];
                if (!Overlaps(nodeMin[node], nodeMax[node], lo, hi))
                {
                    continue;
                }
                if (nodeRight[node] >= 0)
                {
                    stack[top++] = nodeRight[node];
                    stack[top++] = node + 1;
                    continue;
                }
                for (int k = nodeStart[node], end = k + nodeCount[node]; k < end; k++)
                {
                    int i = order[k];
                    if (!Overlaps(Min[i], Max[i], lo, hi))
                    {
                        continue;
                    }
                    Vector3 toCenter = Center[i] - a, near = toCenter - ab * Mathf.Clamp01(Vector3.Dot(toCenter, ab) * inverseLength);
                    float limit = radius + Reach[i];
                    if (near.sqrMagnitude > limit * limit)
                    {
                        continue;
                    }
                    float d = SegmentTriangle(a, b, A[i], B[i], C[i], N[i], out Vector3 onSegment, out Vector3 onTriangle, out bool crosses, radius);
                    if (crosses || d < inside)
                    {
                        return true;
                    }
                    if (d < best && d <= radius)
                    {
                        best = d;
                        behind = Vector3.Dot(N[i], onSegment - onTriangle) < 0f;
                    }
                }
            }
            if (best == float.MaxValue)
            {
                return 0f > threshold;
            }
            return (behind ? radius + best : radius - best) > threshold;
        }
        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out float distance, out Vector3 normal)
        {
            distance = maxDistance;
            normal = Vector3.zero;
            EnsureTree();
            if (nodes == 0)
            {
                return false;
            }
            bool hit = false;
            Vector3 inverse = new Vector3(1f / direction.x, 1f / direction.y, 1f / direction.z);
            int top = 0;
            stack[top++] = 0;
            while (top > 0)
            {
                int node = stack[--top];
                if (!SlabHit(origin, inverse, nodeMin[node], nodeMax[node], distance))
                {
                    continue;
                }
                if (nodeRight[node] >= 0)
                {
                    stack[top++] = nodeRight[node];
                    stack[top++] = node + 1;
                    continue;
                }
                for (int k = nodeStart[node], end = k + nodeCount[node]; k < end; k++)
                {
                    int i = order[k];
                    Vector3 e1 = B[i] - A[i], e2 = C[i] - A[i], p = Vector3.Cross(direction, e2);
                    float det = Vector3.Dot(e1, p);
                    if (det > -1e-12f && det < 1e-12f)
                    {
                        continue;
                    }
                    float invDet = 1f / det;
                    Vector3 s = origin - A[i];
                    float u = Vector3.Dot(s, p) * invDet;
                    if (u < 0f || u > 1f)
                    {
                        continue;
                    }
                    Vector3 q = Vector3.Cross(s, e1);
                    float v = Vector3.Dot(direction, q) * invDet;
                    if (v < 0f || u + v > 1f)
                    {
                        continue;
                    }
                    float t = Vector3.Dot(e2, q) * invDet;
                    if (t < 0f || t >= distance)
                    {
                        continue;
                    }
                    distance = t;
                    normal = Vector3.Dot(N[i], direction) > 0f ? -N[i] : N[i];
                    hit = true;
                }
            }
            return hit;
        }
        private static bool SlabHit(Vector3 o, Vector3 inv, Vector3 mn, Vector3 mx, float max)
        {
            float t1 = (mn.x - o.x) * inv.x, t2 = (mx.x - o.x) * inv.x, tmin = Mathf.Min(t1, t2), tmax = Mathf.Max(t1, t2);
            t1 = (mn.y - o.y) * inv.y;
            t2 = (mx.y - o.y) * inv.y;
            tmin = Mathf.Max(tmin, Mathf.Min(t1, t2));
            tmax = Mathf.Min(tmax, Mathf.Max(t1, t2));
            t1 = (mn.z - o.z) * inv.z;
            t2 = (mx.z - o.z) * inv.z;
            tmin = Mathf.Max(tmin, Mathf.Min(t1, t2));
            tmax = Mathf.Min(tmax, Mathf.Max(t1, t2));
            return !(tmax < 0f || tmin > tmax || tmin > max) || float.IsNaN(tmin) || float.IsNaN(tmax);
        }
        public static float SegmentTriangle(Vector3 p0, Vector3 p1, Vector3 a, Vector3 b, Vector3 c, Vector3 n, out Vector3 onSegment, out Vector3 onTriangle, out bool crosses, float reject = float.MaxValue)
        {
            crosses = false;
            float d0 = Vector3.Dot(n, p0 - a), d1 = Vector3.Dot(n, p1 - a);
            if ((d0 > reject && d1 > reject) || (d0 < -reject && d1 < -reject))
            {
                onSegment = p0;
                onTriangle = a;
                return float.MaxValue;
            }
            if ((d0 <= 0f) != (d1 <= 0f) || d0 == 0f || d1 == 0f)
            {
                float denom = d0 - d1;
                Vector3 x = denom != 0f ? p0 + (p1 - p0) * (d0 / denom) : p0;
                if (Vector3.Dot(Vector3.Cross(b - a, x - a), n) >= 0f && Vector3.Dot(Vector3.Cross(c - b, x - b), n) >= 0f && Vector3.Dot(Vector3.Cross(a - c, x - c), n) >= 0f)
                {
                    crosses = true;
                    onSegment = onTriangle = x;
                    return 0f;
                }
            }
            onTriangle = ClosestOnTriangle(p0, a, b, c);
            onSegment = p0;
            float best = (p0 - onTriangle).sqrMagnitude;
            Vector3 q = ClosestOnTriangle(p1, a, b, c);
            float d = (p1 - q).sqrMagnitude;
            if (d < best)
            {
                best = d;
                onSegment = p1;
                onTriangle = q;
            }
            Edge(p0, p1, a, b, ref best, ref onSegment, ref onTriangle);
            Edge(p0, p1, b, c, ref best, ref onSegment, ref onTriangle);
            Edge(p0, p1, c, a, ref best, ref onSegment, ref onTriangle);
            return Mathf.Sqrt(best);
        }
        private static void Edge(Vector3 p0, Vector3 p1, Vector3 q0, Vector3 q1, ref float best, ref Vector3 onSegment, ref Vector3 onTriangle)
        {
            ClosestSegmentSegment(p0, p1, q0, q1, out Vector3 s, out Vector3 t);
            float d = (s - t).sqrMagnitude;
            if (d >= best)
            {
                return;
            }
            best = d;
            onSegment = s;
            onTriangle = t;
        }
        public static void ClosestSegmentSegment(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out Vector3 c1, out Vector3 c2)
        {
            Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r), s, t;
            if (a <= 1e-12f && e <= 1e-12f)
            {
                c1 = p1;
                c2 = p2;
                return;
            }
            if (a <= 1e-12f)
            {
                s = 0f;
                t = Mathf.Clamp01(f / e);
            }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= 1e-12f)
                {
                    t = 0f;
                    s = Mathf.Clamp01(-c / a);
                }
                else
                {
                    float b = Vector3.Dot(d1, d2), denom = a * e - b * b;
                    s = denom != 0f ? Mathf.Clamp01((b * f - c * e) / denom) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f)
                    {
                        t = 0f;
                        s = Mathf.Clamp01(-c / a);
                    }
                    else if (t > 1f)
                    {
                        t = 1f;
                        s = Mathf.Clamp01((b - c) / a);
                    }
                }
            }
            c1 = p1 + d1 * s;
            c2 = p2 + d2 * t;
        }
        public static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f)
            {
                return a;
            }
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3)
            {
                return b;
            }
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                return a + ab * (d1 / (d1 - d3));
            }
            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6)
            {
                return c;
            }
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                return a + ac * (d2 / (d2 - d6));
            }
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
            {
                return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
            }
            float denom = 1f / (va + vb + vc);
            return a + ab * (vb * denom) + ac * (vc * denom);
        }
    }
    public struct BasisRendererHit
    {
        public Vector3 point, normal;
        public float distance;
    }
    [NoAutoStaticsCleanup]
    public static class BasisRendererRaycast
    {
        private static readonly BasisMeshGraspSurface cache = new BasisMeshGraspSurface();
        private static Renderer cachedRenderer;
        private static int cachedFrame = -1;
        public static void Invalidate() => cachedRenderer = null;
        public static bool Raycast(Renderer renderer, Ray ray, float maxDistance, out BasisRendererHit hit)
        {
            hit = default;
            if (renderer == null || !renderer.enabled || maxDistance <= 0f)
            {
                return false;
            }
            Vector3 direction = ray.direction;
            if (direction.sqrMagnitude < 1e-12f)
            {
                return false;
            }
            direction.Normalize();
            if (!(renderer is SkinnedMeshRenderer))
            {
                Bounds bounds = renderer.bounds;
                if (!bounds.IntersectRay(new Ray(ray.origin, direction), out float entry) && !bounds.Contains(ray.origin))
                {
                    return false;
                }
                if (entry > maxDistance)
                {
                    return false;
                }
            }
            if (!ReferenceEquals(cachedRenderer, renderer) || cachedFrame != Time.frameCount)
            {
                cache.Clear();
                cachedRenderer = null;
                if (!cache.Add(renderer, Vector3.zero, 0f))
                {
                    return false;
                }
                cachedRenderer = renderer;
                cachedFrame = Time.frameCount;
            }
            if (!cache.Raycast(ray.origin, direction, maxDistance, out float distance, out Vector3 normal))
            {
                return false;
            }
            hit = new BasisRendererHit { point = ray.origin + direction * distance, normal = normal, distance = distance };
            return true;
        }
    }
    [NoAutoStaticsCleanup]
    public static class BasisFingerGraspSolver
    {
        public const float OpenCurl = 0.75f, ClosedCurl = -1f;
        public static int MinSteps = 6, MaxSteps = 48, BisectSteps = 6;
        public static float JamDepth = 0.5f, StepTravel = 1f;
        private static readonly Vector3[] points = new Vector3[4];
        private static readonly float[] baseDepth = new float[3], curl = new float[3];
        public static bool BuildChain(Transform wrist, Transform proximal, Transform intermediate, Transform distal, out BasisFingerChain chain)
        {
            chain = default;
            if (wrist == null || proximal == null || intermediate == null || distal == null || proximal.parent == null || intermediate.parent == null || distal.parent == null)
            {
                return false;
            }
            Quaternion wristInverse = Quaternion.Inverse(wrist.rotation), proximalInverse = Quaternion.Inverse(proximal.rotation), intermediateInverse = Quaternion.Inverse(intermediate.rotation), distalInverse = Quaternion.Inverse(distal.rotation);
            chain.proximalOffset = wristInverse * (proximal.position - wrist.position);
            chain.proximalParent = wristInverse * proximal.parent.rotation;
            chain.intermediateOffset = proximalInverse * (intermediate.position - proximal.position);
            chain.intermediateParent = proximalInverse * intermediate.parent.rotation;
            chain.distalOffset = intermediateInverse * (distal.position - intermediate.position);
            chain.distalParent = intermediateInverse * distal.parent.rotation;
            float middle = chain.distalOffset.magnitude;
            Vector3 tip = distal.position + (distal.position - intermediate.position) * 0.8f;
            if (distal.childCount > 0)
            {
                Vector3 child = distal.GetChild(0).position;
                float length = (child - distal.position).magnitude;
                if (length > middle * 0.3f && length < middle * 1.6f)
                {
                    tip = child;
                }
            }
            chain.tipOffset = distalInverse * (tip - distal.position);
            float proximalLength = chain.intermediateOffset.magnitude;
            if (proximalLength < 1e-5f || middle < 1e-5f)
            {
                return false;
            }
            chain.radius = proximalLength * 0.22f;
            chain.valid = true;
            return true;
        }
        public static float SegmentRadius(in BasisFingerChain chain, int segment) => chain.radius * (segment == 0 ? 1f : segment == 1 ? 0.9f : 0.8f);
        public static void Pose(in BasisFingerChain chain, BasisHandPoseGrid grid, int finger, Vector3 wristPosition, Quaternion wristRotation, float c0, float c1, float c2, float splay, Vector3[] output)
        {
            Quaternion r = wristRotation * chain.proximalParent * (Quaternion)grid.SampleJoint(finger, 0, new float2(c0, splay));
            Vector3 p = wristPosition + wristRotation * chain.proximalOffset;
            output[0] = p;
            p += r * chain.intermediateOffset;
            output[1] = p;
            r = r * chain.intermediateParent * (Quaternion)grid.SampleJoint(finger, 1, new float2(c1, splay));
            p += r * chain.distalOffset;
            output[2] = p;
            r = r * chain.distalParent * (Quaternion)grid.SampleJoint(finger, 2, new float2(c2, splay));
            output[3] = p + r * chain.tipOffset;
        }
        private static int Hit(in BasisFingerChain chain, BasisHandPoseGrid grid, int finger, Vector3 wp, Quaternion wr, float splay, BasisGraspSurface surface, int first, float skin)
        {
            Pose(chain, grid, finger, wp, wr, curl[0], curl[1], curl[2], splay, points);
            for (int k = 2; k >= first; k--)
            {
                if (surface.Exceeds(points[k], points[k + 1], SegmentRadius(chain, k), baseDepth[k] + skin))
                {
                    return k;
                }
            }
            return -1;
        }
        private static readonly Vector3[] closedPoints = new Vector3[4];
        private static int Steps(in BasisFingerChain chain, BasisHandPoseGrid grid, int finger, Vector3 wp, Quaternion wr, float splay, int first, float start)
        {
            Pose(chain, grid, finger, wp, wr, curl[0], curl[1], curl[2], splay, points);
            float c0 = first > 0 ? curl[0] : ClosedCurl, c1 = first > 1 ? curl[1] : ClosedCurl;
            Pose(chain, grid, finger, wp, wr, c0, c1, ClosedCurl, splay, closedPoints);
            float travel = 0f;
            for (int k = 1; k < 4; k++)
            {
                travel = Mathf.Max(travel, (closedPoints[k] - points[k]).magnitude);
            }
            return Mathf.Clamp(Mathf.CeilToInt(travel / (StepTravel * SegmentRadius(chain, 2))), MinSteps, MaxSteps);
        }
        private static void SetFrom(int first, float value)
        {
            for (int j = first; j < 3; j++)
            {
                curl[j] = value;
            }
        }
        public static int SolveFinger(BasisHandPoseGrid grid, int finger, in BasisFingerChain chain, Vector3 wristPosition, Quaternion wristRotation, BasisGraspSurface surface, float splay, float[] curls, int offset)
        {
            float reach = chain.intermediateOffset.magnitude + chain.distalOffset.magnitude + chain.tipOffset.magnitude + chain.radius;
            if (!surface.Focus(wristPosition + wristRotation * chain.proximalOffset, reach))
            {
                curls[offset] = curls[offset + 1] = curls[offset + 2] = ClosedCurl;
                return 0;
            }
            SetFrom(0, OpenCurl);
            Pose(chain, grid, finger, wristPosition, wristRotation, OpenCurl, OpenCurl, OpenCurl, splay, points);
            for (int k = 0; k < 3; k++)
            {
                baseDepth[k] = Mathf.Max(0f, surface.Depth(points[k], points[k + 1], SegmentRadius(chain, k)));
            }
            float skin = chain.radius * 0.03f;
            int first = 0, contacts = 0;
            for (int k = 2; k >= 0; k--)
            {
                if (baseDepth[k] <= JamDepth * SegmentRadius(chain, k))
                {
                    continue;
                }
                first = k + 1;
                contacts = 1;
                break;
            }
            while (first < 3)
            {
                float start = curl[first], free = start, hit = ClosedCurl;
                if (start <= ClosedCurl + 1e-4f)
                {
                    break;
                }
                int hitSegment = -1, steps = Steps(chain, grid, finger, wristPosition, wristRotation, splay, first, start);
                for (int s = 1; s <= steps; s++)
                {
                    float v = start + (ClosedCurl - start) * s / steps;
                    SetFrom(first, v);
                    int k = Hit(chain, grid, finger, wristPosition, wristRotation, splay, surface, first, skin);
                    if (k >= 0)
                    {
                        hit = v;
                        hitSegment = k;
                        break;
                    }
                    free = v;
                }
                if (hitSegment < 0)
                {
                    break;
                }
                for (int b = 0; b < BisectSteps; b++)
                {
                    float mid = (free + hit) * 0.5f;
                    SetFrom(first, mid);
                    int k = Hit(chain, grid, finger, wristPosition, wristRotation, splay, surface, first, skin);
                    if (k >= 0)
                    {
                        hit = mid;
                        hitSegment = k;
                    }
                    else
                    {
                        free = mid;
                    }
                }
                SetFrom(first, free);
                contacts++;
                first = hitSegment + 1;
            }
            curls[offset] = curl[0];
            curls[offset + 1] = curl[1];
            curls[offset + 2] = curl[2];
            return contacts;
        }
        public static int SolveHand(BasisHandPoseGrid grid, bool left, Transform wrist, Transform[] joints, Vector3 wristPosition, Quaternion wristRotation, BasisGraspSurface surface, float[] splays, float[] curls, int[] fingerContacts)
        {
            int contacts = 0, firstFinger = left ? 0 : 5;
            surface.Begin();
            try
            {
                for (int f = firstFinger; f < firstFinger + 5; f++)
                {
                    int j = f * 3;
                    fingerContacts[f] = -1;
                    if (!BuildChain(wrist, joints[j], joints[j + 1], joints[j + 2], out BasisFingerChain chain))
                    {
                        continue;
                    }
                    fingerContacts[f] = SolveFinger(grid, f, chain, wristPosition, wristRotation, surface, splays != null ? splays[f] : 0f, curls, j);
                    contacts += fingerContacts[f];
                }
            }
            finally
            {
                surface.End();
            }
            return contacts;
        }
    }
}
