Shader "Custom/DepthRegistrationForward"
{
    // Forward (scatter) depth registration with SPLATTING.
    //
    // Renders the depth map as a grid of small screen-space quads (one per depth
    // texel) and projects each into the RGB camera using a reprojection matrix
    // built from the RGB intrinsics. No iteration, parallax correct; the hardware
    // z-buffer resolves occlusion (nearest wins). Splatting fills the holes that a
    // 1-pixel point cloud leaves when the depth map is lower-res than the RGB image.
    //
    // Drive it with Graphics.DrawProceduralNow(MeshTopology.Triangles, depthW*depthH*6)
    // into an RFloat RenderTexture that HAS a depth buffer. See KeyFrameManager.
    Properties { }

    SubShader
    {
        Tags { "RenderType"="Opaque" }

        Pass
        {
            Cull Off
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5            // SV_VertexID + Texture2DArray.Load
            #include "UnityCG.cginc"

            Texture2DArray<float> _MainTex;   // Meta _EnvironmentDepthTexture

            float4x4 _DepthInvReproj;   // depth-clip -> world  (= reproj.inverse)
            float4x4 _RGBReprojMatrix;  // world -> RGB clip     (= K * V, see C#)
            int   _DepthWidth;
            int   _DepthHeight;
            int   _Slice;               // depth array slice (0 = left eye)
            float _MinRawDepth;         // texels below this are "no data"
            float _RGBWidth;            // output RT width  (pixels)
            float _RGBHeight;           // output RT height (pixels)
            float _SplatSize;           // quad size in RGB pixels (fills the holes)

            struct v2f
            {
                float4 pos      : SV_POSITION;
                float  linDepth : TEXCOORD0;  // metric depth along RGB optical axis
            };

            // unit quad as two triangles, corners in [-1,1]
            static const float2 kCorners[6] = {
                float2(-1, -1), float2( 1, -1), float2(-1,  1),
                float2(-1,  1), float2( 1, -1), float2( 1,  1)
            };

            v2f vert(uint id : SV_VertexID)
            {
                v2f o;

                uint quad   = id / 6u;
                uint corner = id % 6u;

                int px = (int)(quad % (uint)_DepthWidth);
                int py = (int)(quad / (uint)_DepthWidth);

                float rawDepth = _MainTex.Load(int4(px, py, _Slice, 0));

                // Invalid / no-data texel -> degenerate (whole quad clipped).
                if (rawDepth < _MinRawDepth)
                {
                    o.pos      = float4(0, 0, 0, -1);
                    o.linDepth = 0;
                    return o;
                }

                // texel center -> depth NDC.  rawDepth IS the stored ndc_z.
                float2 uv  = (float2(px, py) + 0.5) / float2(_DepthWidth, _DepthHeight);
                float3 ndc = float3(uv * 2.0 - 1.0, rawDepth);

                // depth NDC -> world point
                float4 worldH = mul(_DepthInvReproj, float4(ndc, 1.0));
                float3 world  = worldH.xyz / worldH.w;

                // world -> RGB clip.  clip.w == camera-space z == metric depth.
                float4 clip = mul(_RGBReprojMatrix, float4(world, 1.0));

                // Expand into a screen-space quad of _SplatSize pixels around the
                // projected center. 1 pixel = 2/res in NDC; half-extent = size/res.
                float2 centerNdc = clip.xy / clip.w;
                float2 offset    = kCorners[corner] * _SplatSize / float2(_RGBWidth, _RGBHeight);
                float2 splatNdc  = centerNdc + offset;

                o.pos      = float4(splatNdc * clip.w, clip.z, clip.w);
                o.linDepth = clip.w;
                return o;
            }

            float frag(v2f i) : SV_Target
            {
                return i.linDepth;
            }
            ENDCG
        }
    }
    FallBack Off
}
