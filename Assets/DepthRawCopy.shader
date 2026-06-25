Shader "Custom/DepthRawCopy"
{
    // Copies slice 0 (left eye) of the environment depth Texture2DArray into a
    // single-channel render target WITHOUT any modification. Used to save the
    // unregistered raw depth (reversed-Z buffer values, raw=0 near .. raw=1 far)
    // to rawDepth.exr.
    //
    // We sample the GLOBAL _EnvironmentDepthTexture directly (the same name the
    // Meta SDK binds via Shader.SetGlobalTexture and samples in its own cginc),
    // rather than relying on Graphics.Blit to bind the array source to _MainTex.
    // A plain Blit binds a Texture2DArray source as a 2D sampler, so a
    // UNITY_SAMPLE_TEX2DARRAY(_MainTex, ...) read returns the cleared far value
    // (a flat/uniform "monocolore" result). Reading the global avoids that.
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma require 2darray
            #include "UnityCG.cginc"

            UNITY_DECLARE_TEX2DARRAY(_EnvironmentDepthTexture);

            float frag(v2f_img i) : SV_Target
            {
                // Raw reversed-Z depth value, slice 0 (left eye), unmodified.
                return UNITY_SAMPLE_TEX2DARRAY(_EnvironmentDepthTexture, float3(i.uv, 0)).r;
            }
            ENDCG
        }
    }
    FallBack Off
}
