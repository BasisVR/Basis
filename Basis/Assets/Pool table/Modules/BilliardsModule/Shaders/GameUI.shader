Shader "metaphira/GameUI"
{
	Properties
	{
		_MainTex ("Texture", 2D) = "white" {}
		_Trf ("Transform", Vector) = (0,0,0,0)
	}
	SubShader
	{
		Tags { "RenderQueue"="Transparent" }
		Cull Off
		ZWrite Off
		ZTest Always

		Blend SrcAlpha OneMinusSrcAlpha

		Pass
		{
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag

			#include "UnityCG.cginc"

			struct appdata
			{
				float4 vertex : POSITION;
				float2 uv : TEXCOORD0;
			};

			struct v2f
			{
				float2 uv : TEXCOORD0;
				float4 vertex : SV_POSITION;
			};

			sampler2D _MainTex;
			float4 _Trf;

			v2f vert (appdata v)
			{
				float ratio = (_ScreenParams.x / _ScreenParams.y);
				float2 anchor = float2( 1.0, 1.0 );

				float2 scalepos = v.vertex.xz*_Trf.xy;
				scalepos.y *= ratio;
				float2 location = anchor + scalepos + _Trf.zw;

				if (_ProjectionParams.x > 0.0)
					location.y = -location.y;					 

				v2f o;
				o.vertex = float4( -location.x, location.y, UNITY_NEAR_CLIP_VALUE, 1.0 );
				o.uv = v.uv;
				return o;
			}

			fixed4 frag (v2f i) : SV_Target
			{
				fixed4 col = tex2D(_MainTex, i.uv);
				return col;
			}
			ENDCG
		}
	}
}
