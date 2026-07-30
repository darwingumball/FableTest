using Game.Net;
using Game.UI;
using Game.World;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Interaction
{
    /// <summary>
    /// Hold-Grab physics carry: raycast a Rigidbody, float it in front of the camera with
    /// a spring-damper, scroll to adjust distance, Throw to launch. Networked items get
    /// ownership transferred while held (forces only work on a dynamic body we own).
    /// Runs only on the owning player.
    ///
    /// Holding cargo over a <see cref="PlacementZone"/> also previews lashing it down, and
    /// releasing on a green preview commits it - the same mechanism the crane uses, so a
    /// barrel carried aboard by hand ends up in exactly the state one craned aboard does.
    /// Releasing on red just drops it, which is what makes the preview worth reading.
    ///
    /// Rotate (R) adds a manual yaw offset on top of the camera-facing hold rotation, stepped
    /// by whatever zone the item is currently over - furniture wants 15 degree turns, a deck
    /// lashing area wants 30. <see cref="PlacementZone.Plan"/> snaps to its own grid regardless
    /// of what is fed in, so stepping by exactly that zone's increment is what makes every
    /// press of R visibly move the ghost to the next legal facing rather than most presses
    /// doing nothing because the plan re-snapped over them.
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
        [Tooltip("Manual rotate step when the held item is not currently over any " +
                 "PlacementZone - there is nothing to snap to, so this is purely how far one " +
                 "press of R turns it.")]
        [SerializeField] private float freeRotateStepDegrees = 45f;

        [Header("Heavy items")]
        [SerializeField] private float heavyMassThreshold = 8f;
        [SerializeField, Range(0.2f, 1f)] private float heavySpeedMultiplier = 0.55f;

        public bool IsHolding => _heldBody != null;
        /// <summary>What is currently being carried, or null. Read by anything that cares
        /// what a player is holding without owning the carry itself - e.g. FuelTank checking
        /// for a jerry can.</summary>
        public GameObject HeldObject => _heldBody != null ? _heldBody.gameObject : null;

        private Camera _camera;
        private NetworkPlayer _player;
        private Player.FirstPersonController _fpc;
        private InputAction _grabAction, _throwAction, _scrollAction, _rotateAction;

        private Rigidbody _heldBody;
        private WorldItemNetworkSync _heldSync;
        private CargoAttachment _heldCargo;

        // Manual yaw added on top of the camera-facing hold rotation. Reset to zero on every
        // new grab - a rotation dialled in for the last thing carried has no bearing on this
        // one.
        private float _manualYaw;

        // Live placement plan for whatever is in hand. Recomputed every frame, because the
        // hold point moves with the camera and the deck moves with the sea.
        private PlacementZone _planZone;
        private Vector3 _planPosition;
        private Quaternion _planRotation;
        private bool _planValid;

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
                _rotateAction = map?.FindAction("Rotate");
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

            if (_heldBody != null && _rotateAction != null && _rotateAction.WasPressedThisFrame())
            {
                // Queried fresh rather than read from last frame's cached plan - the item may
                // have drifted in or out of a zone since UpdatePlacementPlan last ran, and R is
                // meant to answer "what would this turn to right now".
                var zone = PlacementZone.Find(_heldBody.position);
                float step = zone != null ? zone.YawSnapDegrees : freeRotateStepDegrees;
                _manualYaw = Mathf.Repeat(_manualYaw + step, 360f);
            }

            UpdatePlacementPlan();
        }

        /// <summary>
        /// Works out where the held cargo would land if let go, and shows it. Nothing here
        /// touches the world - the plan is only acted on in <see cref="Release"/>, and the
        /// server re-plans for itself, so being wrong costs an inaccurate preview and nothing
        /// else.
        /// </summary>
        private void UpdatePlacementPlan()
        {
            _planZone = null;
            _planValid = false;

            if (_heldBody == null || _heldCargo == null) return;

            Vector3 point = _heldBody.position;
            var zone = PlacementZone.Find(point);
            if (zone == null) return;

            _planZone = zone;
            _planValid = zone.Plan(_heldBody.gameObject, point,
                _heldBody.transform.rotation, out _planPosition, out _planRotation);
            PlacementGhost.Show(_heldBody.gameObject, _planPosition, _planRotation, _planValid);
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

            // Manual yaw is applied about world up, on top of the camera-facing hold rotation -
            // so R turns the item in place without fighting where the camera itself is
            // pointed.
            Quaternion targetRot = Quaternion.AngleAxis(_manualYaw, Vector3.up)
                                  * Quaternion.LookRotation(_camera.transform.forward, Vector3.up);
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
            _heldCargo = rb.GetComponent<CargoAttachment>();
            _holdDistance = Mathf.Clamp(hit.distance, minHoldDistance, maxHoldDistance);
            _manualYaw = 0f;

            // Picking lashed cargo back up unlashes it. The body stays kinematic until the
            // server agrees, and FixedUpdate already sits out that round trip.
            if (_heldCargo != null && _heldCargo.IsAttached) _heldCargo.RequestDetach();

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
                // Restored before anything branches: if the server refuses the attach below,
                // the body carries on as a normal dynamic object rather than one still
                // wearing the carry's zero gravity.
                _heldBody.useGravity = _origGravity;
                _heldBody.linearDamping = _origDrag;
                _heldBody.angularDamping = _origAngularDrag;
                _heldBody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

                if (throwing)
                    _heldBody.AddForce(_camera.transform.forward * throwForce, ForceMode.VelocityChange);
                else if (_planValid && _planZone != null && _heldCargo != null)
                    _heldCargo.RequestAttach(_planZone, _planPosition, _planRotation);

                _heldSync?.ReleaseOwnership();
            }
            ClearHeld();
        }

        private void ClearHeld()
        {
            _heldBody = null;
            _heldSync = null;
            _heldCargo = null;
            _planZone = null;
            _planValid = false;
            PlacementGhost.Clear();
            if (_heavyApplied && _fpc != null) _fpc.SetSpeedMultiplier(1f);
            _heavyApplied = false;
        }
    }
}
