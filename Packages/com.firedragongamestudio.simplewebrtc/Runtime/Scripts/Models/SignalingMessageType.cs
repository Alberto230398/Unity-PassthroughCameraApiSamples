using System;

namespace SimpleWebRTC {
    [Serializable]
    public enum SignalingMessageType {
        NEWPEER,
        NEWPEERACK,
        OFFER,
        ANSWER,
        CANDIDATE,
        DATA,
        DISPOSE,
        PEERLEFT,  // inviato dal SERVER quando un peer cade senza mandare DISPOSE
        COMPLETE,
        OTHER
    }
}