#ifndef BASIS_WORLD_REFLECTION_INCLUDED
#define BASIS_WORLD_REFLECTION_INCLUDED

#ifndef BASISGI_WORLD_GLOBALS_DECLARED
#define BASISGI_WORLD_GLOBALS_DECLARED
float4 _BasisGIWorldLevel[4];
float4 _BasisGIWorldParams;
float4 _BasisGIWorldSHParams;
TEXTURE3D(_BasisGIWorldSH0);
TEXTURE3D(_BasisGIWorldSH1);
TEXTURE3D(_BasisGIWorldSH2);
TEXTURE3D(_BasisGIWorldSH3);
Texture3D<float4> _BasisGIVoxAlbedo;
#endif

#ifndef BASISGI_WORLD_SPECULAR_DECLARED
#define BASISGI_WORLD_SPECULAR_DECLARED
TEXTURE2D_ARRAY(_BasisGIWorldSpecular);
float4 _BasisGIWorldSpecularParams;
#endif
static half3 basisWorldReflectionNormal = half3(0.0, 0.0, 0.0);

half4 BasisWorldReflection(float3 positionWS, half3 normalWS, half3 reflectVector, half perceptualRoughness)
{
#if defined(SHADER_API_GLES3)
    return half4(0.0, 0.0, 0.0, 0.0);
#else
    UNITY_BRANCH
    if (_BasisGIWorldSpecularParams.x <= 0.0 || _BasisGIWorldParams.w < 0.5 || dot(normalWS, normalWS) < 0.25) { return half4(0.0, 0.0, 0.0, 0.0); }

    int levels = (int)_BasisGIWorldParams.x;
    float voxels = _BasisGIWorldParams.y;
    int probes = (int)_BasisGIWorldParams.z;
    float3 normal = normalize((float3)normalWS);
    float3 origin = positionWS + normal * _BasisGIWorldLevel[0].w;
    int level = -1;
    float edge = 1.0;
    UNITY_LOOP
    for (int candidate = 0; candidate < levels; candidate++)
    {
        float4 bounds = _BasisGIWorldLevel[candidate];
        float size = bounds.w * voxels;
        float margin = bounds.w * 2.0;
        float3 local = origin - bounds.xyz;
        if (all(local >= margin) && all(local <= size - margin))
        {
            level = candidate;
            float3 inner = min(local - margin, size - margin - local) / (bounds.w * 8.0);
            edge = candidate == levels - 1 ? saturate(min(inner.x, min(inner.y, inner.z))) : 1.0;
            break;
        }
    }
    if (level < 0) { return half4(0.0, 0.0, 0.0, 0.0); }

    float4 box = _BasisGIWorldLevel[level];
    origin = positionWS + normal * box.w;
    float spacing = box.w * 2.0;
    float3 grid = origin / spacing - 0.5;
    int3 cell = (int3)floor(grid);
    float3 fraction = grid - (float3)cell;
    int3 first = (int3)round(box.xyz / spacing);
    int slab = (int)(_BasisGIWorldSHParams.x * _BasisGIWorldSHParams.y) + level * probes;

    float3 direction = normalize((float3)reflectVector);
    float bins = _BasisGIWorldSpecularParams.y;
    float2 binTexel = clamp((PackNormalOctQuadEncode(direction) * 0.5 + 0.5) * bins, 0.5, bins - 0.5);
    float atlasScale = 1.0 / (probes * bins);
    float inverseVoxel = 1.0 / box.w;

    float roughness = max(Sq((float)perceptualRoughness), 0.002);
    float exponent = max(2.0 / Sq(roughness) - 2.0, 0.0);
    float lobeBand = (exponent + 1.0) / (exponent + 2.0);
    float roughBlend = saturate(((float)perceptualRoughness - 0.45) * 2.5);

    float3 glossy = float3(0.0, 0.0, 0.0), diffuse = float3(0.0, 0.0, 0.0);
    float weightSum = 0.0;
    UNITY_UNROLL
    for (int index = 0; index < 8; index++)
    {
        int3 offset = int3(index & 1, (index >> 1) & 1, index >> 2);
        int3 probe = cell + offset;
        if (any(probe < first) || any(probe >= first + probes)) { continue; }
        int3 slot = probe & (probes - 1);
        slot.z += slab;
        float4 c0 = LOAD_TEXTURE3D(_BasisGIWorldSH0, slot);
        float3 trilinear = lerp(1.0 - fraction, fraction, (float3)offset);
        float weight = trilinear.x * trilinear.y * trilinear.z * c0.a;
        if (weight <= 1e-5) { continue; }

        float3 probeWS = ((float3)probe + 0.5) * spacing;
        weight *= Sq(dot(normalize(probeWS - origin), normal) * 0.5 + 0.5) + 0.05;
        float3 midpoint = (origin + probeWS) * 0.5;
        float3 voxel = (midpoint - box.xyz) * inverseVoxel;
        if (all(voxel >= 0.0) && all(voxel < voxels))
        {
            float3 uvw = clamp(voxel, 0.5, voxels - 0.5) + float3(0.0, 0.0, level * voxels);
            weight *= saturate((0.6 - _BasisGIVoxAlbedo.SampleLevel(sampler_LinearClamp, uvw / float3(voxels, voxels, voxels * levels), 0).a) * 5.0);
            if (weight <= 1e-5) { continue; }
        }

        UNITY_BRANCH
        if (roughBlend < 1.0)
        {
            float2 atlasUv = ((float2)(slot.xy * bins) + binTexel) * atlasScale;
            glossy += SAMPLE_TEXTURE2D_ARRAY_LOD(_BasisGIWorldSpecular, sampler_LinearClamp, atlasUv, slot.z, 0).rgb * weight;
        }
        UNITY_BRANCH
        if (roughBlend > 0.0)
        {
            float3 band = LOAD_TEXTURE3D(_BasisGIWorldSH1, slot).rgb * direction.x + LOAD_TEXTURE3D(_BasisGIWorldSH2, slot).rgb * direction.y + LOAD_TEXTURE3D(_BasisGIWorldSH3, slot).rgb * direction.z;
            diffuse += (c0.rgb * 0.282095 + band * (0.488603 * lobeBand)) * weight;
        }
        weightSum += weight;
    }
    if (weightSum <= 1e-5) { return half4(0.0, 0.0, 0.0, 0.0); }

    float3 world = max(0.0, lerp(glossy, diffuse, roughBlend) / weightSum) * _BasisGIWorldSpecularParams.z;
    return half4((half3)world, (half)edge);
#endif
}

#endif
