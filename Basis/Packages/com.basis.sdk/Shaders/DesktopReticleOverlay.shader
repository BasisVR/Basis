// URP Procedural SDF reticle for desktop aim. Three concentric radial bands:
//   1. Center dot                 (d <= _DotRadius)              — light fill
//   2. Gap between dot and ring   (_DotRadius..._RingInnerRadius) — dark contrast layer
//   3. Outer ring                 (_RingInnerRadius..._RingOuterRadius) — light fill
// The dark gap gives the reticle a high-contrast outline so it stays readable
// against any background.
Shader "Custom/DesktopReticleOverlay"
{
    Properties
    {
        _DotRadius      ("Dot Radius (UV)",                Range(0, 0.5)) = 0.07
        _RingInnerRadius("Ring Inner Radius (UV)",         Range(0, 0.5)) = 0.125
        _RingOuterRadius("Ring Outer Radius (UV)",         Range(0, 0.5)) = 0.20
        _DotColor       ("Dot Color",                      Color)         = (1, 1, 1, 0.85)
        _GapColor       ("Gap Color (between dot & ring)", Color)         = (0, 0, 0, 0.55)
        _RingColor      ("Ring Color",                     Color)         = (1, 1, 1, 0.20)
    }
    SubShader
    {
        // Queue 5000 sits at the top of everything
        Tags { "Queue"="Overlay+1000" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" "IgnoreProjector"="True" }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend One OneMinusSrcAlpha

        Pass
        {
            Name "Overlay"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                half  _DotRadius;
                half  _RingInnerRadius;
                half  _RingOuterRadius;
                half4 _DotColor;
                half4 _GapColor;
                half4 _RingColor;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half2 c = IN.uv - 0.5h;
                half d = length(c);
                half edge = fwidth(d);

                // Anti-aliased radial transitions. `pastX` ramps 0->1 as `d` crosses radius X.
                half pastDot   = smoothstep(_DotRadius      - edge, _DotRadius      + edge, d);
                half pastInner = smoothstep(_RingInnerRadius - edge, _RingInnerRadius + edge, d);
                half pastOuter = smoothstep(_RingOuterRadius - edge, _RingOuterRadius + edge, d);

                // Mutually-exclusive band masks (sum ~= 1 inside the reticle, 0 outside).
                half dotMask  = 1.0h - pastDot;
                half gapMask  = pastDot   * (1.0h - pastInner);
                half ringMask = pastInner * (1.0h - pastOuter);

                // Premultiplied-alpha composite — paired with Blend One OneMinusSrcAlpha.
                half4 col;
                col.a = _DotColor.a * dotMask + _GapColor.a * gapMask + _RingColor.a * ringMask;
                col.rgb = _DotColor.rgb  * (_DotColor.a  * dotMask)
                        + _GapColor.rgb  * (_GapColor.a  * gapMask)
                        + _RingColor.rgb * (_RingColor.a * ringMask);
                return col;
            }
            ENDHLSL
        }
    }
}
