using System.Collections;
using UnityEngine;

public class KeyFrameManager : MonoBehaviour
{
    public Material blendMat;
    public Texture2D keyFrameTexture;
    private RenderTexture blendedTexture;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        blendedTexture = new RenderTexture(1920, 1920, 0);
        blendedTexture.Create();

        StartCoroutine(BlendAndSaveKeyFrame());
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    IEnumerator BlendAndSaveKeyFrame()
    {
        yield return new WaitForSeconds(3f);
        SaveKeyFrame();
    }


    void SaveKeyFrame()
    {
        string path = Application.dataPath + "/KeyFrames/";

        RenderTexture.active = blendedTexture;
        Texture2D blendedTexture2D = new Texture2D(blendedTexture.width, blendedTexture.height, TextureFormat.RGB24, false);
        blendedTexture2D.ReadPixels(new Rect(0, 0, blendedTexture.width, blendedTexture.height), 0, 0);
        blendedTexture2D.Apply();

        byte[] bytes = blendedTexture2D.EncodeToPNG();
        System.IO.File.WriteAllBytes(path + "KeyFrame_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png", bytes);
    }
}
