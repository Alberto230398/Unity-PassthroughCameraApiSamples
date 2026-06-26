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
        blitCoroutine = StartCoroutine(BlitPassthroughStereo(target));
        return target;
    }

    private IEnumerator BlitPassthroughStereo(RenderTexture target)
    {
        if (_blitMat != null) Destroy(_blitMat);
        _blitMat = Instantiate(sideBySideMat);

        while (true)
        {
            if (useStereoPassthrough)
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
            }
            else
            {
                if (passthroughCameraLeft.IsPlaying)
                {
                    Texture left = passthroughCameraLeft.GetTexture();
                    if (left != null)
                        Graphics.Blit(left, target);
                }
            }
            yield return null;
        }
    }

    void VideoInterface.stop()
    {
        if (blitCoroutine != null)
        {
            StopCoroutine(blitCoroutine);
            blitCoroutine = null;
        }
        if (_blitMat != null)
        {
            Destroy(_blitMat);
            _blitMat = null;
        }
    }
}
