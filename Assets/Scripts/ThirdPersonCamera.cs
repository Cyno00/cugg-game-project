using UnityEngine;
using UnityEngine.InputSystem;

public class ThirdPersonCamera : MonoBehaviour
{
    [Header("Target")]
    public Transform target;
    public Vector3 targetOffset = new Vector3(0f, 1.6f, 0f);

    [Header("Orbit")]
    public float mouseXSensitivity = 150f;
    public float mouseYSensitivity = 120f;
    public float minPitch = -35f;
    public float maxPitch = 70f;
    public float rotationDamp = 12f;

    [Header("Zoom")]
    public float minDistance = 1.2f;
    public float maxDistance = 6.0f;
    public float distanceDamp = 10f;

    [Header("Collision")]
    public LayerMask collisionMask = ~0;
    public float collisionRadius = 0.25f;
    public float collisionBuffer = 0.1f;

    [Header("Input Actions")]
    public InputActionReference lookAction;         // Vector2 (Mouse delta / RightStick)
    public InputActionReference zoomAction;         // float  (Mouse scroll / Triggers)
    public InputActionReference toggleCursorAction; // Button (Esc / Start)

    [Header("Cursor")]
    public bool lockCursorOnStart = true;

    private float yaw;
    private float pitch;
    private float desiredDistance;
    private float currentDistance;
    private Vector3 currentPivotPos;

    private void Start()
    {
        if (target == null)
        {
            Debug.LogWarning("ThirdPersonCamera_InputSystem: No target assigned.");
            enabled = false;
            return;
        }

        Vector3 dir = transform.position - (target.position + targetOffset);
        desiredDistance = Mathf.Clamp(dir.magnitude, minDistance, maxDistance);
        currentDistance = desiredDistance;

        Vector3 angles = transform.eulerAngles;
        yaw = angles.y;
        pitch = Mathf.Clamp(NormalizePitch(angles.x), minPitch, maxPitch);

        if (lockCursorOnStart) ApplyCursorLock(true);
    }

    private void OnEnable()
    {
        lookAction?.action.Enable();
        zoomAction?.action.Enable();
        toggleCursorAction?.action.Enable();

        if (toggleCursorAction != null)
            toggleCursorAction.action.performed += OnToggleCursor;
    }

    private void OnDisable()
    {
        if (toggleCursorAction != null)
            toggleCursorAction.action.performed -= OnToggleCursor;

        lookAction?.action.Disable();
        zoomAction?.action.Disable();
        toggleCursorAction?.action.Disable();
    }

    private void Update()
    {
        // Look input
        Vector2 look = lookAction != null ? lookAction.action.ReadValue<Vector2>() : Vector2.zero;
        yaw += look.x * mouseXSensitivity * Time.deltaTime;
        pitch -= look.y * mouseYSensitivity * Time.deltaTime;
        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);

        // Zoom input
        float zoomDelta = zoomAction != null ? zoomAction.action.ReadValue<float>() : 0f;
        desiredDistance = Mathf.Clamp(desiredDistance - zoomDelta, minDistance, maxDistance);
    }

    private void LateUpdate()
    {
        if (target == null) return;

        // Smooth rotation
        Quaternion desiredRot = Quaternion.Euler(pitch, yaw, 0f);
        transform.rotation = Quaternion.Slerp(transform.rotation, desiredRot, 1f - Mathf.Exp(-rotationDamp * Time.deltaTime));

        // Smooth pivot (player + offset)
        Vector3 pivot = target.position + targetOffset;
        currentPivotPos = Vector3.Lerp(currentPivotPos, pivot, 1f - Mathf.Exp(-rotationDamp * Time.deltaTime));

        // Smooth distance
        currentDistance = Mathf.Lerp(currentDistance, desiredDistance, 1f - Mathf.Exp(-distanceDamp * Time.deltaTime));

        // Desired camera position
        Vector3 desiredCamPos = currentPivotPos - transform.rotation * Vector3.forward * currentDistance;

        // Collision push-in
        Vector3 toCam = desiredCamPos - currentPivotPos;
        float dist = toCam.magnitude;
        if (dist > 0.001f)
        {
            Vector3 dir = toCam / dist;
            if (Physics.SphereCast(currentPivotPos, collisionRadius, dir, out RaycastHit hit, dist + collisionBuffer, collisionMask, QueryTriggerInteraction.Ignore))
            {
                float safe = Mathf.Clamp(hit.distance - collisionBuffer, minDistance * 0.4f, dist);
                desiredCamPos = currentPivotPos + dir * safe;
            }
        }

        transform.position = desiredCamPos;
    }

    private void OnToggleCursor(InputAction.CallbackContext ctx)
    {
        bool lockNow = Cursor.lockState != CursorLockMode.Locked;
        ApplyCursorLock(lockNow);
    }

    private void ApplyCursorLock(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    // Keep pitch in -180..180 before clamping (prevents jump when crossing 360)
    private float NormalizePitch(float xAngle)
    {
        xAngle = Mathf.Repeat(xAngle + 180f, 360f) - 180f;
        return xAngle;
    }
}
