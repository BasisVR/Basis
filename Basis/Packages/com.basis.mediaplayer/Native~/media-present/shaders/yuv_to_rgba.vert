// Fullscreen triangle for the YCbCr -> RGBA pass: three vertices cover the
// target, with no vertex buffer. The fragment shader works from
// gl_FragCoord, so nothing is passed down.
//
// Regenerate yuv_to_rgba.vert.spv with the NDK's glslc:
//   glslc -fshader-stage=vert -O -o yuv_to_rgba.vert.spv yuv_to_rgba.vert
#version 450

void main() {
    vec2 p = vec2(float((gl_VertexIndex << 1) & 2), float(gl_VertexIndex & 2));
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}
