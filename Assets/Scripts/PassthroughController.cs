using System.Collections;
using Meta.XR;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

public class PassthroughController : MonoBehaviour
{
    [SerializeField] PassthroughCameraAccess PCALeft;
    [SerializeField] RawImage rawImage;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        //StartCoroutine(GetRGBFeed());
    }

    // Update is called once per frame
    void Update()
    {
    }

    IEnumerator GetRGBFeed(RenderTexture target)
    {
        while (true)
        {
            if (PCALeft.IsPlaying)
            {
                Texture left = PCALeft.GetTexture();
                if (left != null)
                {
                    // Do something with the texture
                    if (target==null)
                        target = new RenderTexture(left.width, left.height, 0, RenderTextureFormat.BGRA32);
                    Graphics.Blit(left, target);

                    //rawImage.texture = target;
                }
            }
            else
            {
                Debug.Log("Passthrough camera not playing");
            }

            yield return null; // Wait for the next frame
        }
    }

    public void GetPassthroughFeed(RenderTexture target)
    {
        StartCoroutine(GetRGBFeed(target));
    }
}
