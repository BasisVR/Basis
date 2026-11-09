#include "common.hlsli"

#define UPLOAD_WIDTH 2048

uint4 LoadUpload(uint texel)
{
    Texture2D<uint4> upload = ResourceDescriptorHeap[HEAP_UPLOAD + pass.h];
    return Load128(upload, uint2(texel % UPLOAD_WIDTH, texel / UPLOAD_WIDTH));
}

[numthreads(64, 1, 1)]
void CopyVertices(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= pass.b) { return; }
    RWByteAddressBuffer vertices = ResourceDescriptorHeap[HEAP_VERTICES_UAV];
    vertices.Store4(pass.c + id.x * 16, LoadUpload(pass.a + id.x));
}

[numthreads(64, 1, 1)]
void CopyPositions(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= pass.b) { return; }
    RWByteAddressBuffer vertices = ResourceDescriptorHeap[HEAP_VERTICES_UAV];
    vertices.Store4(pass.c + id.x * 32, LoadUpload(pass.a + id.x));
}

[numthreads(64, 1, 1)]
void CopyIndices(uint3 id : SV_DispatchThreadID)
{
    uint first = id.x * 4;
    if (first >= pass.b) { return; }
    RWByteAddressBuffer indices = ResourceDescriptorHeap[HEAP_INDICES_UAV];
    uint4 v = LoadUpload(pass.a + id.x);
    uint remaining = pass.b - first;
    if (remaining >= 4) { indices.Store4(pass.c + first * 4, v); return; }
    indices.Store(pass.c + first * 4, v.x);
    if (remaining > 1) { indices.Store(pass.c + first * 4 + 4, v.y); }
    if (remaining > 2) { indices.Store(pass.c + first * 4 + 8, v.z); }
}

float SrgbToLinear(float c) { return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4); }
float LinearToSrgb(float c) { return c <= 0.0031308 ? c * 12.92 : 1.055 * pow(c, 1.0 / 2.4) - 0.055; }

[numthreads(8, 8, 1)]
void TexturePixels(uint3 id : SV_DispatchThreadID)
{
    uint width = pass.c, rows = pass.e;
    if (id.x >= width || id.y >= rows) { return; }
    uint pixel = id.y * width + id.x;
    uint4 texel = LoadUpload(pass.a + pixel / 4);
    uint value = texel[pixel & 3];
    RWTexture2D<float4> target = ResourceDescriptorHeap[pass.g];
    float4 color = pass.f != 0 ? float4(UnpackRGB9E5(value), 1.0) : UnpackRGBA8(value);
    target[uint2(id.x, pass.d + id.y)] = color;
}

[numthreads(8, 8, 1)]
void Downsample(uint3 id : SV_DispatchThreadID)
{
    uint2 size = uint2(pass.c, pass.d);
    if (id.x >= size.x || id.y >= size.y) { return; }
    RWTexture2D<float4> source = ResourceDescriptorHeap[pass.a];
    RWTexture2D<float4> target = ResourceDescriptorHeap[pass.b];
    uint2 s = id.xy * 2;
    uint2 limit = uint2(pass.e, pass.f) - 1;
    float4 c0 = source[min(s, limit)], c1 = source[min(s + uint2(1, 0), limit)], c2 = source[min(s + uint2(0, 1), limit)], c3 = source[min(s + 1, limit)];
    float4 result;
    if (pass.g != 0) { result = (c0 + c1 + c2 + c3) * 0.25; }
    else
    {
        float4 l = 0.0;
        [unroll] for (uint k = 0; k < 3; k++) { l[k] = (SrgbToLinear(c0[k]) + SrgbToLinear(c1[k]) + SrgbToLinear(c2[k]) + SrgbToLinear(c3[k])) * 0.25; }
        result = float4(LinearToSrgb(l.x), LinearToSrgb(l.y), LinearToSrgb(l.z), max(max(c0.a, c1.a), max(c2.a, c3.a)) * 0.5 + (c0.a + c1.a + c2.a + c3.a) * 0.125);
    }
    target[id.xy] = result;
}
