// ─────────────────────────────────────────────────────────────────────────────
// SIGNALING SERVER
//
// Questo server NON trasporta video. Fa solo da "postino" tra i peer WebRTC:
// riceve messaggi di signaling (NEWPEER, OFFER, ANSWER, CANDIDATE...) e li
// smista al destinatario corretto. Una volta che i peer si sono "trovati"
// tramite lo scambio di SDP e ICE candidates, il video viaggia direttamente
// tra Quest e browser senza passare più da qui.
//
// Porta: 8765  —  host: 0.0.0.0 forza IPv4 (senza host Node apre in IPv6
// dual-stack che su macOS non accetta connessioni IPv4 da altri dispositivi).
// ─────────────────────────────────────────────────────────────────────────────

const WebSocket = require("ws");
const wss = new WebSocket.Server({ port: 8765, host: "0.0.0.0" });

// Registro globale dei peer connessi: { peerId → { ws, isVideoSender } }
const clients = {};

wss.on("connection", (ws) => {
    let peerId = null;

    // Registra un peer nel dizionario e notifica tutti gli altri della sua presenza.
    // isVideoSender=True → è il Quest che manda video
    // isVideoSender=False → è il browser che riceve
    function registerPeer(id, isVideoSender) {
        peerId = id;
        clients[peerId] = { ws, isVideoSender };
        console.log(`Peer registered: ${peerId} (isVideoSender=${isVideoSender})`);

        // Notifica i peer già connessi del nuovo arrivato, e viceversa.
        // Questo permette ai peer di sapere con chi possono connettersi.
        Object.entries(clients).forEach(([id, other]) => {
            if (id !== peerId && other.ws.readyState === WebSocket.OPEN) {
                other.ws.send(`NEWPEER|${peerId}|ALL|New peer ${peerId}|0|${isVideoSender}`);
                ws.send(`NEWPEER|${id}|ALL|New peer ${id}|0|${other.isVideoSender}`);
            }
        });
    }

    ws.on("message", (data) => {
        const msg = data.toString();
        console.log("RECEIVED:", msg.substring(0, 150));
        const parts = msg.split("|");
        const type = parts[0];

        if (type === "NEWPEER" || type === "CONNECT") {
            // Il peer si presenta con il suo ID e dichiara se è sender o receiver
            registerPeer(parts[1], parts[5] || "False");

        } else if (type === "NEWPEERACK") {
            // ACK di conferma: il peer ha ricevuto il NEWPEER. Va inoltrato a tutti
            // tranne il mittente per sincronizzare lo stato delle connessioni.
            Object.entries(clients).forEach(([id, other]) => {
                if (id !== parts[1] && other.ws.readyState === WebSocket.OPEN) {
                    console.log(`  -> Forwarding NEWPEERACK to ${id}`);
                    other.ws.send(msg);
                }
            });

        } else {
            // Tutti gli altri messaggi (OFFER, ANSWER, CANDIDATE, DISPOSE...)
            // hanno un destinatario specifico nel campo parts[2].
            const targetId = parts[2];
            console.log(`  -> Forwarding ${type} to ${targetId}`);

            if (targetId && targetId !== "ALL" && clients[targetId]) {
                // Messaggio diretto a un peer specifico
                clients[targetId].ws.send(msg);
            } else if (targetId === "ALL") {
                // Broadcast a tutti tranne il mittente
                Object.entries(clients).forEach(([id, other]) => {
                    if (id !== peerId && other.ws.readyState === WebSocket.OPEN) {
                        other.ws.send(msg);
                    }
                });
            } else {
                console.log(`  !! Target not found: ${targetId}`);
            }
        }
    });

    ws.on("close", () => {
        if (peerId) {
            delete clients[peerId];
            console.log(`Peer disconnected: ${peerId}`);

            // Quando un peer cade (tab chiusa, rete persa, crash), il server
            // manda PEERLEFT a tutti gli altri. È diverso da DISPOSE che viene
            // inviato dal peer stesso quando si disconnette volontariamente.
            // I client devono gestire PEERLEFT per ripulire le peer connection
            // e permettere la riconnessione con lo stesso ID.
            Object.values(clients).forEach(({ ws: client }) => {
                if (client.readyState === WebSocket.OPEN) {
                    client.send(`PEERLEFT|${peerId}|ALL|Peer left|0|False`);
                }
            });
        }
    });
});

console.log("Signaling server running on ws://0.0.0.0:8765");
