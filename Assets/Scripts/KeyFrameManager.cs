using System.Collections;
using Meta.XR;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using System.Collections.Generic;
using Meta.XR.EnvironmentDepth;
using Unity.XR.Oculus;

public class KeyFrameManager : MonoBehaviour
{
    private Vector3 _lastKeyframePosition;
    private Quaternion _lastKeyframeRotation;

    [SerializeField] float translationThreshold = 0.1f; // 10cm
    [SerializeField] float rotationThreshold = 5f; // 5 gradi

    [SerializeField] PassthroughCameraAccess passthroughCameraLeft;
    [SerializeField] PassthroughCameraAccess passthroughCameraRight;
    [SerializeField] Material depthMaterial;
    private RenderTexture target;
    private RenderTexture rightTarget;

    public Camera leftCamera;

    private List<Keyframe> keyframes = new List<Keyframe>();

    void Start()
    {
        CaptureKeyframe();

        //_occlusionSubsystem = loader.GetLoadedSubsystem<XROcclusionSubsystem>() as MetaOpenXROcclusionSubsystem;
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
        var righjtPose = passthroughCameraRight.GetCameraPose();
        var depthTex = Shader.GetGlobalTexture("_EnvironmentDepthTexture");

        if (depthTex == null || !passthroughCameraLeft.IsPlaying) return;

        // inizializza target dalla camera se non esiste
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

        // aggiorna target con il frame corrente
        Graphics.Blit(passthroughCameraLeft.GetTexture(), target);
        Graphics.Blit(passthroughCameraRight.GetTexture(), rightTarget);

        Texture2D rgb = SaveFrame(target);
        Texture2D depth = SaveDepthFrame(depthTex, target);
        Texture2D rgbRight = SaveFrame(rightTarget);

        Matrix4x4[] reproj = Shader.GetGlobalMatrixArray("_EnvironmentDepthReprojectionMatrices");
        Vector4 zParams = Shader.GetGlobalVector("_EnvironmentDepthZBufferParams");

        Matrix4x4 proj = Matrix4x4.identity;

        var matr = leftCamera.projectionMatrix;
        var cam2worldMatr = leftCamera.cameraToWorldMatrix;

        if (reproj != null && reproj.Length > 0)
        {
            proj = reproj[0]; // proj * view per occhio sinistro, in tracking space
        }

        var kf = new Keyframe
        {
            rgb = rgb,
            depth = depth,
            rgbRight = rgbRight,
            position = pose.position,
            rotation = pose.rotation,
            timestamp = passthroughCameraLeft.Timestamp,
            intrinsics = passthroughCameraLeft.Intrinsics,
            reprojectionMatrix = proj,
            RGBMatrix = matr
        };

        keyframes.Add(kf);
        SaveKeyframeToDisk(kf, keyframes.Count - 1);

        Debug.Log($"Keyframe captured: {keyframes.Count} | pos: {pose.position}");
    }

    void SaveKeyframeToDisk(Keyframe kf, int index)
    {
        string dir = $"{Application.persistentDataPath}/keyframes/{index}";
        System.IO.Directory.CreateDirectory(dir);

        // RGB
        byte[] rgbBytes = kf.rgb.EncodeToPNG();
        System.IO.File.WriteAllBytes($"{dir}/LeftRGB.png", rgbBytes);

        // RGB Right
        byte[] rgbRightBytes = kf.rgbRight.EncodeToPNG();
        System.IO.File.WriteAllBytes($"{dir}/RightRGB.png", rgbRightBytes);

        // Depth
        byte[] depthBytes = kf.depth.EncodeToEXR();
        System.IO.File.WriteAllBytes($"{dir}/depth.exr", depthBytes);

        // Pose
        string pose = JsonUtility.ToJson(new PoseData
        {
            px = kf.position.x, py = kf.position.y, pz = kf.position.z,
            rx = kf.rotation.x, ry = kf.rotation.y, rz = kf.rotation.z, rw = kf.rotation.w,
            timestamp = kf.timestamp.ToString("HH:mm:ss:fff")
        });

        // RGB Intrinsics
        string RGBIntrinsics = JsonUtility.ToJson(new Intrinsics
        {
            FocalLength = kf.intrinsics.FocalLength,
            PrincipalPoint = kf.intrinsics.PrincipalPoint,
            SensorResolution = kf.intrinsics.SensorResolution
        });
        
        System.IO.File.WriteAllText($"{dir}/pose.json", pose);
        System.IO.File.WriteAllText($"{dir}/intrinsics.json", RGBIntrinsics);
        // Reprojection Matrix
        string reproj = JsonUtility.ToJson(kf.reprojectionMatrix);
        System.IO.File.WriteAllText($"{dir}/reprojection.json", reproj);

        string rgbMat = JsonUtility.ToJson(kf.RGBMatrix);
        System.IO.File.WriteAllText($"{dir}/rgbMatrix.json", rgbMat);
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

    Texture2D SaveDepthFrame(Texture depthTexArray, RenderTexture temp)
    {
        RenderTexture rt = new RenderTexture(depthTexArray.width, depthTexArray.height, 0, RenderTextureFormat.RFloat);
        rt.Create();
        Graphics.Blit(depthTexArray, rt, depthMaterial); // stesso mat che usi per il preview
    
        Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RFloat, false);
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = null;
    
        Destroy(rt);
        return tex;
    }

    void RGBIntrinsics(PassthroughCameraAccess cam)
    {
        var intrinsics = cam.Intrinsics;
        Debug.Log($"Intrinsics: {intrinsics.FocalLength}, {intrinsics.PrincipalPoint}, {intrinsics.SensorResolution}");
    }
}

[System.Serializable]
public struct Keyframe
{
    public Texture2D rgb;
    public Texture2D rgbRight;
    public Texture2D depth;
    public Vector3 position;
    public Quaternion rotation;
    public System.DateTime timestamp;
    public PassthroughCameraAccess.CameraIntrinsics intrinsics;
    public Matrix4x4 reprojectionMatrix;
    public Matrix4x4 RGBMatrix;
}

[System.Serializable]
struct PoseData
{
    public float px, py, pz;
    public float rx, ry, rz, rw;
    public string timestamp;
}

[System.Serializable]
struct Intrinsics
{
    public Vector2 FocalLength;
    public Vector2 PrincipalPoint;
    public Vector2 SensorResolution;
}