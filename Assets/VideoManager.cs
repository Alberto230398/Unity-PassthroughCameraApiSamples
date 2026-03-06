using System.Collections;
using System.Linq;
using Meta.XR;
using SimpleWebRTC;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Video;
using static Unity.Burst.Intrinsics.X86.Avx;

public class VideoManager : MonoBehaviour
{
    [SerializeField] MonoBehaviour[] videoSources;
    VideoInterface[] sources => videoSources.Select(s => s as VideoInterface).ToArray();

    [SerializeField] int activeSourceIndex = 0;

    RenderTexture camRenderTexture;
    VideoInterface currentSource;

    void OnEnable() => WebRTCConnection.OnRequestVideoTrack += CreateVideo;
    void OnDisable() => WebRTCConnection.OnRequestVideoTrack -= CreateVideo;

    bool videoActive;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {

    }

    // Update is called once per frame
    void Update()
    {
        if (OVRInput.GetDown(OVRInput.Button.Two) && currentSource != null)
            SwitchSource((activeSourceIndex + 1) % sources.Length);
    }

    // VideoManager
    public RenderTexture CreateVideo()
    {
        camRenderTexture = new RenderTexture(1280, 960, 0, RenderTextureFormat.BGRA32);
        camRenderTexture.Create();
        SwitchSource(activeSourceIndex);
        return camRenderTexture;
    }

    void SwitchSource(int index)
    {
        currentSource?.stop();
        activeSourceIndex = index;
        currentSource = sources[index];
        currentSource.initVideo(camRenderTexture);
    }

   
    /*public void RenderPassthroughCamera()
    {
        camRenderTexture = new RenderTexture(1280, 960, 0, RenderTextureFormat.BGRA32);
        camRenderTexture.Create();
        Graphics.Blit(passthroughCameraLeft.GetTexture(), camRenderTexture);
        StartCoroutine(BlitPassthrough(camRenderTexture));
    }

    public void RenderPassthroughCameraStereo()
    {
        camRenderTexture = new RenderTexture(1280, 960, 0, RenderTextureFormat.BGRA32);
        camRenderTexture.Create();
        Graphics.Blit(passthroughCameraLeft.GetTexture(), camRenderTexture);
        StartCoroutine(BlitPassthrough(camRenderTexture));
    }

    public void RenderVirtualCamera()
    {
        camRenderTexture = new RenderTexture(1280, 960, 0, RenderTextureFormat.BGRA32);
        camRenderTexture.Create();
        virtualCamera.targetTexture = camRenderTexture;
    }

    private IEnumerator BlitPassthrough(RenderTexture target)
    {
        var cmd = new CommandBuffer();
        while (true)
        {
            //if (!IsVideoTransmissionActive) yield break;
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
            }
            }
            yield return null;
        }
    }*/
}
