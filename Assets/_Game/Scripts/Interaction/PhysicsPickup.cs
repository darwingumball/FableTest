using Game.Net;
using Game.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Interaction
{
    /// <summary>
    /// Hold-Grab physics carry: raycast a Rigidbody, float it in front of the camera with
    /// a spring-damper, scroll to adjust distance, Throw to launch. Networked items get
    /// ownership transferred while held (forces only work on a dynamic body we own).
    /// Runs only on the owning player.
    /// </summary>
    public class PhysicsPickup : MonoBehaviour
    {
        [Header("Pickup")]
        [SerializeField] private float grabRange = 4f;
        [SerializeField] private float minHoldDistance = 1f;
        [SerializeField] private float maxHoldDistance = 5f;
        [SerializeField] private float scrollSensitivity = 0.4f;
        [SerializeField] private LayerMask grabMask = ~0;

        [Header("Physics")]
        [SerializeField] private float springForce = 30f;
        [SerializeField] private float damping = 8f;
        [SerializeField] private float maxForce = 50f;
        [SerializeField] private float rotationSmoothSpeed = 10f;
        [SerializeField] private float throwForce = 8f;

        [Header("Heavy items")]
        [SerializeField] private float heavyMassThreshold = 8f;
        [SerializeField, Range(0.2f, 1f)] private float heavySpeedMultiplier = 0.55f;

        public bool IsHolding => _heldBody != null;

        private Camera _camera;
        private NetworkPlayer _player;
        private Player.FirstPersonController _fpc;
        private InputAction _grabAction, _throwAction, _scrollAction;

        private Rigidbody _heldBody;
        private WorldItemNetworkSync _heldSync;
        private float _holdDistance;
        private float _origDrag, _origAngularDrag;
        private bool _origGravity;
        private bool _heavyApplied;

        private void Awake()
        {
            _fpc = GetComponent<Player.FirstPersonController>();
            _player = GetComponent<NetworkPlayer>();
            if (_player != null) _camera = _player.PlayerCamera;
        }

        private void Start()
        {
            if (_camera == null && _player != null)
                _camera = _player.PlayerCamera;
            if (_fpc != null && _fpc.InputActions != null)
            {
                var map = _fpc.InputActions.FindActionMap("Gameplay");
                _grabAction = map?.FindAction("Grab");
                _throwAction = map?.FindAction("Throw");
                _scrollAction = map?.FindAction("AdjustHoldDistance");
            }
        }

        private void Update()
        {
            if (_camera == null || _grabAction == null) return;
            if (_player != null && !_player.IsOwner) return;

            // Held body destroyed externally? Clean up the speed penalty.
            if (_heldBody == null && _heavyApplied) ClearHeld();

            bool uiBlocked = PauseMenu.IsOpen || TabMenuUI.IsOpen;

            if (_grabAction.WasPressedThisFrame() && _heldBody == null && !uiBlocked)
                TryGrab();

            if (_grabAction.WasReleasedThisFrame() && _heldBody != null)
                Release(false);

            if (_heldBody != null && _throwAction != null && _throwAction.WasPressedThisFrame())
                Release(true);

            if (_heldBody != null && _scrollAction != null)
            {
                float scroll = _scrollAction.ReadValue<float>();
                if (Mathf.Abs(scroll) > 0.01f)
                    _holdDistance = Mathf.Clamp(_holdDistance + Mathf.Sign(scroll) * scrollSensitivity,
                        minHoldDistance, maxHoldDistance);
            }
        }

        private void FixedUpdate()
        {
            if (_heldBody == null) return;

            // Ownership round-trip pending: our copy is still kinematic, forces are no-ops.
            if (_heldBody.isKinematic) return;

            Vector3 targetPos = _camera.transform.position + _camera.transform.forward * _holdDistance;
            Vector3 force = (targetPos - _heldBody.position) * springForce - _heldBody.linearVelocity * damping;
            if (force.magnitude > maxForce) force = force.normalized * maxForce;
            _heldBody.AddForce(force, ForceMode.Acceleration);

            Quaternion targetRot = Quaternion.LookRotation(_camera.transform.forward, Vector3.up);
            _heldBody.MoveRotation(Quaternion.Slerp(_heldBody.rotation, targetRot,
                Time.fixedDeltaTime * rotationSmoothSpeed));
        }

        private void TryGrab()
        {
            var ray = new Ray(_camera.transform.position, _camera.transform.forward);
            if (!Physics.Raycast(ray, out var hit, grabRange, grabMask, QueryTriggerInteraction.Ignore)) return;

            var rb = hit.collider.GetComponentInParent<Rigidbody>();
            if (rb == null) return;

            var sync = rb.GetComponent<WorldItemNetworkSync>();
            // Non-networked kinematic bodies (movers etc.) are not grabbable; networked
            // items are kinematic on non-owners and become dynamic after ownership swap.
            if (sync == null && rb.isKinematic) return;

            _heldBody = rb;
            _heldSync = sync;
            _holdDistance = Mathf.Clamp(hit.distance, minHoldDistance, maxHoldDistance);

            _origDrag = rb.linearDamping;
            _origAngularDrag = rb.angularDamping;
            _origGravity = rb.useGravity;
            rb.useGravity = false;
            rb.linearDamping = 1f;
            rb.angularDamping = 3f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            sync?.RequestOwnership();

            if (_fpc != null && rb.mass >= heavyMassThreshold)
            {
                _fpc.SetSpeedMultiplier(heavySpeedMultiplier);
                _heavyApplied = true;
            }
        }

        public void Release(bool throwing)
        {
            if (_heldBody != null)
            {
                _heldBody.useGravity = _origGravity;
                _heldBody.linearDamping = _origDrag;
                _heldBody.angularDamping = _origAngularDrag;
                _heldBody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                if (throwing)
                    _heldBody.AddForce(_camera.transform.forward * throwForce, ForceMode.VelocityChange);
                _heldSync?.ReleaseOwnership();
            }
            ClearHeld();
        }

        private void ClearHeld()
        {
            _heldBody = null;
            _heldSync = null;
            if (_heavyApplied && _fpc != null) _fpc.SetSpeedMultiplier(1f);
            _heavyApplied = false;
        }
    }
}
