using UnityEngine;

public class FPSManager : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
   void Start()
{
    OVRManager.display.displayFrequency = 72f;
    Application.targetFrameRate = 72;
    OVRManager.foveatedRenderingLevel = OVRManager.FoveatedRenderingLevel.Low;
    OVRManager.useDynamicFixedFoveatedRendering = true; // si adatta al carico GPU
}

    // Update is called once per frame
    void Update()
    {
        
    }
}
