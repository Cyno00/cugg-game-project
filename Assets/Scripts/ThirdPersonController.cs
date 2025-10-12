using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public class ThirdPersonController : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 4.0f;
    public float sprintSpeed = 6.5f;
    public float acceleration = 12f;
    public float deceleration = 14f;
    [Range(0f, 1f)] public float airControl = 0.5f;

    [Header("Jump & Gravity")]
    public float jumpHeight = 1.3f;
    public float gravity = 20f;
    public float fallMultiplier = 1.5f;
    public float coyoteTime = 0.1f;

    [Header("Grounding")]
    public LayerMask groundMask = ~0;
    public float groundCheckDistance = 0.2f;
    public float slopeLimit = 45f;

    [Header("Rotation")]
    public float turnSpeed = 10f;

    [Header("References")]
    public Transform cameraTransform;

    [Header("Input Actions")]
    public InputActionReference moveAction;    // Vector2 (WASD / LeftStick)
    public InputActionReference jumpAction;    // Button  (Space / South)
    public InputActionReference sprintAction;  // Button  (Left Shift / Left Stick Press)

    private CharacterController controller;
    private Vector3 velocity;
    private float currentSpeed;
    private float lastGroundedTime;

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        controller.slopeLimit = slopeLimit;

        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;
    }

    private void OnEnable()
    {
        moveAction?.action.Enable();
        jumpAction?.action.Enable();
        sprintAction?.action.Enable();
    }

    private void OnDisable()
    {
        moveAction?.action.Disable();
        jumpAction?.action.Disable();
        sprintAction?.action.Disable();
    }

    private void Update()
    {
        if (cameraTransform == null) return;

        // INPUT (new system)
        Vector2 move2D = moveAction != null ? moveAction.action.ReadValue<Vector2>() : Vector2.zero;
        bool jumpPressed = jumpAction != null && jumpAction.action.WasPressedThisFrame();
        bool sprintHeld = sprintAction != null && sprintAction.action.IsPressed();

        move2D = Vector2.ClampMagnitude(move2D, 1f);

        // CAMERA-RELATIVE
        Vector3 camF = cameraTransform.forward; camF.y = 0f; camF.Normalize();
        Vector3 camR = cameraTransform.right; camR.y = 0f; camR.Normalize();
        Vector3 desiredMove = (camF * move2D.y + camR * move2D.x).normalized;

        // TARGET SPEED & SMOOTHING
        float targetSpeed = (sprintHeld ? sprintSpeed : moveSpeed) * desiredMove.magnitude;
        float accel = IsGrounded() ? acceleration : acceleration * airControl;
        float decc = IsGrounded() ? deceleration : deceleration * (0.5f + 0.5f * airControl);

        if (targetSpeed > currentSpeed)
            currentSpeed = Mathf.MoveTowards(currentSpeed, targetSpeed, accel * Time.deltaTime);
        else
            currentSpeed = Mathf.MoveTowards(currentSpeed, targetSpeed, decc * Time.deltaTime);

        Vector3 horizontalVel = desiredMove * currentSpeed;

        // GROUND CHECK
        bool grounded = GroundCheck();
        if (grounded)
        {
            lastGroundedTime = Time.time;
            if (velocity.y < 0f) velocity.y = -2f;
        }

        // JUMP (with coyote time)
        bool canCoyote = (Time.time - lastGroundedTime) <= coyoteTime;
        if (jumpPressed && (grounded || canCoyote))
            velocity.y = Mathf.Sqrt(2f * gravity * jumpHeight);

        // GRAVITY
        float g = gravity * ((velocity.y < 0f) ? fallMultiplier : 1f);
        velocity.y -= g * Time.deltaTime;

        // MOVE
        Vector3 motion = horizontalVel * Time.deltaTime + Vector3.up * velocity.y * Time.deltaTime;
        controller.Move(motion);

        // FACE MOVE DIRECTION
        Vector3 look = new Vector3(horizontalVel.x, 0f, horizontalVel.z);
        if (look.sqrMagnitude > 0.0001f)
        {
            Quaternion targetRot = Quaternion.LookRotation(look, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, turnSpeed * Time.deltaTime);
        }
    }

    private bool GroundCheck()
    {
        bool ccGrounded = controller.isGrounded;
        Vector3 origin = transform.position + Vector3.up * 0.05f;
        float dist = controller.skinWidth + groundCheckDistance;

        if (Physics.SphereCast(origin, 0.2f, Vector3.down, out _, dist, groundMask, QueryTriggerInteraction.Ignore))
            return true;

        return ccGrounded;
    }

    private bool IsGrounded()
    {
        return controller.isGrounded || (Time.time - lastGroundedTime) <= 0.02f;
    }
}
