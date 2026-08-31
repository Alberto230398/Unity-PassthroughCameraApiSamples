using System.Linq;
using SimpleWebRTC;
using Unity.WebRTC;
using UnityEngine;

// Streamma il video (passthrough/composito) dall'Oculus ai browser via WebRTC.
// Il pacchetto SimpleWebRTC (embedded in Packages/) NON riaggancia il video ai peer che si
// connettono dopo l'avvio della trasmissione: lo fa questa classe, agganciando il track per-peer
// leggendone via reflection i dizionari interni (peerConnections/videoTrackSenders). Vedi EnsureStreaming.
public class VideoManager : MonoBehaviour
{
    [SerializeField] MonoBehaviour[] videoSources;   // MonoBehaviour che implementano VideoInterface
    [SerializeField] AudioSource audioSource;
    [SerializeField] int activeSourceIndex = 0;
    [Tooltip("Se ON l'Oculus aggancia il video ai peer da solo; se OFF solo col Button A.")]
    [SerializeField] bool autoStream = true;

    VideoInterface[] sources => videoSources.Select(s => s as VideoInterface).ToArray();

    RenderTexture camRenderTexture;      // RT su cui disegna la sorgente attiva: è il feed del track
    VideoInterface currentSource;
    WebRTCConnection _webRTCConnection;
    VideoStreamTrack _videoStreamTrack;  // track corrente; vive per una sessione (vedi EnsureStreaming)
    AudioStreamTrack _audioStreamTrack;  // track audio

    MediaStream mediaStream;                // stream che contiene il track video/audio (per il peer)


    float _nextReconcile;                // tempo del prossimo tick del poll
    bool _wasWebSocketActive;            // stato WS al frame precedente, per rilevarne la caduta

    // Cap di banda sui sender. Senza, la bandwidth-estimation di WebRTC rampa da zero e satura il
    // Wi-Fi → lag/freeze. min≈target fa partire l'encoder vicino al target invece che in slow-start.
    // Tetto UNICO del bitrate Quest→PC (prima c'era anche un b=AS lato browser che lo contraddiceva
    // e faceva collassare gli fps). Intervallo coerente min<max, così l'encoder può regolarsi senza
    // essere costretto a scendere di framerate. Alza verso 2.5M se il Wi-Fi regge; abbassa se lagga.
    const ulong MaxBps = 1_500_000u;   // 1.5 Mbps
    const ulong MinBps = 1_000_000u;   // 1.0 Mbps
    // 20 invece di 30: un encoder software che non tiene 30fps realtime accumula ritardo; a 20fps
    // fluidi la latenza resta stabile. Meglio 20 costanti che 30 che scivolano indietro.
    const uint MaxFps = 20u;

    void Awake()
    {
        _webRTCConnection = GetComponentInParent<WebRTCConnection>() ?? FindAnyObjectByType<WebRTCConnection>();
        // WebRTCConnected scatta ad ogni ICE 'Completed' lato sender: momento ideale per agganciare
        // il video al peer appena connesso (in aggiunta al poll in Update).
        if (_webRTCConnection != null)
            _webRTCConnection.WebRTCConnected.AddListener(OnWebRTCConnected);
    }

    void OnWebRTCConnected() => EnsureStreaming(null);

    void Update()
    {
        RestartSignalingIfDropped();

        // Button A: restart manuale, per recuperare da stati sporchi che l'auto-aggancio non vede.
        /*if (OVRInput.GetDown(OVRInput.Button.One))
        {
            _webRTCConnection ??= FindAnyObjectByType<WebRTCConnection>();
            ForceRestartVideo();
        }*/

        // Poll 1s: aggancia il video ai peer che ancora non ce l'hanno.
        if (autoStream && Time.time >= _nextReconcile)
        {
            _nextReconcile = Time.time + 1f;
            EnsureStreaming(null);
        }
    }

    // Il pacchetto tiene un CancellationTokenSource 'cts' readonly: lo cancella ad ogni chiusura del
    // WS (anche un primo tentativo fallito) ma non lo ricrea mai → il SendLoop che spedisce il
    // signaling resta morto e il WS "non si riconnette" più. Quando lo stato WS passa da attivo a
    // non-attivo gli sostituiamo un cts fresco, così il prossimo Connect() torna a inviare.
    // NB: qui NIENTE CloseWebRTC()/StopAllCoroutines(): fermerebbero anche la coroutine WebRTC.Update()
    // (pompa il plugin nativo) → freeze/10fps. La pulizia dei peer la fa EnsureStreaming.
    void RestartSignalingIfDropped()
    {
        bool wsActive = _webRTCConnection != null &&
            (_webRTCConnection.IsWebSocketConnected || _webRTCConnection.ConnectionToWebSocketInProgress);

        if (_wasWebSocketActive && !wsActive)
        {
            var manager = GetWebRTCManager();
            manager?.GetType()
                .GetField("cts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(manager, new System.Threading.CancellationTokenSource());
        }
        _wasWebSocketActive = wsActive;
    }

    // Aggancia il track ad ogni peer stabile che non ce l'ha ancora (WebRTC vuole un AddTrack
    // per-peer). Il track vive per una SESSIONE: quando l'ultimo viewer se ne va lo distruggiamo, così
    // la connessione successiva ne crea uno fresco — riusare lo stesso encoder tra sessioni lasciava
    // ~2s di latenza dopo un restart del server. Chiamato dal poll (1s) e da OnWebRTCConnected.
    void EnsureStreaming(object manager)
    {
        if (_webRTCConnection == null || !_webRTCConnection.IsSender) return;
        manager ??= GetWebRTCManager();
        if (manager == null) return;

        var peers = GetPeerConnections(manager);
        var senders = GetVideoSenders();
        var audioSenders = GetAudioSenders();
        if (peers == null || senders == null) return;

        // 1) Rimuovi i peer morti. Il pacchetto pulisce via PEERLEFT/DISPOSE, ma se il SERVER è
        //    caduto quei messaggi non arrivano: ci basiamo sullo stato ICE. .ToList() perché stiamo
        //    per modificare 'peers' mentre lo iteriamo.
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
                audioSenders.Remove(kv.Key);
            }
        }

        // 2) Nessun viewer rimasto → chiudi la sessione buttando il track (e il suo encoder).
        if (senders.Count == 0 && _videoStreamTrack != null && audioSenders.Count == 0)
        {
            _videoStreamTrack.Dispose();
            _videoStreamTrack = null;

            _audioStreamTrack?.Dispose();
            _audioStreamTrack = null;

            mediaStream?.Dispose();
            mediaStream = null;
        }

        // 3) Aggancia il track ai peer stabili senza video. Il gate SignalingState==Stable evita di
        //    aggiungerlo a metà negoziazione (→ glare/rinegoziazioni → lag). '??=' crea il track
        //    pigramente: al primo peer della sessione è fresco.
        bool addedAny = false;
        foreach (var kv in peers)
        {
            if (senders.ContainsKey(kv.Key)) continue;
            if (kv.Value.SignalingState != RTCSignalingState.Stable) continue;

            mediaStream = new MediaStream();
            _videoStreamTrack ??= new VideoStreamTrack(CreateVideo());
            _audioStreamTrack ??= new AudioStreamTrack(CreateAudio());

            senders[kv.Key] = kv.Value.AddTrack(_videoStreamTrack, mediaStream);   // registra il sender nel dict del pacchetto
            audioSenders[kv.Key] = kv.Value.AddTrack(_audioStreamTrack, mediaStream);   // registra il sender nel dict del pacchetto
            addedAny = true;
            Debug.Log($"[VideoManager] Video agganciato al peer {kv.Key}");
        }
        if (addedAny)
            _webRTCConnection.CreateOfferCoroutine();   // una sola rinegoziazione per tutti i nuovi peer

        // 4) Rinforza il cap ogni tick: una rinegoziazione o il BWE potrebbero averlo azzerato.
        if (senders.Count > 0) ApplyCap(senders);
        //if (audioSenders.Count > 0) ApplyCap(audioSenders);
    }

    // Button A: distrugge il track corrente e riaggancia da zero.
    void ForceRestartVideo()
    {
        var manager = _webRTCConnection != null ? GetWebRTCManager() : null;
        if (manager == null)
        {
            Debug.LogWarning("[VideoManager] WebRTC non pronto: connettiti prima di forzare lo streaming.");
            return;
        }
        ResetTrack(manager);
        EnsureStreaming(manager);
    }

    // (Ri)crea la RT-sorgente (una volta) e (ri)avvia la sorgente attiva che la disegna.
    public RenderTexture CreateVideo()
    {
        // RT riusata tra le connessioni: riallocarla senza Release() perdeva ~6.5MB di VRAM a giro.
        // 640² (era 960²): l'encoder VP8 SOFTWARE non regge il realtime ad alta risoluzione, specie
        // mentre il Quest decodifica anche il video PC→Quest → l'arretrato cresce e la latenza sale
        // nel tempo. Meno pixel = l'encoder sta al passo. (720² se serve più dettaglio e il Quest regge.)
        if (camRenderTexture == null)
        {
            camRenderTexture = new RenderTexture(640, 640, 0, RenderTextureFormat.BGRA32);
            camRenderTexture.Create();
        }
        SwitchSource(activeSourceIndex);
        return camRenderTexture;
    }

    // Avvia il microfono e restituisce l'AudioSource che lo riproduce. Unity.WebRTC cattura l'audio
    // via OnAudioFilterRead sull'AudioSource, quindi DEVE essere in Play() e in loop: senza, il filtro
    // non gira e il track è muto. Per NON sentirsi in locale, instrada l'Output dell'AudioSource su un
    // AudioMixerGroup silenziato (−80 dB): NON usare volume/mute, azzererebbero anche il segnale catturato.
    AudioSource CreateAudio()
    {
        if (Microphone.devices.Length == 0)
        {
            Debug.LogError("[VideoManager] Nessun microfono disponibile (permesso RECORD_AUDIO non concesso?).");
            return null;
        }

        // loop=true: il buffer di 10s viene riscritto in cerchio, così il mic registra all'infinito.
        audioSource.clip = Microphone.Start(Microphone.devices[0], true, 1, AudioSettings.outputSampleRate);
        audioSource.loop = true;   // l'AudioSource rilegge in loop il clip che il mic aggiorna
        audioSource.Play();        // ← senza questo il track è muto: fa girare OnAudioFilterRead
        return audioSource;
    }

    void SwitchSource(int index)
    {
        currentSource?.stop();
        activeSourceIndex = index;
        currentSource = sources[index];
        currentSource.initVideo(camRenderTexture);
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

        ResetTrack(GetWebRTCManager());   // stacca e libera il track prima di distruggere la RT

        currentSource?.stop();
        if (camRenderTexture != null)
        {
            camRenderTexture.Release();
            Destroy(camRenderTexture);
            camRenderTexture = null;
        }
    }

    // --- Accesso ai membri interni del pacchetto SimpleWebRTC via reflection ---
    // Il pacchetto è embedded ma non espone questi membri; la reflection tiene VideoManager
    // disaccoppiato. Se un refactor del pacchetto rinomina questi campi/metodi, va aggiornata qui.

    // Stacca il track da tutti i peer (RemoveVideoTrack è pubblico) e lo libera.
    void ResetTrack(object manager)
    {
        if (_videoStreamTrack == null) return;
        manager?.GetType()
            .GetMethod("RemoveVideoTrack", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            ?.Invoke(manager, null);
        _videoStreamTrack.Dispose();
        _videoStreamTrack = null;

        if (_audioStreamTrack == null) return;
        manager?.GetType()
            .GetMethod("RemoveAudioTrack", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            ?.Invoke(manager, null);
        _audioStreamTrack.Dispose();
        _audioStreamTrack = null;
    }

    object GetWebRTCManager() =>
        typeof(WebRTCConnection)
            .GetField("webRTCManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(_webRTCConnection);

    System.Collections.Generic.Dictionary<string, RTCPeerConnection> GetPeerConnections(object manager) =>
        manager.GetType()
            .GetField("peerConnections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(manager) as System.Collections.Generic.Dictionary<string, RTCPeerConnection>;

    System.Collections.Generic.Dictionary<string, RTCRtpSender> GetVideoSenders()
    {
        var manager = GetWebRTCManager();
        return manager?.GetType()
            .GetField("videoTrackSenders", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(manager) as System.Collections.Generic.Dictionary<string, RTCRtpSender>;
    }

     System.Collections.Generic.Dictionary<string, RTCRtpSender> GetAudioSenders()
    {
        var manager = GetWebRTCManager();
        return manager?.GetType()
            .GetField("audioTrackSenders", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(manager) as System.Collections.Generic.Dictionary<string, RTCRtpSender>;
    }
}
