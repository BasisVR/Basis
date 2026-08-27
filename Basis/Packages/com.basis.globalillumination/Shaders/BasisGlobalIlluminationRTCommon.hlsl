#ifndef BASIS_GLOBAL_ILLUMINATION_RT_COMMON_INCLUDED
#define BASIS_GLOBAL_ILLUMINATION_RT_COMMON_INCLUDED

#define BASISGI_RT_EPSILON 1e-5
// The light buffer's own length, and how many of those a hit may shadow-ray. Resampling decides
// which ones, so the second number buys variance rather than reach.
#define BASISGI_RT_MAX_LIGHTS 64
#define BASISGI_RT_MAX_LIGHT_SAMPLES 4
#define BASISGI_RT_FLAG_HAS_NORMALS 1u

struct BasisGIRtInstance
{
    float4 albedo;
    float4 emission;
    uint4 geometry;
    float4 normal0;
    float4 normal1;
    float4 normal2;
};

struct BasisGIRtLight
{
    float4 position;
    float4 direction;
    float4 color;
    float4 spot;
};

float2 BasisGIRtOctWrap(float2 v)
{
    return (1.0 - abs(v.yx)) * float2(v.x >= 0.0 ? 1.0 : -1.0, v.y >= 0.0 ? 1.0 : -1.0);
}

float2 BasisGIRtEncodeNormal(float3 n)
{
    n /= max(1e-6, abs(n.x) + abs(n.y) + abs(n.z));
    n.xy = n.z >= 0.0 ? n.xy : BasisGIRtOctWrap(n.xy);
    return n.xy;
}

float3 BasisGIRtDecodeNormal(float2 f)
{
    float3 n = float3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    float t = saturate(-n.z);
    n.xy += float2(n.x >= 0.0 ? -t : t, n.y >= 0.0 ? -t : t);
    return normalize(n);
}

/// Two signed 16 bit halves written by BasisGlobalIlluminationRayScene.PackNormal.
float3 BasisGIRtUnpackNormal(uint packed)
{
    int bits = asint(packed);
    int x = (bits << 16) >> 16;
    int y = bits >> 16;
    return BasisGIRtDecodeNormal(clamp(float2(x, y) / 32767.0, -1.0, 1.0));
}

uint BasisGIRtHash(uint x)
{
    x ^= x >> 16;
    x *= 0x7feb352du;
    x ^= x >> 15;
    x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}

/// Interleaved gradient noise: neighbouring pixels get well separated rotations, so what the spatial
/// filter has to remove is a smooth gradient rather than white speckle.
float BasisGIRtInterleavedGradientNoise(float2 pixel, float frame)
{
    pixel += frame * float2(47.0, 17.0) * 0.695;
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

float BasisGIRtUnitFloat(uint u)
{
    return float(u & 0x00ffffffu) * (1.0 / 16777216.0);
}

float2 BasisGIRtHammersley(uint index, uint count, float2 offset)
{
    uint bits = index;
    bits = (bits << 16u) | (bits >> 16u);
    bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xaaaaaaaau) >> 1u);
    bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xccccccccu) >> 2u);
    bits = ((bits & 0x0f0f0f0fu) << 4u) | ((bits & 0xf0f0f0f0u) >> 4u);
    bits = ((bits & 0x00ff00ffu) << 8u) | ((bits & 0xff00ff00u) >> 8u);
    return frac(float2(float(index) / max(1.0, float(count)), float(bits) * 2.3283064365386963e-10) + offset);
}

float3 BasisGIRtCosineHemisphere(float2 u, float3 normalWS)
{
    float z = 1.0 - 2.0 * u.x;
    z = clamp(z, -0.99999, 0.99999);
    float r = sqrt(1.0 - z * z);
    float phi = 6.2831853071795864 * u.y;
    float3 sphere = float3(r * cos(phi), r * sin(phi), z);
    float3 direction = normalWS + sphere;
    float lengthSquared = dot(direction, direction);
    return lengthSquared < 1e-6 ? normalWS : direction * rsqrt(lengthSquared);
}

float3 BasisGIRtClampFirefly(float3 radiance, float ceiling)
{
    float peak = max(radiance.r, max(radiance.g, radiance.b));
    float scale = peak > ceiling ? ceiling / max(peak, BASISGI_RT_EPSILON) : 1.0;
    return radiance * scale;
}

float3 BasisGIRtInstanceNormal(BasisGIRtInstance instance, float3 objectNormal)
{
    float3x3 matrixWS = float3x3(instance.normal0.xyz, instance.normal1.xyz, instance.normal2.xyz);
    float3 worldNormal = mul(matrixWS, objectNormal);
    float lengthSquared = dot(worldNormal, worldNormal);
    return lengthSquared < 1e-12 ? objectNormal : worldNormal * rsqrt(lengthSquared);
}

/// URP's punctual falloff, so a bounce off a wall carries the same shape the wall was lit with.
float BasisGIRtDistanceAttenuation(float distanceSquared, float inverseRangeSquared)
{
    float attenuation = rcp(max(distanceSquared, 1e-4));
    float factor = distanceSquared * inverseRangeSquared;
    float smoothFactor = saturate(1.0 - factor * factor);
    return attenuation * smoothFactor * smoothFactor;
}

float BasisGIRtSpotAttenuation(BasisGIRtLight light, float3 toLight)
{
    float cosAngle = dot(-toLight, normalize(light.direction.xyz));
    float attenuation = saturate(cosAngle * light.spot.x + light.spot.y);
    return attenuation * attenuation;
}

#endif
