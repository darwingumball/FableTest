using Game.Interaction;
using Game.Net;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.World
{
    /// <summary>
    /// A ship's tethered dive station: a winch, a rope down to whoever is diving on it, and an
    /// anchor point where that rope enters the water.
    ///
    /// TWO PEOPLE CAN SHARE THIS ROPE, EACH WITH PARTIAL AUTHORITY OVER ITS LENGTH. The
    /// operator (this component's own IInteractable, taken/released exactly like
    /// <see cref="BoatHelm"/>'s wheel) drives it at <see cref="winchRate"/> - fast, precise,
    /// the "help of a friend" case. The diver drives the SAME rope length at the slower
    /// <see cref="DiveSuit.SignalRate"/> through their own signal, regardless of whether an
    /// operator exists - the solo case. Nothing has to notice or arbitrate between the two:
    /// they are just two inputs nudging one clamped `NetworkVariable&lt;float&gt;`, so if both
    /// happen to act in the same tick the result is exactly what it looks like, a tug of war,
    /// which is a harmless and even reasonable outcome.
    ///
    /// Only one diver at a time - one rope. <see cref="ServerTryClaimDiver"/>/
    /// <see cref="ServerReleaseDiver"/> are called by <see cref="DiveSuit"/>, not the other way
    /// around: this rig does not know how to start or stop a dive, only whether one may claim
    /// its rope.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class DiveRig : NetworkBehaviour, IInteractable
    {
        private const ulong NoOperator = ulong.MaxValue;
        private const ulong NoDiver = ulong.MaxValue;

        [Header("Rope")]
        [Tooltip("Where the rope enters the water. The diver's depth is this point's Y minus " +
                 "the paid-out rope length - straight down, not an arc.")]
        [SerializeField] private Transform anchorPoint;
        [Tooltip("Where a diver is placed when the dive starts or ends - on deck, clear of " +
                 "the rail.")]
        [SerializeField] private Transform exitPoint;
        [SerializeField] private float maxRopeLength = 35f;
        [Tooltip("Metres/second the WINCH OPERATOR can move the rope. Faster than the diver's " +
                 "own signal - a friend on the surface is a real help, not just a formality.")]
        [SerializeField] private float winchRate = 2.2f;

        private readonly NetworkVariable<float> _ropeLength = new(0f,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<ulong> _operatorClientId = new(NoOperator,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<ulong> _diverClientId = new(NoDiver,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Server-side live input from whoever is currently working the winch.
        private float _operatorInput;

        private InputAction _interactAction;
        private bool _localOperating;
        private int _lastToggleFrame = -1;
        private float _lastSendTime;
        private float _lastSentInput;

        public float RopeLength => _ropeLength.Value;
        public float MaxRopeLength => maxRopeLength;
        public Transform AnchorPoint => anchorPoint;
        public Transform ExitPoint => exitPoint != null ? exitPoint : anchorPoint;
        public bool HasDiver => _diverClientId.Value != NoDiver;
        public ulong DiverClientId => _diverClientId.Value;

        public bool IsLocalOperator =>
            IsSpawned && NetworkManager != null && _operatorClientId.Value == NetworkManager.LocalClientId;

        // ---------------- lifecycle ----------------

        public override void OnNetworkSpawn()
        {
            _operatorClientId.OnValueChanged += OnOperatorChanged;
            SetLocalOperating(IsLocalOperator);
            if (IsServer)
                NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
        }

        public override void OnNetworkDespawn()
        {
            _operatorClientId.OnValueChanged -= OnOperatorChanged;
            SetLocalOperating(false);
            if (IsServer && NetworkManager != null)
                NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
        }

        private void OnOperatorChanged(ulong previous, ulong current) => SetLocalOperating(IsLocalOperator);

        private void SetLocalOperating(bool operating)
        {
            if (_localOperating == operating) return;
            _localOperating = operating;

            var local = NetworkPlayer.Local;
            if (local != null && local.Controller != null)
                local.Controller.SetMoveControl(!operating);
        }

        private void OnClientDisconnected(ulong clientId)
        {
            // An operator who quits leaves the winch locked out; a diver who quits leaves the
            // rope claimed forever. Neither should outlive the connection that caused it.
            if (_operatorClientId.Value == clientId) ServerSetOperator(NoOperator);
            if (_diverClientId.Value == clientId) ServerReleaseDiver(clientId);
        }

        // ---------------- diver-facing API (called by DiveSuit) ----------------

        /// <summary>Server-only. True and claims the rope if it was free.</summary>
        public bool ServerTryClaimDiver(ulong clientId)
        {
            if (!IsServer || HasDiver) return false;
            _diverClientId.Value = clientId;
            _ropeLength.Value = 0f;
            return true;
        }

        /// <summary>Server-only. Frees the rope - detaching, surfacing, or a disconnect.</summary>
        public void ServerReleaseDiver(ulong clientId)
        {
            if (!IsServer || _diverClientId.Value != clientId) return;
            _diverClientId.Value = NoDiver;
            _ropeLength.Value = 0f;
        }

        /// <summary>Server-only. The diver's own signal, at their slower self-serve rate,
        /// applied regardless of whether an operator is also present.</summary>
        public void ServerAdjustRope(float metres)
        {
            if (!IsServer) return;
            _ropeLength.Value = Mathf.Clamp(_ropeLength.Value + metres, 0f, maxRopeLength);
        }

        // ---------------- winch operator interaction ----------------

        public string GetPrompt(NetworkPlayer player)
        {
            if (!IsSpawned) return null;
            if (_localOperating) return "Leave the winch";
            if (_operatorClientId.Value != NoOperator) return "Winch in use";
            return HasDiver ? "Operate winch" : "No diver attached";
        }

        public void Interact(NetworkPlayer player)
        {
            // Reachable both from the interaction raycast and from the direct read in Update
            // that keeps an operator from being stranded if they look away - same guard as
            // the helm and the crane.
            if (_lastToggleFrame == Time.frameCount) return;
            _lastToggleFrame = Time.frameCount;
            if (!IsSpawned) return;

            if (_localOperating) RequestControlsServerRpc(false);
            else if (_operatorClientId.Value == NoOperator && HasDiver) RequestControlsServerRpc(true);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestControlsServerRpc(bool take, ServerRpcParams p = default)
        {
            ulong sender = p.Receive.SenderClientId;
            if (take)
            {
                if (_operatorClientId.Value != NoOperator || !HasDiver) return;
                ServerSetOperator(sender);
            }
            else if (_operatorClientId.Value == sender)
            {
                ServerSetOperator(NoOperator);
            }
        }

        private void ServerSetOperator(ulong clientId)
        {
            _operatorClientId.Value = clientId;
            _operatorInput = 0f;
        }

        [ServerRpc(RequireOwnership = false)]
        private void WinchInputServerRpc(float input, ServerRpcParams p = default)
        {
            if (_operatorClientId.Value != p.Receive.SenderClientId) return;
            _operatorInput = Mathf.Clamp(input, -1f, 1f);
        }

        // ---------------- suit-up point (forwarded from DiveSuitRack) ----------------

        public string GetSuitPrompt(NetworkPlayer player)
        {
            var suit = player != null ? player.GetComponent<DiveSuit>() : null;
            if (suit == null) return null;

            if (suit.Mode == DiveMode.Surfaced)
                return HasDiver ? "Dive rig in use" : "Suit up and dive";

            if (!suit.IsUsing(this)) return null;   // this player is diving somewhere else

            return suit.Mode == DiveMode.Free || RopeLength <= 0.5f
                ? "Climb aboard" : "Surface first - reel in the rope";
        }

        public void SuitInteract(NetworkPlayer player)
        {
            var suit = player != null ? player.GetComponent<DiveSuit>() : null;
            if (suit == null) return;

            if (suit.Mode == DiveMode.Surfaced)
            {
                if (!HasDiver) suit.RequestStartDive(this);
                return;
            }

            if (suit.IsUsing(this) && (suit.Mode == DiveMode.Free || RopeLength <= 0.5f))
                suit.RequestExitDive();
        }

        // ---------------- simulation ----------------

        private void Update()
        {
            if (!IsSpawned) return;
            if (_localOperating) OperatorInput();
        }

        private void FixedUpdate()
        {
            if (!IsServer) return;
            if (_operatorClientId.Value != NoOperator && Mathf.Abs(_operatorInput) > 0.01f)
                ServerAdjustRope(_operatorInput * winchRate * Time.fixedDeltaTime);
        }

        private void OperatorInput()
        {
            var local = NetworkPlayer.Local;
            var fpc = local != null ? local.Controller : null;
            if (fpc == null) return;

            _interactAction ??= fpc.InputActions?.FindActionMap("Gameplay")?.FindAction("Interact");
            if (_interactAction != null && _interactAction.WasPressedThisFrame())
            {
                Interact(local);
                return;
            }

            // W/S through the move stick, same as the helm's throttle: +1 pays rope out
            // (lower the diver), -1 hauls in.
            float input = fpc.MoveInput.y;
            bool changed = Mathf.Abs(input - _lastSentInput) > 0.05f;
            if (!changed && Time.time - _lastSendTime < 0.2f) return;

            _lastSentInput = input;
            _lastSendTime = Time.time;
            WinchInputServerRpc(input);
        }

        private void OnDrawGizmosSelected()
        {
            if (anchorPoint == null) return;
            Gizmos.color = new Color(0.3f, 0.7f, 1f, 0.8f);
            Gizmos.DrawLine(anchorPoint.position, anchorPoint.position + Vector3.down * maxRopeLength);
        }
    }
}
