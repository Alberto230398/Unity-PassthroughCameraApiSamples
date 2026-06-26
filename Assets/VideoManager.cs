// ─────────────────────────────────────────────────────────────────────────────
// VIDEO MANAGER
//
// Gestisce quale sorgente video viene streamata via WebRTC.
// Le sorgenti (PassthroughVideo, CompositeVideo...) implementano VideoInterface
// e scrivono tutte sulla stessa RenderTexture condivisa (camRenderTexture).
// WebRTC legge da quella RT e la trasmette al browser.
//
// Flusso:
//  1. WebRTCConnection chiama l'evento statico OnRequestVideoTrack
//  2. CreateVideo() crea la RenderTexture (se non esiste) e attiva la sorgente
//  3. La sorgente fa Graphics.Blit() sulla RT ogni frame
//  4. Quando la connessione WebRTC è stabilita, ApplyBitrateCap() limita il
//     bitrate VP8 per evitare che cresca nel tempo saturando il buffer di rete
//
// Tasto B (Button.Two): cambia sorgente video (passthrough / compositing)
// Tasto A (Button.One): avvia la trasmissione WebRTC (una sola volta)
// ─────────────────────────────────────────────────────────────────────────────

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

    void Awake()
    {
        _webRTCConnection = GetComponentInParent<WebRTCConnection>() ?? FindAnyObjectByType<WebRTCConnection>();
        if (_webRTCConnection != null)
            _webRTCConnection.WebRTCConnected.AddListener(OnWebRTCConnected);
    }

    void Start() { }

    void Update()
    {
        // Tasto B: cicla tra le sorgenti video disponibili
        if (OVRInput.GetDown(OVRInput.Button.Two) && currentSource != null)
            SwitchSource((activeSourceIndex + 1) % sources.Length);

        // Tasto A: avvia la trasmissione WebRTC.
        // Il guard su IsVideoTransmissionActive evita di avviarla due volte
        // (il check definitivo è anche dentro StartVideoTransmission() nel package).
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

    // Chiamato da WebRTCConnection.OnRequestVideoTrack quando il package
    // è pronto per ricevere la RenderTexture da streamare.
    // Riusa la stessa RT tra le riconnessioni: riallocarla ogni volta
    // senza Release() perdeva ~6.5MB di VRAM ad ogni StartVideoTransmission.
    public RenderTexture CreateVideo()
    {
        if (camRenderTexture == null)
        {
            camRenderTexture = new RenderTexture(1280, 1280, 0, RenderTextureFormat.BGRA32);
            camRenderTexture.Create();
        }
        SwitchSource(activeSourceIndex);
        return camRenderTexture;
    }

    // Applica un cap VP8 a maxBps su tutti i sender video dopo la connessione WebRTC.
    // Senza cap il bitrate cresce nel tempo saturando il buffer di rete → lag progressivo.
    // webRTCManager e videoTrackSenders sono privati nel package: accediamo via reflection.
    IEnumerator ApplyBitrateCap()
    {
        const uint maxBps = 6_000_000u;
        yield return new WaitForSeconds(2f); // lascia tempo all'ICE di stabilizzarsi

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
                enc.maxBitrate = maxBps;
            kv.Value.SetParameters(param);
            Debug.Log($"[VideoManager] Bitrate cap {maxBps / 1_000_000f:F1} Mbps → {kv.Key}");
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

    // Ferma la sorgente corrente e ne attiva una nuova sulla stessa RenderTexture.
    // La RT rimane invariata: WebRTC continua a leggere dallo stesso buffer,
    // quindi lo switch è seamless senza rinegoziare la connessione.
    void SwitchSource(int index)
    {
        currentSource?.stop();
        activeSourceIndex = index;
        currentSource = sources[index];
        currentSource.initVideo(camRenderTexture);
    }
}
