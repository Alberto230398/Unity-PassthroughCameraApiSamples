using Meta.XR;
using UnityEngine;

public class TextureBlender : MonoBehaviour
{
    [SerializeField] RenderTexture VRTexture;
    private RenderTexture PCARendTexture;
    [SerializeField] PassthroughController passthroughController;
    public PassthroughCameraAccess PCALeft;
    [SerializeField] Material blendMat;
    private Texture2D _passthroughTexture;
    private Texture2D VRTexture2D;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        //_passthroughTexture = new Texture2D(1280, 960, TextureFormat.BGRA32, false);
        //passthroughController.GetPassthroughFeed(_passthroughTexture);

        //blendMat.SetTexture("PCA_Texture", _passthroughTexture);

        //VRTexture2D = new Texture2D(VRTexture.width, VRTexture.height, TextureFormat.BGRA32, false);

        //VRTexture2D = Rend2Texture(VRTexture);
        //blendMat.SetTexture("VR_Texture", VRTexture2D);

        //GetPCAFeed();

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
        /*passthroughController.GetPassthroughFeed(PCARendTexture);
        _passthroughTexture = new Texture2D(PCARendTexture.width, PCARendTexture.height, TextureFormat.BGRA32, false);
        _passthroughTexture = Rend2Texture(PCARendTexture);*/

        _passthroughTexture = new Texture2D(1280, 960, TextureFormat.BGRA32, false);
        _passthroughTexture = PCALeft.GetTexture() as Texture2D;
        blendMat.SetTexture("PCA_Texture", _passthroughTexture);
    }
}
