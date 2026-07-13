using System.Collections;
using System.Collections.Generic;
using Meta.XR;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Meta.XR.EnvironmentDepth;
using Unity.XR.Oculus;
using static Unity.XR.Oculus.Utils;
using System.Runtime.CompilerServices;


public class KeyFrameManager : MonoBehaviour
{
    private Vector3 _lastKeyframePosition;
    private Quaternion _lastKeyframeRotation;

    private Quaternion _prevHeadRotation;
    private bool _hasPrevHeadRotation = false;
    private bool _hasPrevHeadPosition = false;

    private Vector3 _lastHeadPosition;
    private Quaternion _lastHeadRotation;

    [SerializeField] float translationThreshold = 0.1f; // 10cm
    [SerializeField] float rotationThreshold = 5f; // 5 gradi
    [SerializeField] float maxHeadAngularSpeed = 30f;
    [SerializeField] float maxHeadTranslationSpeed = 0.5f; // metri/sec

    // Accessor alla Passthrough Camera API — sensori fisicamente separati
    // dalla depth camera, ciascuno con pose/intrinseci propri.
    [SerializeField] PassthroughCameraAccess passthroughCameraLeft;
    [SerializeField] PassthroughCameraAccess passthroughCameraRight;
    [SerializeField] Camera leftCamera;
    [SerializeField] Material rawDepthMaterial;         // Shader DepthRawCopy: estrae la slice 0 del Texture2DArray senza modifiche
    [SerializeField] Material alignedDepthMaterial;     // Allinea la depth sul frame RGB della PCA
    [SerializeField] Material depthColorMaterial;       // Shader DepthColorReprojection: depth -> 3D -> colore RGB (point cloud colorato in layout depth)
    [SerializeField] EnvironmentDepthManager environmentDepthManager; // Gate della cattura su IsDepthAvailable
    [SerializeField] Text debugText;

    private RenderTexture target;
    private RenderTexture rightTarget;

    private int _keyframeCount = 0;

    private bool _firstKeyframeCaptured = false;

    private uint _lastCapturedDepthTexId = uint.MaxValue;

    private uint _lastSeenDepthTexId = uint.MaxValue;
    // Buffer in memoria dei record; viene scritto su disco periodicamente e alla chiusura.
    private readonly KeyframeLog _log = new KeyframeLog();
    private double _lastLogFlushTime = 0;

    void Awake()
    {
        // Risolve automaticamente il depth manager se non wired in Inspector,
        // altrimenti il gate IsDepthAvailable sotto blocca ogni cattura.
        if (environmentDepthManager == null)
            environmentDepthManager = FindFirstObjectByType<EnvironmentDepthManager>();
    }

    void OnEnable()
    {
        // Ci agganciamo al render loop invece che a Update: vedi nota in testa.
        Application.onBeforeRender += CaptureKeyframe;
    }

    void OnDisable()
    {
        Application.onBeforeRender -= CaptureKeyframe;
        FlushLog(); // salva su disco gli ultimi record non ancora scritti
    }

    // Su Quest l'app viene messa in pausa quando togli il visore o esci: salviamo
    // il log in quel momento, così non perdiamo i record accumulati in memoria.
    void OnApplicationPause(bool paused)
    {
        if (paused) FlushLog();
    }

    // Ordine 100 > 0 del manager Meta -> giriamo SEMPRE dopo che i global depth
    // sono stati aggiornati col frame corrente.
    [BeforeRenderOrder(100)]
    void CaptureKeyframe()
    {
        Debug.Log("-----------CaptureKeyframe() called at time: " + System.DateTime.Now.ToString("HH:mm:ss.fff") + "-----------");
        // === CONTROLLI DI VALIDITÀ ===
        // Se la depth non è disponibile o la camera PCA non sta girando, esci subito.
        if (environmentDepthManager == null || !environmentDepthManager.IsDepthAvailable) return;
        if (passthroughCameraLeft == null || !passthroughCameraLeft.IsPlaying) return;

        // Pose della testa (camera PCA sinistra) in questo istante.
        var pose = passthroughCameraLeft.GetCameraPose();

        // === STIMA VELOCITÀ DELLA TESTA ===
        float headAngularSpeed = 0f; // gradi/sec
        if (_hasPrevHeadRotation)
        {
            float dt = Time.unscaledDeltaTime;
            if (dt > 0f)
                headAngularSpeed = Quaternion.Angle(pose.rotation, _prevHeadRotation) / dt;
        }
        _prevHeadRotation = pose.rotation;
        _hasPrevHeadRotation = true;

        // === Stima Traslazione testa ===

        float headTranslation = 0f; // metri/sec
        if (_hasPrevHeadPosition)
        {
            float dt = Time.unscaledDeltaTime;
            if (dt > 0f)
                headTranslation = Vector3.Distance(pose.position, _lastHeadPosition) / dt;
        }
        _lastHeadPosition = pose.position;
        _hasPrevHeadPosition = true;

        // === Controllo 1: FRAME DEPTH NUOVO ===

        uint depthTexId = 0;
        if (!Utils.GetEnvironmentDepthTextureId(ref depthTexId)) return;

        // Un frame depth è "nuovo" se l'id è cambiato rispetto al tick precedente.
        bool isNewDepthFrame = depthTexId != _lastSeenDepthTexId;
        _lastSeenDepthTexId = depthTexId;
        if (depthTexId == _lastCapturedDepthTexId) return;

        // === Controllo 2: TESTA TROPPO VELOCE ===
      
        if (headAngularSpeed > maxHeadAngularSpeed)
        {
            if (isNewDepthFrame)
                LogEvent("skip_head_fast", depthTexId, headAngularSpeed, pose, -1, 0f, 0f);
            return;
        }

        // === Controllo 2b: TESTA TROPPO VELOCE (TRASLAZIONE) ===

        if (headTranslation > maxHeadTranslationSpeed)
        {
            if (isNewDepthFrame)
                LogEvent("skip_head_fast_translation", depthTexId, headAngularSpeed, pose, -1, 0f, 0f);
            return;
        }

        // === PRIMO KEYFRAME ===
        // Appena la depth diventa disponibile cattura il primo keyframe

        if (!_firstKeyframeCaptured)
        {
            DoCaptureKeyframe(pose, depthTexId);
            _firstKeyframeCaptured = true;
            _lastKeyframePosition = pose.position;
            _lastKeyframeRotation = pose.rotation; 
            LogEvent("captured", depthTexId, headAngularSpeed, pose, _keyframeCount - 1, 0f, 0f);
            return;
        }

        // === GATE 3: SPAZIATURA TRA KEYFRAME ===
        // Cattura solo se ci siamo spostati/ruotati abbastanza rispetto all'ULTIMO
        // keyframe salvato.

        float translation = Vector3.Distance(pose.position, _lastKeyframePosition);
        float rotation = Quaternion.Angle(pose.rotation, _lastKeyframeRotation);
        if (translation <= translationThreshold && rotation <= rotationThreshold)
        {
            if (isNewDepthFrame)
                LogEvent("skip_spacing", depthTexId, headAngularSpeed, pose, -1, translation, rotation);
            return;
        }

        // Tutti i gate superati: catturiamo il keyframe.
        DoCaptureKeyframe(pose, depthTexId);
        _lastKeyframePosition = pose.position;
        _lastKeyframeRotation = pose.rotation;
        LogEvent("captured", depthTexId, headAngularSpeed, pose, _keyframeCount - 1, translation, rotation);
    }

    // Aggiunge un record al buffer di log e lo scrive su disco a intervalli regolari
    // (oltre che a ogni cattura). Il campo "outcome" dice cosa è successo:
    //   "captured"       -> keyframe salvato su disco (keyframeIndex valido)
    //   "skip_head_fast" -> scartato dal Gate 2 (testa troppo veloce)
    //   "skip_spacing"   -> scartato dal Gate 3 (troppo vicino all'ultimo keyframe)
    void LogEvent(string outcome, uint depthTexId, float headAngularSpeed, Pose headPose,
                  int keyframeIndex, float translationFromLast, float rotationFromLast)
    {
        var now = System.DateTime.UtcNow;
        // "Età" del frame RGB: quanti ms sono passati da quando la camera lo ha
        // esposto a adesso. Se è alta e la testa si muove, è un indizio di skew
        // temporale tra lo stream RGB e quello depth.
        float rgbAgeMs = (float)(now - passthroughCameraLeft.Timestamp).TotalMilliseconds;

        // Confronto diretto tra la pose della camera RGB (al suo istante di cattura) e
        // la pose della camera DEPTH (estratta dalla reproj matrix = istante di cattura
        // della depth). skewAngleDeg è il disallineamento rotazionale reale in gradi.
        bool skewValid = TryComputeDepthRgbSkew(headPose, out Vector3 rgbFwd, out Vector3 depthFwd,
                                                out Vector3 depthEye, out float skewAngleDeg, out float posDiffM);

        _log.entries.Add(new KeyframeLogEntry
        {
            frame = Time.frameCount,
            appTime = Time.realtimeSinceStartupAsDouble,
            systemTimeUtc = now.ToString("HH:mm:ss.fff"),
            outcome = outcome,
            keyframeIndex = keyframeIndex,
            depthTexId = depthTexId,
            headAngularSpeed = headAngularSpeed,
            rgbTimestampUtc = passthroughCameraLeft.Timestamp.ToString("HH:mm:ss.fff"),
            rgbAgeMs = rgbAgeMs,
            headPos = headPose.position,
            headRot = new Vector4(headPose.rotation.x, headPose.rotation.y,
                                  headPose.rotation.z, headPose.rotation.w),
            translationFromLast = translationFromLast,
            rotationFromLast = rotationFromLast,
            skewValid = skewValid,
            rgbForward = rgbFwd,
            depthForward = depthFwd,
            depthEyePos = depthEye,
            skewAngleDeg = skewAngleDeg,
            posDiffM = posDiffM,
        });

        // Flush periodico (ogni 2s) e sempre dopo una cattura: così un eventuale
        // crash/chiusura fa perdere al massimo pochi record.
        if (outcome == "captured" || Time.realtimeSinceStartupAsDouble - _lastLogFlushTime > 2.0)
            FlushLog();
    }

    // Scrive l'intero buffer di log come JSON leggibile su disco, accanto ai keyframe.
    void FlushLog()
    {
        if (_log.entries.Count == 0) return;
        string dir = $"{Application.persistentDataPath}/keyframes";
        System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText($"{dir}/capture_log.json", JsonUtility.ToJson(_log, true));
        _lastLogFlushTime = Time.realtimeSinceStartupAsDouble;
    }

    // Confronta la pose della camera RGB con la pose della camera DEPTH nel momento
    // in cui ciascun frame è stato catturato.
    //
    // La pose RGB arriva da rgbPose (già calcolata al timestamp del frame RGB).
    // La pose DEPTH la ricaviamo dalla reprojection matrix globale della depth
    // (_EnvironmentDepthReprojectionMatrices[0]): quella matrice mappa world -> clip
    // della camera depth ed è costruita da Meta con la createPose, cioè la posizione
    // della testa NEL MOMENTO in cui il sensore ha catturato la depth (non adesso).
    //
    // Invertendo la matrice (clip -> world) ricostruiamo geometricamente dove guardava
    // e dov'era la camera depth in quell'istante. L'angolo tra le due direzioni di
    // sguardo (skewAngleDeg) è il disallineamento reale: se è grande, depth e RGB
    // stanno inquadrando due parti diverse della stanza.
    bool TryComputeDepthRgbSkew(Pose rgbPose, out Vector3 rgbFwd, out Vector3 depthFwd,
                                out Vector3 depthEye, out float skewAngleDeg, out float posDiffM)
    {
        rgbFwd = rgbPose.rotation * Vector3.forward;
        depthFwd = Vector3.zero;
        depthEye = Vector3.zero;
        skewAngleDeg = 0f;
        posDiffM = 0f;

        var reproj = Shader.GetGlobalMatrixArray("_EnvironmentDepthReprojectionMatrices");
        if (reproj == null || reproj.Length == 0) return false;

        // m mappa world -> clip della camera depth (proj * view, con la createPose
        // della depth già dentro). Estraiamo i piani del frustum in world space
        // sommando/sottraendo le righe (Gribb-Hartmann): NIENTE inversa, NIENTE
        // divisione per w — così evitiamo i NaN del metodo precedente.
        Matrix4x4 m = reproj[0];
        Vector4 r0 = m.GetRow(0);
        Vector4 r1 = m.GetRow(1);
        Vector4 r3 = m.GetRow(3);

        // Piani laterali del frustum (passano TUTTI per il centro ottico della camera).
        Vector4 left   = r3 + r0;
        Vector4 right  = r3 - r0;
        Vector4 bottom = r3 + r1;

        // Centro ottico della depth = intersezione dei tre piani laterali.
        // Formula standard di intersezione di 3 piani (n_i · X + d_i = 0).
        Vector3 nL = new Vector3(left.x, left.y, left.z);
        Vector3 nR = new Vector3(right.x, right.y, right.z);
        Vector3 nB = new Vector3(bottom.x, bottom.y, bottom.z);
        Vector3 cRB = Vector3.Cross(nR, nB);
        float det = Vector3.Dot(nL, cRB);
        if (Mathf.Abs(det) < 1e-12f) return false;
        Vector3 cBL = Vector3.Cross(nB, nL);
        Vector3 cLR = Vector3.Cross(nL, nR);
        depthEye = (-left.w * cRB - right.w * cBL - bottom.w * cLR) / det;

        // Direzione di sguardo: la riga w della matrice (r3) punta lungo l'asse ottico,
        // perché left+right = bottom+top = 2*r3 (le componenti laterali si annullano).
        // È indipendente dalla convenzione dello z-clip.
        Vector3 fwd = new Vector3(r3.x, r3.y, r3.z);
        if (fwd.sqrMagnitude < 1e-12f) return false;
        fwd.Normalize();
        // Depth e RGB sono co-locate sulla stessa testa: il forward vero è quasi
        // parallelo a quello RGB. Il segno di r3 dipende dalla convenzione, quindi
        // scegliamo il verso più vicino all'RGB (un vero disallineamento resta piccolo,
        // non arriva mai a ribaltare la scelta).
        if (Vector3.Dot(fwd, rgbFwd) < 0f) fwd = -fwd;
        depthFwd = fwd;

        skewAngleDeg = Vector3.Angle(depthFwd, rgbFwd);
        posDiffM = Vector3.Distance(depthEye, rgbPose.position);
        return true;
    }

    void DoCaptureKeyframe(Pose pose, uint depthTexId)
    {
        // Pose PCA — usate per la riproiezione RGB (world -> spazio colore),
        // NON per l'unprojection della depth (il sensore depth ha pose propria,
        // già bakata nella reprojection matrix, vedi sotto).
        var rightPose = passthroughCameraRight.GetCameraPose();

        // TUTTI i dati depth dai GLOBAL dello shader (nessuna chiamata a Utils
        // per la desc): sono scritti atomicamente dal manager Meta nello stesso
        // OnBeforeRender, quindi mutuamente coerenti col frame corrente.
        var depthTex = Shader.GetGlobalTexture("_EnvironmentDepthTexture");
        if (depthTex == null) return;

        Matrix4x4[] reproj = Shader.GetGlobalMatrixArray("_EnvironmentDepthReprojectionMatrices");
        Vector4 zParams = Shader.GetGlobalVector("_EnvironmentDepthZBufferParams");
        if (reproj == null || reproj.Length == 0) return;

        // Matrice world -> clip della DEPTH camera (fov del sensore + createPose
        // del sensore già inclusi nel blocco proj*view). La sua inversa è
        // l'unprojection depth->world autorevole per il server. NON è la eye
        // camera: è calibrata sul sensore depth.
        Matrix4x4 depthWorldToClip = reproj[0];

        // Inizializza i render target RGB dalle dimensioni della camera texture
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

        //Debug.Log("----------------PCA TIME WHEN SAVING RGB TEXTURE:" + passthroughCameraLeft.Timestamp.ToString("HH:mm:ss:fff"));
        // Blit dei frame camera correnti sui render target
        Graphics.Blit(passthroughCameraLeft.GetTexture(), target);
        Graphics.Blit(passthroughCameraRight.GetTexture(), rightTarget);
        Debug.Log("----------------SYSTEM TIME WHEN SAVING RGB TEXTURE:" + System.DateTime.Now.ToString("HH:mm:ss:fff"));
        Debug.Log("----------------PCA TIME WHEN TEXTURE WAS CREATED:" + passthroughCameraLeft.Timestamp.ToString("HH:mm:ss:fff"));

        // Salva i frame RGB + depth (raw, allineata, colorata)
        Texture2D rgb = SaveFrame(target);
        Texture2D rgbRight = SaveFrame(rightTarget);
        Texture2D rawDepth = SaveDepthFrameRaw(depthTex);
        // Risoluzione dell'immagine RGB corrente (può differire dal sensore pieno):
        // serve per calcolare il crop di aspect-ratio come fa il SDK.
        Vector2 currentRes = passthroughCameraLeft.CurrentResolution;
        Texture2D alignedDepth = SaveAlignedDepthFrame(
            depthTex, depthWorldToClip,
            pose.position, pose.rotation,
            passthroughCameraLeft.Intrinsics, zParams, currentRes);
        Texture2D depthColored = SaveDepthColored(
            depthTex, target, depthWorldToClip,
            pose.position, pose.rotation, passthroughCameraLeft.Intrinsics, currentRes);

        var intrinsics = passthroughCameraLeft.Intrinsics;
        var rightIntrinsics = passthroughCameraRight.Intrinsics;

        if (debugText != null)
            debugText.text = $"kf={_keyframeCount} depthTexId={depthTexId} t={Time.realtimeSinceStartupAsDouble:F3}";

        var kf = new CapturedKeyframe
        {
            rgb = rgb,
            rgbRight = rgbRight,
            rawDepth = rawDepth,
            alignedDepth = alignedDepth,
            depthColored = depthColored,
            position = pose.position,
            rotation = pose.rotation,
            RightCamPosition = rightPose.position,
            RightCamRotation = rightPose.rotation,
            timestamp = passthroughCameraLeft.Timestamp,
            intrinsics = intrinsics,
            rightIntrinsics = rightIntrinsics,
            reprojectionMatrix = depthWorldToClip,
            zBufferParams = zParams,
            depthResolution = new Vector2(depthTex.width, depthTex.height)
        };

        //Debug.Log("----------------PCA TIME WHEN SAVING KEYFRAME:" + passthroughCameraLeft.Timestamp.ToString("HH:mm:ss:fff"));
        //Debug.Log("----------------UNITY TIME WHEN SAVING KEYFRAME:" + System.DateTime.Now.ToString("HH:mm:ss:fff"));
        SaveKeyframeToDisk(kf, _keyframeCount++);

        // Marca il frame depth come consumato: il prossimo keyframe userà un id diverso.
        _lastCapturedDepthTexId = depthTexId;

        // Distrugge subito le texture — sono già su disco, nessun motivo di tenerle in RAM.
        Destroy(kf.rgb);
        Destroy(kf.rgbRight);
        Destroy(kf.rawDepth);
        Destroy(kf.alignedDepth);
        Destroy(kf.depthColored);
        Debug.Log($"Keyframe captured: {_keyframeCount} | pos: {pose.position} | depthTexId: {depthTexId}");
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

        // Depth allineata, registrata, risoluzione frame RGB, float EXR.
        byte[] alignedDepthBytes = kf.alignedDepth.EncodeToEXR();
        System.IO.File.WriteAllBytes($"{dir}/alignedDepth.exr", alignedDepthBytes);

        // Point cloud colorato (depth -> 3D -> colore RGB), risoluzione depth, PNG.
        if (kf.depthColored != null)
        {
            byte[] coloredBytes = kf.depthColored.EncodeToPNG();
            System.IO.File.WriteAllBytes($"{dir}/Colored.png", coloredBytes);
        }

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

        // Matrice di reprojection della DEPTH camera (world -> clip): fov + pose
        // del sensore depth già bakati. Il server unprojetta la depth con la sua
        // inversa (salvata sotto). Sostituisce sia la vecchia reproj "eye" sia
        // DepthCamPose.json (la pose del sensore è dentro questa matrice).
        string reproj = JsonUtility.ToJson(new Matrix4x4Data(kf.reprojectionMatrix));
        string reprojInverse = JsonUtility.ToJson(new Matrix4x4Data(kf.reprojectionMatrix.inverse));

        // zBufferParams (per la linearizzazione offline dei valori di depth raw)
        string zbuf = JsonUtility.ToJson(new ZBufferParamsData
        {
            x = kf.zBufferParams.x,
            y = kf.zBufferParams.y,
            z = kf.zBufferParams.z,
            w = kf.zBufferParams.w
        });

        // Risoluzione nativa della depth texture. FOV/near-far non servono più
        // separati: sono codificati nella reprojection matrix qui sopra.
        string depthMeta = JsonUtility.ToJson(new DepthMetaData
        {
            width = kf.depthResolution.x,
            height = kf.depthResolution.y
        });

        var LeftPose = passthroughCameraLeft.GetCameraPose();
        var rightCamPose = passthroughCameraRight.GetCameraPose();

        float CamDistance = Vector3.Distance(LeftPose.position, rightCamPose.position);

        System.IO.File.WriteAllText($"{dir}/LeftCamPose.json", pose);
        System.IO.File.WriteAllText($"{dir}/RightCamPose.json", rightPose);
        System.IO.File.WriteAllText($"{dir}/LeftIntrinsics.json", RGBIntrinsics);
        System.IO.File.WriteAllText($"{dir}/RightIntrinsics.json", RGBRightInstrinsics);

        System.IO.File.WriteAllText($"{dir}/PassthroughCamDistance.txt",
        CamDistance.ToString(System.Globalization.CultureInfo.InvariantCulture));

        System.IO.File.WriteAllText($"{dir}/reprojection.json", reproj);
        System.IO.File.WriteAllText($"{dir}/reprojection_inverse.json", reprojInverse);
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
    /// rimandati al server usando la reprojection matrix + pose/intrinseci PCA.
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

    // Replica di CalcSensorCropRegion (metodo privato del SDK PassthroughCameraAccess):
    // l'immagine RGB corrente è un ritaglio centrato del sensore pieno per adattarne
    // l'aspect ratio (es. sensore 1280x1280, immagine 1280x960 -> crop (0,160,1280,960)).
    // Restituisce (cropX, cropY, cropWidth, cropHeight) in pixel del sensore: mappa la
    // viewport [0,1] dell'immagine nelle coordinate pixel del sensore, coerente con
    // gli intrinseci (FocalLength/PrincipalPoint sono nel frame del sensore pieno).
    static Vector4 CalcSensorCropRegion(Vector2 sensorResolution, Vector2 currentResolution)
    {
        Vector2 scaleFactor = new Vector2(currentResolution.x / sensorResolution.x,
                                          currentResolution.y / sensorResolution.y);
        scaleFactor /= Mathf.Max(scaleFactor.x, scaleFactor.y);
        return new Vector4(
            sensorResolution.x * (1f - scaleFactor.x) * 0.5f,
            sensorResolution.y * (1f - scaleFactor.y) * 0.5f,
            sensorResolution.x * scaleFactor.x,
            sensorResolution.y * scaleFactor.y);
    }

    Texture2D SaveAlignedDepthFrame(Texture depthTexArray, Matrix4x4 reprojMatrix,
    Vector3 rgbPos, Quaternion rgbRot, PassthroughCameraAccess.CameraIntrinsics intr, Vector4 zParams,
    Vector2 currentResolution)
    {
        if (alignedDepthMaterial == null) { Debug.LogError("alignedDepthMaterial not assigned"); return null; }

        alignedDepthMaterial.SetMatrix("_ReprojMatrix", reprojMatrix);
        alignedDepthMaterial.SetVector("_RGBPosition", rgbPos);
        alignedDepthMaterial.SetMatrix("_RGBRotation", Matrix4x4.Rotate(rgbRot));
        alignedDepthMaterial.SetVector("_FocalLength", new Vector4(intr.FocalLength.x, intr.FocalLength.y));
        alignedDepthMaterial.SetVector("_PrincipalPoint", new Vector4(intr.PrincipalPoint.x, intr.PrincipalPoint.y));
        alignedDepthMaterial.SetVector("_EnvironmentDepthZBufferParams", zParams);

        // Crop di aspect-ratio del sensore (come CalcSensorCropRegion del SDK): mappa
        // la viewport [0,1] dell'immagine RGB nelle coordinate pixel del sensore pieno.
        // Passare (0,0,sensor) darebbe un errore di scala verticale che disallinea la
        // depth ai bordi (0 al centro, massimo in alto/basso).
        alignedDepthMaterial.SetVector("_CropRegion",
            CalcSensorCropRegion(intr.SensorResolution, currentResolution));

        // Output alla risoluzione dell'immagine RGB corrente, così alignedDepth.exr
        // combacia pixel-per-pixel con LeftRGB.png (niente stretch di aspect-ratio).
        RenderTexture rt = new RenderTexture((int)currentResolution.x, (int)currentResolution.y, 0, RenderTextureFormat.ARGBFloat);
        rt.Create();
        Graphics.Blit(depthTexArray, rt, alignedDepthMaterial);
        Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false);
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = null;
        Destroy(rt);
        return tex;
    }

    /// <summary>
    /// Forward warp: depth -> 3D world -> colore RGB. Per ogni pixel della depth
    /// ricostruisce il punto 3D con l'inversa della reproj matrix, lo proietta nella
    /// camera RGB e ne campiona il colore. Output = point cloud colorato in layout
    /// depth (risoluzione depth camera). Usa lo shader Custom/DepthColorReprojection.
    /// </summary>
    Texture2D SaveDepthColored(Texture depthTexArray, Texture rgbTex, Matrix4x4 reprojMatrix,
        Vector3 rgbPos, Quaternion rgbRot, PassthroughCameraAccess.CameraIntrinsics intr,
        Vector2 currentResolution)
    {
        if (depthColorMaterial == null) { Debug.LogError("depthColorMaterial not assigned"); return null; }

        // Inversa: mappa il clip-space della depth -> world. Precalcolata qui (niente inverse in shader).
        depthColorMaterial.SetMatrix("_InvReprojMatrix", reprojMatrix.inverse);
        depthColorMaterial.SetVector("_RGBPosition", rgbPos);
        depthColorMaterial.SetMatrix("_RGBRotation", Matrix4x4.Rotate(rgbRot));
        depthColorMaterial.SetVector("_FocalLength", new Vector4(intr.FocalLength.x, intr.FocalLength.y));
        depthColorMaterial.SetVector("_PrincipalPoint", new Vector4(intr.PrincipalPoint.x, intr.PrincipalPoint.y));

        // Stesso crop di aspect-ratio del SDK: senza, le rgbUV campionate dal
        // point cloud colorato risultano disallineate al colore RGB reale.
        depthColorMaterial.SetVector("_CropRegion",
            CalcSensorCropRegion(intr.SensorResolution, currentResolution));
        depthColorMaterial.SetTexture("_RGBTex", rgbTex);

        RenderTexture rt = new RenderTexture(depthTexArray.width, depthTexArray.height, 0, RenderTextureFormat.ARGB32);
        rt.Create();
        Graphics.Blit(depthTexArray, rt, depthColorMaterial);
        Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
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
    public Texture2D alignedDepth;
    public Texture2D depthColored;    // point cloud colorato: depth -> 3D -> colore RGB
    public Vector3 position;          // posizione camera PCA sinistra
    public Vector3 RightCamPosition;  // posizione camera PCA destra
    public Quaternion rotation;       // rotazione camera PCA sinistra
    public Quaternion RightCamRotation;
    public System.DateTime timestamp;
    public PassthroughCameraAccess.CameraIntrinsics intrinsics;
    public PassthroughCameraAccess.CameraIntrinsics rightIntrinsics;
    public Matrix4x4 reprojectionMatrix; // depth camera world->clip (pose+fov sensore depth bakati)
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
}

// Un record per ogni evento "punto chiave" (frame depth nuovo: catturato o scartato).
[System.Serializable]
struct KeyframeLogEntry
{
    public int frame;                 // Time.frameCount — ordine di render
    public double appTime;            // secondi dall'avvio dell'app
    public string systemTimeUtc;      // ora di sistema (UTC) dell'evento
    public string outcome;            // captured | skip_head_fast | skip_spacing
    public int keyframeIndex;         // cartella keyframes/N se catturato, altrimenti -1
    public uint depthTexId;           // handle del buffer swapchain della depth
    public float headAngularSpeed;    // gradi/sec: quanto ruotava la testa nell'istante
    public string rgbTimestampUtc;    // timestamp del frame RGB della camera PCA
    public float rgbAgeMs;            // età del frame RGB (ms) rispetto all'istante dell'evento
    public Vector3 headPos;           // posizione testa (camera PCA sinistra)
    public Vector4 headRot;           // rotazione testa (quaternione x,y,z,w)
    public float translationFromLast; // spostamento dall'ultimo keyframe salvato (m)
    public float rotationFromLast;    // rotazione dall'ultimo keyframe salvato (gradi)

    // --- CONFRONTO POSE RGB vs DEPTH (skew reale) ---
    public bool skewValid;            // false se non è stato possibile estrarre la pose depth
    public Vector3 rgbForward;        // direzione di sguardo della camera RGB (world), al t di cattura RGB
    public Vector3 depthForward;      // direzione di sguardo della camera DEPTH (world), al t di cattura depth
    public Vector3 depthEyePos;       // posizione della camera depth (world), estratta dalla reproj matrix
    public float skewAngleDeg;        // ANGOLO tra le due direzioni di sguardo = disallineamento rotazionale
    public float posDiffM;            // distanza tra camera depth e camera RGB (m)
}

// Contenitore top-level: JsonUtility non serializza una List da sola, serve wrapparla.
[System.Serializable]
class KeyframeLog
{
    public List<KeyframeLogEntry> entries = new List<KeyframeLogEntry>();
}
