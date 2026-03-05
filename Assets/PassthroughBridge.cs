// In Assets/Scripts/PassthroughBridge.cs
using Meta.XR;
using UnityEngine;

public class PassthroughBridge : MonoBehaviour
{
    public static RenderTexture Texture { get; private set; }

    private PassthroughCameraAccess _pca;

    private void Awake() => _pca = GetComponent<PassthroughCameraAccess>();

    private void Update()
    {
        if (_pca.IsPlaying)
            Texture = _pca.GetTexture() as RenderTexture;
    }
}