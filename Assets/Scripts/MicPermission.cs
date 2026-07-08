using UnityEngine;
using UnityEngine.Android;

public class MicPermissionHandler : MonoBehaviour
{
    void Start()
    {
        #if PLATFORM_ANDROID
        if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
        {
            Permission.RequestUserPermission(Permission.Microphone);
        }
        #endif
    }
}

