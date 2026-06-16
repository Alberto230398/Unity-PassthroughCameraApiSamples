Shader "Custom/DepthRegistration"
{
    Properties
    {
        _MainTex("Depth Texture Array", 2DArray) = "white" {}
    }

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

            UNITY_DECLARE_TEX2DARRAY(_MainTex);

            // RGB camera parameters
            float4x4 _ReprojMatrix;
            float3 _RGBPosition;
            float4x4 _RGBRotation;        // camera-to-world rotation matrix
            float2 _FocalLength;           // fx, fy in pixels
            float2 _PrincipalPoint;        // cx, cy in pixels
            float2 _SensorResolution;      // width, height in pixels

            // Depth linearization (global, set by Meta Depth API)
            float4 _EnvironmentDepthZBufferParams;

            float LinearizeDepth(float rawDepth)
            {
                // Meta/Unity reversed-Z linearization:
                // linearDepth = 1.0 / (zParams.z * rawDepth + zParams.w)
                return 1.0 / (_EnvironmentDepthZBufferParams.z * rawDepth + _EnvironmentDepthZBufferParams.w);
            }

            float frag(v2f_img i) : SV_Target
            {
                // 1. Compute RGB pixel coordinates from output UV
                //    UV (0,0) is bottom-left in Unity; image pixel (0,0) is top-left
                float2 pixel = float2(i.uv.x * _SensorResolution.x,
                                      (1.0 - i.uv.y) * _SensorResolution.y);

                // 2. Unproject pixel to camera-space ray direction (z = 1 plane)
                //    Negate Y because Unity camera space is Y-up but pixel Y goes down
                float3 d_cam = float3(
                    (pixel.x - _PrincipalPoint.x) / _FocalLength.x,
                    -(pixel.y - _PrincipalPoint.y) / _FocalLength.y,
                    1.0
                );

                // 3. Rotate ray direction from camera space to world space
                float3 d_world = mul((float3x3)_RGBRotation, d_cam);

                // 4. Compute reprojection terms:
                //    World point = RGBPosition + t * d_world
                //    Clip = ReprojMatrix * [WorldPoint, 1]
                //         = ReprojMatrix * [RGBPosition, 1] + t * ReprojMatrix * [d_world, 0]
                //         = A + t * B
                float4 A = mul(_ReprojMatrix, float4(_RGBPosition, 1.0));
                float4 B = mul(_ReprojMatrix, float4(d_world, 0.0));

                // 5. Iterative solve: find t such that depth texture at projected UV matches
                float t = 1.0; // initial guess: 1 meter

                [unroll]
                for (int iter = 0; iter < 3; iter++)
                {
                    float4 clip = A + t * B;

                    // Convert NDC [-1,1] → UV [0,1].
                    // The reprojection matrix outputs NDC, not UV directly.
                    float2 depthUV = clip.xy / clip.w * 0.5 + 0.5;

                    // Bounds check: outside depth camera FOV — no recovery possible.
                    if (depthUV.x < 0.0 || depthUV.x > 1.0 ||
                        depthUV.y < 0.0 || depthUV.y > 1.0)
                    {
                        return 0.0;
                    }

                    // Sample depth texture array (slice 0 = left eye)
                    float rawDepth = UNITY_SAMPLE_TEX2DARRAY(_MainTex, float3(depthUV, 0)).r;

                    // Linearize to metric depth.
                    // linearZ equals clip.w (depth camera eye-space z) at the true surface.
                    float linearZ = LinearizeDepth(rawDepth);

                    // Solve for t: clip.w = A.w + t * B.w  →  t = (linearZ - A.w) / B.w
                    if (abs(B.w) < 0.0001)
                    {
                        return 0.0; // degenerate: ray is perpendicular to depth camera axis
                    }
                    t = (linearZ - A.w) / B.w;
                }

                // 6. Final bounds check and output
                float4 finalClip = A + t * B;
                float2 finalUV = finalClip.xy / finalClip.w * 0.5 + 0.5;
                if (finalUV.x < 0.0 || finalUV.x > 1.0 ||
                    finalUV.y < 0.0 || finalUV.y > 1.0)
                {
                    return 0.0;
                }

                // Output metric depth in meters (clamped to positive)
                return max(t, 0.0);
            }
            ENDCG
        }
    }
    FallBack Off
}
