using System.Collections;
using Meta.XR;
using UnityEngine;
using UnityEngine.Rendering;

public class PassthroughVideo : MonoBehaviour, VideoInterface
{
    [SerializeField] PassthroughCameraAccess passthroughCameraLeft;
    [SerializeField] PassthroughCameraAccess passthroughCameraRight;
    [SerializeField] Material sideBySideMat;
    [SerializeField] bool useStereoPassthrough;
    Coroutine blitCoroutine;
    private Material _blitMat;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {

    }

    // Update is called once per frame
    void Update()
    {

    }

    public RenderTexture initVideo(RenderTexture target)
    {
        StartCoroutine(BlitPassthroughStereo(target));
        return target;
    }

    private IEnumerator BlitPassthroughStereo(RenderTexture target)
    {
        _blitMat = Instantiate(sideBySideMat); // istanza privata

        while (true)
        {
            if (passthroughCameraLeft.IsPlaying && passthroughCameraRight.IsPlaying)
            {
                Texture left = passthroughCameraLeft.GetTexture();
                Texture right = passthroughCameraRight.GetTexture();

                if (left != null && right != null)
                {
                    _blitMat.SetTexture("_RightTex", right);
                    Graphics.Blit(left, target, _blitMat);
                }
            }
            yield return null;
        }
    }

    void VideoInterface.stop()
    {
        if (blitCoroutine != null)
            StopCoroutine(blitCoroutine);
    }
}
