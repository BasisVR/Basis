Shader "AudioLink/Video/AudioLinkMiniPlayerOut" {
	Properties {
		_MainTex ("MainTex", 2D) = "black" {}
		_MarginTex("Margin (RGB)", 2D) = "black" {}
		_AspectRatio("Aspect Ratio", Float) = 1.777777
	}

	SubShader {

		Pass {
			Lighting Off

			CGPROGRAM
			#pragma vertex CustomRenderTextureVertexShader
			#pragma fragment frag

			#include "UnityCustomRenderTexture.cginc"

			sampler2D _MainTex;
			float4 _MainTex_ST;
			float4 _MainTex_TexelSize;

			sampler2D _MarginTex;
			float4 _MarginTex_ST;

			uniform float _AspectRatio;

			void ComputeScreenFit(float2 uv, float2 res, out float2 uvFit, out float visibility) {
				float curAspectRatio = res.x / res.y;

				visibility = 1;

				if (abs(curAspectRatio - _AspectRatio) > .001) {
					float2 normRes = float2(res.x / _AspectRatio, res.y);
					float2 correction;

					if (normRes.x > normRes.y)
						correction = float2(1, normRes.y / normRes.x);
					else if (normRes.x < normRes.y)
						correction = float2(normRes.x / normRes.y, 1);

					uv = ((uv - 0.5) / correction) + 0.5;

					float2 uvPadding = (1 / res) * 0.1;
					float2 uvFwidth = fwidth(uv.xy);
					float2 maxf = smoothstep(uvFwidth + uvPadding + 1, uvPadding + 1, uv.xy);
					float2 minf = smoothstep(-uvFwidth - uvPadding, -uvPadding, uv.xy);

					visibility = maxf.x * maxf.y * minf.x * minf.y;
				}

				uvFit = uv;
			}

			float4 frag(v2f_customrendertexture i) : SV_Target {
				float2 uv = float2(0, 0);
				float visibility = 0;
				ComputeScreenFit(i.globalTexcoord.xy, _MainTex_TexelSize.zw, uv, visibility);

				float2 muv = TRANSFORM_TEX(i.globalTexcoord.xy, _MarginTex);
				float4 margin = tex2D(_MarginTex, muv) * (1 - visibility);

				float4 color = tex2D(_MainTex, uv * _MainTex_ST.xy + _MainTex_ST.zw);

				color.rgb = GammaToLinearSpace(color.rgb);

				color = color * visibility + margin;

				return color;
			}
			ENDCG
		}
	}
}
