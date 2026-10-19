Shader "Basis/NamePlate/Panel"
{
    // Unlit, vertex-color, depth-tested transparent panel for the global single-draw nameplate
    // system. The panel color is the per-plate talk-mode color, carried in the vertex color
    // (set from CurrentColor each frame); the shader just renders it with a subtle top sheen.
    // Procedural (no texture fetches), one material across every plate, VR single-pass safe.
    Properties
    {
        _Sheen ("Top Sheen", Range(0,0.4)) = 0.08
        _BaseColor ("Color", Color) = (1,1,1,1)
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 10
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlendAlpha ("Src Blend Alpha", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlendAlpha ("Dst Blend Alpha", Float) = 10
        [ToggleUI] _ZWrite ("ZWrite", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "RenderType"="Transparent"
            "IgnoreProjector"="True"
            "PreviewType"="Plane"
        }

        Cull Off
        Lighting Off
        Fog { Mode Off }
        ZWrite [_ZWrite]
        ZTest LEqual
        Blend [_SrcBlend] [_DstBlend], [_SrcBlendAlpha] [_DstBlendAlpha]

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_instancing
            #pragma multi_compile _ BASIS_NAMEPLATE_GPU BASIS_NAMEPLATE_OBJECT

            #include "UnityCG.cginc"

            struct appdata
            {
                float3 vertex : POSITION;
                float4 color  : COLOR;
                float2 uv     : TEXCOORD0;
                float2 plate  : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                fixed4 color : COLOR;
                float2 uv    : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float _Sheen;
            float4 _BaseColor;
            float _BasisNamePlateDepthPull;

            float4 PulledClipPos(float3 objectPos)
            {
                float3 worldPos = mul(unity_ObjectToWorld, float4(objectPos, 1.0)).xyz;
                if (unity_OrthoParams.w < 0.5)
                {
                    float3 toPoint = worldPos - _WorldSpaceCameraPos;
                    float dist = max(length(toPoint), 1e-5);
                    float pulled = max(dist - _BasisNamePlateDepthPull, min(dist, max(dist * 0.5, _ProjectionParams.y * 2.0)));
                    worldPos = _WorldSpaceCameraPos + toPoint * (pulled / dist);
                }
                return UnityWorldToClipPos(worldPos);
            }

            #if defined(BASIS_NAMEPLATE_GPU)
                // Per-plate billboard data indexed by the plate id in TEXCOORD1.x. Positions stay
                // plate-local in the mesh; the GPU transforms them so the CPU only pushes these
                // (tiny) buffers each frame instead of the whole vertex buffer. Matrices are stored
                // as 4 columns (float4) per plate, matching Unity's column-major Matrix4x4 memory.
                StructuredBuffer<float4> _PlateMatrices;
                StructuredBuffer<float4> _PlateColors;
            #endif

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

            #if defined(BASIS_NAMEPLATE_GPU)
                int id = (int)(v.plate.x + 0.5);
                int b = id * 4;
                float3 p = (_PlateMatrices[b]     * v.vertex.x
                          + _PlateMatrices[b + 1] * v.vertex.y
                          + _PlateMatrices[b + 2] * v.vertex.z
                          + _PlateMatrices[b + 3]).xyz;
                o.pos = PulledClipPos(p);
                float4 col = _PlateColors[id];
            #elif defined(BASIS_NAMEPLATE_OBJECT)
                o.pos = PulledClipPos(v.vertex);
                float4 col = _BaseColor;
            #else
                o.pos = PulledClipPos(v.vertex);
                float4 col = v.color;
            #endif

                // CurrentColor (talk-mode color) arrives as an sRGB color. In a linear
                // project Unity doesn't auto-convert vertex colors the way it does a material
                // _BaseColor, so convert here or the grey (and every mode color) renders ~2x too
                // bright. Guarded so it's a no-op in gamma projects.
                float3 c = col.rgb;
                #if !defined(UNITY_COLORSPACE_GAMMA) && !defined(BASIS_NAMEPLATE_OBJECT)
                    c = GammaToLinearSpace(c);
                #endif
                o.color = fixed4(c, col.a);

                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // Per-plate talk-mode color with a soft top-down sheen.
                float3 body = i.color.rgb * lerp(1.0 - _Sheen, 1.0 + _Sheen, i.uv.y);
                return fixed4(saturate(body), i.color.a);
            }

            ENDHLSL
        }
    }
}
