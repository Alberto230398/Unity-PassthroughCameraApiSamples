using System.Collections;
using Meta.XR;
using UnityEngine;
using UnityEngine.Rendering;

public class PassthroughVideo : MonoBehaviour, VideoInterface
{
    [SerializeField] PassthroughCameraAccess passthroughCameraLeft;
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
                /*else
                {
                    Debug.Log("-----------STEREO PASSTHROUGH-------------");
                    Texture left = passthroughCameraLeft.GetTexture();
                    Texture right = passthroughCameraRight.GetTexture();
                    if (left != null && right != null)
                    {
                        cmd.Clear();
                        cmd.SetRenderTarget(target);
                        cmd.Blit(left, target, new Vector2(0.5f, 1f), new Vector2(0f, 0f));
                        cmd.Blit(right, target, new Vector2(0.5f, 1f), new Vector2(0.5f, 0f));
                        Graphics.ExecuteCommandBuffer(cmd);
                    }
                }
            }*/
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
