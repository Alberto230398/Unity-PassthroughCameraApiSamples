using System.Collections;
using UnityEngine;

public class ObjSpawner : MonoBehaviour
{
    public GameObject objectToSpawn;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        Debug.Log("HELLO!");
        StartCoroutine(SpawnObjects());
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    IEnumerator SpawnObjects()
    {
        yield return new WaitForSeconds(3f);
        objectToSpawn.SetActive(true);
    }
}
