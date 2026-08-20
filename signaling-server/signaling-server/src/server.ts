import { WebSocketServer, WebSocket, type RawData } from "ws";

const PORT = 8765;

interface Client {
    ws: WebSocket;
    isVideoSender: string; // "True" | "False" come stringa, così com'è nel protocollo di signaling
}

const wss = new WebSocketServer({ port: PORT });
const clients: Record<string, Client> = {};

wss.on("connection", (ws: WebSocket) => {
    let peerId: string | null = null;

    function registerPeer(id: string, isVideoSender: string): void {
        peerId = id;
        clients[peerId] = { ws, isVideoSender };
        console.log(`Peer registered: ${peerId} (isVideoSender=${isVideoSender})`);
        for (const [otherId, other] of Object.entries(clients)) {
            if (otherId !== peerId && other.ws.readyState === WebSocket.OPEN) {
                other.ws.send(`NEWPEER|${peerId}|ALL|New peer ${peerId}|0|${isVideoSender}`);
                ws.send(`NEWPEER|${otherId}|ALL|New peer ${otherId}|0|${other.isVideoSender}`);
            }
        }
    }

    ws.on("message", (data: RawData) => {
        const msg = data.toString();
        console.log("RECEIVED:", msg.substring(0, 150));
        const parts = msg.split("|");
        const type = parts[0];

        if (type === "NEWPEER" || type === "CONNECT") {
            registerPeer(parts[1], parts[5] ?? "False");
        } else if (type === "NEWPEERACK") {
            for (const [otherId, other] of Object.entries(clients)) {
                if (otherId !== parts[1] && other.ws.readyState === WebSocket.OPEN) {
                    console.log(`  -> Forwarding NEWPEERACK to ${otherId}`);
                    other.ws.send(msg);
                }
            }
        } else {
            const targetId = parts[2];
            console.log(`  -> Forwarding ${type} to ${targetId}`);
            if (targetId && targetId !== "ALL" && clients[targetId]) {
                clients[targetId].ws.send(msg);
            } else if (targetId === "ALL") {
                for (const [otherId, other] of Object.entries(clients)) {
                    if (otherId !== peerId && other.ws.readyState === WebSocket.OPEN) {
                        other.ws.send(msg);
                    }
                }
            } else {
                console.log(`  !! Target not found: ${targetId}`);
            }
        }
    });

    ws.on("close", () => {
        if (peerId) {
            delete clients[peerId];
            console.log(`Peer disconnected: ${peerId}`);
            for (const { ws: client } of Object.values(clients)) {
                if (client.readyState === WebSocket.OPEN) {
                    client.send(`PEERLEFT|${peerId}|ALL|Peer left|0|False`);
                }
            }
        }
    });
});

console.log(`Signaling server running on ws://0.0.0.0:${PORT}`);
