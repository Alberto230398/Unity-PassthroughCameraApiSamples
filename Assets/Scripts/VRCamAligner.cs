using Meta.XR;
using UnityEngine;

/// <summary>
/// Allinea la camera che renderizza il virtuale (VRCam) alla camera fisica del
/// passthrough, così il virtuale combacia col reale nel frame compositato.
///
/// Corregge le tre cause di disallineamento/swim:
///  - PROIEZIONE: FOV ricavato dalle intrinsics reali del sensore.
///  - POSA: posizione + rotazione da GetCameraPose(), che include il LensOffset
///    (offset e ~11° di pitch del sensore rispetto alla testa).
///  - TIMING: GetCameraPose() usa la posa all'istante di cattura del frame, quindi
///    la vista del virtuale corrisponde al frame passthrough attualmente sulla texture.
/// </summary>
[RequireComponent(typeof(Camera))]
public class VRCamAligner : MonoBehaviour
{
    [SerializeField] private PassthroughCameraAccess passthroughCamera;
    [Tooltip("TrackingSpace dell'OVRCameraRig (il parent degli *EyeAnchor). Serve a " +
             "convertire la posa da tracking space a world space. Se il rig NON è " +
             "all'origine e lasci questo campo vuoto, l'allineamento sballa.")]
    [SerializeField] private Transform trackingSpace;
    [Tooltip("Se ON usa la projectionMatrix esatta da intrinsics (gestisce anche il " +
             "principal point decentrato). Se OFF usa il solo FOV verticale: sufficiente " +
             "quando il principal point è quasi centrato.")]
    [SerializeField] private bool useExactProjection = false;

    [Header("Diagnostica")]
    [Tooltip("Se ON applica l'allineamento (posa + proiezione). Mettilo OFF per vedere " +
             "il disallineamento com'è ORA: il log del delta continua a girare senza toccare la camera.")]
    [SerializeField] private bool applyAlignment = true;
    [Tooltip("Logga (throttled) la differenza tra la posa attuale di VRCam e quella " +
             "della camera fisica (GetCameraPose). Serve a vedere numericamente il gap, es. gli ~11° di pitch.")]
    [SerializeField] private bool logPoseDelta = false;
    [SerializeField] private float logIntervalSeconds = 1f;

    private Camera _cam;
    private float _nextLogTime;

    private void Awake() => _cam = GetComponent<Camera>();

    // LateUpdate: la posa della testa è già aggiornata per questo frame.
    private void LateUpdate()
    {
        if (passthroughCamera == null || !passthroughCamera.IsPlaying)
            return;

        // --- POSA (include LensOffset + timestamp del frame) ---
        // GetCameraPose() è in TRACKING SPACE: va convertita in world tramite la
        // transform del TrackingSpace, altrimenti (rig non all'origine) sballa "di tanto".
        var trackingPose = passthroughCamera.GetCameraPose();
        Vector3 worldPos;
        Quaternion worldRot;
        if (trackingSpace != null)
        {
            worldPos = trackingSpace.TransformPoint(trackingPose.position);
            worldRot = trackingSpace.rotation * trackingPose.rotation;
        }
        else
        {
            // Fallback: assume tracking space == world (valido solo se il rig è all'origine).
            worldPos = trackingPose.position;
            worldRot = trackingPose.rotation;
        }
        var pose = new Pose(worldPos, worldRot);

        // Catturo la posa di VRCam PRIMA dell'allineamento (per il log before/after).
        transform.GetPositionAndRotation(out var posBefore, out var rotBefore);

        if (applyAlignment)
            transform.SetPositionAndRotation(pose.position, pose.rotation);

        // Diagnostica: posizione di VRCam prima e dopo, più il target fisico.
        // Con applyAlignment=OFF "dopo" coincide con "prima" (non scrivo la posa).
        if (logPoseDelta && Time.time >= _nextLogTime)
        {
            _nextLogTime = Time.time + Mathf.Max(0.1f, logIntervalSeconds);
            var posAfter = transform.position;
            var deltaBefore = Vector3.Distance(posBefore, pose.position);
            var deltaAfter = Vector3.Distance(posAfter, pose.position);
            var angBefore = Quaternion.Angle(rotBefore, pose.rotation);
            Debug.Log(
                "[VRCamAligner] Posizione VRCam (before/after allineamento)\n" +
                $"PRIMA  : {posBefore}   (delta dalla fisica: {deltaBefore * 100f:F1} cm, {angBefore:F2}°)\n" +
                $"DOPO   : {posAfter}   (delta dalla fisica: {deltaAfter * 100f:F1} cm)\n" +
                $"FISICA : {pose.position}");
        }

        if (!applyAlignment)
            return;

        // --- PROIEZIONE ---
        var res = passthroughCamera.CurrentResolution;
        var intr = passthroughCamera.Intrinsics;

        if (useExactProjection)
        {
            _cam.projectionMatrix = BuildProjectionFromIntrinsics(
                intr.FocalLength, intr.PrincipalPoint, res, _cam.nearClipPlane, _cam.farClipPlane);
        }
        else
        {
            // FOV verticale dalle intrinsics. Impostiamo asse verticale + aspect reale
            // così Unity ricava correttamente anche il FOV orizzontale.
            _cam.usePhysicalProperties = false;
            _cam.ResetProjectionMatrix();
            _cam.aspect = (float)res.x / res.y;
            _cam.fieldOfView = 2f * Mathf.Atan2(res.y, 2f * intr.FocalLength.y) * Mathf.Rad2Deg;
        }
    }

    // Matrice di proiezione off-axis dalle intrinsics del pinhole (fx,fy,cx,cy).
    // Nota: cy viene ribaltato perché le coordinate immagine hanno origine in alto,
    // mentre Unity/NDC ha y verso l'alto.
    private static Matrix4x4 BuildProjectionFromIntrinsics(
        Vector2 focal, Vector2 principal, Vector2Int res, float near, float far)
    {
        float w = res.x, h = res.y;
        float fx = focal.x, fy = focal.y;
        float cx = principal.x, cy = principal.y;

        var m = new Matrix4x4();
        m[0, 0] = 2f * fx / w;
        m[1, 1] = 2f * fy / h;
        m[0, 2] = 1f - 2f * cx / w;
        m[1, 2] = 2f * cy / h - 1f;
        m[2, 2] = -(far + near) / (far - near);
        m[2, 3] = -(2f * far * near) / (far - near);
        m[3, 2] = -1f;
        return m;
    }
}
