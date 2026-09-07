#ifndef DOWNSAMPLE_DEPTH_FROM_EXTERNAL_INCLUDED
#define DOWNSAMPLE_DEPTH_FROM_EXTERNAL_INCLUDED

// Reduces an already-reduced (closest, furthest) linear eye depth pair - built for some other screen space
// effect earlier in the same camera's frame, bound to the ordinary blit source by whoever records this pass
// - into the same checkerboard-packed raw depth DownsampleDepth.shader's own pass produces from the full
// resolution camera depth. Everything downstream of _DownsampledCameraDepthTexture (the raymarch in
// VolumetricFog.hlsl, the bilateral upsample in DepthAwareUpsample.hlsl) reads the result identically either
// way and needed no changes for this to exist.

// Exact algebraic inverse of Core.hlsl's LinearEyeDepth(depth, zBufferParams): eyeDepth = 1 / (z*depth + w),
// solved for depth. Round-tripping raw -> eye -> raw returns the original bits to float precision, for
// either z convention, since _ZBufferParams already absorbs that difference the same way LinearEyeDepth
// itself does - there is no separate UNITY_REVERSED_Z branch needed here for the same reason there isn't
// one in LinearEyeDepth.
float RawDepthFromLinearEyeDepth(float eyeDepth)
{
    return (rcp(max(eyeDepth, 1e-8)) - _ZBufferParams.w) / _ZBufferParams.z;
}

float Frag(Varyings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    // Sampled by UV, not an integer offset like the full-resolution passes in DownsampleDepth.shader use -
    // the source may be a different resolution than this pass's own output, and UV sampling resolves that
    // for free at the cost of a point tap instead of a proper reduction when the source is coarser.
    float2 closestFurthest = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp, UnityStereoTransformScreenSpaceTex(input.texcoord), 0).rg;

    // The source's own sentinel for "no geometry under this texel" is a huge eye depth in r, exactly zero
    // in g (see _BasisGITracedDepth in BasisGlobalIlluminationCommon.hlsl) - g would blow up the inverse
    // above, so the far raw plane is used directly instead of running it through the formula.
    bool isSky = closestFurthest.g <= 0.0;
    float closestRaw = RawDepthFromLinearEyeDepth(closestFurthest.r);
    float furthestRaw = isSky ? (UNITY_REVERSED_Z ? 0.0 : 1.0) : RawDepthFromLinearEyeDepth(closestFurthest.g);

    // Raw depth inverts the ordering linear eye depth has: under reversed-Z the nearer surface (closest,
    // the smaller eye depth) is the LARGER raw value, so it is what Min3/Max3 over raw samples in the
    // ordinary pass would have called "max" - and the reverse under a standard [0,1] buffer. Matching that
    // parity is what makes this pass's output interchangeable with the ordinary one to every reader.
    float minRaw = UNITY_REVERSED_Z ? furthestRaw : closestRaw;
    float maxRaw = UNITY_REVERSED_Z ? closestRaw : furthestRaw;

    return (uint(input.positionCS.x + input.positionCS.y) & 1) > 0 ? minRaw : maxRaw;
}

#endif
