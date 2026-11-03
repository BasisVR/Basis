Shader "Basis/Highlight/Blur"
{
    // Filters for the BasisHighlight mask texture, driven by Blitter.BlitTexture.
    //   Pass 0 = Gaussian Horizontal
    //   Pass 1 = Gaussian Vertical
    //   Pass 2 = Dilate (max) Horizontal
    //   Pass 3 = Dilate (max) Vertical
    //
    // Dilation is used to grow the silhouette by N pixels so a contour-following
    // outline ring can be derived as (dilated - raw). Gaussian is used to
    // soften that dilated field into a glow halo.

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        float _BasisHighlightBlurRadius;
        float _BasisHighlightDilateRadius;

        half4 BlurAxis(Varyings input, float2 dir)
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            float2 uv = input.texcoord;
            float2 texel = _BlitTexture_TexelSize.xy * dir * _BasisHighlightBlurRadius;

            // 9-tap gaussian (sigma ~ 2.0)
            const float w0 = 0.227027;
            const float w1 = 0.1945946;
            const float w2 = 0.1216216;
            const float w3 = 0.054054;
            const float w4 = 0.016216;

            half4 c = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv) * w0;
            c += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + texel * 1) * w1;
            c += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - texel * 1) * w1;
            c += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + texel * 2) * w2;
            c += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - texel * 2) * w2;
            c += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + texel * 3) * w3;
            c += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - texel * 3) * w3;
            c += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + texel * 4) * w4;
            c += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - texel * 4) * w4;
            return c;
        }

        // Separable max filter (dilation). Fully unrolled with a uniform-condition
        // branch per tap pair instead of `[unroll(N)] { ... break; }`, which some
        // compilers (notably Adreno on Quest) implement incorrectly when the bound
        // is a uniform. Taps past the radius are skipped rather than fetched and
        // zero-weighted: the mask is >= 0 so they could never raise the max, and
        // the usual radius is 1-2, so this drops ~14 of 17 fetches per pixel.
        // _LOD sampling keeps gradient ops out of flow control; the mask targets
        // have a single mip, so the result is identical.
        #define MAX_DILATE_TAPS 8

        half4 DilateAxis(Varyings input, float2 dir)
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            float2 uv = input.texcoord;
            float2 texelStep = _BlitTexture_TexelSize.xy * dir;

            float radius = clamp(_BasisHighlightDilateRadius, 0.0, (float)MAX_DILATE_TAPS);

            half m = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0).r;

            UNITY_UNROLL
            for (int i = 1; i <= MAX_DILATE_TAPS; i++)
            {
                UNITY_BRANCH
                if ((float)i <= radius)
                {
                    float2 ofs = texelStep * (float)i;
                    half a = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv + ofs, 0).r;
                    half b = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv - ofs, 0).r;
                    m = max(m, max(a, b));
                }
            }

            return half4(m, m, m, m);
        }
        ENDHLSL

        Pass
        {
            Name "BlurHorizontal"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            half4 Frag(Varyings IN) : SV_Target { return BlurAxis(IN, float2(1, 0)); }
            ENDHLSL
        }

        Pass
        {
            Name "BlurVertical"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            half4 Frag(Varyings IN) : SV_Target { return BlurAxis(IN, float2(0, 1)); }
            ENDHLSL
        }

        Pass
        {
            Name "DilateHorizontal"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            half4 Frag(Varyings IN) : SV_Target { return DilateAxis(IN, float2(1, 0)); }
            ENDHLSL
        }

        Pass
        {
            Name "DilateVertical"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            half4 Frag(Varyings IN) : SV_Target { return DilateAxis(IN, float2(0, 1)); }
            ENDHLSL
        }
    }
}
