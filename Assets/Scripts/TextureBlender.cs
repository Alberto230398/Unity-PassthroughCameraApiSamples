using System.Collections;
using System.Linq;
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

        StartCoroutine(LogSensorInfo());
    }

    // Diagnostica: stampa la risoluzione REALE della camera (che può differire da
    // RequestedResolution) e tutti i parametri intrinseci del sensore. Le Intrinsics
    // sono valide solo dopo che la camera è in play, quindi aspettiamo IsPlaying.
    IEnumerator LogSensorInfo()
    {
        while (!PCALeft.IsPlaying)
            yield return null;

        var supported = PassthroughCameraAccess.GetSupportedResolutions(PCALeft.CameraPosition);
        var supportedStr = supported != null && supported.Length > 0
            ? string.Join(", ", supported.Select(r => $"{r.x}x{r.y}"))
            : "(nessuna / permesso mancante)";

        var res = PCALeft.CurrentResolution;
        var intr = PCALeft.Intrinsics;
        var focal = intr.FocalLength;      // fx, fy in pixel
        var pp = intr.PrincipalPoint;      // cx, cy in pixel
        var sensor = intr.SensorResolution;
        var lens = intr.LensOffset;        // posa del sensore rispetto alla testa


        // FOV ricavati dalle intrinsics: FOV = 2*atan(dim / (2*focal))
        float fovH = 2f * Mathf.Atan2(res.x, 2f * focal.x) * Mathf.Rad2Deg;
        float fovV = 2f * Mathf.Atan2(res.y, 2f * focal.y) * Mathf.Rad2Deg;

        Debug.Log(
            "[TextureBlender] === Passthrough sensor info ===\n" +
            $"CameraPosition      : {PCALeft.CameraPosition}\n" +
            $"RequestedResolution : {PCALeft.RequestedResolution}\n" +
            $"CurrentResolution   : {res.x}x{res.y}  (aspect {(float)res.x / res.y:F3})\n" +
            $"SupportedResolutions: {supportedStr}\n" +
            $"SensorResolution    : {sensor.x}x{sensor.y}\n" +
            $"FocalLength (fx,fy) : ({focal.x:F2}, {focal.y:F2}) px\n" +
            $"PrincipalPoint(cx,cy): ({pp.x:F2}, {pp.y:F2}) px  -> centro img: ({res.x / 2f:F1}, {res.y / 2f:F1})\n" +
            $"FOV (H x V)         : {fovH:F2}° x {fovV:F2}°\n" +
            $"MaxFramerate        : {PCALeft.MaxFramerate}\n" +
            $"LensOffset pos      : {lens.position}\n" +
            $"LensOffset rot(euler): {lens.rotation.eulerAngles}"
        );
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
