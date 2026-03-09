Shader "Custom/SideBySide"
{
    Properties 
    { 
        _MainTex("Left", 2D) = "black" {}
        _RightTex("Right", 2D) = "black" {}
    }
    SubShader {
        Pass {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            
            sampler2D _MainTex;
            sampler2D _RightTex;
            
            fixed4 frag(v2f_img i) : SV_Target {
                return i.uv.x < 0.5
                    ? tex2D(_MainTex,  float2(i.uv.x * 2.0, i.uv.y))
                    : tex2D(_RightTex, float2((i.uv.x - 0.5) * 2.0, i.uv.y));
            }
            ENDCG
        }
    }
}