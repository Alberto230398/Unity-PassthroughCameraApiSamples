using Meta.XR;
using System.Collections;
//using System.Drawing;
using UnityEngine;
using UnityEngine.UI;
#if WEBRTC_ENABLED
using SimpleWebRTC;
#endif

namespace QuestCameraKit.WebRTC {
    public class WebRTCController : MonoBehaviour {

    public Material material;
#if WEBRTC_ENABLED
        [SerializeField] private WebRTCConnection _webRTCConnection;

        private void Awake() {
            if (_webRTCConnection == null)
                _webRTCConnection = GetComponentInParent<WebRTCConnection>() ?? FindAnyObjectByType<WebRTCConnection>();
        }

        private void Update() {
            if (_webRTCConnection == null) return;

            /*if (OVRInput.GetDown(OVRInput.Button.One)) {
                ChangeColor(Color.blue);
                _webRTCConnection.StartVideoTransmission();
                Debug.Log("BUTTON DETECTED");
                ChangeColor(Color.red);
            }*/
#if UNITY_EDITOR
            if (Input.GetKeyUp(KeyCode.Space)) {
                _webRTCConnection.StartVideoTransmission();
                Debug.Log("Space Bar");
            }
#endif
        }
#endif
    public void ChangeColor(Color c)
        {
            if (material != null)
                material.color = c;
        }
    }


}
