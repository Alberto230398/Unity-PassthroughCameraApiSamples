const WebSocket = require("ws");
const wss = new WebSocket.Server({ port: 8765 });
const clients = {};

wss.on("connection", (ws) => {
    let peerId = null;

    function registerPeer(id, isVideoSender) {
        peerId = id;
        clients[peerId] = { ws, isVideoSender };
        console.log(`Peer registered: ${peerId} (isVideoSender=${isVideoSender})`);
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
            registerPeer(parts[1], parts[5] || "False");
        } else if (type === "NEWPEERACK") {
            Object.entries(clients).forEach(([id, other]) => {
                if (id !== parts[1] && other.ws.readyState === WebSocket.OPEN) {
                    console.log(`  -> Forwarding NEWPEERACK to ${id}`);
                    other.ws.send(msg);
                }
            });
        } else {
            const targetId = parts[2];
            console.log(`  -> Forwarding ${type} to ${targetId}`);
            if (targetId && targetId !== "ALL" && clients[targetId]) {
                clients[targetId].ws.send(msg);
            } else if (targetId === "ALL") {
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
            Object.values(clients).forEach(({ ws: client }) => {
                if (client.readyState === WebSocket.OPEN) {
                    client.send(`PEERLEFT|${peerId}|ALL|Peer left|0|False`);
                }
            });
        }
    });
});

console.log("Signaling server running on ws://0.0.0.0:8765");
