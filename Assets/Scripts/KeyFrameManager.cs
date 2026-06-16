using System.Collections;
using Meta.XR;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Meta.XR.EnvironmentDepth;
using Unity.XR.Oculus;

public class KeyFrameManager : MonoBehaviour
{
    private Vector3 _lastKeyframePosition;
    private Quaternion _lastKeyframeRotation;

    [SerializeField] float translationThreshold = 0.1f; // 10cm
    [SerializeField] float rotationThreshold = 5f; // 5 degrees

    [SerializeField] PassthroughCameraAccess passthroughCameraLeft;
    [SerializeField] PassthroughCameraAccess passthroughCameraRight;
    [SerializeField] Camera leftCamera;
    [SerializeField] Material depthMaterial;            // Legacy preview shader (DepthShader)
    [SerializeField] Material registrationMaterial;     // DepthRegistration shader for aligned output

    private RenderTexture target;
    private RenderTexture rightTarget;

    private int _keyframeCount = 0;

    void Start()
    {
        CaptureKeyframe();
    }

    void Update()
    {
        var pose = passthroughCameraLeft.GetCameraPose();

        float translation = Vector3.Distance(pose.position, _lastKeyframePosition);
        float rotation = Quaternion.Angle(pose.rotation, _lastKeyframeRotation);

        if (translation > translationThreshold || rotation > rotationThreshold)
        {
            CaptureKeyframe();
            _lastKeyframePosition = pose.position;
            _lastKeyframeRotation = pose.rotation;
        }
    }

    void CaptureKeyframe()
    {
        var pose = passthroughCameraLeft.GetCameraPose();
        var rightPose = passthroughCameraRight.GetCameraPose();
        var depthTex = Shader.GetGlobalTexture("_EnvironmentDepthTexture");

        if (depthTex == null || !passthroughCameraLeft.IsPlaying) return;

        // Get reprojection matrices and zBufferParams early — needed for registration
        Matrix4x4[] reproj = Shader.GetGlobalMatrixArray("_EnvironmentDepthReprojectionMatrices");
        Vector4 zParams = Shader.GetGlobalVector("_EnvironmentDepthZBufferParams");

        if (reproj == null || reproj.Length == 0) return;

        Matrix4x4 reprojMatrix = reproj[0]; // left eye reprojection matrix

        // Initialize RGB render targets from camera texture dimensions
        if (target == null)
        {
            var camTex = passthroughCameraLeft.GetTexture();
            if (camTex == null) return;
            target = new RenderTexture(camTex.width, camTex.height, 0, RenderTextureFormat.BGRA32);
            target.Create();
        }

        if (rightTarget == null)
        {
            var camTex = passthroughCameraRight.GetTexture();
            if (camTex == null) return;
            rightTarget = new RenderTexture(camTex.width, camTex.height, 0, RenderTextureFormat.BGRA32);
            rightTarget.Create();
        }

        // Blit current camera frames to render targets
        Graphics.Blit(passthroughCameraLeft.GetTexture(), target);
        Graphics.Blit(passthroughCameraRight.GetTexture(), rightTarget);

        // Save RGB frames
        Texture2D rgb = SaveFrame(target);
        Texture2D rgbRight = SaveFrame(rightTarget);
        Texture2D rawDepth = SaveDepthFrameRaw(depthTex);

        // Save registered depth (reprojected into RGB camera space)
        var intrinsics = passthroughCameraLeft.Intrinsics;

        Texture2D depth = SaveRegisteredDepthFrame(
            depthTex,
            pose,
            intrinsics,
            reprojMatrix,
            zParams
        );

        var rgbMatrix = leftCamera.projectionMatrix;

        var kf = new CapturedKeyframe
        {
            rgb = rgb,
            depth = depth,
            rgbRight = rgbRight,
            rawDepth = rawDepth,
            position = pose.position,
            rotation = pose.rotation,
            timestamp = passthroughCameraLeft.Timestamp,
            intrinsics = intrinsics,
            reprojectionMatrix = reprojMatrix,
            zBufferParams = zParams,
            depthResolution = new Vector2(depthTex.width, depthTex.height)
        };

        SaveKeyframeToDisk(kf, _keyframeCount++);

        // Destroy textures immediately — they are on disk, no reason to keep them in RAM.
        Destroy(kf.rgb);
        Destroy(kf.rgbRight);
        Destroy(kf.depth);

        Debug.Log($"Keyframe captured: {_keyframeCount} | pos: {pose.position} | depth registered at {target.width}x{target.height}");
    }

    /// <summary>
    /// Reprojects the depth texture into the RGB camera's pixel space using the registration shader.
    /// Output is a metric-depth RFloat texture at RGB resolution, pixel-aligned with the RGB image.
    /// </summary>
    Texture2D SaveRegisteredDepthFrame(Texture depthTexArray, Pose rgbPose,
        PassthroughCameraAccess.CameraIntrinsics intrinsics, Matrix4x4 reproj, Vector4 zParams)
    {
        // Set registration shader uniforms
        registrationMaterial.SetMatrix("_ReprojMatrix", reproj);
        registrationMaterial.SetVector("_RGBPosition", rgbPose.position);
        registrationMaterial.SetMatrix("_RGBRotation", Matrix4x4.Rotate(rgbPose.rotation));
        registrationMaterial.SetVector("_FocalLength", intrinsics.FocalLength);
        registrationMaterial.SetVector("_PrincipalPoint", intrinsics.PrincipalPoint);
        //registrationMaterial.SetVector("_SensorResolution", intrinsics.SensorResolution);
        registrationMaterial.SetVector("_SensorResolution", new Vector4(intrinsics.SensorResolution.x, intrinsics.SensorResolution.y, 0, 0));


        Debug.Log($"++++++++++++++++zParams: {zParams}");
        Debug.Log($"++++++++++++++++reprojMatrix row0: {reproj.GetRow(0)}");
        Debug.Log($"++++++++++++++++reprojMatrix row1: {reproj.GetRow(1)}");
        Debug.Log($"++++++++++++++++reprojMatrix row2: {reproj.GetRow(2)}");
        Debug.Log($"++++++++++++++++reprojMatrix row3: {reproj.GetRow(3)}");
        // Output at RGB camera resolution (matches the saved PNG dimensions)
        int w = target.width;
        int h = target.height;
        RenderTexture rt = new RenderTexture(w, h, 0, RenderTextureFormat.RFloat);
        rt.Create();

        Graphics.Blit(depthTexArray, rt, registrationMaterial); //registrationMaterial

        Texture2D tex = new Texture2D(w, h, TextureFormat.RFloat, false);
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tex.Apply();
        RenderTexture.active = null;

        Destroy(rt);
        return tex;
    }

    void SaveKeyframeToDisk(CapturedKeyframe kf, int index)
    {
        string dir = $"{Application.persistentDataPath}/keyframes/{index}";
        System.IO.Directory.CreateDirectory(dir);

        // RGB
        byte[] rgbBytes = kf.rgb.EncodeToPNG();
        System.IO.File.WriteAllBytes($"{dir}/LeftRGB.png", rgbBytes);

        // RGB Right
        byte[] rgbRightBytes = kf.rgbRight.EncodeToPNG();
        System.IO.File.WriteAllBytes($"{dir}/RightRGB.png", rgbRightBytes);

        // Registered Depth (metric, pixel-aligned with LeftRGB)
        byte[] depthBytes = kf.depth.EncodeToEXR();
        System.IO.File.WriteAllBytes($"{dir}/depth.exr", depthBytes);

        byte[] rawDepthBytes = kf.rawDepth.EncodeToEXR();
        System.IO.File.WriteAllBytes($"{dir}/rawDepth.exr", rawDepthBytes);


        // Pose
        string pose = JsonUtility.ToJson(new PoseData
        {
            px = kf.position.x, py = kf.position.y, pz = kf.position.z,
            rx = kf.rotation.x, ry = kf.rotation.y, rz = kf.rotation.z, rw = kf.rotation.w,
            timestamp = kf.timestamp.ToString("HH:mm:ss:fff")
        });

        // RGB Intrinsics
        string RGBIntrinsics = JsonUtility.ToJson(new IntrinsicsData
        {
            FocalLength = kf.intrinsics.FocalLength,
            PrincipalPoint = kf.intrinsics.PrincipalPoint,
            SensorResolution = kf.intrinsics.SensorResolution
        });

        // Reprojection Matrix
        string reproj = JsonUtility.ToJson(kf.reprojectionMatrix);

        // zBufferParams (for offline verification/debugging of linearization)
        string zbuf = JsonUtility.ToJson(new ZBufferParamsData
        {
            x = kf.zBufferParams.x,
            y = kf.zBufferParams.y,
            z = kf.zBufferParams.z,
            w = kf.zBufferParams.w
        });

        // Depth texture native resolution (for reference)
        string depthMeta = JsonUtility.ToJson(new DepthMetaData
        {
            width = kf.depthResolution.x,
            height = kf.depthResolution.y
        });

        System.IO.File.WriteAllText($"{dir}/pose.json", pose);
        System.IO.File.WriteAllText($"{dir}/intrinsics.json", RGBIntrinsics);
        System.IO.File.WriteAllText($"{dir}/reprojection.json", reproj);
        System.IO.File.WriteAllText($"{dir}/zbuffer_params.json", zbuf);
        System.IO.File.WriteAllText($"{dir}/depth_meta.json", depthMeta);
    }

    Texture2D SaveFrame(RenderTexture rt)
    {
        Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = null;
        return tex;
    }

    /// <summary>
    /// Legacy: saves raw depth at native depth camera resolution (unregistered).
    /// Kept for debug/preview purposes. Not used in the main capture pipeline.
    /// </summary>
    Texture2D SaveDepthFrameRaw(Texture depthTexArray)
    {
        RenderTexture rt = new RenderTexture(depthTexArray.width, depthTexArray.height, 0, RenderTextureFormat.RFloat);
        rt.Create();
        Graphics.Blit(depthTexArray, rt, registrationMaterial);

        Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RFloat, false);
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = null;

        Destroy(rt);
        return tex;
    }
}

[System.Serializable]
public struct CapturedKeyframe
{
    public Texture2D rgb;
    public Texture2D rgbRight;
    public Texture2D depth;
    public Texture2D rawDepth;
    public Vector3 position;
    public Quaternion rotation;
    public System.DateTime timestamp;
    public PassthroughCameraAccess.CameraIntrinsics intrinsics;
    public Matrix4x4 reprojectionMatrix;
    public Vector4 zBufferParams;
    public Vector2 depthResolution;
}

[System.Serializable]
struct PoseData
{
    public float px, py, pz;
    public float rx, ry, rz, rw;
    public string timestamp;
}

[System.Serializable]
struct IntrinsicsData
{
    public Vector2 FocalLength;
    public Vector2 PrincipalPoint;
    public Vector2 SensorResolution;
}

[System.Serializable]
struct ZBufferParamsData
{
    public float x, y, z, w;
}

[System.Serializable]
struct DepthMetaData
{
    public float width, height;
}
