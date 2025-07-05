// Made with Amplify Shader Editor
// Available at the Unity Asset Store - http://u3d.as/y3X 
Shader "AudioLink/Amplify/AudioLinkAmplify_FilteredVU"
{
	Properties
	{
		[IntRange]_FilterLevel("Filter Level", Range( 0 , 3)) = 0
		_Gain("Gain", Float) = 1
		_IntensityColor("Intensity Color", Color) = (0,1,0.1634262,0)
		_MarkerColor("Marker Color", Color) = (1,1,1,0)

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

			uniform float4 _IntensityColor;
			uniform float _Gain;
			uniform float _FilterLevel;
			uniform float4 _MarkerColor;
			inline float AudioLinkData1_g1( float Filter )
			{
				return AudioLinkLerp(ALPASS_FILTEREDVU_INTENSITY + int2(Filter, 0)).r;
			}
			
			inline float AudioLinkData7_g1( float Filter )
			{
				return AudioLinkLerp(ALPASS_FILTEREDVU_MARKER + int2(Filter, 0)).r;
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
				float2 texCoord44 = i.ase_texcoord1.xy * float2( 1,1 ) + float2( 0,0 );
				float temp_output_8_0_g1 = _FilterLevel;
				float Filter1_g1 = temp_output_8_0_g1;
				float localAudioLinkData1_g1 = AudioLinkData1_g1( Filter1_g1 );
				float Filter7_g1 = temp_output_8_0_g1;
				float localAudioLinkData7_g1 = AudioLinkData7_g1( Filter7_g1 );
				
				
				finalColor = ( ( _IntensityColor * ( texCoord44.y < ( _Gain * localAudioLinkData1_g1 ) ? 1.0 : 0.0 ) ) + ( ( saturate( ( 0.005 - abs( ( ( _Gain * localAudioLinkData7_g1 ) - texCoord44.y ) ) ) ) / 0.001 ) * _MarkerColor ) );
				return finalColor;
			}
			ENDCG
		}
	}
	CustomEditor "ASEMaterialInspector"
	
	
}
/*ASEBEGIN
Version=18935
456;73;1463;926;1025.68;1308.728;2.498556;True;True
Node;AmplifyShaderEditor.RangedFloatNode;62;-627.3104,-466.4664;Inherit;False;Property;_FilterLevel;Filter Level;0;1;[IntRange];Create;True;0;0;0;False;0;False;0;0;0;3;0;1;FLOAT;0
Node;AmplifyShaderEditor.FunctionNode;61;-316.7311,-463.2865;Inherit;False;VUFiltered;-1;;1;9e3a3efd07ae5af42820ceacf2214050;0;1;8;FLOAT;0;False;2;FLOAT;0;FLOAT;2
Node;AmplifyShaderEditor.RangedFloatNode;63;-211.7907,-543.8465;Inherit;False;Property;_Gain;Gain;1;0;Create;True;0;0;0;False;0;False;1;1;0;0;0;1;FLOAT;0
Node;AmplifyShaderEditor.RangedFloatNode;45;-314.8451,-259.9608;Inherit;False;Constant;_Off;Off;0;0;Create;True;0;0;0;False;0;False;0;0;0;0;0;1;FLOAT;0
Node;AmplifyShaderEditor.RangedFloatNode;46;-312.6051,-349.9608;Inherit;False;Constant;_On;On;0;0;Create;True;0;0;0;False;0;False;1;0;0;0;0;1;FLOAT;0
Node;AmplifyShaderEditor.TextureCoordinatesNode;44;-310.2554,-160.4695;Inherit;False;0;-1;2;3;2;SAMPLER2D;;False;0;FLOAT2;1,1;False;1;FLOAT2;0,0;False;5;FLOAT2;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;65;-11.45079,-452.6864;Inherit;False;2;2;0;FLOAT;0;False;1;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;64;-6.150735,-554.4465;Inherit;False;2;2;0;FLOAT;0;False;1;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.FunctionNode;49;233.7349,-235.781;Inherit;False;DrawLine;-1;;8;b931a6c4da53ab6489d06086e5e19048;0;4;5;FLOAT;0;False;1;FLOAT;0;False;2;FLOAT;0.005;False;3;FLOAT;0.001;False;1;FLOAT;0
Node;AmplifyShaderEditor.Compare;48;231.455,-384.3209;Inherit;False;4;4;0;FLOAT;0;False;1;FLOAT;0;False;2;FLOAT;0;False;3;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.ColorNode;57;240.8291,-569.2864;Inherit;False;Property;_IntensityColor;Intensity Color;2;0;Create;True;0;0;0;False;0;False;0,1,0.1634262,0;0,0,0,0;True;0;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.ColorNode;56;227.0493,-76.38652;Inherit;False;Property;_MarkerColor;Marker Color;3;0;Create;True;0;0;0;False;0;False;1,1,1,0;0,0,0,0;True;0;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;58;503.7091,-487.6665;Inherit;False;2;2;0;COLOR;0,0,0,0;False;1;FLOAT;0;False;1;COLOR;0
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;59;530.2094,-145.2865;Inherit;False;2;2;0;FLOAT;0;False;1;COLOR;0,0,0,0;False;1;COLOR;0
Node;AmplifyShaderEditor.SimpleAddOpNode;50;724.3947,-350.9608;Inherit;False;2;2;0;COLOR;0,0,0,0;False;1;COLOR;0,0,0,0;False;1;COLOR;0
Node;AmplifyShaderEditor.TemplateMultiPassMasterNode;66;924.2071,-372.684;Float;False;True;-1;2;ASEMaterialInspector;100;1;AudioLink/Amplify/AudioLinkAmplify_FilteredVU;0770190933193b94aaa3065e307002fa;True;Unlit;0;0;Unlit;2;False;True;0;1;False;-1;0;False;-1;0;1;False;-1;0;False;-1;True;0;False;-1;0;False;-1;False;False;False;False;False;False;False;False;False;True;0;False;-1;False;True;0;False;-1;False;True;True;True;True;True;0;False;-1;False;False;False;False;False;False;False;True;False;255;False;-1;255;False;-1;255;False;-1;7;False;-1;1;False;-1;1;False;-1;1;False;-1;7;False;-1;1;False;-1;1;False;-1;1;False;-1;False;True;1;False;-1;True;3;False;-1;True;True;0;False;-1;0;False;-1;True;1;RenderType=Opaque=RenderType;True;2;False;0;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;True;1;LightMode=ForwardBase;False;False;0;;0;0;Standard;1;Vertex Position,InvertActionOnDeselection;1;0;0;1;True;False;;False;0
WireConnection;61;8;62;0
WireConnection;65;0;63;0
WireConnection;65;1;61;2
WireConnection;64;0;63;0
WireConnection;64;1;61;0
WireConnection;49;5;44;2
WireConnection;49;1;65;0
WireConnection;48;0;44;2
WireConnection;48;1;64;0
WireConnection;48;2;46;0
WireConnection;48;3;45;0
WireConnection;58;0;57;0
WireConnection;58;1;48;0
WireConnection;59;0;49;0
WireConnection;59;1;56;0
WireConnection;50;0;58;0
WireConnection;50;1;59;0
WireConnection;66;0;50;0
ASEEND*/
//CHKSM=F16851DE2C868212D37B6F1B5A42C8E7CD9D15F6