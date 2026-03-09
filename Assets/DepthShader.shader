Shader "Custom/DepthPreview"
{
    Properties { _MainTex("Depth", 2DArray) = "white" {} }
    SubShader {
        Pass {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma require 2darray
            #include "UnityCG.cginc"
            
            UNITY_DECLARE_TEX2DARRAY(_MainTex);
            
            fixed4 frag(v2f_img i) : SV_Target {
                float depth = UNITY_SAMPLE_TEX2DARRAY(_MainTex, float3(i.uv, 0)).r;
                depth = 1.0 - depth;
                return fixed4(depth, depth, depth, 1.0);
            }
            ENDCG
        }
    }
}