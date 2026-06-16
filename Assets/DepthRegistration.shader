Shader "Custom/DepthRegistration"
{
    /*Properties
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
    }*/

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

            float4x4 _ReprojMatrix;
            float3 _RGBPosition;
            float4x4 _RGBRotation;
            float2 _FocalLength;
            float2 _PrincipalPoint;
            float2 _SensorResolution;
            float4 _EnvironmentDepthZBufferParams;

            // Meta's formula from EnvironmentOcclusion.cginc:
            // linearDepth = x / (rawDepth*2-1 + y)
            float LinearizeDepth(float rawDepth)
            {
                float ndc = rawDepth * 2.0 - 1.0;
                return _EnvironmentDepthZBufferParams.x / (ndc + _EnvironmentDepthZBufferParams.y);
            }

            float frag(v2f_img i) : SV_Target
            {
                // Sensor coords: SDK uses Y-up convention (sensor.y=0 at bottom, sensor.y=H at top)
                // i.uv.y=0 is bottom of screen → sensor.y=0; i.uv.y=1 is top → sensor.y=H
                float2 sensor = float2(i.uv.x * _SensorResolution.x,
                                       i.uv.y * _SensorResolution.y);

                // Unproject to camera-space ray — no Y-negation (sensor Y is already up)
                float3 d_cam = float3(
                    (sensor.x - _PrincipalPoint.x) / _FocalLength.x,
                    (sensor.y - _PrincipalPoint.y) / _FocalLength.y,
                    1.0
                );

                // World-space ray direction
                float3 d_world = mul((float3x3)_RGBRotation, d_cam);

                // clip(t) = A + t*B  for worldPos = RGBPos + t * d_world
                // Correct order: mul(matrix, vector) — confirmed by Meta's EnvironmentOcclusion.cginc line 85
                float4 A = mul(_ReprojMatrix, float4(_RGBPosition, 1.0));
                float4 B = mul(_ReprojMatrix, float4(d_world, 0.0));

                // Precompute for analytical t-solve from sampled linearDepth:
                // linearDepth = x * (A.w + t*B.w) / ((A.z + y*A.w) + t*(B.z + y*B.w))
                // → t = (x*A.w - linZ*P) / (linZ*Q - x*B.w)
                float zx = _EnvironmentDepthZBufferParams.x;
                float zy = _EnvironmentDepthZBufferParams.y;
                float P = A.z + zy * A.w;
                float Q = B.z + zy * B.w;

                float t = 1.0; // initial guess: 1 meter

                [unroll]
                for (int iter = 0; iter < 4; iter++)
                {
                    float4 clip = A + t * B;
                    float2 depthUV = clip.xy / clip.w * 0.5 + 0.5;

                    if (depthUV.x < 0.0 || depthUV.x > 1.0 ||
                        depthUV.y < 0.0 || depthUV.y > 1.0)
                        return 0.0;

                    float rawDepth = UNITY_SAMPLE_TEX2DARRAY(_MainTex, float3(depthUV, 0)).r;
                    float linZ = LinearizeDepth(rawDepth);

                    float denom = linZ * Q - zx * B.w;
                    if (abs(denom) < 1e-5)
                        return 0.0;

                    t = (zx * A.w - linZ * P) / denom;
                }

                // Final bounds check
                float4 finalClip = A + t * B;
                float2 finalUV = finalClip.xy / finalClip.w * 0.5 + 0.5;
                if (finalUV.x < 0.0 || finalUV.x > 1.0 ||
                    finalUV.y < 0.0 || finalUV.y > 1.0)
                    return 0.0;

                // t is the z-depth in RGB camera space (since d_cam.z = 1.0)
                return max(t, 0.0);
            }
            ENDCG
        }
    }
    FallBack Off
}
