using UnityEngine;

public class Movement : MonoBehaviour
{
    [Header("Movement Settings")]
    [SerializeField] private float moveSpeed = 1f;

    [Header("Rotation Settings")]
    [SerializeField] private float rotationSpeed = 45f;
    [SerializeField] private bool rotateAroundCenter;

    [Header("Vertical Movement Settings")]
    [SerializeField] private float verticalSpeed = 1f;

    private Vector3 visualCenter;
    private Transform controlTarget;

    // Tutti i Rigidbody nel macchinario
    private Rigidbody[] allRigidbodies;
    private bool[] originalKinematicState;

    // Riferimento al Grabbable Building Block
    private MonoBehaviour handGrabInteractable;

    // Stato controller
    private bool controllersWereActive = false;

    private Camera mainCamera;

    public bool isGrabbed = false;
    public ObjectSpawner objectSpawner; // Riferimento allo script ObjectSpawner
    void Start()
    {
        mainCamera = Camera.main;
        // Il target è il parent (macchinario)
        controlTarget = transform;

        if (controlTarget == null)
        {
            Debug.LogError("❌ ObjectController deve essere su un oggetto FIGLIO, non sul parent!");
            return;
        }

        Debug.Log($"✅ Controllo l'oggetto: {controlTarget.name}");

        // Trova TUTTI i Rigidbody nel macchinario (parent e children)
        allRigidbodies = controlTarget.GetComponentsInChildren<Rigidbody>();
        originalKinematicState = new bool[allRigidbodies.Length];

        Debug.Log($"🔧 Trovati {allRigidbodies.Length} Rigidbody:");
        for (int i = 0; i < allRigidbodies.Length; i++)
        {
            originalKinematicState[i] = allRigidbodies[i].isKinematic;
            Debug.Log($"   [{i}] {allRigidbodies[i].gameObject.name} - Kinematic: {allRigidbodies[i].isKinematic}");
        }

        // Trova l'HandGrabInteractable (sul Grabbable Building Block)
        MonoBehaviour[] components = GetComponentsInChildren<MonoBehaviour>();
        foreach (MonoBehaviour component in components)
        {
            if (component.GetType().Name == "HandGrabInteractable")
            {
                handGrabInteractable = component;
                Debug.Log($"🎯 HandGrabInteractable trovato su: {component.gameObject.name}");
                break;
            }
        }

        // Calcola il centro visivo
        CalculateVisualCenter();
    }

    void Update()
    {
        if (objectSpawner != null && objectSpawner.HasObject() && !isGrabbed)
        {
            return;
        }
        
        if (controlTarget == null) return;

        // Controlla se i controller sono in mano (connessi e attivi)
        bool controllersActive = AreControllersInHand();

        // Cambia modalità quando cambia lo stato dei controller
        if (controllersActive && !controllersWereActive)
        {
            // Controller appena presi in mano
            EnableControllerMode();
            controllersWereActive = true;
        }
        else if (!controllersActive && controllersWereActive)
        {
            // Controller appena posati
            EnableHandMode();
            controllersWereActive = false;
        }

        // Gestisci movimento, rotazione e altezza SOLO se i controller sono in mano
        if (controllersActive)
        {
            HandleMovement();
            HandleRotation();
            HandleVerticalMovement();
        }
    }

    bool AreControllersInHand()
    {
        // Controlla se i controller sono connessi e attivi
        OVRInput.Controller connectedControllers = OVRInput.GetConnectedControllers();

        // Considera i controller "in mano" se almeno uno dei due è connesso
        bool leftActive = (connectedControllers & OVRInput.Controller.LTouch) != 0;
        bool rightActive = (connectedControllers & OVRInput.Controller.RTouch) != 0;

        return leftActive || rightActive;
    }

    void EnableControllerMode()
    {
        Debug.Log("🎮 CONTROLLER IN MANO - Disabilitando interferenze...");

        // Ricalcola il centro visivo quando entri in modalità controller
        if (rotateAroundCenter)
        {
            CalculateVisualCenter();
        }

        // Disabilita TUTTI i Rigidbody
        for (int i = 0; i < allRigidbodies.Length; i++)
        {
            if (allRigidbodies[i] != null)
            {
                allRigidbodies[i].isKinematic = true;
                allRigidbodies[i].linearVelocity = Vector3.zero;
                allRigidbodies[i].angularVelocity = Vector3.zero;
                Debug.Log($"   ✓ {allRigidbodies[i].gameObject.name} → Kinematic");
            }
        }

        // Disabilita l'HandGrabInteractable
        if (handGrabInteractable != null)
        {
            handGrabInteractable.enabled = false;
            Debug.Log("   ✓ HandGrabInteractable → Disabilitato");
        }

        Debug.Log("✅ Modalità movimento ATTIVA - Interferenze disabilitate");
    }

    void EnableHandMode()
    {
        Debug.Log("✋ CONTROLLER POSATI - Riabilitando interazioni...");

        // Ripristina lo stato originale di tutti i Rigidbody
        for (int i = 0; i < allRigidbodies.Length; i++)
        {
            if (allRigidbodies[i] != null)
            {
                allRigidbodies[i].isKinematic = originalKinematicState[i];
                Debug.Log($"   ✓ {allRigidbodies[i].gameObject.name} → Kinematic: {originalKinematicState[i]}");
            }
        }

        // Riabilita l'HandGrabInteractable
        if (handGrabInteractable != null)
        {
            handGrabInteractable.enabled = true;
            Debug.Log("   ✓ HandGrabInteractable → Abilitato");
        }

        Debug.Log("✅ Modalità interazione ATTIVA - Hand tracking abilitato");
    }

    void CalculateVisualCenter()
    {
        Renderer[] renderers = controlTarget.GetComponentsInChildren<Renderer>();

        if (renderers.Length > 0)
        {
            Bounds combinedBounds = renderers[0].bounds;
            foreach (Renderer r in renderers)
            {
                combinedBounds.Encapsulate(r.bounds);
            }

            visualCenter = combinedBounds.center;
            Debug.Log($"✅ Centro visivo calcolato: {visualCenter}");

            // Visualizza il centro visivo per debug
            Debug.DrawLine(visualCenter + Vector3.up * 0.5f, visualCenter - Vector3.up * 0.5f, Color.red, 5f);
            Debug.DrawLine(visualCenter + Vector3.right * 0.5f, visualCenter - Vector3.right * 0.5f, Color.green, 5f);
            Debug.DrawLine(visualCenter + Vector3.forward * 0.5f, visualCenter - Vector3.forward * 0.5f, Color.blue, 5f);
        }
        else
        {
            visualCenter = controlTarget.position;
            Debug.LogWarning("⚠️ Nessun Renderer trovato, uso la posizione del transform");
        }
    }

    void HandleMovement()
    {
        // Leggi stick sinistro
        Vector2 leftStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);

        if (leftStick.magnitude > 0.1f)
        {
            //Camera mainCamera = Camera.main;
            if (mainCamera != null)
            {
                Vector3 cameraForward = mainCamera.transform.forward;
                Vector3 cameraRight = mainCamera.transform.right;

                cameraForward.y = 0;
                cameraRight.y = 0;
                cameraForward.Normalize();
                cameraRight.Normalize();

                Vector3 movement = cameraRight * leftStick.x + cameraForward * leftStick.y;
                Vector3 movementDelta = movement * moveSpeed * Time.deltaTime;

                controlTarget.position += movementDelta;

                // IMPORTANTE: Aggiorna anche il centro visivo quando ti muovi
                if (rotateAroundCenter)
                {
                    visualCenter += movementDelta;
                }
            }
        }
    }

    void HandleVerticalMovement()
    {
        // Leggi pulsanti X e Y del controller SINISTRO
        bool buttonX = OVRInput.Get(OVRInput.Button.Two, OVRInput.Controller.LTouch);   // X - Alza
        bool buttonY = OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.LTouch);   // Y - Abbassa

        float verticalDirection = 0f;

        if (buttonX)
        {
            verticalDirection = 1f;  // Alza
        }
        else if (buttonY)
        {
            verticalDirection = -1f; // Abbassa
        }

        if (verticalDirection != 0f)
        {
            Vector3 verticalMovement = Vector3.up * verticalDirection * verticalSpeed * Time.deltaTime;
            controlTarget.position += verticalMovement;

            // Aggiorna anche il centro visivo
            if (rotateAroundCenter)
            {
                visualCenter += verticalMovement;
            }
        }
    }

    void HandleRotation()
    {
        // Leggi pulsanti X e Y del controller DESTRO
        bool buttonX = OVRInput.Get(OVRInput.Button.Two, OVRInput.Controller.RTouch);   // X
        bool buttonY = OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.RTouch);   // Y

        float rotationDirection = 0f;

        if (buttonY)
        {
            rotationDirection = 1f;
        }
        else if (buttonX)
        {
            rotationDirection = -1f;
        }

        if (rotationDirection != 0f)
        {
            float rotation = rotationDirection * rotationSpeed * Time.deltaTime;

            if (rotateAroundCenter)
            {
                // Salva la posizione attuale del centro
                Vector3 pivotPoint = visualCenter;

                // Calcola l'offset dal centro al macchinario
                Vector3 offset = controlTarget.position - pivotPoint;

                // Ruota l'offset
                offset = Quaternion.Euler(0, rotation, 0) * offset;

                // Riposiziona il macchinario
                controlTarget.position = pivotPoint + offset;

                // Ruota il macchinario su se stesso
                controlTarget.Rotate(0, rotation, 0, Space.World);
            }
            else
            {
                // Rotazione semplice attorno al proprio pivot
                controlTarget.Rotate(0, rotation, 0, Space.World);
            }
        }
    }
}
