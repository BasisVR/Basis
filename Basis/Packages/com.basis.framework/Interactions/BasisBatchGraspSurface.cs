using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
namespace Basis.Scripts.BasisSdk.Interactions
{
    public sealed class BasisBatchGraspSurface : BasisGraspSurface
    {
        private BasisRendererRayBatch batch;
        private NativeArray<ulong> mask;
        private bool masked, focused;
        private readonly List<int> focus = new List<int>();
        private readonly int[] stack = new int[128];
        public void Set(BasisRendererRayBatch source, NativeArray<ulong> rendererMask = default)
        {
            batch = source;
            mask = rendererMask;
            masked = rendererMask.IsCreated;
            focus.Clear();
            focused = false;
        }
        private bool Allowed(int part)
        {
            return !masked || (part >> 6) < mask.Length && (mask[part >> 6] & (1ul << (part & 63))) != 0;
        }
        private bool PerRenderer => batch.groupCount == batch.parts.Count;
        private bool Eligible(int g)
        {
            if (batch.groupNodeCount[g] == 0 || batch.groupStale[g] != 0)
            {
                return false;
            }
            return !masked || !PerRenderer || Allowed(batch.groups[g].partStart);
        }
        public override void Begin()
        {
            batch?.CompleteBuild();
        }
        public override void End()
        {
            focused = false;
            focus.Clear();
        }
        public override bool Focus(Vector3 center, float radius)
        {
            focus.Clear();
            focused = true;
            if (batch == null || batch.groupCount == 0)
            {
                return false;
            }
            float3 c = center;
            for (int g = 0; g < batch.groupCount; g++)
            {
                if (!Eligible(g))
                {
                    continue;
                }
                BasisRayBvhNode root = batch.nodes[batch.groups[g].nodeStart];
                float3 near = math.clamp(c, root.min, root.max);
                if (math.distancesq(near, c) <= radius * radius)
                {
                    focus.Add(g);
                }
            }
            return focus.Count > 0;
        }
        private static bool Overlaps(float3 mn, float3 mx, float3 lo, float3 hi)
        {
            return !(mx.x < lo.x || mn.x > hi.x || mx.y < lo.y || mn.y > hi.y || mx.z < lo.z || mn.z > hi.z);
        }
        private float Query(Vector3 a, Vector3 b, float radius, float threshold, bool early, out bool exceeded)
        {
            exceeded = false;
            float best = float.MaxValue;
            bool behind = false;
            if (batch == null || batch.groupCount == 0)
            {
                return 0f;
            }
            float3 pad = new float3(radius), lo = math.min((float3)a, (float3)b) - pad, hi = math.max((float3)a, (float3)b) + pad;
            float inside = radius - threshold;
            bool laneMask = masked && !PerRenderer;
            int groupTotal = focused ? focus.Count : batch.groupCount;
            for (int gi = 0; gi < groupTotal; gi++)
            {
                int g = focused ? focus[gi] : gi;
                if (!focused && !Eligible(g))
                {
                    continue;
                }
                int top = 0;
                stack[top++] = batch.groups[g].nodeStart;
                while (top > 0)
                {
                    int n = stack[--top];
                    BasisRayBvhNode node = batch.nodes[n];
                    if (!Overlaps(node.min, node.max, lo, hi))
                    {
                        continue;
                    }
                    if (node.right >= 0)
                    {
                        if (top < stack.Length - 2)
                        {
                            stack[top++] = node.right;
                            stack[top++] = n + 1;
                        }
                        continue;
                    }
                    int first = batch.nodePack[n], last = first + ((node.count + 3) >> 2);
                    for (int k = first; k < last; k++)
                    {
                        BasisRayTrianglePack p = batch.packs[k];
                        for (int lane = 0; lane < 4; lane++)
                        {
                            int t = p.triangle[lane];
                            if (t < 0)
                            {
                                break;
                            }
                            if (laneMask && !Allowed(batch.sortedPart[t]))
                            {
                                continue;
                            }
                            Vector3 v0 = new Vector3(p.v0x[lane], p.v0y[lane], p.v0z[lane]), e1 = new Vector3(p.e1x[lane], p.e1y[lane], p.e1z[lane]), e2 = new Vector3(p.e2x[lane], p.e2y[lane], p.e2z[lane]);
                            Vector3 v1 = v0 + e1, v2 = v0 + e2;
                            float3 tlo = math.min((float3)v0, math.min((float3)v1, (float3)v2)), thi = math.max((float3)v0, math.max((float3)v1, (float3)v2));
                            if (!Overlaps(tlo, thi, lo, hi))
                            {
                                continue;
                            }
                            Vector3 normal = Vector3.Cross(e1, e2);
                            float area = normal.magnitude;
                            if (area < 1e-12f)
                            {
                                continue;
                            }
                            normal /= area;
                            float d = BasisMeshGraspSurface.SegmentTriangle(a, b, v0, v1, v2, normal, out Vector3 onSegment, out Vector3 onTriangle, out bool crosses, radius);
                            if (crosses)
                            {
                                exceeded = true;
                                return 2f * radius;
                            }
                            if (early && d < inside)
                            {
                                exceeded = true;
                                return radius - d;
                            }
                            if (d < best && d <= radius)
                            {
                                best = d;
                                behind = Vector3.Dot(normal, onSegment - onTriangle) < 0f;
                            }
                        }
                    }
                }
            }
            if (best == float.MaxValue)
            {
                exceeded = 0f > threshold;
                return 0f;
            }
            float depth = behind ? radius + best : radius - best;
            exceeded = depth > threshold;
            return depth;
        }
        public override float Depth(Vector3 a, Vector3 b, float radius)
        {
            return Query(a, b, radius, 0f, false, out _);
        }
        public override bool Exceeds(Vector3 a, Vector3 b, float radius, float threshold)
        {
            Query(a, b, radius, threshold, true, out bool exceeded);
            return exceeded;
        }
    }
}
