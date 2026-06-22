using Meta.XR;
using UnityEngine;
using UnityEngine.UI;

public class TextureBlender : MonoBehaviour
{
    [SerializeField] RenderTexture VRTexture;
    private RenderTexture PCARendTexture;
    [SerializeField] PassthroughController passthroughController;
    public PassthroughCameraAccess PCALeft;
    [SerializeField] Material blendMat;
    private Texture2D _passthroughTexture;
    private Texture2D VRTexture2D;
    public RawImage VRRawImage;

    private RenderTexture finalTexture;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        blendMat.SetTexture("_PCA_Texture", PCALeft.GetTexture());

        //Graphics.Blit(null, VRTexture, blendMat);
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    Texture2D Rend2Texture(RenderTexture source)
    {
        RenderTexture.active = source;
        VRTexture2D.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
        VRTexture2D.Apply();
        return VRTexture2D;
    }

    void GetPCAFeed()
    {
        _passthroughTexture = new Texture2D(1280, 960, TextureFormat.BGRA32, false);
        _passthroughTexture = PCALeft.GetTexture() as Texture2D;
        blendMat.SetTexture("PCA_Texture", _passthroughTexture);
    }
}
