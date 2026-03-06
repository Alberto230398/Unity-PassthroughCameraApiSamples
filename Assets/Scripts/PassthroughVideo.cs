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
        StartCoroutine(BlitPassthrough(target));
        return target;
    }

    private IEnumerator BlitPassthrough(RenderTexture target)
    {
        var cmd = new CommandBuffer();
        while (true)
        {
            if (passthroughCameraLeft.IsPlaying)
            {
                if (!useStereoPassthrough)
                {
                    Texture src = passthroughCameraLeft.GetTexture();
                    if (src != null) Graphics.Blit(src, target);
                }
                else
                {
                    Texture left = passthroughCameraLeft.GetTexture();
                    Texture right = passthroughCameraRight.GetTexture();

                    if (left != null && right != null)
                    {
                        sideBySideMat.SetTexture("_LeftTex", left);
                        sideBySideMat.SetTexture("_RightTex", right);
                        Graphics.Blit(null, target, sideBySideMat);
                    }
                }
                yield return null;
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
