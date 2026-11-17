#ifndef BASIS_RT_HITOBJECT
#define BASIS_RT_HITOBJECT

struct Payload
{
    uint hit, instance, geometry, primitive;
    float2 barycentrics;
    float t;
    float3x4 objectToWorld;
    float3x4 worldToObject;
};

struct HitObj
{
    Payload p;
    float3 direction;
    bool IsHit() { return p.hit != 0u; }
    uint GetInstanceID() { return p.instance; }
    uint GetGeometryIndex() { return p.geometry; }
    uint GetPrimitiveIndex() { return p.primitive; }
    float GetRayTCurrent() { return p.t; }
    float3 GetWorldRayDirection() { return direction; }
    float3x4 GetObjectToWorld3x4() { return p.objectToWorld; }
    float3x4 GetWorldToObject3x4() { return p.worldToObject; }
    void GetAttributes(out BuiltInTriangleIntersectionAttributes a) { a.barycentrics = p.barycentrics; }
};

static uint2 traceIndex;

#ifdef BASIS_SPIRV
[[vk::binding(4, 0)]] RaytracingAccelerationStructure basisTlas;
#define LOAD_TLAS() basisTlas
#else
#define LOAD_TLAS() ResourceDescriptorHeap[HEAP_TLAS]
#endif

#endif
