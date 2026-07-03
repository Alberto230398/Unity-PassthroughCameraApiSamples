using System.Reflection;
using SimpleWebRTC;
using UnityEngine;
using UnityEngine.UI;

// Chiede l'indirizzo del signaling server via tastiera di sistema ad ogni avvio,
// cosi' non serve piu' ribuildare l'app quando l'IP del server cambia (rete diversa, ecc).
// WebSocketServerAddress su WebRTCConnection e' privato: viene impostato via reflection,
// stesso approccio gia' usato in VideoManager.GetVideoSenders() per leggere campi privati del package.
public class ServerAddressPrompt : MonoBehaviour
{
    const string PrefsKey = "SignalingServerAddress";
    const string DefaultAddress = "ws://10.10.10.147:8765";

    static readonly FieldInfo AddressField =
        typeof(WebRTCConnection).GetField("WebSocketServerAddress", BindingFlags.NonPublic | BindingFlags.Instance);

    WebRTCConnection _webRTCConnection;
    TouchScreenKeyboard _keyboard;

    GameObject _displayGO;
    Text _displayText;

    void Awake()
    {
        _webRTCConnection = GetComponentInParent<WebRTCConnection>() ?? FindAnyObjectByType<WebRTCConnection>();
    }

    void Start()
    {
        BuildDisplayUI();

        string prefill = PlayerPrefs.GetString(PrefsKey, DefaultAddress);
        //_displayText.text = prefill;
        _keyboard = TouchScreenKeyboard.Open("", TouchScreenKeyboardType.URL, false, false, false, false, "ws://indirizzo:porta");
    }

    void Update()
    {
        if (_keyboard == null) return;

        // TouchScreenKeyboard non mostra da solo il testo digitato nella scena VR:
        // lo rispecchiamo ogni frame nel Text creato in BuildDisplayUI così l'utente vede cosa sta scrivendo.
        _displayText.text = string.IsNullOrEmpty(_keyboard.text) ? "..." : _keyboard.text;

        switch (_keyboard.status)
        {
            case TouchScreenKeyboard.Status.Done:
                Apply(_keyboard.text);
                _keyboard = null;
                break;
            case TouchScreenKeyboard.Status.Canceled:
            case TouchScreenKeyboard.Status.LostFocus:
                // Nessun indirizzo confermato: riusa l'ultimo salvato (o il default) e prova comunque a connettersi.
                Apply(PlayerPrefs.GetString(PrefsKey, DefaultAddress));
                _keyboard = null;
                break;
        }
    }

    // Crea un piccolo World Space Canvas agganciato alla camera, solo per dare un riscontro
    // visivo di cosa si sta digitando. Costruito interamente a codice per non dipendere da
    // Canvas/font gia' presenti in scena.
    void BuildDisplayUI()
    {
        var cam = Camera.main != null ? Camera.main : FindAnyObjectByType<Camera>();

        _displayGO = new GameObject("ServerAddressPrompt-Display");
        if (cam != null) _displayGO.transform.SetParent(cam.transform, false);
        _displayGO.transform.localPosition = new Vector3(0, -0.15f, 0.6f);
        _displayGO.transform.localRotation = Quaternion.identity;
        _displayGO.transform.localScale = Vector3.one * 0.001f;

        var canvas = _displayGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var canvasRT = canvas.GetComponent<RectTransform>();
        canvasRT.sizeDelta = new Vector2(800, 200);

        var textGO = new GameObject("Text");
        textGO.transform.SetParent(_displayGO.transform, false);
        var textRT = textGO.AddComponent<RectTransform>();
        textRT.anchorMin = Vector2.zero;
        textRT.anchorMax = Vector2.one;
        textRT.offsetMin = Vector2.zero;
        textRT.offsetMax = Vector2.zero;

        _displayText = textGO.AddComponent<Text>();
        _displayText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        _displayText.fontSize = 36;
        _displayText.alignment = TextAnchor.MiddleCenter;
        _displayText.color = Color.white;
    }

    void Apply(string address)
    {
        if (_displayGO != null) Destroy(_displayGO);

        address = address?.Trim();
        if (string.IsNullOrEmpty(address)) address = DefaultAddress;

        PlayerPrefs.SetString(PrefsKey, address);
        PlayerPrefs.Save();

        if (_webRTCConnection == null)
        {
            Debug.LogError("[ServerAddressPrompt] Nessuna WebRTCConnection trovata in scena.");
            return;
        }

        if (AddressField == null)
        {
            Debug.LogError("[ServerAddressPrompt] Campo WebSocketServerAddress non trovato via reflection: il package SimpleWebRTC potrebbe essere stato aggiornato.");
            return;
        }

        AddressField.SetValue(_webRTCConnection, address);
        Debug.Log($"[ServerAddressPrompt] Signaling server impostato a {address}");
        _webRTCConnection.Connect();
    }
}
