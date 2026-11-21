#ifndef BASIS_RENDERER_RAYS_INCLUDED
#define BASIS_RENDERER_RAYS_INCLUDED
struct BasisRayNode
{
    uint lo01;
    uint lo2hi0;
    uint hi12;
    int link;
    int extra;
};
struct BasisRayGroup
{
    int triangleStart;
    int triangleCount;
    int nodeStart;
    int partStart;
    int partEnd;
};
struct BasisRayHit
{
    float3 position;
    float distance;
    float3 normal;
    int renderer;
    int triangleIndex;
    int hit;
};
StructuredBuffer<BasisRayNode> _BasisRayNodes;
StructuredBuffer<int3> _BasisRayIndices;
StructuredBuffer<float3> _BasisRayVertices;
StructuredBuffer<int> _BasisRayTrianglePart;
StructuredBuffer<int> _BasisRayTriangleLocal;
StructuredBuffer<BasisRayGroup> _BasisRayGroups;
StructuredBuffer<int> _BasisRayGroupNodeCount;
StructuredBuffer<float3> _BasisRayGroupOrigin;
int _BasisRayGroupCount;
void BasisRayDecode(BasisRayNode node, out float3 lo, out float3 hi)
{
    lo = float3(f16tof32(node.lo01), f16tof32(node.lo01 >> 16), f16tof32(node.lo2hi0));
    hi = float3(f16tof32(node.lo2hi0 >> 16), f16tof32(node.hi12), f16tof32(node.hi12 >> 16));
}
uint BasisRayHalfDown(float v)
{
    uint h = f32tof16(v);
    if (f16tof32(h) > v)
    {
        h = (h & 0x7fff) == 0 ? 0x8001 : ((h & 0x8000) != 0 ? h + 1 : h - 1);
    }
    return h;
}
uint BasisRayHalfUp(float v)
{
    uint h = f32tof16(v);
    if (f16tof32(h) < v)
    {
        h = (h & 0x7fff) == 0 ? 0x0001 : ((h & 0x8000) != 0 ? h - 1 : h + 1);
    }
    return h;
}
void BasisRayEncode(inout BasisRayNode node, float3 lo, float3 hi)
{
    node.lo01 = BasisRayHalfDown(lo.x) | (BasisRayHalfDown(lo.y) << 16);
    node.lo2hi0 = BasisRayHalfDown(lo.z) | (BasisRayHalfUp(hi.x) << 16);
    node.hi12 = BasisRayHalfUp(hi.y) | (BasisRayHalfUp(hi.z) << 16);
}
bool BasisRaySlab(BasisRayNode node, float3 local, float3 inverse, float best, out float nearT)
{
    float3 lo, hi;
    BasisRayDecode(node, lo, hi);
    float3 t1 = (lo - local) * inverse;
    float3 t2 = (hi - local) * inverse;
    float3 a = min(t1, t2);
    float3 b = max(t1, t2);
    nearT = max(max(a.x, a.y), a.z);
    float farT = min(min(b.x, b.y), b.z);
    return farT >= max(nearT, 0.0) && nearT <= best;
}
void BasisRayLeaf(BasisRayNode node, float3 origin, float3 d, inout float best, inout int found)
{
    int start = -node.link - 1;
    [loop] for (int t = start; t < start + node.extra; t++)
    {
        int3 i = _BasisRayIndices[t];
        float3 v0 = _BasisRayVertices[i.x];
        float3 e1 = _BasisRayVertices[i.y] - v0;
        float3 e2 = _BasisRayVertices[i.z] - v0;
        float3 p = cross(d, e2);
        float det = dot(e1, p);
        float inv = 1.0 / det;
        float3 s = origin - v0;
        float u = dot(s, p) * inv;
        float3 q = cross(s, e1);
        float v = dot(d, q) * inv;
        float hitT = dot(e2, q) * inv;
        if (abs(det) >= 1e-12 && u >= 0.0 && u <= 1.0 && v >= 0.0 && u + v <= 1.0 && hitT >= 0.0 && hitT < best)
        {
            best = hitT;
            found = t;
        }
    }
}
#if defined(UNITY_COMPILER_DXC) && !defined(BASIS_RAY_STACKLESS)
#define BASIS_RAY_NEAREST_FIRST 1
#define BASIS_RAY_STACK 64
#else
#define BASIS_RAY_NEAREST_FIRST 0
#endif
bool BasisRayTrace(float3 origin, float3 direction, float maxDistance, out BasisRayHit result)
{
    result = (BasisRayHit)0;
    result.renderer = -1;
    result.triangleIndex = -1;
    result.distance = -1.0;
    float lengthSq = dot(direction, direction);
    if (!(lengthSq > 0.0) || !(maxDistance > 0.0))
    {
        return false;
    }
    float3 d = direction * rsqrt(lengthSq);
    float3 inverse = 1.0 / d;
    float best = maxDistance;
    int found = -1;
#if BASIS_RAY_NEAREST_FIRST
    int stack[BASIS_RAY_STACK];
#endif
    [loop] for (int g = 0; g < _BasisRayGroupCount; g++)
    {
        int count = _BasisRayGroupNodeCount[g];
        if (count == 0)
        {
            continue;
        }
        int first = _BasisRayGroups[g].nodeStart;
        float3 local = origin - _BasisRayGroupOrigin[g];
        float rootNear;
        if (!BasisRaySlab(_BasisRayNodes[first], local, inverse, best, rootNear))
        {
            continue;
        }
#if BASIS_RAY_NEAREST_FIRST
        int top = 0;
        stack[top++] = first;
        [loop] while (top > 0)
        {
            int n = stack[--top];
            BasisRayNode node = _BasisRayNodes[n];
            float nearT;
            if (!BasisRaySlab(node, local, inverse, best, nearT))
            {
                continue;
            }
            if (node.link < 0)
            {
                BasisRayLeaf(node, origin, d, best, found);
                continue;
            }
            float nearLeft, nearRight;
            bool hitLeft = BasisRaySlab(_BasisRayNodes[n + 1], local, inverse, best, nearLeft);
            bool hitRight = BasisRaySlab(_BasisRayNodes[node.link], local, inverse, best, nearRight);
            if (top > BASIS_RAY_STACK - 2)
            {
                continue;
            }
            if (hitLeft && hitRight)
            {
                bool leftFirst = nearLeft <= nearRight;
                stack[top++] = leftFirst ? node.link : n + 1;
                stack[top++] = leftFirst ? n + 1 : node.link;
            }
            else if (hitLeft)
            {
                stack[top++] = n + 1;
            }
            else if (hitRight)
            {
                stack[top++] = node.link;
            }
        }
#else
        int n = first;
        int end = first + count;
        [loop] while (n < end)
        {
            BasisRayNode node = _BasisRayNodes[n];
            float nearT;
            bool inside = BasisRaySlab(node, local, inverse, best, nearT);
            if (node.link >= 0)
            {
                n = inside ? n + 1 : node.extra;
                continue;
            }
            if (inside)
            {
                BasisRayLeaf(node, origin, d, best, found);
            }
            n = n + 1;
        }
#endif
    }
    if (found < 0)
    {
        return false;
    }
    int3 hitIndex = _BasisRayIndices[found];
    float3 a = _BasisRayVertices[hitIndex.x];
    float3 normal = normalize(cross(_BasisRayVertices[hitIndex.y] - a, _BasisRayVertices[hitIndex.z] - a));
    if (dot(normal, d) > 0.0)
    {
        normal = -normal;
    }
    result.position = origin + d * best;
    result.distance = best;
    result.normal = normal;
    result.renderer = _BasisRayTrianglePart[found];
    result.triangleIndex = _BasisRayTriangleLocal[found];
    result.hit = 1;
    return true;
}
#endif
