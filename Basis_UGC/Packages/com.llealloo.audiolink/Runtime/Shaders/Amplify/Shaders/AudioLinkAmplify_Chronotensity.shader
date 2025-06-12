// Made with Amplify Shader Editor
// Available at the Unity Asset Store - http://u3d.as/y3X 
Shader "AudioLink/Amplify/AudioLinkAmplify_Chronotensity"
{
	Properties
	{
		_TextureSample0("Texture Sample 0", 2D) = "white" {}
		[IntRange]_Mode("Mode", Range( 0 , 3)) = 0
		[IntRange]_Band("Band", Range( 0 , 3)) = 0
		[Toggle]_Speed("Speed", Float) = 0

	}
	
	SubShader
	{
		
		
		Tags { "RenderType"="Opaque" }
	LOD 100

		CGINCLUDE
		#pragma target 3.0
		ENDCG
		Blend Off
		AlphaToMask Off
		Cull Back
		ColorMask RGBA
		ZWrite On
		ZTest LEqual
		Offset 0 , 0
		
		
		
		Pass
		{
			Name "Unlit"
			Tags { "LightMode"="ForwardBase" }
			CGPROGRAM

			

			#ifndef UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX
			//only defining to not throw compilation error over Unity 5.5
			#define UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input)
			#endif
			#pragma vertex vert
			#pragma fragment frag
			#pragma multi_compile_instancing
			#include "UnityCG.cginc"
			#include "Packages/com.llealloo.audiolink/Runtime/Shaders/AudioLink.cginc"


			struct appdata
			{
				float4 vertex : POSITION;
				float4 color : COLOR;
				float4 ase_texcoord : TEXCOORD0;
				UNITY_VERTEX_INPUT_INSTANCE_ID
			};
			
			struct v2f
			{
				float4 vertex : SV_POSITION;
				#ifdef ASE_NEEDS_FRAG_WORLD_POSITION
				float3 worldPos : TEXCOORD0;
				#endif
				float4 ase_texcoord1 : TEXCOORD1;
				UNITY_VERTEX_INPUT_INSTANCE_ID
				UNITY_VERTEX_OUTPUT_STEREO
			};

			uniform sampler2D _TextureSample0;
			uniform float _Band;
			uniform float _Mode;
			uniform float _Speed;
			inline int AudioLinkDecodeDataAsUInt6_g2( int Band, int Mode )
			{
				return AudioLinkDecodeDataAsUInt( ALPASS_CHRONOTENSITY + int2(Mode, Band));
			}
			

			
			v2f vert ( appdata v )
			{
				v2f o;
				UNITY_SETUP_INSTANCE_ID(v);
				UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
				UNITY_TRANSFER_INSTANCE_ID(v, o);

				o.ase_texcoord1.xy = v.ase_texcoord.xy;
				
				//setting value to unused interpolator channels and avoid initialization warnings
				o.ase_texcoord1.zw = 0;
				float3 vertexValue = float3(0, 0, 0);
				#if ASE_ABSOLUTE_VERTEX_POS
				vertexValue = v.vertex.xyz;
				#endif
				vertexValue = vertexValue;
				#if ASE_ABSOLUTE_VERTEX_POS
				v.vertex.xyz = vertexValue;
				#else
				v.vertex.xyz += vertexValue;
				#endif
				o.vertex = UnityObjectToClipPos(v.vertex);

				#ifdef ASE_NEEDS_FRAG_WORLD_POSITION
				o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
				#endif
				return o;
			}
			
			fixed4 frag (v2f i ) : SV_Target
			{
				UNITY_SETUP_INSTANCE_ID(i);
				UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
				fixed4 finalColor;
				#ifdef ASE_NEEDS_FRAG_WORLD_POSITION
				float3 WorldPosition = i.worldPos;
				#endif
				float2 texCoord18 = i.ase_texcoord1.xy * float2( 1,1 ) + float2( 0,0 );
				float2 break5_g4 = ( texCoord18 - ( float2( 0,0 ) + float2( 0.5,0.5 ) ) );
				int Band6_g2 = (int)_Band;
				int Mode6_g2 = ( ( (int)_Mode * 2 ) + (int)_Speed );
				int localAudioLinkDecodeDataAsUInt6_g2 = AudioLinkDecodeDataAsUInt6_g2( Band6_g2 , Mode6_g2 );
				float temp_output_4_0_g4 = ( ( localAudioLinkDecodeDataAsUInt6_g2 % 628319 ) / 100000.0 );
				float temp_output_19_0_g4 = cos( temp_output_4_0_g4 );
				float temp_output_11_0_g4 = sin( temp_output_4_0_g4 );
				float2 break26_g4 = float2( 1,1 );
				float2 appendResult21_g4 = (float2(( ( ( ( break5_g4.x * temp_output_19_0_g4 ) + ( break5_g4.y * temp_output_11_0_g4 ) ) / break26_g4.x ) + 0.5 ) , ( ( ( ( break5_g4.y * temp_output_19_0_g4 ) - ( break5_g4.x * temp_output_11_0_g4 ) ) / break26_g4.y ) + 0.5 )));
				
				
				finalColor = tex2D( _TextureSample0, appendResult21_g4 );
				return finalColor;
			}
			ENDCG
		}
	}
	CustomEditor "ASEMaterialInspector"
	
	
}
/*ASEBEGIN
Version=18935
818;73;1101;926;-140.1289;746.8373;1.06;True;True
Node;AmplifyShaderEditor.RangedFloatNode;41;-534.0307,-68.96692;Inherit;False;Property;_Speed;Speed;3;1;[Toggle];Create;True;0;0;0;False;0;False;0;0;0;0;0;1;FLOAT;0
Node;AmplifyShaderEditor.RangedFloatNode;38;-659.1104,-273.5469;Inherit;False;Property;_Mode;Mode;1;1;[IntRange];Create;True;0;0;0;False;0;False;0;0;0;3;0;1;FLOAT;0
Node;AmplifyShaderEditor.RangedFloatNode;39;-659.1104,-173.907;Inherit;False;Property;_Band;Band;2;1;[IntRange];Create;True;0;0;0;False;0;False;0;0;0;3;0;1;FLOAT;0
Node;AmplifyShaderEditor.TextureCoordinatesNode;18;-312.4881,-406.5215;Inherit;False;0;-1;2;3;2;SAMPLER2D;;False;0;FLOAT2;1,1;False;1;FLOAT2;0,0;False;5;FLOAT2;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.FunctionNode;43;-325.2094,-182.3872;Inherit;False;4BandChronotensity;-1;;2;f89bf659661089e4aa165728fa84fd68;0;3;4;INT;0;False;15;INT;0;False;29;INT;0;False;1;FLOAT;13
Node;AmplifyShaderEditor.FunctionNode;36;35.1893,-315.9468;Inherit;False;TranslateUV;-1;;4;7d5888c75b9480c40af61bef980bcf35;0;4;1;FLOAT2;0,0;False;2;FLOAT2;0,0;False;3;FLOAT2;1,1;False;4;FLOAT;0;False;1;FLOAT2;0
Node;AmplifyShaderEditor.SamplerNode;30;290.6495,-349.8675;Inherit;True;Property;_TextureSample0;Texture Sample 0;0;0;Create;True;0;0;0;False;0;False;-1;None;None;True;0;False;white;Auto;False;Object;-1;Auto;Texture2D;8;0;SAMPLER2D;;False;1;FLOAT2;0,0;False;2;FLOAT;0;False;3;FLOAT2;0,0;False;4;FLOAT2;0,0;False;5;FLOAT;1;False;6;FLOAT;0;False;7;SAMPLERSTATE;;False;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.TemplateMultiPassMasterNode;44;714.327,-295.304;Float;False;True;-1;2;ASEMaterialInspector;100;1;AudioLink/Amplify/AudioLinkAmplify_Chronotensity;0770190933193b94aaa3065e307002fa;True;Unlit;0;0;Unlit;2;False;True;0;1;False;-1;0;False;-1;0;1;False;-1;0;False;-1;True;0;False;-1;0;False;-1;False;False;False;False;False;False;False;False;False;True;0;False;-1;False;True;0;False;-1;False;True;True;True;True;True;0;False;-1;False;False;False;False;False;False;False;True;False;255;False;-1;255;False;-1;255;False;-1;7;False;-1;1;False;-1;1;False;-1;1;False;-1;7;False;-1;1;False;-1;1;False;-1;1;False;-1;False;True;1;False;-1;True;3;False;-1;True;True;0;False;-1;0;False;-1;True;1;RenderType=Opaque=RenderType;True;2;False;0;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;True;1;LightMode=ForwardBase;False;False;0;;0;0;Standard;1;Vertex Position,InvertActionOnDeselection;1;0;0;1;True;False;;False;0
WireConnection;43;4;38;0
WireConnection;43;15;39;0
WireConnection;43;29;41;0
WireConnection;36;1;18;0
WireConnection;36;4;43;13
WireConnection;30;1;36;0
WireConnection;44;0;30;0
ASEEND*/
//CHKSM=0161434B4EC1B4972ECE5C02AAE55E9E51601ABB