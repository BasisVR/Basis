// Made with Amplify Shader Editor
// Available at the Unity Asset Store - http://u3d.as/y3X 
Shader "AudioLink/Amplify/AudioLinkAmplify_Waveform"
{
	Properties
	{
		_WaveformHeight("Waveform Height", Range( 0 , 1)) = 0.5
		_WaveformThickness("Waveform Thickness", Range( 0 , 1)) = 0.05
		_WaveformLineSmoothing("Waveform Line Smoothing", Range( 0.0001 , 1)) = 0.01

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

			uniform float _WaveformThickness;
			uniform float _WaveformHeight;
			uniform float _WaveformLineSmoothing;
			inline float AudioLinkLerpMultiline1_g4( float Sample )
			{
				return AudioLinkLerpMultiline( ALPASS_WAVEFORM + uint2( Sample * 1024, 0 ) ).rrrr;;
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
				float Sample1_g4 = texCoord18.x;
				float localAudioLinkLerpMultiline1_g4 = AudioLinkLerpMultiline1_g4( Sample1_g4 );
				float2 texCoord22 = i.ase_texcoord1.xy * float2( 1,1 ) + float2( 0,0 );
				float4 temp_cast_0 = (( saturate( ( _WaveformThickness - abs( ( ( localAudioLinkLerpMultiline1_g4 + _WaveformHeight ) - texCoord22.y ) ) ) ) / _WaveformLineSmoothing )).xxxx;
				
				
				finalColor = temp_cast_0;
				return finalColor;
			}
			ENDCG
		}
	}
	CustomEditor "ASEMaterialInspector"
	
	
}
/*ASEBEGIN
Version=18935
456;73;1463;926;573.9481;847.002;1.17;True;True
Node;AmplifyShaderEditor.TextureCoordinatesNode;18;-500.2381,-271.9461;Inherit;False;0;-1;2;3;2;SAMPLER2D;;False;0;FLOAT2;1,1;False;1;FLOAT2;0,0;False;5;FLOAT2;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.RangedFloatNode;20;-156.2578,-153.7764;Inherit;False;Property;_WaveformHeight;Waveform Height;0;0;Create;True;0;0;0;False;0;False;0.5;0;0;1;0;1;FLOAT;0
Node;AmplifyShaderEditor.FunctionNode;35;-127.008,-282.4769;Inherit;False;Waveform;-1;;4;86000a57e77967c4ea51f70716038ec2;0;1;2;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.SimpleAddOpNode;31;195.912,-255.5666;Inherit;False;2;2;0;FLOAT;0;False;1;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.TextureCoordinatesNode;22;198.2521,-401.8164;Inherit;False;0;-1;2;3;2;SAMPLER2D;;False;0;FLOAT2;1,1;False;1;FLOAT2;0,0;False;5;FLOAT2;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.RangedFloatNode;25;195.9112,-139.7364;Inherit;False;Property;_WaveformThickness;Waveform Thickness;1;0;Create;True;0;0;0;False;0;False;0.05;0;0;1;0;1;FLOAT;0
Node;AmplifyShaderEditor.RangedFloatNode;32;200.5919,-41.45654;Inherit;False;Property;_WaveformLineSmoothing;Waveform Line Smoothing;2;0;Create;True;0;0;0;False;0;False;0.01;0;0.0001;1;0;1;FLOAT;0
Node;AmplifyShaderEditor.FunctionNode;30;558.6118,-287.1565;Inherit;False;DrawLine;-1;;5;b931a6c4da53ab6489d06086e5e19048;0;4;5;FLOAT;0;False;1;FLOAT;0;False;2;FLOAT;0;False;3;FLOAT;0.5;False;1;FLOAT;0
Node;AmplifyShaderEditor.TemplateMultiPassMasterNode;36;899.912,-249.0287;Float;False;True;-1;2;ASEMaterialInspector;100;1;AudioLink/Amplify/AudioLinkAmplify_Waveform;0770190933193b94aaa3065e307002fa;True;Unlit;0;0;Unlit;2;False;True;0;1;False;-1;0;False;-1;0;1;False;-1;0;False;-1;True;0;False;-1;0;False;-1;False;False;False;False;False;False;False;False;False;True;0;False;-1;False;True;0;False;-1;False;True;True;True;True;True;0;False;-1;False;False;False;False;False;False;False;True;False;255;False;-1;255;False;-1;255;False;-1;7;False;-1;1;False;-1;1;False;-1;1;False;-1;7;False;-1;1;False;-1;1;False;-1;1;False;-1;False;True;1;False;-1;True;3;False;-1;True;True;0;False;-1;0;False;-1;True;1;RenderType=Opaque=RenderType;True;2;False;0;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;True;1;LightMode=ForwardBase;False;False;0;;0;0;Standard;1;Vertex Position,InvertActionOnDeselection;1;0;0;1;True;False;;False;0
WireConnection;35;2;18;1
WireConnection;31;0;35;0
WireConnection;31;1;20;0
WireConnection;30;5;22;2
WireConnection;30;1;31;0
WireConnection;30;2;25;0
WireConnection;30;3;32;0
WireConnection;36;0;30;0
ASEEND*/
//CHKSM=078E4ACBE80641E8A960DD64EEFF0DB3DA94CC4F