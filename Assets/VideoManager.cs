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

    // Applica un cap di bitrate + framerate su tutti i sender video dopo la connessione.
    // Senza cap il bitrate cresce saturando il buffer di rete → lag progressivo; il primo
    // keyframe a bitrate libero era enorme e saturava subito il link → freeze.
    IEnumerator ApplyBitrateCap()
    {
        const ulong maxBps = 2_500_000u;   // 2.5 Mbps: sostenibile per 960² su Wi-Fi
        const uint maxFps = 30u;           // cap framerate per non sforare la banda
        yield return new WaitForSeconds(0.5f); // applica presto, prima del primo keyframe

        // webRTCManager è privato nel package; accediamo con reflection
        var managerField = typeof(SimpleWebRTC.WebRTCConnection)
            .GetField("webRTCManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (managerField == null) { Debug.LogWarning("[VideoManager] webRTCManager field not found"); yield break; }

        var manager = managerField.GetValue(_webRTCConnection);
        if (manager == null) yield break;

        var sendersField = manager.GetType()
            .GetField("videoTrackSenders", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (sendersField == null) { Debug.LogWarning("[VideoManager] videoTrackSenders field not found"); yield break; }

        var senders = sendersField.GetValue(manager) as System.Collections.Generic.Dictionary<string, RTCRtpSender>;
        if (senders == null) yield break;

        foreach (var kv in senders)
        {
            var param = kv.Value.GetParameters();
            foreach (var enc in param.encodings)
            {
                enc.maxBitrate = maxBps;
                enc.maxFramerate = maxFps;
            }
            kv.Value.SetParameters(param);
            Debug.Log($"[VideoManager] Cap {maxBps / 1_000_000f:F1} Mbps / {maxFps} fps applicato al sender → {kv.Key}");
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
