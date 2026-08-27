#ifndef BASIS_RTAO_COMMON_INCLUDED
#define BASIS_RTAO_COMMON_INCLUDED

float2 BasisRtaoOctWrap(float2 v)
{
    return (1.0 - abs(v.yx)) * float2(v.x >= 0.0 ? 1.0 : -1.0, v.y >= 0.0 ? 1.0 : -1.0);
}

float2 BasisRtaoEncodeNormal(float3 n)
{
    n /= max(1e-6, abs(n.x) + abs(n.y) + abs(n.z));
    n.xy = n.z >= 0.0 ? n.xy : BasisRtaoOctWrap(n.xy);
    return n.xy;
}

float3 BasisRtaoDecodeNormal(float2 f)
{
    float3 n = float3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    float t = saturate(-n.z);
    n.xy += float2(n.x >= 0.0 ? -t : t, n.y >= 0.0 ? -t : t);
    return normalize(n);
}

uint BasisRtaoHash(uint x)
{
    x ^= x >> 16;
    x *= 0x7feb352du;
    x ^= x >> 15;
    x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}

uint BasisRtaoHashCell(int3 cell, uint seed)
{
    uint h = uint(cell.x) * 73856093u ^ uint(cell.y) * 19349663u ^ uint(cell.z) * 83492791u;
    return BasisRtaoHash(h ^ BasisRtaoHash(seed));
}

float BasisRtaoUnitFloat(uint u)
{
    return float(u & 0x00ffffffu) * (1.0 / 16777216.0);
}

float2 BasisRtaoHammersley(uint index, uint count, float2 offset)
{
    uint bits = index;
    bits = (bits << 16u) | (bits >> 16u);
    bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xaaaaaaaau) >> 1u);
    bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xccccccccu) >> 2u);
    bits = ((bits & 0x0f0f0f0fu) << 4u) | ((bits & 0xf0f0f0f0u) >> 4u);
    bits = ((bits & 0x00ff00ffu) << 8u) | ((bits & 0xff00ff00u) >> 8u);
    return frac(float2(float(index) / float(count), float(bits) * 2.3283064365386963e-10) + offset);
}

// Duff et al., branchless and stable for every n including n.z near -1, which the naive
// cross-with-an-axis basis is not.
void BasisRtaoOrthonormalBasis(float3 n, out float3 tangent, out float3 bitangent)
{
    float s = n.z >= 0.0 ? 1.0 : -1.0;
    float a = -1.0 / (s + n.z);
    float b = n.x * n.y * a;
    tangent = float3(1.0 + s * n.x * n.x * a, s * b, -s * n.x);
    bitangent = float3(b, s + n.y * n.y * a, -n.y);
}

// Concentric disc lifted onto the hemisphere, in the surface's own tangent frame. The older form
// normalised normalWS plus a uniform sphere point: also exactly cosine distributed, but its
// stratification lives in world axes rather than the surface frame, so how evenly a handful of rays
// cover the hemisphere depended on which way the surface happened to face, and the sphere point
// landing near -normalWS collapsed the sum to nothing and fell back to firing straight up.
float3 BasisRtaoCosineHemisphere(float2 u, float3 normalWS)
{
    float3 tangent, bitangent;
    BasisRtaoOrthonormalBasis(normalWS, tangent, bitangent);
    float radius = sqrt(saturate(u.x));
    float phi = 6.2831853071795864 * u.y;
    return tangent * (radius * cos(phi)) + bitangent * (radius * sin(phi)) + normalWS * sqrt(saturate(1.0 - u.x));
}

// The per pixel start of the sample sequence, and how it advances between frames.
//
// A fixed world grid is many pixels across up close and sub pixel far away, so it reads as blocky
// patches on anything near the camera that quietly vanish with distance. Scaling the cell with view
// distance keeps it roughly one screen pixel everywhere, and both eyes still see the same distance
// for the same surface point, so they still agree on the seed.
//
// The frame index advances the offset along the R2 lattice instead of re-hashing the cell with it.
// Re-hashing draws an independent white noise offset every frame, so what the temporal filter
// averages is a random walk that converges at 1/sqrt(n); R2 is low discrepancy in time, so the same
// number of accumulated frames covers the square far more evenly. Both eyes advance by the same
// amount from the same start, so stereo coherence is untouched.
//
// The index wraps first. A raw frame counter reaches six figures inside an hour, and by then a float
// has no fraction left to carry - the lattice collapses onto a handful of values and the sequence
// quietly stops advancing. The longest accumulation is 64 frames, so a period of 1024 is never
// something the filter can see.
float2 BasisRtaoSampleJitter(float3 positionWS, float viewDistance, float cellSize, uint3 pixel, uint frameIndex, bool stereoCoherent)
{
    uint seed;
    if (stereoCoherent)
    {
        float noiseCell = max(1e-5, cellSize * max(0.05, viewDistance));
        seed = BasisRtaoHashCell(int3(floor(positionWS / noiseCell)), 0u);
    }
    else
    {
        seed = BasisRtaoHash(pixel.x * 1973u + pixel.y * 9277u + pixel.z * 26699u);
    }

    float2 origin = float2(BasisRtaoUnitFloat(seed), BasisRtaoUnitFloat(BasisRtaoHash(seed ^ 0x9e3779b9u)));
    return frac(origin + float(frameIndex & 1023u) * float2(0.7548776662466927, 0.5698402909980532));
}

float2 BasisRtaoProjectToScreenUV(float4x4 viewProj, float3 positionWS, out float clipW)
{
    float4 clip = mul(viewProj, float4(positionWS, 1.0));
    clipW = clip.w;
    return clip.xy / max(1e-6, clip.w) * 0.5 + 0.5;
}

float BasisRtaoViewDepth(float3 positionWS, float3 referencePositionWS, float3 viewForwardWS)
{
    return dot(positionWS - referencePositionWS, viewForwardWS);
}

#endif
