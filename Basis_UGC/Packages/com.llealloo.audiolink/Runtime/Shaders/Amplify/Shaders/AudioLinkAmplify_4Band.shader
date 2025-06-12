// Made with Amplify Shader Editor
// Available at the Unity Asset Store - http://u3d.as/y3X 
Shader "AudioLink/Amplify/AudioLinkAmplify_4Band"
{
	Properties
	{
		_Band0Color("Band 0 Color", Color) = (0,0,0,0)
		_Band1Color("Band 1 Color", Color) = (0,0,0,0)
		_Band2Color("Band 2 Color", Color) = (0,0,0,0)
		_Band3Color("Band 3 Color", Color) = (0,0,0,0)
		[ToggleUI]_SmoothHistory("Smooth History", Float) = 0
		_History("History", Range( 0 , 128)) = 32

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

			uniform float _SmoothHistory;
			uniform float _History;
			uniform float4 _Band0Color;
			uniform float4 _Band1Color;
			uniform float4 _Band2Color;
			uniform float4 _Band3Color;
			inline float AudioLinkLerp3_g5( int Band, float Delay )
			{
				return AudioLinkLerp( ALPASS_AUDIOLINK + float2( Delay, Band ) ).r;
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
				float2 texCoord9 = i.ase_texcoord1.xy * float2( 1,1 ) + float2( 0,0 );
				float temp_output_38_0 = ( texCoord9.y * 4.0 );
				int Band3_g5 = (int)temp_output_38_0;
				float temp_output_39_0 = ( _History * texCoord9.x );
				float Delay3_g5 = (( _SmoothHistory )?( temp_output_39_0 ):( floor( temp_output_39_0 ) ));
				float localAudioLinkLerp3_g5 = AudioLinkLerp3_g5( Band3_g5 , Delay3_g5 );
				float4 temp_cast_1 = (localAudioLinkLerp3_g5).xxxx;
				float4 temp_output_1_0_g6 = temp_cast_1;
				float4 break5_g6 = temp_output_1_0_g6;
				float band23 = floor( temp_output_38_0 );
				float4 bandColor47 = ( ( band23 == 0.0 ? _Band0Color : float4( 0,0,0,0 ) ) + ( band23 == 1.0 ? _Band1Color : float4( 0,0,0,0 ) ) + ( band23 == 2.0 ? _Band2Color : float4( 0,0,0,0 ) ) + ( band23 == 3.0 ? _Band3Color : float4( 0,0,0,0 ) ) );
				float4 temp_output_2_0_g6 = bandColor47;
				
				
				finalColor = ( ( ( break5_g6.r * 0.2 ) + ( break5_g6.g * 0.7 ) + ( break5_g6.b * 0.1 ) ) < 0.5 ? ( 2.0 * temp_output_1_0_g6 * temp_output_2_0_g6 ) : ( 1.0 - ( 2.0 * ( 1.0 - temp_output_1_0_g6 ) * ( 1.0 - temp_output_2_0_g6 ) ) ) );
				return finalColor;
			}
			ENDCG
		}
	}
	CustomEditor "ASEMaterialInspector"
	
	
}
/*ASEBEGIN
Version=18935
845;73;1074;926;284.3011;53.74744;1.04106;True;True
Node;AmplifyShaderEditor.TextureCoordinatesNode;9;-371.5333,196.0154;Inherit;False;0;-1;2;3;2;SAMPLER2D;;False;0;FLOAT2;1,1;False;1;FLOAT2;0,0;False;5;FLOAT2;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;38;-17.21937,245.5924;Inherit;False;2;2;0;FLOAT;0;False;1;FLOAT;4;False;1;FLOAT;0
Node;AmplifyShaderEditor.FloorOpNode;43;189.3487,324.8571;Inherit;False;1;0;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.RegisterLocalVarNode;23;366.9301,284.259;Inherit;False;band;-1;True;1;0;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.CommentaryNode;35;-401.281,441.9745;Inherit;False;1096.091;812.7791;Color per band;14;47;18;30;26;28;22;24;6;25;7;29;5;27;4;;1,0.7122642,0.9412366,1;0;0
Node;AmplifyShaderEditor.GetLocalVarNode;24;-143.2811,499.9748;Inherit;False;23;band;1;0;OBJECT;;False;1;FLOAT;0
Node;AmplifyShaderEditor.ColorNode;6;-347.281,898.9748;Inherit;False;Property;_Band2Color;Band 2 Color;2;0;Create;True;0;0;0;False;0;False;0,0,0,0;0,0,0,0;True;0;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.RangedFloatNode;46;-368.5045,99.07338;Inherit;False;Property;_History;History;5;0;Create;True;0;0;0;False;0;False;32;0;0;128;0;1;FLOAT;0
Node;AmplifyShaderEditor.GetLocalVarNode;29;-141.7811,1035.973;Inherit;False;23;band;1;0;OBJECT;;False;1;FLOAT;0
Node;AmplifyShaderEditor.ColorNode;5;-349.281,715.9749;Inherit;False;Property;_Band1Color;Band 1 Color;1;0;Create;True;0;0;0;False;0;False;0,0,0,0;0,0,0,0;True;0;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.ColorNode;4;-345.281,538.975;Inherit;False;Property;_Band0Color;Band 0 Color;0;0;Create;True;0;0;0;False;0;False;0,0,0,0;0,0,0,0;True;0;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.GetLocalVarNode;27;-143.7811,861.9749;Inherit;False;23;band;1;0;OBJECT;;False;1;FLOAT;0
Node;AmplifyShaderEditor.GetLocalVarNode;25;-141.7811,676.9749;Inherit;False;23;band;1;0;OBJECT;;False;1;FLOAT;0
Node;AmplifyShaderEditor.ColorNode;7;-351.281,1074.973;Inherit;False;Property;_Band3Color;Band 3 Color;3;0;Create;True;0;0;0;False;0;False;0,0,0,0;0,0,0,0;True;0;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.Compare;26;39.21894,667.9648;Inherit;False;0;4;0;FLOAT;0;False;1;FLOAT;1;False;2;COLOR;0,0,0,0;False;3;COLOR;0,0,0,0;False;1;COLOR;0
Node;AmplifyShaderEditor.Compare;22;37.71891,491.9748;Inherit;False;0;4;0;FLOAT;0;False;1;FLOAT;0;False;2;COLOR;0,0,0,0;False;3;COLOR;0,0,0,0;False;1;COLOR;0
Node;AmplifyShaderEditor.Compare;30;39.21894,1027.973;Inherit;False;0;4;0;FLOAT;0;False;1;FLOAT;3;False;2;COLOR;0,0,0,0;False;3;COLOR;0,0,0,0;False;1;COLOR;0
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;39;-20.82236,131.4997;Inherit;False;2;2;0;FLOAT;0;False;1;FLOAT;0.25;False;1;FLOAT;0
Node;AmplifyShaderEditor.Compare;28;37.21891,853.9749;Inherit;False;0;4;0;FLOAT;0;False;1;FLOAT;2;False;2;COLOR;0,0,0,0;False;3;COLOR;0,0,0,0;False;1;COLOR;0
Node;AmplifyShaderEditor.FloorOpNode;51;168.7977,22.54166;Inherit;False;1;0;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.SimpleAddOpNode;18;281.8029,725.5386;Inherit;False;4;4;0;COLOR;0,0,0,0;False;1;COLOR;0,0,0,0;False;2;COLOR;0,0,0,0;False;3;COLOR;0,0,0,0;False;1;COLOR;0
Node;AmplifyShaderEditor.RegisterLocalVarNode;47;445.7571,739.1934;Inherit;False;bandColor;-1;True;1;0;COLOR;0,0,0,0;False;1;COLOR;0
Node;AmplifyShaderEditor.ToggleSwitchNode;45;347.2777,88.26514;Inherit;False;Property;_SmoothHistory;Smooth History;4;0;Create;True;0;0;0;False;0;False;0;False;2;0;FLOAT;0;False;1;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.GetLocalVarNode;48;651.1245,284.0234;Inherit;False;47;bandColor;1;0;OBJECT;;False;1;COLOR;0
Node;AmplifyShaderEditor.FunctionNode;50;608.4901,160.3232;Inherit;False;4BandAmplitudeLerp;-1;;5;3cf4b6e83381a9a4f84f8cf857bc3af5;0;2;2;INT;0;False;4;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.FunctionNode;34;879.9441,222.8554;Inherit;False;BlendOverlay;-1;;6;6c2f15995e7c1bf4bafb65b1a44446b2;0;2;1;COLOR;0,0,0,0;False;2;COLOR;0,0,0,0;False;1;COLOR;0
Node;AmplifyShaderEditor.TemplateMultiPassMasterNode;52;1132.697,182.0305;Float;False;True;-1;2;ASEMaterialInspector;100;1;AudioLink/Amplify/AudioLinkAmplify_4Band;0770190933193b94aaa3065e307002fa;True;Unlit;0;0;Unlit;2;False;True;0;1;False;-1;0;False;-1;0;1;False;-1;0;False;-1;True;0;False;-1;0;False;-1;False;False;False;False;False;False;False;False;False;True;0;False;-1;False;True;0;False;-1;False;True;True;True;True;True;0;False;-1;False;False;False;False;False;False;False;True;False;255;False;-1;255;False;-1;255;False;-1;7;False;-1;1;False;-1;1;False;-1;1;False;-1;7;False;-1;1;False;-1;1;False;-1;1;False;-1;False;True;1;False;-1;True;3;False;-1;True;True;0;False;-1;0;False;-1;True;1;RenderType=Opaque=RenderType;True;2;False;0;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;True;1;LightMode=ForwardBase;False;False;0;;0;0;Standard;1;Vertex Position,InvertActionOnDeselection;1;0;0;1;True;False;;False;0
WireConnection;38;0;9;2
WireConnection;43;0;38;0
WireConnection;23;0;43;0
WireConnection;26;0;25;0
WireConnection;26;2;5;0
WireConnection;22;0;24;0
WireConnection;22;2;4;0
WireConnection;30;0;29;0
WireConnection;30;2;7;0
WireConnection;39;0;46;0
WireConnection;39;1;9;1
WireConnection;28;0;27;0
WireConnection;28;2;6;0
WireConnection;51;0;39;0
WireConnection;18;0;22;0
WireConnection;18;1;26;0
WireConnection;18;2;28;0
WireConnection;18;3;30;0
WireConnection;47;0;18;0
WireConnection;45;0;51;0
WireConnection;45;1;39;0
WireConnection;50;2;38;0
WireConnection;50;4;45;0
WireConnection;34;1;50;0
WireConnection;34;2;48;0
WireConnection;52;0;34;0
ASEEND*/
//CHKSM=878F9C5FEFC3EB07306B89B9BE7D67DDCCAAA882