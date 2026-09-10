#ifndef VOLUMETRIC_FOG_FROXEL_INCLUDED
#define VOLUMETRIC_FOG_FROXEL_INCLUDED

#define VF_FROXEL_TEMPORAL_FEEDBACK 0.9
#define VF_FROXEL_CULL_SENTINEL -1.0

// Camera-aligned froxel grid: xy = uv in the volume view (views packed side by side along x), z = view depth
// distributed quadratically from the near plane to the fog distance so near slices are thin and far
// slices coarse.
float4 _VFFroxelParams;       // x = near plane, y = depth range, z = 1 / depth range, w = volume view count
float4 _VFFroxelGridSize;     // x = width per view, y = height, z = slices, w = 1 / slices
float4 _VFFroxelApplyParams;  // x = 1 / volume view count, y = half texel in x, z = 1 - half texel in x, w = 0.5 / slices

float FroxelSliceToViewDepth(float t)
{
    return _VFFroxelParams.x + _VFFroxelParams.y * t * t;
}

float FroxelViewDepthToSlice(float viewDepth)
{
    return sqrt(saturate((viewDepth - _VFFroxelParams.x) * _VFFroxelParams.z));
}

float FroxelViewX(float uvX, uint viewIndex)
{
    uint viewCount = max(1u, (uint)_VFFroxelParams.w);
    viewIndex = min(viewIndex, viewCount - 1u);
    return (clamp(uvX, _VFFroxelApplyParams.y, _VFFroxelApplyParams.z) + (float)viewIndex) * _VFFroxelApplyParams.x;
}

// Coordinates into the lighting volume, whose values sit at froxel centres.
float3 FroxelUVW(float2 uv, float viewDepth, uint viewIndex)
{
    return float3(FroxelViewX(uv.x, viewIndex), uv.y, FroxelViewDepthToSlice(viewDepth));
}

// Coordinates into the integrated volume, whose slice i holds the accumulation at the end of slice i.
float3 FroxelIntegratedUVW(float2 uv, float viewDepth, uint viewIndex)
{
    return float3(FroxelViewX(uv.x, viewIndex), uv.y, FroxelViewDepthToSlice(viewDepth) - _VFFroxelApplyParams.w);
}

// Last slice worth lighting in a column whose furthest opaque surface sits at maxViewDepth, with one slice
// of margin for the trilinear taps the apply pass takes around that depth.
float FroxelCullSlice(float maxViewDepth)
{
    return ceil(FroxelViewDepthToSlice(maxViewDepth) * _VFFroxelGridSize.z) + 1.0;
}

#endif
