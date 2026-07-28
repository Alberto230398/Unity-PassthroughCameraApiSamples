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
    bool _wasWebSocketActive;

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

        // Il pacchetto tiene un CancellationTokenSource "cts" readonly che viene cancellato ad
        // ogni chiusura del WS (anche un primo TENTATIVO fallito, es. server non ancora acceso):
        // essendo readonly non viene mai ricreato, quindi il SendLoop che spedisce i messaggi di
        // signaling (NEWPEER/OFFER/ANSWER/CANDIDATE) resta morto per sempre anche se il WS poi si
        // riconnette a livello di socket → "non si connette" anche quando il server è raggiungibile.
        // Teniamo traccia di "connesso OPPURE tentativo in corso": quando questo stato torna a
        // false (connessione riuscita poi caduta, O tentativo fallito da subito) sostituiamo il
        // token con uno nuovo così il prossimo Connect() può ripartire a inviare messaggi.
        // NB: niente CloseWebRTC()/StopAllCoroutines() qui: fermerebbe anche la coroutine
        // WebRTC.Update() che pompa il plugin nativo, causando un freeze/10fps mentre lo
        // streaming è ancora attivo (la pulizia dei peer morti la fa già EnsureStreaming ogni
        // secondo controllando IceConnectionState).
        bool wsActive = _webRTCConnection != null &&
            (_webRTCConnection.IsWebSocketConnected || _webRTCConnection.ConnectionToWebSocketInProgress);
        if (_wasWebSocketActive && !wsActive)
        {
            var manager = GetWebRTCManager();
            var ctsField = manager?.GetType()
                .GetField("cts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            ctsField?.SetValue(manager, new System.Threading.CancellationTokenSource());
        }
        _wasWebSocketActive = wsActive;

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

    // L'Oculus (sender) "streamma e basta": aggancia il video track ad ogni peer stabile che
    // ancora non ce l'ha. WebRTC richiede un AddTrack per-peer, quindi iteriamo peerConnections e
    // aggiungiamo solo dove manca (niente doppioni). Il track vive per la durata di una SESSIONE:
    // quando l'ultimo viewer se ne va (senders vuoto) viene disposto, e alla prossima connessione
    // se ne crea uno fresco → nessuna latenza accumulata dal riuso dello stesso encoder.
    void EnsureStreaming(object manager)
    {
        if (_webRTCConnection == null || !_webRTCConnection.IsSender) return;
        manager ??= GetWebRTCManager();
        if (manager == null) return;

        var peers = GetPeerConnections(manager);
        if (peers == null) return;
        var senders = GetVideoSenders();
        if (senders == null) return;

        // Passata 1 — cleanup dei peer morti (browser chiuso o in riconnessione). Il pacchetto
        // pulisce via PEERLEFT/DISPOSE/STALE-RECONNECT, ma se il SERVER è caduto quei messaggi
        // non arrivano: qui rimuoviamo su ICE Disconnected/Failed/Closed come backstop, così alla
        // riconnessione il peer è pulito e il video riparte da solo (fix "devo premere A").
        foreach (var kv in peers.ToList())
        {
            var state = kv.Value.IceConnectionState;
            if (state == RTCIceConnectionState.Disconnected ||
                state == RTCIceConnectionState.Failed ||
                state == RTCIceConnectionState.Closed)
            {
                kv.Value.Close();
                peers.Remove(kv.Key);
                senders.Remove(kv.Key);
            }
        }

        // Fine sessione: se non resta nessun sender attivo, butta via il track (e il suo encoder).
        // La prossima connessione ne crea uno fresco → niente latenza accumulata dal riuso dello
        // stesso encoder (sintomo: dopo un restart del server lo stream torna ma con ~2s di ritardo).
        if (senders.Count == 0 && _videoStreamTrack != null)
        {
            _videoStreamTrack.Dispose();
            _videoStreamTrack = null;
        }

        // Passata 2 — aggancia il video ai peer stabili che ancora non ce l'hanno.
        bool addedAny = false;
        foreach (var kv in peers)
        {
            if (senders.ContainsKey(kv.Key)) continue;   // questo peer ha già il video

            // Aspetta che la negoziazione iniziale del peer sia conclusa: aggiungere il track a
            // metà negoziazione causa glare/rinegoziazioni → lag (fix "Quest prima, poi browser").
            if (kv.Value.SignalingState != RTCSignalingState.Stable) continue;

            _videoStreamTrack ??= new VideoStreamTrack(CreateVideo());   // creato fresco a inizio sessione (avvia anche il blit)
            senders[kv.Key] = kv.Value.AddTrack(_videoStreamTrack);      // registra il sender nel dict del pacchetto
            addedAny = true;
            Debug.Log($"[VideoManager] Video track agganciato al peer {kv.Key}");
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
    const ulong MinBps = 2_000_000u;   // forza l'encoder a partire vicino al target invece di
                                        // rampare da zero (slow-start della bandwidth estimation
                                        // di WebRTC) → niente scatti/bitrate basso nei primi secondi
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
                enc.minBitrate = MinBps;
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
