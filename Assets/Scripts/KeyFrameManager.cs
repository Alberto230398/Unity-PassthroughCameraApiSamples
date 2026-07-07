using System.Collections;
using Meta.XR;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Meta.XR.EnvironmentDepth;
using Unity.XR.Oculus;
using static Unity.XR.Oculus.Utils;
using System.Runtime.CompilerServices;

// Cattura keyframe (RGB stereo + depth raw + pose/intrinseci) su disco per
// ricostruzione offline/server-side (unproject deterministico -> init Gaussiane).
// Nessuna fusion/meshing on-device: questo script è solo un esportatore dati.
public class KeyFrameManager : MonoBehaviour
{
    // Pose della camera PCA sinistra all'ultimo keyframe catturato, usata per
    // il gating dei nuovi keyframe per movimento invece che a intervalli fissi.
    private Vector3 _lastKeyframePosition;
    private Quaternion _lastKeyframeRotation;

    [SerializeField] float translationThreshold = 0.1f; // 10cm
    [SerializeField] float rotationThreshold = 5f; // 5 gradi

    // Accessor alla Passthrough Camera API — sensori fisicamente separati
    // dalla depth camera, ciascuno con pose/intrinseci propri.
    [SerializeField] PassthroughCameraAccess passthroughCameraLeft;
    [SerializeField] PassthroughCameraAccess passthroughCameraRight;
    [SerializeField] Camera leftCamera;
    [SerializeField] Material rawDepthMaterial;         // Shader DepthRawCopy: estrae la slice 0 del Texture2DArray senza modifiche
    [SerializeField] EnvironmentDepthManager environmentDepthManager; // Gate della cattura su IsDepthAvailable
    [SerializeField] Text debugText;

    private RenderTexture target;
    private RenderTexture rightTarget;

    private int _keyframeCount = 0;

    private bool _firstKeyframeCaptured = false;

    void Awake()
    {
        // Risolve automaticamente il depth manager se non wired in Inspector,
        // altrimenti il gate IsDepthAvailable sotto blocca ogni cattura.
        if (environmentDepthManager == null)
            environmentDepthManager = FindFirstObjectByType<EnvironmentDepthManager>();
    }

    void Update()
    {
        Matrix4x4[] reprojMatrix = Shader.GetGlobalMatrixArray("_EnvironmentDepthReprojectionMatrices");
        //debugText.text = "Matrix saved: " + reprojMatrix[1].ToString();
        // La depth non è prodotta nei primi frame dopo l'attivazione del depth
        // manager; catturare prima produce una texture tutta-lontana (raw=1.0).
        if (environmentDepthManager == null || !environmentDepthManager.IsDepthAvailable) return;

        var pose = passthroughCameraLeft.GetCameraPose();

        // Cattura il primo keyframe appena la depth diventa disponibile.
        if (!_firstKeyframeCaptured)
        {
            CaptureKeyframe();
            _firstKeyframeCaptured = true;
            _lastKeyframePosition = pose.position;
            _lastKeyframeRotation = pose.rotation;
            return;
        }

        // Gating per pose-delta (non a intervalli fissi): minimizza frame
        // ridondanti nel training set inviato al server di ricostruzione.
        float translation = Vector3.Distance(pose.position, _lastKeyframePosition);
        float rotation = Quaternion.Angle(pose.rotation, _lastKeyframeRotation);

        if (translation > translationThreshold || rotation > rotationThreshold)
        {
            CaptureKeyframe();
            _lastKeyframePosition = pose.position;
            _lastKeyframeRotation = pose.rotation;
        }
    }

    void Start()
    {
        //StartCoroutine(writeMatrix());
    }

    IEnumerator writeMatrix()
    {
        yield return new WaitForSeconds(3f);
        Matrix4x4 reprojMatrix = Shader.GetGlobalMatrix("_EnvironmentDepthReprojectionMatrices");
        debugText.text = "Matrix saved: " + reprojMatrix.ToString();
    }
    void CaptureKeyframe()
    {
        // Pose PCA — usate per la riproiezione RGB (world -> spazio colore),
        // NON per l'unprojection della depth (il sensore depth ha pose propria, vedi sotto).
        var pose = passthroughCameraLeft.GetCameraPose();
        var rightPose = passthroughCameraRight.GetCameraPose();
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

        var depthTex = Shader.GetGlobalTexture("_EnvironmentDepthTexture");
        // Blit dei frame camera correnti sui render target
        Graphics.Blit(passthroughCameraLeft.GetTexture(), target);
        Graphics.Blit(passthroughCameraRight.GetTexture(), rightTarget);

        Debug.Log("----------------UNITY TIME WHEN SAVING DEPTH TEXTURE:" + System.DateTime.Now.ToString("HH:mm:ss:fff"));

        if (depthTex == null || !passthroughCameraLeft.IsPlaying) return;

        // NOTA: _EnvironmentDepthReprojectionMatrices/_EnvironmentDepthZBufferParams
        // sono calibrate per la eye render camera (occlusion in-headset),
        // NON per la PCA. Tenute solo per debug/riferimento — non usare
        // reprojMatrix per allineare la depth al frame RGB della PCA; quella
        // registrazione va ricostruita server-side da FOV+pose (vedi sotto).
        Matrix4x4[] reproj = Shader.GetGlobalMatrixArray("_EnvironmentDepthReprojectionMatrices");
        Vector4 zParams = Shader.GetGlobalVector("_EnvironmentDepthZBufferParams");

        if (reproj == null || reproj.Length == 0) return;

        Matrix4x4 reprojMatrix = reproj[0]; // matrice di reprojection eye sinistro (solo debug/riferimento)

        //debugText.text = "Matrix saved: " + reprojMatrix.ToString();

        // Inizializza i render target RGB dalle dimensioni della camera texture
        
        Debug.Log("----------------PCA TIME WHEN SAVING RGB TEXTURE:" + passthroughCameraLeft.Timestamp.ToString("HH:mm:ss:fff"));
        Debug.Log("----------------UNITY TIME WHEN SAVING RGB TEXTURE:" + System.DateTime.Now.ToString("HH:mm:ss:fff"));

        // Salva i frame RGB
        Texture2D rgb = SaveFrame(target);
        Texture2D rgbRight = SaveFrame(rightTarget);
        Texture2D rawDepth = SaveDepthFrameRaw(depthTex);

        // Salva la depth registrata (riproiettata nello spazio della camera RGB)
        var intrinsics = passthroughCameraLeft.Intrinsics;
        var rightIntrinsics = passthroughCameraRight.Intrinsics;

        var depthFrameDesc = Utils.GetEnvironmentDepthFrameDesc(0);

        string depthDebug = $"valid={depthFrameDesc.isValid} t={depthFrameDesc.predictedDisplayTime} pos={depthFrameDesc.createPoseLocation} timestamp = {Time.realtimeSinceStartupAsDouble}";
        debugText.text = depthDebug;

        // Pose propria del sensore depth al momento della creazione del frame —
        // NECESSARIA per l'unprojection depth->world corretta. Non riusare la
        // pose PCA: depth camera e PCA sono sensori/extrinsics fisici diversi.
        // CreatePoseLocation restituisce la posizione; si assume che la
        // rotazione sia esposta allo stesso modo — verificare firma esatta
        // sulla versione SDK in uso.
        Vector3 depthCamPosition = depthFrameDesc.createPoseLocation;
        Vector4 depthCamRotation = depthFrameDesc.createPoseRotation;

        var kf = new CapturedKeyframe
        {
            rgb = rgb,
            rgbRight = rgbRight,
            rawDepth = rawDepth,
            position = pose.position,
            rotation = pose.rotation,
            RightCamPosition = rightPose.position,
            RightCamRotation = rightPose.rotation,
            timestamp = passthroughCameraLeft.Timestamp,
            intrinsics = intrinsics,
            rightIntrinsics = rightIntrinsics,
            reprojectionMatrix = reprojMatrix,
            zBufferParams = zParams,
            depthResolution = new Vector2(depthTex.width, depthTex.height),
            depthData = depthFrameDesc,
            depthCamPosition = depthCamPosition,
            depthCamRotation = depthCamRotation
        };

        Debug.Log("----------------PCA TIME WHEN SAVING KEYFRAME:" + passthroughCameraLeft.Timestamp.ToString("HH:mm:ss:fff"));
        Debug.Log("----------------UNITY TIME WHEN SAVING KEYFRAME:" + System.DateTime.Now.ToString("HH:mm:ss:fff"));
        SaveKeyframeToDisk(kf, _keyframeCount++);

        // Distrugge subito le texture — sono già su disco, nessun motivo di tenerle in RAM.
        Destroy(kf.rgb);
        Destroy(kf.rgbRight);
        Destroy(kf.rawDepth);

        Debug.Log($"Keyframe captured: {_keyframeCount} | pos: {pose.position} | depth registered at {target.width}x{target.height}");
    }

    void SaveKeyframeToDisk(CapturedKeyframe kf, int index)
    {
        string dir = $"{Application.persistentDataPath}/keyframes/{index}";
        System.IO.Directory.CreateDirectory(dir);

        // RGB
        byte[] rgbBytes = kf.rgb.EncodeToPNG();
        System.IO.File.WriteAllBytes($"{dir}/LeftRGB.png", rgbBytes);

        // RGB destro
        byte[] rgbRightBytes = kf.rgbRight.EncodeToPNG();
        System.IO.File.WriteAllBytes($"{dir}/RightRGB.png", rgbRightBytes);

        // Depth raw, non registrata, risoluzione nativa depth camera, float EXR.
        byte[] rawDepthBytes = kf.rawDepth.EncodeToEXR();
        System.IO.File.WriteAllBytes($"{dir}/rawDepth.exr", rawDepthBytes);

        // Pose PCA sinistra/destra (world/tracking space) — usate per
        // riproiettare i punti depth unprojected nello spazio camera RGB server-side.
        string pose = JsonUtility.ToJson(new PoseData
        {
            px = kf.position.x, py = kf.position.y, pz = kf.position.z,
            rx = kf.rotation.x, ry = kf.rotation.y, rz = kf.rotation.z, rw = kf.rotation.w,
            timestamp = kf.timestamp.ToString("HH:mm:ss:fff")
        });

        string rightPose = JsonUtility.ToJson(new PoseData
        {
            px = kf.RightCamPosition.x, py = kf.RightCamPosition.y, pz = kf.RightCamPosition.z,
            rx = kf.RightCamRotation.x, ry = kf.RightCamRotation.y, rz = kf.RightCamRotation.z, rw = kf.RightCamRotation.w,
            timestamp = kf.timestamp.ToString("HH:mm:ss:fff")
        });

        // Pose della depth camera — usata per l'unprojection depth->world server-side.
        // Posizione/rotazione da EnvironmentDepthFrameDesc, NON dalla PCA.
        string depthCamPose = JsonUtility.ToJson(new DepthCamPoseData
        {
            px = kf.depthCamPosition.x, py = kf.depthCamPosition.y, pz = kf.depthCamPosition.z,
            rx = kf.depthCamRotation.x, ry = kf.depthCamRotation.y, rz = kf.depthCamRotation.z, rw = kf.depthCamRotation.w
        });

        // Intrinseci RGB
        string RGBIntrinsics = JsonUtility.ToJson(new IntrinsicsData
        {
            FocalLength = kf.intrinsics.FocalLength,
            PrincipalPoint = kf.intrinsics.PrincipalPoint,
            SensorResolution = kf.intrinsics.SensorResolution
        });

        string RGBRightInstrinsics = JsonUtility.ToJson(new IntrinsicsData
        {
            FocalLength = kf.rightIntrinsics.FocalLength,
            PrincipalPoint = kf.rightIntrinsics.PrincipalPoint,
            SensorResolution = kf.rightIntrinsics.SensorResolution
        });

        // Matrice di reprojection della eye camera — tenuta solo per debug/riferimento.
        // NON valida per allineare la depth al frame RGB della PCA (vedi nota in CaptureKeyframe).
        string reproj = JsonUtility.ToJson(new Matrix4x4Data(kf.reprojectionMatrix));

        // zBufferParams (per la linearizzazione offline dei valori di depth raw)
        string zbuf = JsonUtility.ToJson(new ZBufferParamsData
        {
            x = kf.zBufferParams.x,
            y = kf.zBufferParams.y,
            z = kf.zBufferParams.z,
            w = kf.zBufferParams.w
        });

        // Risoluzione nativa della depth texture + FOV — usate server-side per
        // costruire la matrice di proiezione della depth camera (step di unprojection).
        string depthMeta = JsonUtility.ToJson(new DepthMetaData
        {
            width = kf.depthResolution.x,
            height = kf.depthResolution.y,
            FOVleft = kf.depthData.fovLeftAngle,
            FOVright = kf.depthData.fovRightAngle,
            nearZ = kf.depthData.nearZ,
            farZ = kf.depthData.farZ,
            minDepth = kf.depthData.minDepth,
            maxDepth = kf.depthData.maxDepth,
            createTime = kf.depthData.createTime
        });

        var LeftPose = passthroughCameraLeft.GetCameraPose();
        var rightCamPose = passthroughCameraRight.GetCameraPose();

        float CamDistance = Vector3.Distance(LeftPose.position, rightCamPose.position);

        System.IO.File.WriteAllText($"{dir}/LeftCamPose.json", pose);
        System.IO.File.WriteAllText($"{dir}/RightCamPose.json", rightPose);
        System.IO.File.WriteAllText($"{dir}/DepthCamPose.json", depthCamPose);
        System.IO.File.WriteAllText($"{dir}/LeftIntrinsics.json", RGBIntrinsics);
        System.IO.File.WriteAllText($"{dir}/RightIntrinsics.json", RGBRightInstrinsics);

        System.IO.File.WriteAllText($"{dir}/PassthroughCamDistance.txt",
        CamDistance.ToString(System.Globalization.CultureInfo.InvariantCulture));

        System.IO.File.WriteAllText($"{dir}/reprojection.json", reproj);
        System.IO.File.WriteAllText($"{dir}/zbuffer_params.json", zbuf);
        System.IO.File.WriteAllText($"{dir}/depth_meta.json", depthMeta);
    }

    // Legge un render target RGBA in una Texture2D CPU-side per encoding/salvataggio.
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
    /// Salva la depth raw a risoluzione nativa della depth camera (non
    /// registrata, non allineata all'RGB). Registrazione/allineamento
    /// rimandati al server usando FOV depth + pose depth camera + intrinseci/pose PCA.
    /// </summary>
    Texture2D SaveDepthFrameRaw(Texture depthTexArray)
    {
        // Deve passare attraverso lo shader di array-sampling (DepthRawCopy):
        // un Graphics.Blit semplice usa lo shader sampler2D di default e non
        // può leggere una slice di Texture2DArray, dando un risultato
        // piatto/uniforme ("monocolore").
        if (rawDepthMaterial == null)
        {
            Debug.LogError("rawDepthMaterial (Custom/DepthRawCopy) is not assigned — rawDepth.exr would be monochrome.");
            return null;
        }

        // Usa un target float a 4 canali (come il path di preview BGRA32 funzionante):
        // render target RFloat a canale singolo + ReadPixels sono inaffidabili su Quest.
        RenderTexture rt = new RenderTexture(depthTexArray.width, depthTexArray.height, 0, RenderTextureFormat.ARGBFloat);
        rt.Create();
        Graphics.Blit(depthTexArray, rt, rawDepthMaterial);

        Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false);
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = null;

        Destroy(rt);
        return tex;
    }
}

// Bundle in memoria per un singolo keyframe catturato, prima della serializzazione su disco.
[System.Serializable]
public struct CapturedKeyframe
{
    public Texture2D rgb;
    public Texture2D rgbRight;
    public Texture2D rawDepth;
    public Vector3 position;          // posizione camera PCA sinistra
    public Vector3 RightCamPosition;  // posizione camera PCA destra
    public Quaternion rotation;       // rotazione camera PCA sinistra
    public Quaternion RightCamRotation;
    public System.DateTime timestamp;
    public PassthroughCameraAccess.CameraIntrinsics intrinsics;
    public PassthroughCameraAccess.CameraIntrinsics rightIntrinsics;
    public Matrix4x4 reprojectionMatrix; // reprojection eye camera, solo debug
    public Vector4 zBufferParams;
    public Vector2 depthResolution;
    public EnvironmentDepthFrameDesc depthData;
    public Vector3 depthCamPosition;  // pose del sensore depth (sorgente per unprojection)
    public Vector4 depthCamRotation;  // quaternion (x,y,z,w)
}

[System.Serializable]
struct PoseData
{
    public float px, py, pz;
    public float rx, ry, rz, rw;
    public string timestamp;
}

[System.Serializable]
struct DepthCamPoseData
{
    public float px, py, pz;
    public float rx, ry, rz, rw;
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
struct Matrix4x4Data
{
    public float m00, m01, m02, m03;
    public float m10, m11, m12, m13;
    public float m20, m21, m22, m23;
    public float m30, m31, m32, m33;

    public Matrix4x4Data(Matrix4x4 m)
    {
        m00 = m.m00; m01 = m.m01; m02 = m.m02; m03 = m.m03;
        m10 = m.m10; m11 = m.m11; m12 = m.m12; m13 = m.m13;
        m20 = m.m20; m21 = m.m21; m22 = m.m22; m23 = m.m23;
        m30 = m.m30; m31 = m.m31; m32 = m.m32; m33 = m.m33;
    }
}

[System.Serializable]
struct DepthMetaData
{
    public float width, height;
    public float FOVleft, FOVright;
    public float nearZ, farZ;       // near/far della proiezione depth camera — necessari per la matrice di proiezione
    public float minDepth, maxDepth; // range di confidenza del sensore, utile per filtrare la depth raw
    public double createTime;        // istante di cattura reale del sensore, per sync con timestamp RGB
}