using UnityEngine;
using UnityEngine.UI;

public class VirtualCamera : MonoBehaviour, VideoInterface
{
    [SerializeField] Camera virtualCamera;
    public RawImage image;
    public Material mat;

    public void Start()
    {  
    }
    public RenderTexture initVideo(RenderTexture target)
    {
        virtualCamera.targetTexture = target;
        image.texture = target;
        mat.color = Color.red;
        return target;
    }

    public void stop() => virtualCamera.targetTexture = null;
}
