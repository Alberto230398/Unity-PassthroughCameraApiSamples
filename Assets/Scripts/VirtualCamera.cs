using UnityEngine;

public class VirtualCamera : MonoBehaviour, VideoInterface
{
    [SerializeField] Camera virtualCamera;

    public RenderTexture initVideo(RenderTexture target)
    {
        virtualCamera.targetTexture = target;
        return target;
    }

    public void stop() => virtualCamera.targetTexture = null;
}