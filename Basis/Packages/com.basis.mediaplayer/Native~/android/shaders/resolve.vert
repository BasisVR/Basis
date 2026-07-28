#version 450
/* Fullscreen triangle: 3 verts cover the screen, no vertex buffer.
 * vUV spans the source's display region: uvXform.xy scales and uvXform.zw
 * offsets the 0..1 quad so only the codec's crop rectangle is sampled (the
 * coded buffer pads the height up to a macroblock multiple; sampling the whole
 * buffer would draw the pad rows as an edge strip). Identity (1,1,0,0) samples
 * the full buffer. */
layout(location = 0) out vec2 vUV;
layout(push_constant) uniform Crop { vec4 uvXform; };
void main() {
    vec2 p = vec2(float((gl_VertexIndex << 1) & 2), float(gl_VertexIndex & 2));
    vUV = p * uvXform.xy + uvXform.zw;
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}
