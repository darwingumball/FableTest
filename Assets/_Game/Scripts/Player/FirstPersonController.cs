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
    /// <see cref="ApplyCarry"/> (moving platforms use this to carry their riders).
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

        [Header("Swimming")]
        [Tooltip("Horizontal speed while swimming.")]
        [SerializeField] private float swimSpeed = 3.2f;
        [Tooltip("Rise rate while the jump key is held.")]
        [SerializeField] private float swimUpSpeed = 2.6f;
        [Tooltip("Sink rate when the jump key is NOT held. Deliberately slow - fast enough " +
                 "to feel like treading water is a choice, slow enough not to be punishing.")]
        [SerializeField] private float sinkSpeed = 1.1f;
        [Tooltip("How far the head can rise above the waterline while swimming up. Without " +
                 "this cap you launch clear of the water like a cork.")]
        [SerializeField] private float swimSurfaceClamp = 0.25f;

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
        /// <summary>
        /// Raw movement stick, ungated by <see cref="SetMoveControl"/>. Systems that take
        /// the player over and steer something else with WASD - the ladder, the boat helm -
        /// need the input precisely while the player's own movement is suppressed. Menus
        /// still cut it off, because opening one disables the whole Gameplay action map.
        /// </summary>
        public Vector2 MoveInput => _moveAction?.ReadValue<Vector2>() ?? Vector2.zero;
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
        private bool _controlEnabled = true;
        private bool _lookEnabled = true;
        private bool _inWater;
        private float _submersion;
        private float _waterSurfaceY;
        private bool _climbing;

        /// <summary>
        /// Re-reads facing from the transform. Call after any external repositioning
        /// (spawn, teleport, save restore) so the tracked yaw doesn't snap back.
        /// </summary>
        public void SyncRotationFromTransform()
        {
            _yaw = transform.eulerAngles.y;
            transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
        }

        /// <summary>
        /// Turns the player with a rotating surface underfoot (a boat).
        ///
        /// This has to add to the TRACKED yaw, not the transform: ApplyLook rewrites the
        /// rotation from _yaw every frame, so a rotation written straight to the transform
        /// is erased on the next frame and the player appears welded to world north while
        /// the deck turns under them.
        /// </summary>
        public void AddYaw(float degrees) => SetYaw(_yaw + degrees);

        /// <summary>Faces an absolute heading. Same tracked-yaw requirement as <see cref="AddYaw"/>.</summary>
        public void SetYaw(float degrees)
        {
            _yaw = degrees;
            transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
        }

        /// <summary>
        /// Places the body ignoring collision. The CharacterController has to be cycled or
        /// it keeps its own cached position and snaps back on the next Move.
        ///
        /// Used by <see cref="Game.World.Ladder"/> every frame while climbing: a ladder pins
        /// you to a fixed track, and moving along it with Move() would let the hull it is
        /// bolted to shove you off that track.
        /// </summary>
        public void TeleportTo(Vector3 worldPosition)
        {
            _controller.enabled = false;
            transform.position = worldPosition;
            _controller.enabled = true;
            _velocity = Vector3.zero;
        }

        /// <summary>
        /// Hands vertical position over to a ladder. Movement, gravity and jumping are all
        /// suppressed; look stays free so you can still see where you are going.
        /// </summary>
        public void SetClimbing(bool climbing)
        {
            if (_climbing == climbing) return;
            _climbing = climbing;
            _velocity = Vector3.zero;
            // _lastGroundedTime is deliberately left stale, so stepping off a ladder
            // halfway up does not hand out a free coyote-time jump.
        }

        public bool IsClimbing => _climbing;

        /// <summary>
        /// Reported by <see cref="Game.World.SwimmerProbe"/>. <paramref name="submersion"/>
        /// is metres of the body below the waterline.
        /// </summary>
        public void SetSwimState(bool inWater, float submersion, float surfaceY)
        {
            _inWater = inWater;
            _submersion = submersion;
            _waterSurfaceY = surfaceY;
        }

        public bool IsSwimming => _inWater;

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
            if (_climbing)
            {
                // The ladder drives position outright - it has to, because the rungs may
                // themselves be moving on a boat. Nothing here may add gravity, walking or
                // a carry delta on top of that. Checked before the swim branch so grabbing
                // a ladder from the water actually gets you out of it.
                return;
            }

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

            Vector3 wishDirRaw = (transform.right * moveInput.x + transform.forward * moveInput.y);
            if (wishDirRaw.sqrMagnitude > 1f) wishDirRaw.Normalize();

            if (_inWater)
            {
                ApplySwimming(wishDirRaw);
                return;
            }

            float targetSpeed = (IsSprinting ? sprintSpeed : IsCrouching ? crouchSpeed : walkSpeed) * _speedMultiplier;
            Vector3 wishDir = wishDirRaw;
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

            _controller.Move(_velocity * Time.deltaTime);
        }

        /// <summary>
        /// Swimming. No gravity accumulation and no jump: vertical motion is entirely a
        /// choice. Hold jump to rise, let go and you sink - slowly, so treading water reads
        /// as a decision rather than a punishment.
        /// </summary>
        private void ApplySwimming(Vector3 wishDir)
        {
            Vector3 horizontal = wishDir * (swimSpeed * _speedMultiplier);
            _velocity.x = horizontal.x;
            _velocity.z = horizontal.z;

            bool rising = _controlEnabled && _jumpAction.IsPressed();
            float vertical = rising ? swimUpSpeed : -sinkSpeed;

            // Don't let a held jump fire the player out of the water. Once the body is at
            // the surface, rising is capped to holding station there.
            if (rising)
            {
                float headroom = _waterSurfaceY + swimSurfaceClamp - transform.position.y;
                if (headroom <= 0f) vertical = 0f;
                else vertical = Mathf.Min(vertical, headroom / Mathf.Max(Time.deltaTime, 1e-4f));
            }
            _velocity.y = vertical;

            _controller.Move(_velocity * Time.deltaTime);
        }

        /// <summary>
        /// Carries the player with a moving surface, applied IMMEDIATELY.
        ///
        /// This used to queue the delta for the next Update, which put the rider a full
        /// frame behind the platform: platforms move after the player has already moved, so
        /// a queued delta always lands late. An elevator at 2 m/s hides that; a boat at
        /// 9 m/s in a seaway does not - it reads as the deck stuttering underneath you, and
        /// when the deck is also heaving it lets the hull sweep through the capsule before
        /// the capsule is told to move.
        /// </summary>
        public void ApplyCarry(Vector3 worldDelta)
        {
            if (worldDelta == Vector3.zero || _climbing) return;

            // A purely horizontal Move can leave the controller reporting airborne on a
            // deck it is plainly standing on, which costs the rider their jump and starts
            // gravity ramping. A hair of downward bias keeps the contact alive; the
            // controller stops at the deck, so it does not accumulate.
            bool grounded = _controller.isGrounded;
            _controller.Move(grounded ? worldDelta + Vector3.down * 0.02f : worldDelta);
        }

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
