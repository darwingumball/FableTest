using Game.Net;
using Game.Player;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.World
{
    public enum DiveMode : byte { Surfaced, Tethered, Free }

    /// <summary>
    /// Worn dive gear. Interact with a <see cref="DiveSuitRack"/> to start; from then on this
    /// drives the player's depth from whichever <see cref="DiveRig"/> claimed the dive, and
    /// tracks the air that makes going deep a real decision rather than a free swim.
    ///
    /// THREE STATES. <see cref="DiveMode.Surfaced"/> is everyone, always, when not diving.
    /// <see cref="DiveMode.Tethered"/> is the assisted dive the rig is for: air is unlimited
    /// (surface-supplied, through the rope) and depth is NOT under the player's own control -
    /// <see cref="FirstPersonController.SetTethered"/> hands vertical position to the rig's
    /// rope length every frame, the same way a ladder hands it to a climb track. Horizontal
    /// swimming stays the player's own. <see cref="DiveMode.Free"/> is what "detach" (Throw,
    /// while tethered) buys: full free-swim in every axis again, at the cost of a draining
    /// reserve tank that runs dry and starts costing health if it is not managed - the "scuba"
    /// half of the ask, and the reason to stay clipped in unless there is a good reason not to.
    ///
    /// Ending EITHER state at the rig (<see cref="RequestExitDive"/>) returns to Surfaced with
    /// a full reserve tank - the tank is a limited BUDGET for time spent detached, not something
    /// that has to be topped up between dives.
    /// </summary>
    [RequireComponent(typeof(FirstPersonController))]
    [RequireComponent(typeof(NetworkObject))]
    public class DiveSuit : NetworkBehaviour
    {
        public struct State : INetworkSerializable
        {
            public DiveMode Mode;
            public NetworkObjectReference Rig;   // valid only when Mode != Surfaced
            public float ReserveOxygen;           // seconds; drains only in Free

            public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
            {
                s.SerializeValue(ref Mode);
                s.SerializeValue(ref Rig);
                s.SerializeValue(ref ReserveOxygen);
            }
        }

        [Header("Reserve air (Free mode only)")]
        [SerializeField] private float reserveCapacitySeconds = 90f;
        [Tooltip("Reserve drains faster the deeper you are - surface-supplied air not " +
                 "needing this at all is the whole point of staying tethered.")]
        [SerializeField] private float reserveDrainPerMetreDepth = 0.02f;
        [SerializeField] private float noAirDamagePerSecond = 8f;

        [Header("Diver's own signal (Tethered mode)")]
        [Tooltip("Metres/second the diver can move their OWN rope by signalling, regardless " +
                 "of whether a winch operator is also present. Slower than the operator's " +
                 "winch - a friend on the surface is a real help, not a formality.")]
        [SerializeField] private float signalRate = 0.9f;

        private readonly NetworkVariable<State> _state = new(default,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private FirstPersonController _fpc;
        private PlayerStats _stats;
        private DiveRig _rig;   // resolved locally on every peer from _state.Value.Rig

        private InputAction _jumpAction, _crouchAction, _throwAction;
        private float _lastSentSignal;
        private float _lastSignalSendTime;

        public DiveMode Mode => _state.Value.Mode;
        public bool IsDiving => Mode != DiveMode.Surfaced;
        public float ReserveOxygen => _state.Value.ReserveOxygen;
        public float ReserveCapacity => reserveCapacitySeconds;

        /// <summary>The rig THIS player is currently diving on, or null. Read by the HUD for
        /// a depth/rope readout, and by DiveRig to check "is this player using me".</summary>
        public DiveRig CurrentRig => Mode != DiveMode.Surfaced ? _rig : null;

        public bool IsUsing(DiveRig rig) => Mode != DiveMode.Surfaced && _rig == rig;

        private void Awake()
        {
            _fpc = GetComponent<FirstPersonController>();
            _stats = GetComponent<PlayerStats>();
        }

        public override void OnNetworkSpawn()
        {
            _state.OnValueChanged += OnStateChanged;
            ApplyLocal(_state.Value);   // late joiner / initial value

            if (IsOwner)
            {
                var map = _fpc.InputActions?.FindActionMap("Gameplay");
                _jumpAction = map?.FindAction("Jump");
                _crouchAction = map?.FindAction("Crouch");
                _throwAction = map?.FindAction("Throw");
            }
        }

        public override void OnNetworkDespawn() => _state.OnValueChanged -= OnStateChanged;

        private void OnStateChanged(State previous, State current) => ApplyLocal(current);

        private void ApplyLocal(State state)
        {
            _rig = state.Rig.TryGet(out NetworkObject netObj) ? netObj.GetComponent<DiveRig>() : null;
            _fpc.SetTethered(state.Mode == DiveMode.Tethered && _rig != null);
        }

        private void Update()
        {
            // Depth tracks the rig's rope length continuously, not just on state change - the
            // winch moves every frame the operator or the diver works it.
            if (Mode == DiveMode.Tethered && _rig != null)
                _fpc.SetTetherDepth(_rig.AnchorPoint.position.y - _rig.RopeLength);

            if (IsOwner) HandleInput();
            if (IsServer) ServerTick(Time.deltaTime);
        }

        // ---------------- owner input ----------------

        private void HandleInput()
        {
            if (Mode != DiveMode.Tethered) return;

            float signal = 0f;
            if (_jumpAction != null && _jumpAction.IsPressed()) signal -= 1f;      // ascend
            if (_crouchAction != null && _crouchAction.IsPressed()) signal += 1f;  // descend

            // Same throttle-or-keepalive pattern as BoatHelm/CraneController input streaming.
            if (Mathf.Abs(signal - _lastSentSignal) > 0.05f || Time.time - _lastSignalSendTime > 0.2f)
            {
                _lastSentSignal = signal;
                _lastSignalSendTime = Time.time;
                SendSignalServerRpc(signal);
            }

            if (_throwAction != null && _throwAction.WasPressedThisFrame())
                RequestDetachServerRpc();
        }

        [ServerRpc]
        private void SendSignalServerRpc(float signal)
        {
            if (Mode != DiveMode.Tethered || _rig == null) return;
            _rig.ServerAdjustRope(Mathf.Clamp(signal, -1f, 1f) * signalRate * Time.deltaTime);
        }

        // ---------------- start / detach / exit ----------------

        public void RequestStartDive(DiveRig rig)
        {
            if (!IsSpawned || rig == null) return;
            var rigNetObj = rig.GetComponent<NetworkObject>();
            if (rigNetObj == null || !rigNetObj.IsSpawned) return;
            var reference = new NetworkObjectReference(rigNetObj);
            if (IsServer) ServerStartDive(reference); else StartDiveServerRpc(reference);
        }

        [ServerRpc]
        private void StartDiveServerRpc(NetworkObjectReference rigRef) => ServerStartDive(rigRef);

        private void ServerStartDive(NetworkObjectReference rigRef)
        {
            if (!IsServer || Mode != DiveMode.Surfaced) return;
            if (!rigRef.TryGet(out NetworkObject netObj)) return;
            var rig = netObj.GetComponent<DiveRig>();
            if (rig == null || !rig.ServerTryClaimDiver(OwnerClientId)) return;

            _state.Value = new State
            {
                Mode = DiveMode.Tethered, Rig = rigRef, ReserveOxygen = reserveCapacitySeconds,
            };
            _fpc.TeleportTo(rig.AnchorPoint.position);
        }

        [ServerRpc]
        private void RequestDetachServerRpc() => ServerDetach();

        private void ServerDetach()
        {
            if (!IsServer || Mode != DiveMode.Tethered) return;
            var state = _state.Value;
            if (state.Rig.TryGet(out NetworkObject netObj))
                netObj.GetComponent<DiveRig>()?.ServerReleaseDiver(OwnerClientId);

            state.Mode = DiveMode.Free;
            _state.Value = state;   // reserve air carries over untouched - it has not been used yet
        }

        public void RequestExitDive()
        {
            if (!IsSpawned) return;
            if (IsServer) ServerExitDive(); else RequestExitDiveServerRpc();
        }

        [ServerRpc]
        private void RequestExitDiveServerRpc() => ServerExitDive();

        private void ServerExitDive()
        {
            if (!IsServer || Mode == DiveMode.Surfaced) return;
            var state = _state.Value;

            Vector3 exitPosition = transform.position;
            if (state.Rig.TryGet(out NetworkObject netObj))
            {
                var rig = netObj.GetComponent<DiveRig>();
                if (rig != null)
                {
                    if (state.Mode == DiveMode.Tethered) rig.ServerReleaseDiver(OwnerClientId);
                    exitPosition = rig.ExitPoint.position;
                }
            }

            _state.Value = new State { Mode = DiveMode.Surfaced, Rig = default, ReserveOxygen = reserveCapacitySeconds };
            _fpc.TeleportTo(exitPosition);
        }

        // ---------------- server tick: reserve air ----------------

        private void ServerTick(float dt)
        {
            var state = _state.Value;
            if (state.Mode != DiveMode.Free) return;   // unlimited while tethered - see class summary

            float depthFactor = 1f;
            var water = WaterVolume.Instance;
            if (water != null && water.SampleSurface(transform.position, out float surfaceY, out _))
                depthFactor = 1f + Mathf.Max(0f, surfaceY - transform.position.y) * reserveDrainPerMetreDepth;

            state.ReserveOxygen = Mathf.Max(0f, state.ReserveOxygen - dt * depthFactor);
            _state.Value = state;

            if (state.ReserveOxygen <= 0f) _stats?.ServerApplyDamage(noAirDamagePerSecond * dt);
        }
    }
}
