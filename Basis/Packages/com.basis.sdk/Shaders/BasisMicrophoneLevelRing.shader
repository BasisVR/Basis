// Procedural SDF replacement for the microphone HUD sprite: a circle drawn as an outline only,
// whose radius rides the local voice level, so a glance at it reads loudness without the glyph
// covering anything behind it. There is no fill at any level — only the stroke and, while muted,
// a slash through it — so whatever is behind the icon stays visible.
//
// Drawn on the same SpriteRenderer the microphone sprite used, over a full-rect quad (the sprite's
// own mesh is tight-fitted to the glyph and cannot carry a circle). Radii and the stroke are in
// quad half-extents, so _RadiusLoud + _Thickness <= 1 keeps the loudest ring inside the quad.
Shader "Basis/UI/MicrophoneLevelRing"
{
    Properties
    {
        // Never sampled — the ring is procedural — but the sprite pipeline binds it per renderer.
        [PerRendererData] _MainTex ("Sprite Texture", 2D)         = "white" {}
        _Color      ("Tint",                  Color)              = (1, 1, 1, 1)
        [HideInInspector] _RendererColor ("Renderer Color", Color) = (1, 1, 1, 1)
        _Level      ("Voice Level",           Range(0, 1))        = 0
        _RadiusQuiet("Radius At Silence",     Range(0, 1))        = 0.05
        _RadiusLoud ("Radius At Full Scale",  Range(0, 1))        = 0.88
        _RadiusMuted("Radius When Muted",     Range(0, 1))        = 0.45
        _Thickness  ("Stroke Half Width",     Range(0.002, 0.3))  = 0.06
        _Muted      ("Muted",                 Range(0, 1))        = 0
        _SlashSpan  ("Muted Slash Overhang",  Range(0, 2))        = 1.15
    }

    SubShader
    {
        // Matches the microphone sprite's own material (Basis/UI/Main): overlay queue, depth test
        // off, straight alpha — so switching styles does not change how the icon composites.
        Tags
        {
            "Queue"="Overlay"
            "RenderType"="Transparent"
            "RenderPipeline"="UniversalPipeline"
            "IgnoreProjector"="True"
            "PreviewType"="Plane"
        }

        Cull Off
        ZWrite Off
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "Ring"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_instancing

            // Core2D supplies unity_SpriteColor for both the regular and instanced SpriteRenderer
            // paths. SpriteRenderer.color is no longer guaranteed to be baked into COLOR0.
            #include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/Core2D.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float4 color       : COLOR;
                float2 uv          : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // The stroke is a couple of percent of the quad wide, and its coverage comes off a
            // derivative — half precision quantises fwidth badly enough to dash the ring on some
            // mobile GPUs, so the distance-field math stays float. Only the colour is half.
            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float _Level;
                float _RadiusQuiet;
                float _RadiusLoud;
                float _RadiusMuted;
                float _Thickness;
                float _Muted;
                float _SlashSpan;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                SetUpSpriteInstanceProperties();
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                // Unity supplies the renderer tint separately from the mesh vertex colour. Include
                // both so mute red and the other talk-mode colours survive dynamic batching and
                // GPU instancing as well as the ordinary SpriteRenderer path.
                OUT.color = IN.color * _Color * unity_SpriteColor;
                OUT.uv = IN.uv;
                return OUT;
            }

            // Coverage of a stroke centred on `distance == 0`, softened by one pixel footprint so a
            // thin ring stays smooth at every size instead of aliasing into a dashed circle. A
            // radius under _Thickness leaves no hole, which is what closes the ring into a dot.
            float StrokeCoverage(float distance, float footprint)
            {
                return 1.0f - smoothstep(-footprint, footprint, abs(distance) - _Thickness);
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 p = (IN.uv - 0.5f) * 2.0f;
                float d = length(p);
                float footprint = max(fwidth(d), 1e-5f);

                // At silence _RadiusQuiet sits inside the stroke, so the ring closes up into a dot
                // and stays out of the way until there is something to show. Muted is not a level,
                // so it parks at its own radius instead of reading as "quiet".
                bool muted = _Muted > 0.5;
                float radius = muted ? _RadiusMuted : lerp(_RadiusQuiet, _RadiusLoud, saturate(_Level));
                float coverage = StrokeCoverage(d - radius, footprint);

                // Colour alone is a weak signal for anyone who cannot separate the red, so a slash
                // through the ring carries mute at the same visual weight the crossed-out sprite
                // did. Its span follows the radius, overhanging the stroke by a fixed margin.
                float2 direction = float2(0.70710678f, 0.70710678f);
                float span = (radius + _Thickness) * _SlashSpan;
                float along = clamp(dot(p, direction), -span, span);
                float slash = StrokeCoverage(length(p - direction * along), footprint);
                coverage = max(coverage, muted ? slash : 0.0f);

                half4 col = IN.color;
                col.a *= saturate(coverage);
                return col;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
