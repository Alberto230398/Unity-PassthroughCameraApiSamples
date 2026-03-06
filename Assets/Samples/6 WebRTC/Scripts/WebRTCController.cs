using Meta.XR;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
#if WEBRTC_ENABLED
using SimpleWebRTC;
#endif

namespace QuestCameraKit.WebRTC {
    public class WebRTCController : MonoBehaviour {

#if WEBRTC_ENABLED
        [SerializeField] private WebRTCConnection _webRTCConnection;

        private void Update() {
            if (_webRTCConnection == null) return; // guard
             
            if (OVRInput.GetDown(OVRInput.Button.One)) {
                if (!_webRTCConnection.IsVideoTransmissionActive)
                    _webRTCConnection.StartVideoTransmission();
                    Debug.Log("BUTTON DETECTED");
            }
#if UNITY_EDITOR
            if (Input.GetKeyUp(KeyCode.Space)) {
                _webRTCConnection.StartVideoTransmission();
                Debug.Log("Space Bar");
            }
#endif
        }
#endif
    }
}
