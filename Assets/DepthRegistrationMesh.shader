Shader "Custom/DepthRegistrationMesh"
{
    // Forward depth registration with a CONNECTED TRIANGLE MESH.
    //
    // Same idea as DepthRegistrationForward (project depth texels into the RGB
    // camera with a reprojection matrix built from the RGB intrinsics), but
    // instead of one isolated quad per texel (which leaves a dotted point
    // cloud), it renders the depth map as a continuous grid of triangles. Each
    // (W-1)x(H-1) cell becomes a quad = 2 triangles; the rasterizer interpolates
    // the surface between depth samples, so there are no holes and the result
    // follows the surface. The hardware z-buffer resolves occlusion.
    //
    // Quads that straddle a depth discontinuity (a silhouette edge) are dropped
    // so foreground and background are not bridged by a rubber sheet.
    //
    // Drive it with Graphics.DrawProceduralNow(MeshTopology.Triangles,
    // (depthW-1)*(depthH-1)*6) into an RFloat RenderTexture WITH a depth buffer.
    // See KeyFrameManager.SaveRegisteredDepthFrameMesh.
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
            float4 _ZBufferParams2;     // Meta _EnvironmentDepthZBufferParams
            int   _DepthWidth;
            int   _DepthHeight;
            int   _Slice;               // depth array slice (0 = left eye)
            float _MinRawDepth;         // texels below this are "no data"
            float _EdgeThreshold;       // metric depth jump (m) that breaks a quad

            struct v2f
            {
                float4 pos      : SV_POSITION;
                float  linDepth : TEXCOORD0;  // metric depth along RGB optical axis
            };

            // Two triangles of a unit cell, corners as (dx,dy) in {0,1}.
            // tri A: (0,0)(1,0)(0,1)   tri B: (0,1)(1,0)(1,1)
            static const int2 kCell[6] = {
                int2(0,0), int2(1,0), int2(0,1),
                int2(0,1), int2(1,0), int2(1,1)
            };

            // Reversed-Z infinite projection: z_eye = zx / (zy * rawDepth)
            float LinearizeDepth(float rawDepth)
            {
                return _ZBufferParams2.x / (_ZBufferParams2.y * rawDepth);
            }

            // Depth texel -> metric depth along the RGB optical axis (clip.w).
            // Returns the projected clip-space position; sets ok=false if no data.
            float4 ProjectTexel(int px, int py, out float linDepth)
            {
                float raw = _MainTex.Load(int4(px, py, _Slice, 0));

                // texel center -> depth NDC. rawDepth IS the stored ndc_z.
                float2 uv  = (float2(px, py) + 0.5) / float2(_DepthWidth, _DepthHeight);
                float3 ndc = float3(uv * 2.0 - 1.0, raw);

                // depth NDC -> world -> RGB clip. clip.w == camera-space z == metric depth.
                float4 worldH = mul(_DepthInvReproj, float4(ndc, 1.0));
                float3 world  = worldH.xyz / worldH.w;
                float4 clip   = mul(_RGBReprojMatrix, float4(world, 1.0));

                linDepth = clip.w;
                return clip;
            }

            v2f vert(uint id : SV_VertexID)
            {
                v2f o;

                uint cell   = id / 6u;
                uint corner = id % 6u;

                uint cellsX = (uint)(_DepthWidth  - 1);
                uint gx = cell % cellsX;
                uint gy = cell / cellsX;

                // The four raw depths of this cell decide if the quad is valid.
                float d00 = _MainTex.Load(int4(gx,   gy,   _Slice, 0));
                float d10 = _MainTex.Load(int4(gx+1, gy,   _Slice, 0));
                float d01 = _MainTex.Load(int4(gx,   gy+1, _Slice, 0));
                float d11 = _MainTex.Load(int4(gx+1, gy+1, _Slice, 0));

                float rawMin = min(min(d00, d10), min(d01, d11));

                // Linear (metric) range across the cell — a big jump means this
                // quad spans a silhouette edge; drop it so we don't bridge it.
                float l00 = LinearizeDepth(d00), l10 = LinearizeDepth(d10);
                float l01 = LinearizeDepth(d01), l11 = LinearizeDepth(d11);
                float lMin = min(min(l00, l10), min(l01, l11));
                float lMax = max(max(l00, l10), max(l01, l11));

                if (rawMin < _MinRawDepth || (lMax - lMin) > _EdgeThreshold)
                {
                    // Degenerate -> the whole triangle is clipped away.
                    o.pos      = float4(0, 0, 0, -1);
                    o.linDepth = 0;
                    return o;
                }

                int2 c  = kCell[corner];
                int  px = (int)gx + c.x;
                int  py = (int)gy + c.y;

                float linDepth;
                o.pos      = ProjectTexel(px, py, linDepth);
                o.linDepth = linDepth;
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
