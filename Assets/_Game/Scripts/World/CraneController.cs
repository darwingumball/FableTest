using Game.Interaction;
using Game.Net;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.World
{
    /// <summary>
    /// A deck crane. Interact to take the controls, A/D to slew, W/S to luff the boom out and
    /// in, scroll to work the winch, Space to take or let go of a load, Interact to step off.
    ///
    /// Server-authoritative, for the same reason as <see cref="BoatHelm"/>: the rig is driven
    /// by live human input, so there is no function of time to evaluate and something has to
    /// go on the wire. What goes is three floats - slew, luff and rope length - and every
    /// peer, the host included, eases onto them and derives the boom and hook from there.
    /// The hook's position is therefore never sent; it falls out of the same three numbers
    /// everywhere.
    ///
    /// The LOAD is not replicated here either. Cargo carries its own claim on the hook (see
    /// <see cref="CargoAttachment"/>), which means a client already knows what is hanging
    /// there and can preview where releasing would put it, without the crane repeating that
    /// state a second time and risking the two disagreeing.
    /// </summary>
    public class CraneController : NetworkBehaviour, IInteractable
    {
        /// <summary>Replicated rig state. Three floats, sent only while someone is working it.</summary>
        public struct Rig : INetworkSerializable
        {
            public float Slew;    // degrees about the pedestal
            public float Luff;    // boom elevation, degrees above horizontal
            public float Hoist;   // rope paid out below the boom tip, metres

            public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
            {
                s.SerializeValue(ref Slew);
                s.SerializeValue(ref Luff);
                s.SerializeValue(ref Hoist);
            }
        }

        [Header("Rig")]
        [Tooltip("Yaws with the slew. The boom must be a child of this.")]
        [SerializeField] private Transform pedestal;
        [Tooltip("Pitches with the luff. Local +Z points along the boom.")]
        [SerializeField] private Transform boom;
        [Tooltip("Node at the far end of the boom, where the rope hangs from.")]
        [SerializeField] private Transform boomTip;
        [Tooltip("Rope visual. A unit cube; scaled to the paid-out length.")]
        [SerializeField] private Transform rope;
        [Tooltip("Carries the hook. Must NOT be a child of the boom - it hangs straight down " +
                 "in crane space, and a child of the boom would swing the rope out sideways " +
                 "with the luff.")]
        [SerializeField] private Transform hookRoot;
        [SerializeField] private CraneHook hook;

        [Header("Limits")]
        [SerializeField] private float slewRange = 150f;
        [SerializeField] private float luffMin = 8f;
        [SerializeField] private float luffMax = 72f;
        [SerializeField] private float hoistMin = 0.5f;
        [SerializeField] private float hoistMax = 11f;
        [SerializeField] private float ropeThickness = 0.07f;

        [Header("Rates")]
        [Tooltip("Deliberately slow, like the real thing. A crane that slews as fast as you " +
                 "can push the stick is a crane that puts a tonne of crab pot through the " +
                 "wheelhouse window.")]
        [SerializeField] private float slewRate = 24f;
        [SerializeField] private float luffRate = 15f;
        [Tooltip("Metres of rope per scroll notch.")]
        [SerializeField] private float hoistStep = 0.45f;

        [Header("Grabbing")]
        [Tooltip("Take loose cargo the moment the hook reaches it, without a keypress. Cargo " +
                 "already lashed into a placement zone always needs Space - see CraneHook.")]
        [SerializeField] private bool autoGrabLoose = true;

        [Header("Client smoothing")]
        [SerializeField] private float smoothing = 10f;

        private const ulong NoOperator = ulong.MaxValue;

        private readonly NetworkVariable<Rig> _rig = new(default,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<ulong> _operator = new(NoOperator,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Server-side live input from the operator.
        private float _slewInput, _luffInput;

        // Local presentation, eased toward _rig on every peer including the server.
        private float _slew, _luff, _hoist;
        private bool _initialised;

        private InputAction _interactAction, _grabAction, _winchAction;
        private bool _localOperating;
        private int _lastToggleFrame = -1;
        private float _lastSendTime;
        private float _lastSentSlew, _lastSentLuff;

        public bool IsLocalOperator =>
            IsSpawned && NetworkManager != null && _operator.Value == NetworkManager.LocalClientId;

        // ---------------- lifecycle ----------------

        private void Awake()
        {
            // Authored pose is the starting pose, so the crane in the scene view is the crane
            // you get on the first frame rather than one that snaps to zero.
            _luff = Mathf.Clamp((luffMin + luffMax) * 0.5f, luffMin, luffMax);
            _hoist = Mathf.Clamp(1.5f, hoistMin, hoistMax);
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer) _rig.Value = new Rig { Slew = 0f, Luff = _luff, Hoist = _hoist };

            _operator.OnValueChanged += OnOperatorChanged;
            SetLocalOperating(IsLocalOperator);

            if (IsServer)
                NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
        }

        public override void OnNetworkDespawn()
        {
            _operator.OnValueChanged -= OnOperatorChanged;
            SetLocalOperating(false);
            if (IsServer && NetworkManager != null)
                NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
        }

        private void OnOperatorChanged(ulong previous, ulong current) =>
            SetLocalOperating(IsLocalOperator);

        private void SetLocalOperating(bool operating)
        {
            if (_localOperating == operating) return;
            _localOperating = operating;

            // Same trade as the helm: you cannot walk while working the controls, but you can
            // still look around, which is the whole point of standing up here.
            var local = NetworkPlayer.Local;
            if (local != null && local.Controller != null)
                local.Controller.SetMoveControl(!operating);

            if (!operating) PlacementGhost.Clear();
        }

        private void OnClientDisconnected(ulong clientId)
        {
            // An operator who quits mid-lift would otherwise leave the crane locked out for
            // everyone else, with a load still on the hook.
            if (_operator.Value == clientId) ServerSetOperator(NoOperator);
        }

        // ---------------- interaction ----------------

        public string GetPrompt(NetworkPlayer player)
        {
            if (!IsSpawned) return null;
            if (_localOperating) return "Leave the crane";
            return _operator.Value == NoOperator ? "Operate the crane" : "Crane in use";
        }

        public void Interact(NetworkPlayer player)
        {
            // Reachable both from the interaction raycast and from the direct read below that
            // keeps an operator from being stranded if they look away from the controls.
            if (_lastToggleFrame == Time.frameCount) return;
            _lastToggleFrame = Time.frameCount;

            if (!IsSpawned) return;

            if (_localOperating) RequestControlsServerRpc(false);
            else if (_operator.Value == NoOperator) RequestControlsServerRpc(true);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestControlsServerRpc(bool take, ServerRpcParams p = default)
        {
            ulong sender = p.Receive.SenderClientId;
            if (take)
            {
                if (_operator.Value != NoOperator) return;   // someone got there first
                ServerSetOperator(sender);
            }
            else if (_operator.Value == sender)
            {
                ServerSetOperator(NoOperator);
            }
        }

        private void ServerSetOperator(ulong clientId)
        {
            _operator.Value = clientId;
            // Controls centre whenever the seat changes, so a stale hard-over slew cannot be
            // inherited by the next person up the ladder. Any load stays on the hook - a
            // crane that dropped its lift because someone stepped away would be worse.
            _slewInput = 0f;
            _luffInput = 0f;
        }

        // ---------------- input ----------------

        [ServerRpc(RequireOwnership = false)]
        private void DriveServerRpc(float slew, float luff, ServerRpcParams p = default)
        {
            if (_operator.Value != p.Receive.SenderClientId) return;
            _slewInput = Mathf.Clamp(slew, -1f, 1f);
            _luffInput = Mathf.Clamp(luff, -1f, 1f);
        }

        [ServerRpc(RequireOwnership = false)]
        private void WinchServerRpc(float delta, ServerRpcParams p = default)
        {
            if (_operator.Value != p.Receive.SenderClientId) return;
            // Clamped per message, so a client cannot pay out the whole drum in one packet.
            var rig = _rig.Value;
            rig.Hoist = Mathf.Clamp(rig.Hoist + Mathf.Clamp(delta, -1f, 1f), hoistMin, hoistMax);
            _rig.Value = rig;
        }

        [ServerRpc(RequireOwnership = false)]
        private void ToggleLoadServerRpc(ServerRpcParams p = default)
        {
            if (_operator.Value != p.Receive.SenderClientId) return;
            ServerToggleLoad();
        }

        private void OperatorInput()
        {
            var local = NetworkPlayer.Local;
            var fpc = local != null ? local.Controller : null;
            if (fpc == null) return;

            var map = fpc.InputActions?.FindActionMap("Gameplay");
            _interactAction ??= map?.FindAction("Interact");
            _grabAction ??= map?.FindAction("Jump");
            _winchAction ??= map?.FindAction("AdjustHoldDistance");

            if (_interactAction != null && _interactAction.WasPressedThisFrame())
            {
                Interact(local);
                return;
            }

            if (_grabAction != null && _grabAction.WasPressedThisFrame())
                ToggleLoadServerRpc();

            if (_winchAction != null)
            {
                float scroll = _winchAction.ReadValue<float>();
                // Scroll up hauls in. Sign only: the raw value is a notch count on some
                // mice and a pixel delta on others.
                if (Mathf.Abs(scroll) > 0.01f)
                    WinchServerRpc(-Mathf.Sign(scroll) * hoistStep);
            }

            // Ungated move input: SetMoveControl(false) has already stopped this from walking
            // the player anywhere, but the stick itself is still readable.
            Vector2 move = fpc.MoveInput;

            // Only on a real change, or every 200 ms as a keepalive. Same reasoning as the
            // helm - streaming two floats a frame would cost more than the state they drive.
            bool changed = Mathf.Abs(move.x - _lastSentSlew) > 0.05f ||
                           Mathf.Abs(move.y - _lastSentLuff) > 0.05f;
            if (!changed && Time.time - _lastSendTime < 0.2f) return;

            _lastSentSlew = move.x;
            _lastSentLuff = move.y;
            _lastSendTime = Time.time;
            DriveServerRpc(move.x, move.y);
        }

        // ---------------- simulation ----------------

        private void Update()
        {
            if (!IsSpawned) return;

            if (IsServer) ServerIntegrate(Time.deltaTime);
            if (_localOperating) OperatorInput();

            ApplyRig(Time.deltaTime);

            if (_localOperating) UpdateGhost();
        }

        private void ServerIntegrate(float dt)
        {
            if (_operator.Value != NoOperator && (_slewInput != 0f || _luffInput != 0f))
            {
                var rig = _rig.Value;
                rig.Slew = Mathf.Clamp(rig.Slew + _slewInput * slewRate * dt, -slewRange, slewRange);
                // W pushes the boom DOWN and out, which is where the operator is looking when
                // they reach for the water.
                rig.Luff = Mathf.Clamp(rig.Luff - _luffInput * luffRate * dt, luffMin, luffMax);
                _rig.Value = rig;
            }

            if (autoGrabLoose && _operator.Value != NoOperator && hook != null && hook.First == null)
            {
                var loose = hook.FindTarget(looseOnly: true);
                if (loose != null) ServerGrab(loose);
            }
        }

        private void ApplyRig(float dt)
        {
            var rig = _rig.Value;
            if (!_initialised)
            {
                // A late joiner arrives on the crane's real pose, not lerping onto it from
                // whatever the scene file happened to hold.
                _slew = rig.Slew;
                _luff = rig.Luff;
                _hoist = rig.Hoist;
                _initialised = true;
            }
            else
            {
                float k = 1f - Mathf.Exp(-smoothing * dt);
                _slew = Mathf.Lerp(_slew, rig.Slew, k);
                _luff = Mathf.Lerp(_luff, rig.Luff, k);
                _hoist = Mathf.Lerp(_hoist, rig.Hoist, k);
            }

            if (pedestal != null) pedestal.localRotation = Quaternion.Euler(0f, _slew, 0f);
            // Negative about X lifts local +Z toward +Y, so luff reads as elevation.
            if (boom != null) boom.localRotation = Quaternion.Euler(-_luff, 0f, 0f);

            if (boomTip == null || hookRoot == null) return;

            // The hook hangs off the CRANE root, not the boom, so the rope stays plumb however
            // the boom is set instead of swinging out sideways with the luff. "Down" is
            // resolved through the crane's own transform rather than assumed to be local -Y,
            // so this stays correct if the crane is ever mounted on something that tilts.
            Vector3 tip = transform.InverseTransformPoint(boomTip.position);
            Vector3 down = transform.InverseTransformDirection(Vector3.down);

            hookRoot.localPosition = tip + down * _hoist;
            // The block hangs level with the world, not with whatever it is bolted to.
            hookRoot.localRotation = Quaternion.Inverse(transform.rotation);

            if (rope != null)
            {
                rope.localPosition = tip + down * (_hoist * 0.5f);
                rope.localRotation = Quaternion.FromToRotation(Vector3.up, -down);
                rope.localScale = new Vector3(ropeThickness, _hoist, ropeThickness);
            }
        }

        // ---------------- load ----------------

        private void ServerToggleLoad()
        {
            if (hook == null) return;

            var load = hook.First;
            if (load != null) { ServerRelease(load); return; }

            // Space takes anything in reach, lashed or not - this is the deliberate keypress
            // that lets the operator lift a pot back off a loaded deck.
            var target = hook.FindTarget(looseOnly: false);
            if (target != null) ServerGrab(target);
        }

        private void ServerGrab(CargoAttachment cargo)
        {
            if (!IsServer || cargo == null || hook == null) return;

            // The hook takes the load by the TOP of it, so it hangs beneath the block instead
            // of skewered through the middle. Levelled while we are at it: a crate coming off
            // the water is usually lying at an angle, and a crane straightening it out as it
            // comes up is both what happens and what looks right.
            Bounds own = CargoBounds.InOwnFrame(cargo.gameObject);
            Quaternion rotation = Quaternion.Euler(0f, cargo.transform.eulerAngles.y, 0f);
            Vector3 position = hook.AttachRoot.position
                               - rotation * own.center
                               - Vector3.up * own.extents.y;

            cargo.ServerAttach(hook, position, rotation);
        }

        private void ServerRelease(CargoAttachment cargo)
        {
            if (!IsServer || cargo == null) return;

            // Over a lashing area with a legal plan? Lash it. Otherwise let go and let physics
            // have it - which is what makes dropping a pot over the side work at all.
            Vector3 point = cargo.transform.position;
            var zone = PlacementZone.Find(point);
            if (zone != null && zone.Plan(cargo.gameObject, point,
                    cargo.transform.eulerAngles.y, out var planned, out var plannedRotation))
            {
                cargo.ServerAttach(zone, planned, plannedRotation);
                return;
            }

            cargo.ServerDetach();
        }

        /// <summary>
        /// Previews where releasing would put the load, for the operator only. Purely local -
        /// the server re-plans for itself on release, so a client that got this wrong loses
        /// nothing but an accurate preview.
        /// </summary>
        private void UpdateGhost()
        {
            if (hook == null) return;
            var load = hook.First;
            if (load == null) return;

            Vector3 point = load.transform.position;
            var zone = PlacementZone.Find(point);
            if (zone == null) return;

            bool valid = zone.Plan(load.gameObject, point, load.transform.eulerAngles.y,
                out var position, out var rotation);
            PlacementGhost.Show(load.gameObject, position, rotation, valid);
        }
    }
}
