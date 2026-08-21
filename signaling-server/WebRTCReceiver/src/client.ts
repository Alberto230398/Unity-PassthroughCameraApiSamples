// WebRTC receiver client (browser). Compilato in dist/client.js (script classico) e caricato
// dall'HTML. Fa da answerer verso il Quest e, per la videochiamata bidirezionale, invia
// webcam+mic locali agganciandoli alle m-line sendrecv offerte dal Quest.

const PEER_ID = "Browser-PeerId";
const RETRY_MS = 10000;

// TEST A/B: metti a false per tornare "solo ricezione" (come il setup che funzionava prima,
// niente webcam/mic inviati al Quest). Serve a capire se è proprio l'invio browser→Quest a far
// piantare la negoziazione sul Quest. true = videochiamata bidirezionale.
const SEND_TO_QUEST = true;

// --- riferimenti DOM (lo script è un modulo: viene eseguito a DOM pronto) ---
const $ = <T extends HTMLElement>(id: string): T => {
  const el = document.getElementById(id);
  if (!el) throw new Error(`Elemento #${id} non trovato`);
  return el as T;
};

const videoEl = $<HTMLVideoElement>("video");
const localVideoEl = $<HTMLVideoElement>("localVideo");
const overlayEl = $<HTMLDivElement>("overlay");
const wsUrlEl = $<HTMLInputElement>("wsUrl");
const questIdEl = $<HTMLInputElement>("questId");
const btnConnect = $<HTMLButtonElement>("btnConnect");
const btnDisconnect = $<HTMLButtonElement>("btnDisconnect");
const btnAudio = $<HTMLButtonElement>("btnAudio");
const statusEl = $<HTMLSpanElement>("status");

// --- stato ---
let ws: WebSocket | null = null;
let pc: RTCPeerConnection | null = null;
let pendingCandidates: RTCIceCandidateInit[] = [];
let remoteDescSet = false;
let manualDisconnect = false;
let retryTimer: ReturnType<typeof setTimeout> | null = null;
let inboundStream: MediaStream | null = null; // raccoglie video + audio dalle rinegoziazioni
let audioEnabled = true;                       // parte con audio; se bloccato si sblocca al primo gesto
let gestureArmed = false;                       // listener "sblocca al primo click/tasto" installato?
let localStream: MediaStream | null = null;     // webcam + mic del computer, inviati al Quest
let localMediaPromise: Promise<MediaStream | null> | null = null;

function setStatus(s: string): void { statusEl.textContent = s; }
function setOverlay(text: string, white = false): void {
  overlayEl.textContent = text;
  overlayEl.style.color = white ? "#fff" : "";
  overlayEl.style.display = "flex";
}
function showOverlay(): void { setOverlay("NO SIGNAL"); }
function hideOverlay(): void { overlayEl.style.display = "none"; }

function scheduleRetry(): void {
  if (retryTimer) return; // già in attesa, non accavallare retry
  setOverlay("WAITING FOR SERVER", true);
  setStatus("waiting for server");
  btnConnect.disabled = false;
  btnDisconnect.disabled = true;
  retryTimer = setTimeout(() => { retryTimer = null; connect(); }, RETRY_MS);
}

function connect(): void {
  if (retryTimer) { clearTimeout(retryTimer); retryTimer = null; }
  manualDisconnect = false;

  // Chiedi subito webcam+mic, così il permesso appare all'apertura e lo stream è pronto
  // quando arriva l'offerta del Quest (lo aggancieremo all'answer).
  if (SEND_TO_QUEST) void ensureLocalMedia();

  const url = wsUrlEl.value.trim();
  const questId = questIdEl.value.trim();

  ws = new WebSocket(url);

  ws.onopen = () => {
    setStatus("ws connected");
    setOverlay("WAITING FOR VIDEO STREAM", true);
    // Ultimo campo = IsVideoAudioSender: se inviamo, il Quest (se IsVideoAudioReceiver) crea le
    // RawImage/AudioSource per ricevere la nostra webcam+mic. In modalità "solo ricezione"
    // dichiariamo False, così il Quest non prepara nulla → comportamento identico al setup vecchio.
    ws!.send(`CONNECT|${PEER_ID}|ALL|${PEER_ID} joined|0|${SEND_TO_QUEST ? "True" : "False"}`);
    btnConnect.disabled = true;
    btnDisconnect.disabled = false;
  };

  ws.onmessage = async (e: MessageEvent) => {
    const parts = String(e.data).split("|");
    const type = parts[0];
    const sender = parts[1];
    const payload = parts[3];

    console.log(`[WS] ${type} from ${sender}`);

    switch (type) {
      case "NEWPEER": {
        if (sender === questId) {
          // Quest si è (ri)registrato: chiudi sempre la pc vecchia prima di crearne una nuova.
          // Senza questo, dopo che il Quest esce e rientra dall'app la pc precedente
          // rimane aperta e createPeerConnection() la riutilizzava (if pc return),
          // causando negoziazione su una connessione già morta → lag.
          closePeerConnection();
          await createPeerConnection(sender);
          ws!.send(`NEWPEERACK|${PEER_ID}|${sender}|ack|0|${SEND_TO_QUEST ? "True" : "False"}`);
          // NIENTE offerta dal browser: fa SOLO da answerer. Il Quest crea la sua offerta da
          // solo. Se offrissero entrambi in contemporanea → glare. Un solo offerente = niente gara.
        } else {
          console.warn(`[WS] NEWPEER da "${sender}" ignorato: non combacia con questId "${questId}"`);
        }
        break;
      }
      case "NEWPEERACK": {
        if (sender === questId && !pc) {
          // Anche qui il browser fa solo da answerer: crea la pc e aspetta l'OFFER del Quest.
          await createPeerConnection(sender);
        } else if (sender !== questId) {
          console.warn(`[WS] NEWPEERACK da "${sender}" ignorato: non combacia con questId "${questId}"`);
        }
        break;
      }
      case "OFFER":
        await handleOffer(sender, payload);
        break;
      case "ANSWER":
        if (!pc) break;
        await pc.setRemoteDescription(JSON.parse(payload) as RTCSessionDescriptionInit);
        remoteDescSet = true;
        for (const c of pendingCandidates) {
          try { await pc.addIceCandidate(new RTCIceCandidate(c)); } catch (_) { /* ignora */ }
        }
        pendingCandidates = [];
        break;
      case "CANDIDATE": {
        try {
          const c = JSON.parse(payload) as RTCIceCandidateInit;
          if (remoteDescSet && pc) {
            await pc.addIceCandidate(new RTCIceCandidate(c));
          } else {
            pendingCandidates.push(c);
          }
        } catch (_) { /* ignora candidati malformati */ }
        break;
      }
      case "PEERLEFT": {
        // Il server invia PEERLEFT quando un peer perde il WebSocket senza DISPOSE
        // (es. Quest va in background). Chiudiamo la pc così quando il Quest rientra
        // e manda NEWPEER partiamo da zero invece di riutilizzare una pc morta.
        if (sender === questId) {
          closePeerConnection();
          setOverlay("WAITING FOR VIDEO STREAM", true);
        }
        break;
      }
    }
  };

  ws.onclose = () => {
    setStatus("disconnected");
    // Se il WS cade la negoziazione è persa; alla prossima connessione serve una pc fresca.
    closePeerConnection();
    btnConnect.disabled = false;
    btnDisconnect.disabled = true;

    if (manualDisconnect) {
      showOverlay();
    } else {
      // Server non raggiungibile (o caduto): ritenta da solo, niente click manuale.
      scheduleRetry();
    }
  };
  ws.onerror = () => setStatus("ws error");
}

function closePeerConnection(): void {
  if (pc) { pc.close(); pc = null; }
  pendingCandidates = [];
  remoteDescSet = false;
  inboundStream = null;
  videoEl.srcObject = null;
  btnAudio.disabled = true;
}

async function createPeerConnection(remotePeerId: string): Promise<void> {
  if (pc) return;
  pendingCandidates = [];
  remoteDescSet = false;

  pc = new RTCPeerConnection({ iceServers: [{ urls: "stun:stun.l.google.com:19302" }] });

  pc.onicecandidate = (e: RTCPeerConnectionIceEvent) => {
    if (e.candidate && ws)
      ws.send(`CANDIDATE|${PEER_ID}|${remotePeerId}|${JSON.stringify(e.candidate)}|0|False`);
  };

  pc.oniceconnectionstatechange = () => {
    if (!pc) return;
    console.log(`[ICE] ${remotePeerId}: ${pc.iceConnectionState}`);
    setStatus(pc.iceConnectionState);
    // Mostra subito "WAITING" appena ICE fallisce/si disconnette, senza aspettare PEERLEFT.
    if (pc.iceConnectionState === "failed" || pc.iceConnectionState === "disconnected") {
      videoEl.srcObject = null;
      setOverlay("WAITING FOR VIDEO STREAM", true);
    }
  };

  pc.ontrack = (e: RTCTrackEvent) => {
    console.log(`[TRACK] ricevuto ${e.track.kind} da ${remotePeerId}`);
    // Il Quest aggiunge video e audio con due AddTrack distinti. In ogni caso ontrack scatta una
    // volta per track e non è garantito che condividano lo stesso e.streams[]. Per questo li
    // raccogliamo noi in un unico MediaStream agganciato al <video>.
    if (!inboundStream) inboundStream = new MediaStream();

    // Dopo una rinegoziazione dello stesso kind rimpiazza il vecchio track, niente duplicati.
    inboundStream.getTracks()
      .filter((t) => t.kind === e.track.kind)
      .forEach((t) => inboundStream!.removeTrack(t));
    inboundStream.addTrack(e.track);

    if (videoEl.srcObject !== inboundStream) videoEl.srcObject = inboundStream;

    if (e.track.kind === "video") {
      hideOverlay();
    } else if (e.track.kind === "audio") {
      // Minimizza il jitter buffer del browser: in LAN togliamo la latenza di buffering.
      // jitterBufferTarget non è ancora nei tipi standard del DOM.
      try {
        const rcv = e.receiver as RTCRtpReceiver & { jitterBufferTarget?: number | null };
        if ("jitterBufferTarget" in rcv) rcv.jitterBufferTarget = 0;
      } catch (_) { /* non supportato */ }
      btnAudio.disabled = false;
      applyAudioState();
    }
  };
}

function applyAudioState(): void {
  videoEl.muted = !audioEnabled;
  btnAudio.textContent = audioEnabled ? "Audio On" : "Audio Off";

  const p = videoEl.play();
  if (audioEnabled && p) {
    // Il browser può rifiutare l'autoplay con suono (policy). In quel caso teniamo il video
    // muto (così almeno le immagini partono) e armiamo lo sblocco al primo gesto utente.
    p.catch(() => {
      videoEl.muted = true;
      btnAudio.textContent = "Audio Off";
      armGestureUnlock();
    });
  }
}

// Sblocca l'audio al PRIMO gesto qualsiasi sulla pagina (click, tasto, touch), non solo sul
// pulsante: così basta toccare lo schermo una volta invece di centrare il bottone.
function armGestureUnlock(): void {
  if (gestureArmed) return;
  gestureArmed = true;
  const events: Array<keyof WindowEventMap> = ["pointerdown", "keydown", "touchstart"];
  const unlock = () => {
    gestureArmed = false;
    events.forEach((ev) => window.removeEventListener(ev, unlock));
    audioEnabled = true;
    applyAudioState();
  };
  events.forEach((ev) => window.addEventListener(ev, unlock));
}

function toggleAudio(): void {
  audioEnabled = !audioEnabled;
  applyAudioState();
}

// Acquisisce webcam+mic locali una sola volta. Se l'utente nega o non c'è hardware,
// la chiamata resta comunque ricevente (senza inviare nulla al Quest).
function ensureLocalMedia(): Promise<MediaStream | null> {
  if (localStream) return Promise.resolve(localStream);
  if (!localMediaPromise) {
    // Risoluzione/fps contenuti: il Quest deve decodificare questo stream MENTRE codifica il suo
    // passthrough. Tenere la webcam a 480p/20fps riduce il rischio di saturare il Wi-Fi e di
    // freeze del decoder (perdita di keyframe). Alza questi valori solo se la rete regge.
    const constraints: MediaStreamConstraints = {
      video: { width: { ideal: 640 }, height: { ideal: 480 }, frameRate: { ideal: 20, max: 24 } },
      audio: { echoCancellation: true, noiseSuppression: true },
    };
    localMediaPromise = navigator.mediaDevices.getUserMedia(constraints)
      .then((s) => {
        localStream = s;
        localVideoEl.srcObject = s;
        localVideoEl.style.display = "block";
        return s;
      })
      .catch((err: DOMException) => {
        console.warn("[MEDIA] webcam/mic non disponibili:", err.name, err.message);
        localMediaPromise = null; // consenti un nuovo tentativo alla prossima connessione
        return null;
      });
  }
  return localMediaPromise;
}

// Aggancia i track locali alle m-line che il Quest ha GIÀ offerto (sendrecv), riusando il
// loro sender vuoto. NON facciamo addTrack di una nuova m-line: creerebbe una rinegoziazione
// dal lato browser → glare con il Quest (unico offerente). Se una m-line manca, quel track
// resta in attesa. Va chiamata DOPO setRemoteDescription(offer) e PRIMA di createAnswer.
function attachLocalTracks(): void {
  if (!localStream || !pc) return;
  for (const track of localStream.getTracks()) {
    if (pc.getSenders().some((s) => s.track === track)) continue; // già inviato
    const tr = pc.getTransceivers().find((t) =>
      t.sender && t.sender.track === null &&                        // sender ancora vuoto
      t.receiver && t.receiver.track && t.receiver.track.kind === track.kind &&
      t.direction !== "inactive" && t.direction !== "sendonly" &&
      t.currentDirection !== "stopped");
    if (tr) {
      void tr.sender.replaceTrack(track);
      tr.direction = "sendrecv"; // l'answer dichiarerà che inviamo su questa m-line
      console.log(`[MEDIA] invio ${track.kind} al Quest sulla m-line esistente`);
    } else {
      console.warn(`[MEDIA] nessuna m-line ${track.kind} nell'offerta: track in attesa`);
    }
  }
}

async function handleOffer(remotePeerId: string, payload: string): Promise<void> {
  if (!pc) await createPeerConnection(remotePeerId);
  if (!pc) return;
  await pc.setRemoteDescription(JSON.parse(payload) as RTCSessionDescriptionInit);
  remoteDescSet = true;
  // Prima di rispondere, aggancia la nostra webcam+mic alle m-line sendrecv del Quest.
  if (SEND_TO_QUEST) {
    await ensureLocalMedia();
    attachLocalTracks();
  }
  for (const c of pendingCandidates) {
    try { await pc.addIceCandidate(new RTCIceCandidate(c)); } catch (_) { /* ignora */ }
  }
  pendingCandidates = [];
  const answer = await pc.createAnswer();
  await pc.setLocalDescription(answer);
  ws?.send(`ANSWER|${PEER_ID}|${remotePeerId}|${JSON.stringify(answer)}|0|True`);
  await capOutgoingVideoBitrate();
}

// Limita il bitrate/framerate del video che INVIAMO al Quest, per non saturare l'uplink Wi-Fi
// (che il Quest condivide col proprio stream in uscita). Va chiamata dopo setLocalDescription,
// quando il sender ha già i suoi parametri di encoding.
async function capOutgoingVideoBitrate(maxKbps = 800, maxFps = 20): Promise<void> {
  if (!pc) return;
  for (const sender of pc.getSenders()) {
    if (!sender.track || sender.track.kind !== "video") continue;
    const params = sender.getParameters();
    if (!params.encodings || params.encodings.length === 0) params.encodings = [{}];
    params.encodings[0].maxBitrate = maxKbps * 1000;
    params.encodings[0].maxFramerate = maxFps;
    try {
      await sender.setParameters(params);
      console.log(`[MEDIA] bitrate video in uscita limitato a ${maxKbps} kbps / ${maxFps} fps`);
    } catch (e) {
      console.warn("[MEDIA] setParameters fallito:", e);
    }
  }
}

function disconnect(): void {
  manualDisconnect = true;
  if (retryTimer) { clearTimeout(retryTimer); retryTimer = null; }
  closePeerConnection();
  // Spegni webcam+mic e nascondi la self-view (il LED della camera si spegne).
  if (localStream) {
    localStream.getTracks().forEach((t) => t.stop());
    localStream = null;
    localMediaPromise = null;
    localVideoEl.srcObject = null;
    localVideoEl.style.display = "none";
  }
  ws?.close();
  ws = null;
  showOverlay();
  btnConnect.disabled = false;
  btnDisconnect.disabled = true;
  setStatus("disconnected");
}

// --- wiring: sostituisce gli onclick inline dell'HTML ---
btnConnect.addEventListener("click", connect);
btnDisconnect.addEventListener("click", disconnect);
btnAudio.addEventListener("click", toggleAudio);

// Avvia da solo: lo <script> è in fondo al <body>, quindi il DOM è già pronto.
connect();
