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
    VideoStreamTrack _videoStreamTrack;

    [Tooltip("Se ON l'Oculus (sender) aggancia da solo il video track ad ogni peer che si connette. " +
             "Se OFF si aggancia solo col Button A.")]
    [SerializeField] bool autoStream = true;

    // L'evento WebRTCConnection.OnRequestVideoTrack era una modifica custom al pacchetto:
    // spariva ad ogni aggiornamento. Ora agganciamo il video track ai peer via reflection
    // (leggendo peerConnections/videoTrackSenders), così il pacchetto resta vanilla e aggiornabile.
    //void OnEnable() => WebRTCConnection.OnRequestVideoTrack += CreateVideo;
    //void OnDisable() => WebRTCConnection.OnRequestVideoTrack -= CreateVideo;

    // Scatta ad ogni (ri)connessione di un peer (ICE completed, lato sender): buon momento
    // per agganciare il video al peer appena connesso.
    void OnWebRTCConnected() => EnsureStreaming(null);

    float _nextReconcile;

    void Awake()
    {
        _webRTCConnection = GetComponentInParent<WebRTCConnection>() ?? FindAnyObjectByType<WebRTCConnection>();
        if (_webRTCConnection != null)
            _webRTCConnection.WebRTCConnected.AddListener(OnWebRTCConnected);
    }

    void Start() { }

    void Update()
    {
        //if (OVRInput.GetDown(OVRInput.Button.Two) && currentSource != null)
            //SwitchSource((activeSourceIndex + 1) % sources.Length);

        // Button A: recovery manuale — ricrea il track e lo riaggancia a tutti i peer.
        // Utile per stati sporchi (es. browser caduto senza inviare DISPOSE: il peerConnection
        // lato Unity resta appeso col suo sender e l'auto-aggancio non lo rileva come mancante).
        if (OVRInput.GetDown(OVRInput.Button.One))
        {
            _webRTCConnection ??= FindAnyObjectByType<WebRTCConnection>();
            ForceRestartVideo();
        }

        // Auto: aggancia il video ai peer che ancora non ce l'hanno.
        if (autoStream && Time.time >= _nextReconcile)
        {
            _nextReconcile = Time.time + 1f;
            EnsureStreaming(null);
        }
    }

    // L'Oculus (sender) "streamma e basta": crea il video track una sola volta e lo aggancia ad
    // ogni peer che ancora non ce l'ha. Il track NON viene smontato quando i peer se ne vanno,
    // così ogni browser che si connette lo riceve. WebRTC richiede comunque un AddTrack per-peer:
    // per questo iteriamo peerConnections e aggiungiamo solo dove manca (niente doppioni).
    void EnsureStreaming(object manager)
    {
        if (_webRTCConnection == null || !_webRTCConnection.IsSender) return;
        manager ??= GetWebRTCManager();
        if (manager == null) return;

        var peers = GetPeerConnections(manager);
        if (peers == null) return;
        var senders = GetVideoSenders();
        if (senders == null) return;

        bool addedAny = false;

        // Snapshot: dentro il ciclo modifichiamo peers/senders (cleanup dei peer morti).
        foreach (var kv in peers.ToList())
        {
            var id = kv.Key;
            var pc = kv.Value;
            var state = pc.IceConnectionState;

            // Peer morto (browser chiuso o in riconnessione): rimuovilo, così alla riconnessione
            // il nuovo peerConnection è trattato pulito e riceve il video da solo (fix "devo premere A").
            // Il pacchetto NON fa cleanup su ICE Disconnected/Failed.
            if (state == RTCIceConnectionState.Disconnected ||
                state == RTCIceConnectionState.Failed ||
                state == RTCIceConnectionState.Closed)
            {
                pc.Close();
                peers.Remove(id);
                senders.Remove(id);
                continue;
            }

            if (senders.ContainsKey(id)) continue;   // questo peer ha già il video

            // Aspetta che la negoziazione iniziale del peer sia conclusa: aggiungere il track a
            // metà negoziazione causa glare/rinegoziazioni → lag (fix "Quest prima, poi browser").
            if (pc.SignalingState != RTCSignalingState.Stable) continue;

            _videoStreamTrack ??= new VideoStreamTrack(CreateVideo());   // creato una sola volta (avvia anche il blit)
            senders[id] = pc.AddTrack(_videoStreamTrack);               // registra il sender nel dict del pacchetto
            addedAny = true;
            Debug.Log($"[VideoManager] Video track agganciato al peer {id}");
        }

        if (addedAny)
            _webRTCConnection.CreateOfferCoroutine();   // rinegozia coi peer (API pubblica del pacchetto)

        // Cap continuo: riapplicato ogni tick così una rinegoziazione o il BWE non lo azzerano
        // (senza cap il bitrate rampa fino a saturare il Wi-Fi → lag/freeze).
        if (senders.Count > 0) ApplyCap(senders);
    }

    // Button A: butta via il track corrente e lo ricrea/riaggancia da zero.
    void ForceRestartVideo()
    {
        if (_webRTCConnection == null)
        {
            Debug.LogWarning("[VideoManager] Nessuna WebRTCConnection in scena.");
            return;
        }
        var manager = GetWebRTCManager();
        if (manager == null)
        {
            Debug.LogWarning("[VideoManager] WebRTCManager non pronto: connetti il WebRTC prima di avviare il video.");
            return;
        }
        ResetTrack(manager);
        EnsureStreaming(manager);
        Debug.Log("[VideoManager] Button A → restart streaming forzato");
    }

    // Rimuove il track da tutti i peer (API pubblica del pacchetto) e lo dispone.
    void ResetTrack(object manager)
    {
        if (_videoStreamTrack == null) return;

        manager?.GetType()
            .GetMethod("RemoveVideoTrack", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            ?.Invoke(manager, null);

        _videoStreamTrack.Dispose();
        _videoStreamTrack = null;
    }

    // Dizionario privato peerConnections del WebRTCManager, tipizzato.
    System.Collections.Generic.Dictionary<string, RTCPeerConnection> GetPeerConnections(object manager)
    {
        var field = manager.GetType()
            .GetField("peerConnections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return field?.GetValue(manager) as System.Collections.Generic.Dictionary<string, RTCPeerConnection>;
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

    // Legge il campo privato webRTCManager dal WebRTCConnection via reflection.
    object GetWebRTCManager()
    {
        var managerField = typeof(SimpleWebRTC.WebRTCConnection)
            .GetField("webRTCManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return managerField?.GetValue(_webRTCConnection);
    }

    // Legge il dizionario privato videoTrackSenders dal WebRTCManager via reflection.
    System.Collections.Generic.Dictionary<string, RTCRtpSender> GetVideoSenders()
    {
        var manager = GetWebRTCManager();
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
        }
    }

    void OnDestroy()
    {
        if (_webRTCConnection != null)
            _webRTCConnection.WebRTCConnected.RemoveListener(OnWebRTCConnected);

        // Rimuove il track dai peer e libera le risorse native prima di distruggere la RT.
        ResetTrack(GetWebRTCManager());

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
