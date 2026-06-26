using System.Collections;
using System.Linq;
using Meta.XR;
using SimpleWebRTC;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Video;

public class VideoManager : MonoBehaviour
{
    [SerializeField] MonoBehaviour[] videoSources;
    VideoInterface[] sources => videoSources.Select(s => s as VideoInterface).ToArray();

    [SerializeField] int activeSourceIndex = 0;

    RenderTexture camRenderTexture;
    VideoInterface currentSource;
    WebRTCConnection _webRTCConnection;

    void OnEnable() => WebRTCConnection.OnRequestVideoTrack += CreateVideo;
    void OnDisable() => WebRTCConnection.OnRequestVideoTrack -= CreateVideo;

    void OnWebRTCConnected() => StartCoroutine(ApplyBitrateCap());

    bool videoActive;

    void Awake()
    {
        _webRTCConnection = GetComponentInParent<WebRTCConnection>() ?? FindAnyObjectByType<WebRTCConnection>();
        if (_webRTCConnection != null)
            _webRTCConnection.WebRTCConnected.AddListener(OnWebRTCConnected);
    }

    void Start() { }

    void Update()
    {
        if (OVRInput.GetDown(OVRInput.Button.Two) && currentSource != null)
            SwitchSource((activeSourceIndex + 1) % sources.Length);

        if (OVRInput.GetDown(OVRInput.Button.One))
        {
            _webRTCConnection ??= FindAnyObjectByType<WebRTCConnection>();
            if (_webRTCConnection != null && !_webRTCConnection.IsVideoTransmissionActive)
            {
                _webRTCConnection.StartVideoTransmission();
                Debug.Log("[VideoManager] Button A → StartVideoTransmission");
            }
        }
    }

    // VideoManager
    public RenderTexture CreateVideo()
    {
        // Riusa la stessa RenderTexture tra le riconnessioni: riallocarla ogni volta
        // senza Release() perdeva ~6.5MB di VRAM ad ogni StartVideoTransmission.
        if (camRenderTexture == null)
        {
            // 960x960 invece di 1280x1280: ~44% di pixel in meno. A 1280² l'encoder
            // (VP8 software / H264 hardware) non reggeva il realtime → freeze sul keyframe.
            camRenderTexture = new RenderTexture(960, 960, 0, RenderTextureFormat.BGRA32);
            camRenderTexture.Create();
        }
        SwitchSource(activeSourceIndex);
        return camRenderTexture;
    }

    const ulong MaxBps = 2_500_000u;   // 2.5 Mbps: sostenibile per 960² su Wi-Fi
    const uint MaxFps = 30u;           // cap framerate per non sforare la banda

    // Applica un cap di bitrate + framerate su tutti i sender video.
    // Senza cap il BWE rampa il bitrate nei primi secondi fino a saturare il Wi-Fi →
    // perdita pacchetti → freeze (tipicamente dopo 4-5s). I sender però esistono solo
    // dopo StartVideoTransmission(), che può avvenire DOPO l'evento WebRTCConnected:
    // per questo aspettiamo che compaiano invece di applicare una sola volta a 0.5s.
    IEnumerator ApplyBitrateCap()
    {
        System.Collections.Generic.Dictionary<string, RTCRtpSender> senders = null;

        float timeout = 30f;
        while (timeout > 0f)
        {
            senders = GetVideoSenders();
            if (senders != null && senders.Count > 0) break;
            yield return new WaitForSeconds(0.5f);
            timeout -= 0.5f;
        }

        if (senders == null || senders.Count == 0)
        {
            Debug.LogWarning("[VideoManager] Nessun sender video trovato entro il timeout: cap NON applicato");
            yield break;
        }

        ApplyCap(senders);

        // Riapplica dopo qualche secondo: una rinegoziazione o il reset dei parametri
        // lato BWE potrebbe azzerare il cap.
        yield return new WaitForSeconds(4f);
        senders = GetVideoSenders();
        if (senders != null && senders.Count > 0) ApplyCap(senders);
    }

    // Legge il dizionario privato videoTrackSenders dal WebRTCManager via reflection.
    System.Collections.Generic.Dictionary<string, RTCRtpSender> GetVideoSenders()
    {
        var managerField = typeof(SimpleWebRTC.WebRTCConnection)
            .GetField("webRTCManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var manager = managerField?.GetValue(_webRTCConnection);
        if (manager == null) return null;

        var sendersField = manager.GetType()
            .GetField("videoTrackSenders", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return sendersField?.GetValue(manager) as System.Collections.Generic.Dictionary<string, RTCRtpSender>;
    }

    void ApplyCap(System.Collections.Generic.Dictionary<string, RTCRtpSender> senders)
    {
        foreach (var kv in senders)
        {
            var param = kv.Value.GetParameters();
            foreach (var enc in param.encodings)
            {
                enc.maxBitrate = MaxBps;
                enc.maxFramerate = MaxFps;
            }
            kv.Value.SetParameters(param);
            Debug.Log($"[VideoManager] Cap {MaxBps / 1_000_000f:F1} Mbps / {MaxFps} fps applicato al sender → {kv.Key}");
        }
    }

    void OnDestroy()
    {
        if (_webRTCConnection != null)
            _webRTCConnection.WebRTCConnected.RemoveListener(OnWebRTCConnected);
        currentSource?.stop();
        if (camRenderTexture != null)
        {
            camRenderTexture.Release();
            Destroy(camRenderTexture);
            camRenderTexture = null;
        }
    }

    void SwitchSource(int index)
    {
        currentSource?.stop();
        activeSourceIndex = index;
        currentSource = sources[index];
        currentSource.initVideo(camRenderTexture);
    }
}
