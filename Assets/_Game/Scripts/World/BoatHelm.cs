using Game.Interaction;
using Game.Net;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.World
{
    /// <summary>
    /// The wheel. Interact to take it, W/S for throttle, A/D for rudder, Interact to let go.
    ///
    /// The boat is server-authoritative once anyone touches the wheel. That is a deliberate
    /// break from <see cref="BoatMotion"/>'s autopilot, which is a pure function of server
    /// time and needs no replication at all: a driven boat depends on live human input, so
    /// there is no function of time to evaluate and *something* has to be sent. The server
    /// integrates the hull and replicates only <see cref="Nav"/> - twelve bytes of position,
    /// heading and speed. Clients ease toward it rather than snapping, which hides both the
    /// tick quantisation and any packet loss, and costs nothing in fidelity because a
    /// nine-metre-a-second tug simply cannot move far between ticks.
    ///
    /// Once engaged the boat never returns to its patrol circle. Handing control back to a
    /// time-driven course would teleport the hull to wherever that course says it should be
    /// by now, and there is no way to hide that. Released, it just coasts to a stop.
    /// </summary>
    public class BoatHelm : NetworkBehaviour, IInteractable
    {
        /// <summary>Replicated hull state. Kept flat and small - it is sent every tick while driving.</summary>
        public struct Nav : INetworkSerializable
        {
            public Vector2 Position;    // world XZ; Y is the wave response, computed locally
            public float Heading;       // degrees
            public float Speed;         // m/s along Heading

            public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
            {
                s.SerializeValue(ref Position);
                s.SerializeValue(ref Heading);
                s.SerializeValue(ref Speed);
            }
        }

        [Header("Handling")]
        [SerializeField] private float maxSpeed = 9f;
        [Tooltip("Astern is deliberately far slower than ahead, like the real thing.")]
        [SerializeField] private float maxReverseSpeed = 3f;
        [Tooltip("Deliberately slow. A boat has no brakes and no grip - the whole character " +
                 "of driving one is that it takes a long time to get moving and longer to " +
                 "stop, so every manoeuvre has to be planned well before you need it.")]
        [SerializeField] private float acceleration = 1.1f;
        [Tooltip("Deceleration when the throttle is centred or released. Lower than the " +
                 "acceleration: a hull loses way to drag alone, which is a slow business.")]
        [SerializeField] private float dragDeceleration = 0.4f;
        [Tooltip("Astern builds even more slowly than ahead.")]
        [SerializeField] private float reverseAcceleration = 0.7f;
        [SerializeField] private float turnRateDegrees = 26f;
        [Tooltip("How fast the rudder itself swings to the input. Instant rudder is the " +
                 "other half of a boat feeling like a car.")]
        [SerializeField] private float rudderRate = 1.6f;
        [Tooltip("Speed at which the rudder reaches full authority. A boat with no way on " +
                 "has no steering at all, which is the single thing that makes a boat feel " +
                 "like a boat rather than a car.")]
        [SerializeField] private float rudderAuthoritySpeed = 4f;

        [Header("Fuel")]
        [Tooltip("Optional. An empty tank does not stop the boat outright - it stops the " +
                 "THROTTLE, so a boat under way coasts to a halt on drag alone rather than " +
                 "slamming to a stop, and steering still answers for as long as there is way " +
                 "on. Leave unassigned for a boat that should never need fuel.")]
        [SerializeField] private FuelTank fuelTank;
        [Tooltip("Burn while the throttle is engaged, scaled by how far it is pushed. A ship " +
                 "engine burns faster than a stationary generator - it is moving several " +
                 "tonnes of hull, not just turning a lamp on.")]
        [SerializeField] private float fuelBurnLitersPerHour = 30f;

        [Header("Bounds")]
        [Tooltip("Centre of the navigable water, world XZ.")]
        [SerializeField] private Vector2 boundsCentre = new(0f, 620f);
        [SerializeField] private Vector2 boundsHalfExtents = new(560f, 560f);

        [Header("Client smoothing")]
        [Tooltip("How fast a non-driving peer eases onto the replicated state.")]
        [SerializeField] private float smoothing = 8f;

        private readonly NetworkVariable<Nav> _nav = new(default,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<ulong> _driver = new(NoDriver,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<bool> _engaged = new(false,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private const ulong NoDriver = ulong.MaxValue;

        // Server-side live input from the driver, and the rudder angle easing toward it.
        private float _throttleInput, _rudderInput;
        private float _rudder;

        // Local presentation state, eased toward _nav on every peer including the server.
        private Vector2 _position;
        private float _heading;
        private bool _localInitialised;

        private InputAction _interactAction;
        private float _lastSendTime;
        private float _lastSentThrottle, _lastSentRudder;
        private int _lastToggleFrame = -1;
        private bool _localDriving;

        /// <summary>True once someone has taken the wheel. <see cref="BoatMotion"/> reads this.</summary>
        public bool HasControl => _engaged.Value;
        public Vector3 Position => new(_position.x, 0f, _position.y);
        public Vector3 Forward => Quaternion.Euler(0f, _heading, 0f) * Vector3.forward;

        public bool IsLocalDriver =>
            IsSpawned && NetworkManager != null && _driver.Value == NetworkManager.LocalClientId;

        // ---------------- lifecycle ----------------

        public override void OnNetworkSpawn()
        {
            _position = new Vector2(transform.position.x, transform.position.z);
            _heading = transform.eulerAngles.y;
            _localInitialised = false;

            // Who is driving is decided by the server, so the local "am I at the wheel"
            // state follows the replicated value rather than the local button press. That
            // matters on the way out: releasing optimistically would hand movement back a
            // few frames before the server agreed, and if the server then refused, the
            // player would be walking around while still steering.
            _driver.OnValueChanged += OnDriverChanged;
            SetLocalDriving(IsLocalDriver);

            if (IsServer)
                NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
        }

        public override void OnNetworkDespawn()
        {
            _driver.OnValueChanged -= OnDriverChanged;
            SetLocalDriving(false);
            if (IsServer && NetworkManager != null)
                NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
        }

        private void OnDriverChanged(ulong previous, ulong current) => SetLocalDriving(IsLocalDriver);

        private void SetLocalDriving(bool driving)
        {
            if (_localDriving == driving) return;
            _localDriving = driving;

            var local = NetworkPlayer.Local;
            if (local != null && local.Controller != null)
                local.Controller.SetMoveControl(!driving);
        }

        private void OnClientDisconnected(ulong clientId)
        {
            // A driver who rage-quits mid-turn would otherwise leave the wheel jammed and
            // the boat locked out for everyone else.
            if (_driver.Value == clientId) ServerSetDriver(NoDriver);
        }

        // ---------------- interaction ----------------

        public string GetPrompt(NetworkPlayer player)
        {
            if (!IsSpawned) return null;
            if (_localDriving) return "Leave the helm";
            return _driver.Value == NoDriver ? "Take the helm" : "Helm in use";
        }

        public void Interact(NetworkPlayer player)
        {
            // Same once-per-frame guard as the ladder: this is reachable both from the
            // interaction raycast and from the direct read in Update that keeps a driver
            // from being stranded if they look away from the wheel.
            if (_lastToggleFrame == Time.frameCount) return;
            _lastToggleFrame = Time.frameCount;

            if (!IsSpawned) return;

            if (_localDriving) RequestHelmServerRpc(false);
            else if (_driver.Value == NoDriver) RequestHelmServerRpc(true);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestHelmServerRpc(bool take, ServerRpcParams p = default)
        {
            ulong sender = p.Receive.SenderClientId;
            if (take)
            {
                if (_driver.Value != NoDriver) return;   // someone got there first
                if (!_engaged.Value)
                {
                    // Adopt the autopilot's current pose as the starting point, so the
                    // hull does not jump the instant the wheel is taken.
                    _position = new Vector2(transform.position.x, transform.position.z);
                    _heading = transform.eulerAngles.y;
                    _engaged.Value = true;
                    PublishNav(0f);
                }
                ServerSetDriver(sender);
            }
            else if (_driver.Value == sender)
            {
                ServerSetDriver(NoDriver);
            }
        }

        private void ServerSetDriver(ulong clientId)
        {
            _driver.Value = clientId;
            // Wheel centres and the throttle drops to idle whenever the seat changes, so a
            // stale hard-over rudder cannot be inherited by the next person aboard.
            _throttleInput = 0f;
            _rudderInput = 0f;
            _rudder = 0f;
        }

        [ServerRpc(RequireOwnership = false)]
        private void DriveInputServerRpc(float throttle, float rudder, ServerRpcParams p = default)
        {
            if (_driver.Value != p.Receive.SenderClientId) return;
            _throttleInput = Mathf.Clamp(throttle, -1f, 1f);
            _rudderInput = Mathf.Clamp(rudder, -1f, 1f);
        }

        // ---------------- simulation ----------------

        private void Update()
        {
            if (!IsSpawned) return;

            if (IsServer && _engaged.Value) ServerIntegrate(Time.deltaTime);
            if (_localDriving) DriverInput();

            // Every peer - server included - presents the same eased value, so the hull
            // moves identically everywhere instead of the host seeing a crisper boat.
            if (_engaged.Value) FollowNav(Time.deltaTime);
        }

        private void ServerIntegrate(float dt)
        {
            // Only draws down while the throttle is actually engaged - idling at the helm or
            // drifting with the wheel centred costs nothing, which is what makes "no fuel"
            // mean "cannot power the engine" rather than "cannot exist near the helm".
            float throttle = _throttleInput;
            if (fuelTank != null && Mathf.Abs(throttle) > 0.01f)
            {
                bool hasFuel = fuelTank.TryConsume(
                    fuelBurnLitersPerHour / 3600f * Mathf.Abs(throttle), dt);
                if (!hasFuel) throttle = 0f;
            }

            float target = throttle >= 0f
                ? throttle * maxSpeed
                : throttle * maxReverseSpeed;

            var nav = _nav.Value;
            float speed = nav.Speed;

            // Three different rates, because they are three different physical things:
            // driving the prop ahead, driving it astern, and simply losing way to drag.
            // Coasting is much the slowest, which is what makes stopping something you have
            // to plan for rather than something you do.
            // Gated throttle, not the raw input: out of fuel this reads as centred and the
            // boat coasts down on drag, rather than "accelerating" hard toward a target of
            // zero because the player is still holding the key down.
            float rate = Mathf.Abs(throttle) <= 0.01f ? dragDeceleration
                       : target < speed - 0.01f && target < 0f ? reverseAcceleration
                       : acceleration;
            speed = Mathf.MoveTowards(speed, target, rate * dt);

            // The rudder swings toward the input rather than snapping to it.
            _rudder = Mathf.MoveTowards(_rudder, _rudderInput, rudderRate * dt);

            // Rudder authority scales with way on, and reverses going astern - exactly like
            // backing a real boat, where the stern walks the way you did not expect.
            float authority = Mathf.Clamp01(Mathf.Abs(speed) / Mathf.Max(rudderAuthoritySpeed, 0.01f));
            float direction = speed >= 0f ? 1f : -1f;
            _heading += _rudder * turnRateDegrees * authority * direction * dt;

            Vector3 fwd = Quaternion.Euler(0f, _heading, 0f) * Vector3.forward;
            _position += new Vector2(fwd.x, fwd.z) * (speed * dt);

            // Keep the hull on the water quad. Hitting the edge kills way in that axis
            // rather than stopping the boat dead, so grazing the boundary is not a wall.
            Vector2 min = boundsCentre - boundsHalfExtents;
            Vector2 max = boundsCentre + boundsHalfExtents;
            Vector2 clamped = new(Mathf.Clamp(_position.x, min.x, max.x),
                                  Mathf.Clamp(_position.y, min.y, max.y));
            if (clamped != _position)
            {
                _position = clamped;
                speed *= 0.5f;
            }

            PublishNav(speed);
        }

        private void PublishNav(float speed) =>
            _nav.Value = new Nav { Position = _position, Heading = _heading, Speed = speed };

        private void FollowNav(float dt)
        {
            var nav = _nav.Value;
            if (!_localInitialised)
            {
                // A late joiner arrives on the boat's real position, not lerping toward it
                // from wherever the scene file happened to put it.
                _position = nav.Position;
                _heading = nav.Heading;
                _localInitialised = true;
                return;
            }

            float k = 1f - Mathf.Exp(-smoothing * dt);
            _position = Vector2.Lerp(_position, nav.Position, k);
            _heading = Mathf.LerpAngle(_heading, nav.Heading, k);
        }

        private void DriverInput()
        {
            var local = NetworkPlayer.Local;
            var fpc = local != null ? local.Controller : null;
            if (fpc == null) return;

            Vector2 move = fpc.MoveInput;
            _interactAction ??= fpc.InputActions?.FindActionMap("Gameplay")?.FindAction("Interact");
            if (_interactAction != null && _interactAction.WasPressedThisFrame())
            {
                Interact(local);
                return;
            }

            // Only send on a real change, or every 200 ms as a keepalive. Streaming two
            // floats every frame would put more traffic on the wire than the replicated
            // hull state it is driving.
            bool changed = Mathf.Abs(move.y - _lastSentThrottle) > 0.05f ||
                           Mathf.Abs(move.x - _lastSentRudder) > 0.05f;
            if (!changed && Time.time - _lastSendTime < 0.2f) return;

            _lastSentThrottle = move.y;
            _lastSentRudder = move.x;
            _lastSendTime = Time.time;
            DriveInputServerRpc(move.y, move.x);
        }
    }
}
