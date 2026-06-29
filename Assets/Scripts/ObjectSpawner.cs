using UnityEngine;

public class ObjectSpawner : MonoBehaviour
{
    [SerializeField] private GameObject objectToSpawn;
    [SerializeField] private Transform rightHand;

    private GameObject spawnedObject;

    Vector3 initialPosition;

    bool hasObject; 

    public bool debug;
    public float distance = 2f;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        initialPosition = new Vector3(rightHand.position.x*distance, rightHand.position.y, rightHand.position.z);
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
            InstantiateObject();
            hasObject = true;
        }
        else
        {
            LeaveObject();
            hasObject = false;
        }
    }

    void InstantiateObject()
    {
        spawnedObject = Instantiate(objectToSpawn, initialPosition, rightHand.rotation);
        spawnedObject.transform.SetParent(rightHand);
    }

    void LeaveObject()
    {
        spawnedObject.transform.SetParent(null);
        spawnedObject = null;
    }
}
