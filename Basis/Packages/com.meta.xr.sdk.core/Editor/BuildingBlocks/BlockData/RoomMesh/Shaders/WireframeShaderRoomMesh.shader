Shader "Unlit/WireframeShaderRoomMesh" {
  Properties {
    _WireframeColor("WireframeColor", Color) = (1, 0, 0, 1)
    _Color("Color", Color) = (1, 1, 1, 1)
    _LineWidth("LineWidth", Range(0.01, 0.1)) = 0.05
  }

  SubShader {
    Pass {
      CGPROGRAM
      #include "UnityCG.cginc"
      #pragma vertex vert
      #pragma fragment frag

      half4 _WireframeColor, _Color;
      float _LineWidth;

      struct appdata
      {
        float4 vertex : POSITION;
        float4 color : COLOR; // barycentric coords
        UNITY_VERTEX_INPUT_INSTANCE_ID
      };

      struct v2f
      {
        float4 vertex : SV_POSITION;
        float3 color: COLOR;
        UNITY_VERTEX_OUTPUT_STEREO
      };

      v2f vert(appdata v)
      {
        v2f o;
        UNITY_SETUP_INSTANCE_ID(v);
        UNITY_INITIALIZE_OUTPUT(v2f, o);
        UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
        o.vertex = UnityObjectToClipPos(v.vertex);
        o.color = v.color;
        return o;
      }

      fixed4 frag(v2f i) : SV_Target
      {
        // barycentric coord - on triangle edge, we're at 0
        float bc = min(i.color.x, min(i.color.y, i.color.z));

        // use screen-space derivates on the bc
        float bcDeriv = fwidth(bc);

        // limit the thickness by pixel size
        float drawWidth = max(_LineWidth, bcDeriv);

        // multiply to use absolute size (instead of later /2)
        float lineAA = bcDeriv * 1.5f;
        float lineBC = 1.0f - abs(frac(bc) * 2.0f - 1.0f);

        // smoothstep using the BC as the gradient
        float val = smoothstep(drawWidth + lineAA, drawWidth - lineAA, lineBC);

        // fade by how thick we wanted (_LineWidth) vs we're drawing
        val *= saturate(_LineWidth / drawWidth);

        return lerp(_Color, _WireframeColor, val);
      }
      ENDCG
    }
  }
}
