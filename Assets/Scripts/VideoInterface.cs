using UnityEngine;

public interface VideoInterface
{
    RenderTexture initVideo(RenderTexture target);
    void stop();
}
