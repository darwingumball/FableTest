using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Player
{
    /// <summary>
    /// CharacterController-based first person movement on the new Input System.
    /// Reads from the "Gameplay" action map of the assigned actions asset.
    /// Only enabled on the owning client (<see cref="Game.Net.NetworkPlayer"/> gates it).
    ///
    /// External systems can push per-frame world-space displacement through
    /// <see cref="AddExternalMove"/> (moving platforms use this so riders get carried
    /// through the same CharacterController.Move call as their own walking).
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class FirstPersonController : MonoBehaviour
    {
        [Header("Input")]
        [SerializeField] private InputActionAsset inputActions;

        [Header("Movement")]
        [SerializeField] private float walkSpeed = 4.5f;
        [SerializeField] private float sprintSpeed = 7f;
        [SerializeField] private float crouchSpeed = 2.2f;
        [Tooltip("Horizontal velocity snaps straight to the input on the ground - no " +
                 "ramp in, no slide out. Any acceleration ramp reads as inertia when " +
                 "strafing, because reversing direction is a full 2x speed change.")]
        [SerializeField] private bool instantGroundControl = true;
        [Tooltip("Ground acceleration (m/s^2), used only when instantGroundControl is off.")]
        [SerializeField] private float acceleration = 60f;
        [Tooltip("Ground deceleration when input is released. Unused when instant.")]
        [SerializeField] private float deceleration = 70f;
        [Tooltip("Acceleration while airborne - low keeps jumps committal.")]
        [SerializeField] private float airAcceleration = 12f;
        [SerializeField] private float jumpHeight = 1.1f;
        [SerializeField] private float gravity = -20f;
        [SerializeField] private float coyoteTime = 0.15f;

        [Header("Crouch")]
        [SerializeField] private float standHeight = 1.9f;
        [SerializeField] private float crouchHeight = 1.2f;
        [SerializeField] private float crouchLerpSpeed = 10f;

        [Header("Look")]
        [SerializeField] private Transform cameraPivot;
        [Tooltip("Degrees of rotation per unit of mouse delta.")]
        [SerializeField] private float lookSensitivity = 0.08f;
        [SerializeField] private float pitchClamp = 89f;
        [Tooltip("0 = raw (crisp, can feel steppy at low fps). ~0.03-0.06 smooths without " +
                 "adding noticeable lag.")]
        [SerializeField, Range(0f, 0.15f)] private float lookSmoothing = 0.03f;

        public bool IsSprinting { get; private set; }
        public bool IsCrouching { get; private set; }
        public bool IsGrounded => _controller.isGrounded;
        public float CameraPitch => _pitch;
        public Vector3 HorizontalVelocity => new(_velocity.x, 0f, _velocity.z);

        private CharacterController _controller;
        private InputAction _moveAction, _lookAction, _jumpAction, _sprintAction, _crouchAction;
        private Vector3 _velocity;
        private Vector2 _smoothedLook;
        private float _pitch;
        private float _yaw;
        private float _lastGroundedTime;
        private float _speedMultiplier = 1f;
        private float _externalSensitivityScale = 1f;
        private bool _invertY;
        private Vector3 _externalMove;
        private bool _controlEnabled = true;
        private bool _lookEnabled = true;

        /// <summary>
        /// Re-reads facing from the transform. Call after any external repositioning
        /// (spawn, teleport, save restore) so the tracked yaw doesn't snap back.
        /// </summary>
        public void SyncRotationFromTransform()
        {
            _yaw = transform.eulerAngles.y;
            transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
        }

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _yaw = transform.eulerAngles.y;
            if (inputActions != null)
            {
                var map = inputActions.FindActionMap("Gameplay", throwIfNotFound: true);
                _moveAction = map.FindAction("Move", true);
                _lookAction = map.FindAction("Look", true);
                _jumpAction = map.FindAction("Jump", true);
                _sprintAction = map.FindAction("Sprint", true);
                _crouchAction = map.FindAction("Crouch", true);
            }
            else
            {
                Debug.LogError("[FirstPersonController] No InputActionAsset assigned.", this);
            }
        }

        private void OnEnable()
        {
            inputActions?.FindActionMap("Gameplay")?.Enable();
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void OnDisable()
        {
            // The action map is shared (interaction/pickup read it too), so leave it
            // enabled; UI code owns cursor state + map switching when menus open.
        }

        private void Update()
        {
            if (_controller == null || _moveAction == null) return;

            if (_lookEnabled)
                ApplyLook(_lookAction.ReadValue<Vector2>());

            ApplyMovement();
        }

        private void ApplyLook(Vector2 lookDelta)
        {
            // Mouse delta is already per-frame, so it must NOT be multiplied by deltaTime.
            // Smoothing is a short exponential average: it removes per-frame jitter (very
            // visible at unstable editor framerates) without adding input lag.
            if (lookSmoothing > 0f)
            {
                float t = 1f - Mathf.Exp(-Time.unscaledDeltaTime / lookSmoothing);
                _smoothedLook = Vector2.Lerp(_smoothedLook, lookDelta, t);
            }
            else
            {
                _smoothedLook = lookDelta;
            }

            float sens = lookSensitivity * _externalSensitivityScale;
            Vector2 delta = _smoothedLook;
            if (_invertY) delta.y = -delta.y;

            // Track yaw explicitly and write an upright rotation instead of Rotate():
            // Rotate() compounds onto whatever the body already holds, so any stray pitch
            // or roll (spawn placement, teleports, external code) turns into camera roll.
            _yaw += delta.x * sens;
            transform.rotation = Quaternion.Euler(0f, _yaw, 0f);

            _pitch = Mathf.Clamp(_pitch - delta.y * sens, -pitchClamp, pitchClamp);
            if (cameraPivot != null)
                cameraPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }

        private void ApplyMovement()
        {
            bool grounded = _controller.isGrounded;
            if (grounded) _lastGroundedTime = Time.time;

            Vector2 moveInput = _controlEnabled ? _moveAction.ReadValue<Vector2>() : Vector2.zero;
            IsCrouching = _controlEnabled && _crouchAction.IsPressed();
            IsSprinting = _controlEnabled && !IsCrouching && _sprintAction.IsPressed() && moveInput.y > 0.1f;

            float targetHeight = IsCrouching ? crouchHeight : standHeight;
            if (!Mathf.Approximately(_controller.height, targetHeight))
            {
                // Don't stand up into a ceiling.
                if (IsCrouching || !Physics.SphereCast(
                        transform.position + Vector3.up * (_controller.height - _controller.radius),
                        _controller.radius * 0.9f, Vector3.up,
                        out _, standHeight - _controller.height + 0.05f,
                        ~0, QueryTriggerInteraction.Ignore))
                {
                    _controller.height = Mathf.Lerp(_controller.height, targetHeight, Time.deltaTime * crouchLerpSpeed);
                    _controller.center = new Vector3(0f, _controller.height * 0.5f, 0f);
                }
            }

            float targetSpeed = (IsSprinting ? sprintSpeed : IsCrouching ? crouchSpeed : walkSpeed) * _speedMultiplier;
            Vector3 wishDir = (transform.right * moveInput.x + transform.forward * moveInput.y);
            if (wishDir.sqrMagnitude > 1f) wishDir.Normalize();
            Vector3 targetHorizontal = wishDir * targetSpeed;

            Vector3 horizontal;
            if (grounded && instantGroundControl)
            {
                // Snap. Airborne movement still ramps, so a jump stays committal - that
                // reads as momentum you chose, not as the controller lagging your input.
                horizontal = targetHorizontal;
            }
            else
            {
                float rate = !grounded ? airAcceleration
                    : (wishDir.sqrMagnitude > 0.01f ? acceleration : deceleration);
                horizontal = Vector3.MoveTowards(HorizontalVelocity, targetHorizontal, rate * Time.deltaTime);
            }
            _velocity.x = horizontal.x;
            _velocity.z = horizontal.z;

            bool canJump = Time.time - _lastGroundedTime <= coyoteTime;
            if (_controlEnabled && _jumpAction.WasPressedThisFrame() && canJump && !IsCrouching)
            {
                _velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
                _lastGroundedTime = -999f;
            }

            if (grounded && _velocity.y < 0f)
                _velocity.y = -2f;
            _velocity.y += gravity * Time.deltaTime;

            Vector3 motion = _velocity * Time.deltaTime + _externalMove;
            _externalMove = Vector3.zero;
            _controller.Move(motion);
        }

        /// <summary>World-space displacement applied on the next Move (moving platform carry).</summary>
        public void AddExternalMove(Vector3 worldDelta) => _externalMove += worldDelta;

        /// <summary>Disables movement input (dialogs, menus). Look is controlled separately.</summary>
        public void SetMoveControl(bool enabled) => _controlEnabled = enabled;

        public void SetLookControl(bool enabled) => _lookEnabled = enabled;

        public void SetControl(bool enabled)
        {
            _controlEnabled = enabled;
            _lookEnabled = enabled;
        }

        /// <summary>Slow-down for heavy carried items etc. 1 = normal.</summary>
        public void SetSpeedMultiplier(float multiplier) => _speedMultiplier = Mathf.Clamp(multiplier, 0.1f, 2f);

        /// <summary>Settings menu hook. 1 = default sensitivity.</summary>
        public void SetSensitivityScale(float scale) => _externalSensitivityScale = Mathf.Clamp(scale, 0.05f, 5f);

        public void SetInvertY(bool invert) => _invertY = invert;

        public InputActionAsset InputActions => inputActions;
    }
}
