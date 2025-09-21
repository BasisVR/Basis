/************************************************************************************

Copyright (c) Facebook Technologies, LLC and its affiliates. All rights reserved.

See SampleFramework license.txt for license terms.  Unless required by applicable law
or agreed to in writing, the sample code is provided "AS IS" WITHOUT WARRANTIES OR
CONDITIONS OF ANY KIND, either express or implied.  See the license for specific
language governing permissions and limitations under the license.

************************************************************************************/

Shader "Oculus Sample/Alpha Hand Outline (SBB)"
{
    Properties
    {
        _ColorPrimary ("Color Primary", Color) = (0.396078, 0.725490, 1)
        _ColorTop ("Color Top", Color) = (0.031896, 0.0343398, 0.0368894)
        _ColorBottom ("Color Bottom", Color) = (0.0137021, 0.0144438, 0.0152085)
        _RimFactor ("Rim Factor", Range(0.01, 1.0)) = 0.65
        _FresnelPower ("Fresnel Power", Range(0.01,1.0)) = 0.16

        _HandAlpha ("Hand Alpha", Range(0, 1)) = 1.0
        _MinVisibleAlpha ("Minimum Visible Alpha", Range(0,1)) = 0.15
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    struct appdata
    {
        float4 vertex : POSITION;
        float3 normal : NORMAL;

        UNITY_VERTEX_INPUT_INSTANCE_ID
    };

    struct v2f
    {
        float4 vertex : SV_POSITION;
        float3 normal : NORMAL;
        float3 lightDir : TEXCOORD1;

        UNITY_VERTEX_OUTPUT_STEREO
    };

    fixed3 _ColorPrimary;
    fixed3 _ColorTop;
    fixed3 _ColorBottom;
    float _RimFactor;
    float _FresnelPower;
    float _HandAlpha;
    float _MinVisibleAlpha;

    v2f vert (appdata v)
    {
        v2f o;

        UNITY_SETUP_INSTANCE_ID(v);
        UNITY_INITIALIZE_OUTPUT(v2f, o);
        UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

        o.vertex = UnityObjectToClipPos(v.vertex);
        o.normal = UnityObjectToWorldNormal(v.normal);

        float4 posWorld = mul(unity_ObjectToWorld, v.vertex);
        o.lightDir = WorldSpaceLightDir(posWorld);
        return o;
    }

    fixed4 frag (v2f i) : SV_Target
    {
        float3 normalDir = normalize(i.normal);
        float3 lightDir = normalize(i.lightDir);
        half lightDotNormal = saturate(dot(lightDir, normalDir));

        half rim = pow(1.0 - lightDotNormal, 0.5) * (1.0 - _RimFactor) + _RimFactor;
        rim = saturate(rim);

        half3 emission = lerp(float3(0,0,0), _ColorPrimary, rim);
        emission += rim * 0.5;
        emission *= 0.95;

        float fresnel = saturate(pow(1.0 - lightDotNormal, _FresnelPower));
        fixed3 color = lerp(_ColorTop, _ColorBottom, fresnel);
        color *= emission;

        fixed alphaValue = step(_MinVisibleAlpha, _HandAlpha) * _HandAlpha;

        return float4(color, alphaValue);
    }
    ENDCG

    // URP shader (only the Tags are different)
    SubShader
    {
        PackageRequirements { "com.unity.render-pipelines.universal" }
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" }
        LOD 100

        // write depth to see the top-most layer
        Pass
        {
            ZWrite On
            ColorMask 0
        }

        // our shading pass
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha, Zero One

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            ENDCG
        }
    }

    // BiRP shader (only the Tags are different)
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        LOD 100

        Pass
        {
            ZWrite On
            ColorMask 0
        }

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha, Zero One

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            ENDCG
        }
    }
}
