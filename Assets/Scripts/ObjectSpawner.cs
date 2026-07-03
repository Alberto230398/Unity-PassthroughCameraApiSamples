using UnityEngine;

public class ObjectSpawner : MonoBehaviour
{
    [SerializeField] private GameObject objectToSpawn;
    [SerializeField] private Transform rightHand;

    private GameObject spawnedObject;

    [SerializeField] private Transform parent;

    Vector3 initialPosition;

    public bool hasObject; 

    public bool debug;
    public float distance = 2f;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        initialPosition = new Vector3(rightHand.position.x, rightHand.position.y, rightHand.position.z*distance);
    }

    // Update is called once per frame
    void Update()
    {
        if (debug || OVRInput.GetDown(OVRInput.Button.SecondaryHandTrigger))
        {
            debug = false;
            SpawnObject();
        }
    }

    void SpawnObject()
    {
        if (!hasObject)
        {
            Debug.Log("Spawning object");
            InstantiateObject();
            hasObject = true;
        }
        else
        {
            Debug.Log("Leaving object");
            LeaveObject();
            hasObject = false;
        }
    }

    void InstantiateObject()
    {
        spawnedObject = Instantiate(objectToSpawn, initialPosition, rightHand.rotation);
        spawnedObject.transform.SetParent(rightHand);

        spawnedObject.GetComponent<Movement>().isGrabbed = true;
        spawnedObject.GetComponent<Movement>().objectSpawner = this;
    }

    void LeaveObject()
    {
        spawnedObject.transform.SetParent(null);
        spawnedObject.transform.SetParent(parent);
        spawnedObject.GetComponent<Movement>().isGrabbed = false;
        spawnedObject.GetComponent<Movement>().enabled = false;
        spawnedObject = null;
        hasObject = false;
    }

    public bool HasObject()
    {
        return hasObject;
    }
}
