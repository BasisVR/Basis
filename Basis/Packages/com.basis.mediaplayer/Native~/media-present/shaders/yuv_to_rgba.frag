// One-pass YCbCr -> RGBA convert (§6.8): the sampler carries the driver's
// per-buffer suggested VkSamplerYcbcrConversion, so the matrix/range work
// happens in the sampler and this shader only maps pixels. Target pixels
// map 1:1 onto the picture's region of the buffer (`src`): the decoder pads
// the buffer to an aligned size and the picture may start away from its
// top-left corner. A target larger than the picture repeats its last row and
// column rather than reading the padding. `flip` mirrors the sample position
// per axis: Unity samples an externally written RenderTexture vertically
// flipped on Vulkan (row 0 is the on-screen bottom; pinned empirically on
// the Quest pass), so the render side sets flip.y.
//
// The conversion yields gamma-encoded R'G'B'. An sRGB target encodes what is
// written to it, so `linearise` decodes first and the stored bytes are the
// picture's own; Unity then samples them back to linear light, as it does
// the Direct3D BGRA texture. A UNORM target (a Gamma colour-space project)
// stores R'G'B' as it comes.
//
// Regenerate yuv_to_rgba.frag.spv with the NDK's glslc:
//   glslc -fshader-stage=frag -O -o yuv_to_rgba.frag.spv yuv_to_rgba.frag
#version 450

layout(binding = 0) uniform sampler2D src_yuv;

layout(push_constant) uniform Push {
    ivec2 dst_size;
    vec2 inv_coded_size;
    ivec2 flip;
    ivec2 src_origin;
    ivec2 src_size;
    int linearise;
} pc;

layout(location = 0) out vec4 out_rgba;

vec3 srgb_to_linear(vec3 c) {
    return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), greaterThan(c, vec3(0.04045)));
}

void main() {
    ivec2 sp = ivec2(gl_FragCoord.xy);
    if (pc.flip.x != 0) {
        sp.x = pc.dst_size.x - 1 - sp.x;
    }
    if (pc.flip.y != 0) {
        sp.y = pc.dst_size.y - 1 - sp.y;
    }
    sp = min(sp, pc.src_size - 1) + pc.src_origin;
    vec2 uv = (vec2(sp) + 0.5) * pc.inv_coded_size;
    vec3 c = clamp(texture(src_yuv, uv).rgb, 0.0, 1.0);
    if (pc.linearise != 0) {
        c = srgb_to_linear(c);
    }
    out_rgba = vec4(c, 1.0);
}
