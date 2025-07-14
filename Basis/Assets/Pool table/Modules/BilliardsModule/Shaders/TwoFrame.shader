Shader "harry_t/twoframe"
{
	Properties
	{
		_MainTex ("Texture", 2D) = "white" {}
  _Speed("Speed", Float) = 20.0
	}
	SubShader
	{
		Tags { "Queue" = "Transparent" }
		
		ZWrite Off
		Cull Off

		Pass
		{
			Blend One One

			CGPROGRAM

			#pragma vertex vert
			#pragma fragment frag
			// make fog work
			#pragma multi_compile_fog

			#include "UnityCG.cginc"

			struct appdata
			{
				float4 vertex : POSITION;
				float2 uv : TEXCOORD0;
			};

			struct v2f
			{
				float2 uv : TEXCOORD0;
				UNITY_FOG_COORDS(1)
				float4 vertex : SV_POSITION;
			};

			sampler2D _MainTex;
   float _Speed;

			v2f vert (appdata v)
			{
				v2f o;
				o.vertex = UnityObjectToClipPos(v.vertex);
				o.uv = v.uv * float2( 0.5, 1.0 ) + float2( 0.5, 0.0 ) * fmod(floor(_Time.x * _Speed), 2);
				UNITY_TRANSFER_FOG(o,o.vertex);
				return o;
			}

			fixed4 frag (v2f i) : SV_Target
			{
				fixed4 col = tex2D(_MainTex, i.uv);

				// apply fog
				UNITY_APPLY_FOG(i.fogCoord, col);
				return col;
			}

			ENDCG
		}
	}
}
