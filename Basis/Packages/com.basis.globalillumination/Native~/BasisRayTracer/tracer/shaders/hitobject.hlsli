#ifndef BASIS_RT_HITOBJECT
#define BASIS_RT_HITOBJECT

#ifdef BASIS_SPIRV
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

[[vk::binding(4, 0)]] RaytracingAccelerationStructure basisTlas;

HitObj TraceHitObject(RaytracingAccelerationStructure tlas, uint flags, uint mask, RayDesc ray)
{
    Payload payload = (Payload)0;
    TraceRay(tlas, flags & ~RAY_FLAG_SKIP_CLOSEST_HIT_SHADER, mask, 0, 0, 0, ray, payload);
    HitObj h;
    h.p = payload;
    h.direction = ray.Direction;
    return h;
}

#define LOAD_TLAS() basisTlas
#define TraceHit(tlas, flags, mask, ray, payload) TraceHitObject(tlas, flags, mask, ray)
#define ReorderHit(hitObject, hint, bits)
#define STORE_HIT(payload, attributes) payload.hit = 1u; payload.instance = InstanceID(); payload.geometry = GeometryIndex(); payload.primitive = PrimitiveIndex(); payload.barycentrics = attributes.barycentrics; payload.t = RayTCurrent(); payload.objectToWorld = ObjectToWorld3x4(); payload.worldToObject = WorldToObject3x4()
#else
struct [raypayload] Payload { uint unused : read(caller, anyhit, closesthit, miss) : write(caller, anyhit, closesthit, miss); };
#define HitObj dx::HitObject
#define LOAD_TLAS() ResourceDescriptorHeap[HEAP_TLAS]
#define TraceHit(tlas, flags, mask, ray, payload) dx::HitObject::TraceRay(tlas, flags, mask, 0, 0, 0, ray, payload)
#define ReorderHit(hitObject, hint, bits) dx::MaybeReorderThread(hitObject, hint, bits)
#define STORE_HIT(payload, attributes)
#endif

#endif
