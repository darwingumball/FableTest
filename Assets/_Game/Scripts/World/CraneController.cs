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
        /// <summary>
        /// Replicated rig state. Five floats, sent only while something is actually moving.
        ///
        /// The swing angles are in here rather than simulated locally on purpose. Everything
        /// else about this boat is a function of state every peer already has, but a pendulum
        /// is an integrator with history - two peers starting from the same numbers drift
        /// apart, and a load hanging in a different place on each machine is a load that lands
        /// somewhere different depending on who you ask. The server integrates it and everyone
        /// reads the answer.
        /// </summary>
        public struct Rig : INetworkSerializable
        {
            public float Slew;    // degrees about the pedestal
            public float Luff;    // boom elevation, degrees above horizontal
            public float Hoist;   // rope paid out below the boom tip, metres
            public float SwingX;  // rope tilt about world X, degrees - displaces the load in Z
            public float SwingZ;  // rope tilt about world Z, degrees - displaces the load in X

            public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
            {
                s.SerializeValue(ref Slew);
                s.SerializeValue(ref Luff);
                s.SerializeValue(ref Hoist);
                s.SerializeValue(ref SwingX);
                s.SerializeValue(ref SwingZ);
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
        [Tooltip("Metres of rope per scroll notch. Small deliberately - a winch that moved a " +
                 "load half a metre per click was impossible to set down gently.")]
        [SerializeField] private float hoistStep = 0.16f;

        [Header("Grabbing")]
        [Tooltip("Take loose cargo the moment the hook reaches it, without a keypress. Cargo " +
                 "already lashed into a placement zone always needs Space - see CraneHook.")]
        [SerializeField] private bool autoGrabLoose = true;

        [Header("Swing")]
        [Tooltip("How much of the boom tip's acceleration becomes swing. LOWER READS AS " +
                 "HEAVIER: a loaded hook resists being flicked around, so it lags the boom " +
                 "instead of chasing it. This is the main weight knob.")]
        [SerializeField, Range(0f, 1f)] private float swingResponse = 0.3f;
        [Tooltip("How fast the swing dies away, per second. High values read as a heavy block " +
                 "on a stiff wire; low values as a conker on a string.")]
        [SerializeField] private float swingDamping = 1.5f;
        [Tooltip("Ceiling on the swing angle. A crane load free to reach 60 degrees would be " +
                 "through the wheelhouse window.")]
        [SerializeField] private float maxSwingDegrees = 20f;
        [Tooltip("Drive acceleration is clamped to this. A teleport - a boat respawning, the " +
                 "mooring settling on the first frame - is an near-infinite acceleration and " +
                 "would otherwise fire the load straight out sideways.")]
        [SerializeField] private float maxDriveAcceleration = 18f;

        [Header("Client smoothing")]
        [SerializeField] private float smoothing = 10f;
        [Tooltip("Swing is eased faster than the rest of the rig. It is the only part that " +
                 "moves every tick, and easing it as slowly as the boom makes the load lag " +
                 "visibly behind its own rope.")]
        [SerializeField] private float swingSmoothing = 18f;

        private const ulong NoOperator = ulong.MaxValue;

        private readonly NetworkVariable<Rig> _rig = new(default,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<ulong> _operator = new(NoOperator,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Server-side live input from the operator.
        private float _slewInput, _luffInput;

        // Server-side pendulum, in RADIANS. Kept in radians throughout so the trig below has
        // no conversions buried in it; only the published value is degrees.
        private float _swingA, _swingB;          // A tilts about X, B about Z
        private float _swingVelocityA, _swingVelocityB;
        private Vector3 _lastTip, _tipVelocity, _tipAcceleration;
        private bool _swingInitialised;

        // The load that was just let go of. Excluded from AUTO grab until it has drifted clear,
        // or releasing over the water would re-hook it on the very next frame - see
        // ServerIntegrate.
        private CargoAttachment _justReleased;

        // Local presentation, eased toward _rig on every peer including the server.
        private float _slew, _luff, _hoist;
        private float _swingX, _swingZ;
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

            IntegrateSwing(dt);

            if (!autoGrabLoose || _operator.Value == NoOperator || hook == null) return;

            // A load let go over the water is still inside the hook's reach for the first few
            // frames of its fall, so an unguarded auto-grab re-hooked it immediately and Space
            // looked like it did nothing. It becomes eligible again once it has drifted clear -
            // twice the reach, so the hysteresis cannot chatter at the boundary.
            if (_justReleased != null)
            {
                float clear = hook.Reach * 2f;
                if (!_justReleased.IsSpawned ||
                    (_justReleased.transform.position - hook.AttachRoot.position).sqrMagnitude
                        > clear * clear)
                    _justReleased = null;
            }

            if (hook.First != null) return;

            var loose = hook.FindTarget(looseOnly: true, exclude: _justReleased);
            if (loose != null) ServerGrab(loose);
        }

        /// <summary>
        /// A damped pendulum on the rope, driven by the boom tip's own acceleration - which
        /// already contains everything that should make a load swing: slewing, luffing, and the
        /// boat rocking underneath the whole crane.
        ///
        /// Small-angle-independent in the restoring term (real sine, not a linearisation) so a
        /// load pushed to the swing limit still behaves; the two axes are treated as
        /// independent, which is exact for a plane swing and close enough for a circular one.
        /// </summary>
        private void IntegrateSwing(float dt)
        {
            if (boomTip == null || dt <= 0f) return;

            Vector3 tip = boomTip.position;
            if (!_swingInitialised)
            {
                _lastTip = tip;
                _swingInitialised = true;
                return;
            }

            // Finite differences on a transform written once a frame are noisy, and
            // differencing twice squares that noise. Both stages are low-passed or the load
            // buzzes instead of swinging.
            Vector3 velocity = (tip - _lastTip) / dt;
            _lastTip = tip;
            Vector3 smoothed = Vector3.Lerp(_tipVelocity, velocity, 1f - Mathf.Exp(-14f * dt));
            Vector3 acceleration = (smoothed - _tipVelocity) / dt;
            _tipVelocity = smoothed;
            _tipAcceleration = Vector3.Lerp(_tipAcceleration, acceleration,
                1f - Mathf.Exp(-10f * dt));

            Vector3 drive = Vector3.ClampMagnitude(
                new Vector3(_tipAcceleration.x, 0f, _tipAcceleration.z),
                maxDriveAcceleration) * swingResponse;

            // Period comes from the rope length, exactly as a real pendulum's does, so hauling
            // the load right up under the block makes it snappy and paying out a full drum
            // makes it slow and ponderous. Floored so a fully-hauled hook cannot produce an
            // absurd frequency.
            float length = Mathf.Max(_rig.Value.Hoist, 0.6f);
            float gravity = -Physics.gravity.y;

            // Positive A tilts the load toward -Z, so +Z drive pushes A up. Positive B tilts it
            // toward +X, so +X drive pushes B down. Both restore toward zero.
            float accelA = (-gravity * Mathf.Sin(_swingA) + drive.z * Mathf.Cos(_swingA)) / length
                           - swingDamping * _swingVelocityA;
            float accelB = (-gravity * Mathf.Sin(_swingB) - drive.x * Mathf.Cos(_swingB)) / length
                           - swingDamping * _swingVelocityB;

            _swingVelocityA += accelA * dt;
            _swingVelocityB += accelB * dt;
            _swingA += _swingVelocityA * dt;
            _swingB += _swingVelocityB * dt;

            float limit = maxSwingDegrees * Mathf.Deg2Rad;
            if (Mathf.Abs(_swingA) > limit)
            {
                _swingA = Mathf.Sign(_swingA) * limit;
                _swingVelocityA = 0f;
            }
            if (Mathf.Abs(_swingB) > limit)
            {
                _swingB = Mathf.Sign(_swingB) * limit;
                _swingVelocityB = 0f;
            }

            // Published on a deadband, not every frame. The boat is always moving a little, so
            // without this a moored crane would put a NetworkVariable write on the wire every
            // tick forever for a swing nobody can see.
            var current = _rig.Value;
            float swingX = _swingA * Mathf.Rad2Deg;
            float swingZ = _swingB * Mathf.Rad2Deg;
            if (Mathf.Abs(swingX - current.SwingX) < 0.05f &&
                Mathf.Abs(swingZ - current.SwingZ) < 0.05f) return;

            current.SwingX = swingX;
            current.SwingZ = swingZ;
            _rig.Value = current;
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
                _swingX = rig.SwingX;
                _swingZ = rig.SwingZ;
                _initialised = true;
            }
            else
            {
                float k = 1f - Mathf.Exp(-smoothing * dt);
                _slew = Mathf.Lerp(_slew, rig.Slew, k);
                _luff = Mathf.Lerp(_luff, rig.Luff, k);
                _hoist = Mathf.Lerp(_hoist, rig.Hoist, k);

                float swingK = 1f - Mathf.Exp(-swingSmoothing * dt);
                _swingX = Mathf.Lerp(_swingX, rig.SwingX, swingK);
                _swingZ = Mathf.Lerp(_swingZ, rig.SwingZ, swingK);
            }

            if (pedestal != null) pedestal.localRotation = Quaternion.Euler(0f, _slew, 0f);
            // Negative about X lifts local +Z toward +Y, so luff reads as elevation.
            if (boom != null) boom.localRotation = Quaternion.Euler(-_luff, 0f, 0f);

            if (boomTip == null || hookRoot == null) return;

            // The rope and hook are placed in WORLD space, deliberately. The boom hangs off the
            // rolling hull so it leans with the boat, while gravity does not - resolving this
            // in world space means the rope hangs plumb from wherever the tip has ended up,
            // with no assumption about how either object is parented.
            Vector3 tip = boomTip.position;
            Quaternion swing = Quaternion.AngleAxis(_swingX, Vector3.right)
                             * Quaternion.AngleAxis(_swingZ, Vector3.forward);
            Vector3 ropeDirection = swing * Vector3.down;

            // The block hangs along the rope rather than staying bolt upright, so a swinging
            // load leans into the swing the way a real one does.
            hookRoot.SetPositionAndRotation(tip + ropeDirection * _hoist, swing);

            if (rope != null)
            {
                rope.SetPositionAndRotation(tip + ropeDirection * (_hoist * 0.5f), swing);
                // A unit cube: scaling Y by the length gives exactly that length, and the
                // rotation above has already aligned local +Y back up the rope.
                rope.localScale = new Vector3(ropeThickness, _hoist, ropeThickness);
            }
        }

        // ---------------- load ----------------

        private void ServerToggleLoad()
        {
            if (hook == null) return;

            // Releasing ALWAYS works, wherever the load happens to be - mid-air over open
            // water included. That is what makes it possible to fish a pot up on one side and
            // let it go over another vessel's deck.
            var load = hook.First;
            if (load != null) { ServerRelease(load); return; }

            // Space takes anything in reach, lashed or not - this is the deliberate keypress
            // that lets the operator lift a pot back off a loaded deck. It also clears the
            // auto-grab block, because a keypress is never something to second-guess.
            _justReleased = null;
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
            // Blocked from the automatic grab until it drifts clear, or the hook it is still
            // falling through would take it straight back. Set before either branch: cargo
            // lashed into a zone must not be re-hooked either.
            _justReleased = cargo;

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
