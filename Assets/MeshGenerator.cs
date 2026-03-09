using Meta.XR;
using Meta.XR.EnvironmentDepth;
using UnityEngine;

public class MeshGenerator : MonoBehaviour
{
    [Header("Passthrough Camera")]
    [SerializeField] PassthroughCameraAccess passthroughCameraRight;
    [SerializeField] PassthroughCameraAccess passthroughCameraLeft;
    private Texture leftCamera;
    private Texture rightCamera;
    private Vector3 leftCameraPosition;
    private Quaternion leftCameraRotation;
    private Vector3 rightCameraPosition;
    private Quaternion rightCameraRotation;

    [Header("Depth Mesh Sensor")]
    [SerializeField] EnvironmentDepthManager environmentDepthManager;
    private Texture depthTexture;

    //Head Pose
    private Vector3 headPosition;
    private Quaternion headRotation;

    // Other data


    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        GetData();
    }

    void GetData()
    {
        GetCameraTexture();
        GetDepthTexture();
        GetUserHead();
        GetLeftCameraData();
        GetRightCameraData();

    }

    void GetCameraTexture()
    {
        leftCamera = passthroughCameraLeft.GetTexture();
        rightCamera = passthroughCameraRight.GetTexture();
    }

    void GetDepthTexture()
    {
        depthTexture = Shader.GetGlobalTexture("_EnvironmentDepthTexture");
    }

    void GetUserHead()
    {
        var headPose = OVRPlugin.GetNodePoseStateImmediate(OVRPlugin.Node.Head).Pose.ToOVRPose();
        headPosition = headPose.position;
        headRotation = headPose.orientation;
    }

    void GetLeftCameraData()
    {
        var cameraPose = passthroughCameraLeft.GetCameraPose();
        leftCameraPosition = cameraPose.position;
        leftCameraRotation = cameraPose.rotation;
    }

    void GetRightCameraData()
    {
        var cameraPose = passthroughCameraRight.GetCameraPose();
        rightCameraPosition = cameraPose.position;
        rightCameraRotation = cameraPose.rotation;
    }
}
