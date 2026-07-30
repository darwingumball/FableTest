using Game.Interaction;
using Game.Net;
using Unity.Netcode;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// A refillable tank: a ship's engine, or a property's generator. Interact while carrying
    /// a <see cref="FuelContainer"/> (a jerry can, a fuel barrel) to start pouring it in;
    /// interact again - or walk the container away, or let go of it - to stop. Interact while
    /// carrying nothing just reads the gauge.
    ///
    /// POURING TAKES TIME, at <see cref="RefuelLitersPerSecond"/>, rather than emptying the can
    /// the instant E is pressed. That is what makes "in the middle of a transfer" a real state
    /// worth showing a gauge for, and it is also what makes the exclusivity below matter - two
    /// players cannot both be pouring into the same nozzle at once.
    ///
    /// Server-authoritative, like every other shared resource here: the level is one
    /// `NetworkVariable&lt;float&gt;`, written only by the server, so every peer agrees on how
    /// much fuel is left without the tank needing its own RPC round trip for every reader. WHO
    /// is currently refuelling is replicated the same way `BoatHelm`'s driver and
    /// `CraneController`'s operator are - a single client id, `Everyone`-readable, so any peer
    /// can tell at a glance that a tank is already in use.
    ///
    /// <see cref="TryConsume"/> is the other half of the contract - the thing this tank feeds
    /// (a <see cref="Generator"/>, a boat's `BoatHelm`) calls it once a tick on the server and
    /// gets back whether there was fuel to draw. Neither consumer needs to know anything about
    /// litres, containers, or refuelling; they just get told yes or no.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class FuelTank : NetworkBehaviour, IInteractable
    {
        private const ulong NoRefueler = ulong.MaxValue;
        private const float RefuelLitersPerSecond = 1f;
        // The container has to stay roughly where it was when pouring started - a nozzle
        // inserted, not a can waved in the general direction of the tank. Physics-carry keeps
        // a held item pinned near the camera, so this only ever trips if the player actually
        // walks away or lets go.
        private const float MaxRefuelDistance = 1.6f;

        [SerializeField] private float capacityLiters = 100f;
        [Tooltip("What the tank holds when the scene starts. Not the capacity - most tanks " +
                 "should start partly used, so both running dry and refuelling are things a " +
                 "player can actually test without waiting.")]
        [SerializeField] private float startingLiters = 40f;

        private readonly NetworkVariable<float> _liters = new(0f,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<ulong> _refuelerClientId = new(NoRefueler,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Server-only bookkeeping for the active pour. Never read on a non-server peer.
        private FuelContainer _serverContainer;
        private NetworkObject _serverContainerObject;

        private int _lastToggleFrame = -1;

        public float Liters => _liters.Value;
        public float Capacity => capacityLiters;
        public float Fraction => capacityLiters > 0f ? _liters.Value / capacityLiters : 0f;

        /// <summary>
        /// What the LOCAL player is pouring from, purely for the HUD gauge - set directly by
        /// whichever peer initiates the interaction (see <see cref="Interact"/>), never
        /// replicated. Only meaningful when <see cref="LocalActive"/> is this tank.
        /// </summary>
        public FuelContainer LocalRefuelSource { get; private set; }

        /// <summary>The one tank the local player is currently refuelling, or null. The HUD
        /// polls this rather than every FuelTank in the scene.</summary>
        public static FuelTank LocalActive { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => LocalActive = null;

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                _liters.Value = Mathf.Clamp(startingLiters, 0f, capacityLiters);
                NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
            }
            _refuelerClientId.OnValueChanged += OnRefuelerChanged;
        }

        public override void OnNetworkDespawn()
        {
            _refuelerClientId.OnValueChanged -= OnRefuelerChanged;
            if (IsServer && NetworkManager != null)
                NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
        }

        private void OnClientDisconnected(ulong clientId)
        {
            // A refueller who quits mid-pour would otherwise leave the tank locked out for
            // everyone else forever.
            if (_refuelerClientId.Value == clientId) StopRefuel();
        }

        private void OnRefuelerChanged(ulong previous, ulong current)
        {
            if (NetworkManager == null) return;
            ulong localId = NetworkManager.LocalClientId;
            // The server ended OUR session (tank filled, can ran dry, distance/ownership
            // failed) without us asking - clear the local HUD state reactively rather than
            // leaving a stale gauge on screen.
            if (previous == localId && current != localId)
            {
                LocalRefuelSource = null;
                if (LocalActive == this) LocalActive = null;
            }
        }

        // ---------------- consumer-facing API ----------------

        /// <summary>
        /// Server-only. Draws the tank down by <paramref name="litersPerSecond"/> * dt and
        /// reports whether there was fuel to draw BEFORE this call. The last tick before the
        /// tank runs dry still returns true - some fuel was burned, so an engine or generator
        /// does not cut out mid-tick - and the very next call reports false, which is the
        /// caller's signal to shut down.
        /// </summary>
        public bool TryConsume(float litersPerSecond, float dt)
        {
            if (!IsServer) return _liters.Value > 0f;
            if (_liters.Value <= 0f) return false;
            if (litersPerSecond > 0f)
                _liters.Value = Mathf.Max(0f, _liters.Value - litersPerSecond * dt);
            return true;
        }

        // ---------------- interaction ----------------

        public string GetPrompt(NetworkPlayer player)
        {
            if (!IsSpawned || NetworkManager == null) return null;
            ulong localId = NetworkManager.LocalClientId;

            if (_refuelerClientId.Value == localId) return "Stop refuelling";
            if (_refuelerClientId.Value != NoRefueler) return "Fuel tank: refuelling (in use)";

            var container = HeldContainer(player);
            if (container != null)
            {
                if (_liters.Value >= capacityLiters - 0.01f)
                    return $"Fuel tank full ({_liters.Value:0}/{capacityLiters:0} L)";
                return $"Refuel with {Label(container)} ({container.Liters:0.0} L available)";
            }

            // Nothing to pour in - just read the gauge. Interact() is a safe no-op here, the
            // same shape as BoatHelm reporting "Helm in use": informative, not actionable.
            return $"Fuel tank: {_liters.Value:0}/{capacityLiters:0} L";
        }

        public void Interact(NetworkPlayer player)
        {
            // Reachable both from the interaction raycast and could otherwise double-toggle
            // in one frame - same guard as the ladder, the helm, the crane.
            if (_lastToggleFrame == Time.frameCount) return;
            _lastToggleFrame = Time.frameCount;
            if (!IsSpawned || player == null || NetworkManager == null) return;

            ulong localId = NetworkManager.LocalClientId;

            if (_refuelerClientId.Value == localId)
            {
                LocalRefuelSource = null;
                if (LocalActive == this) LocalActive = null;
                if (IsServer) StopRefuel(); else RequestStopRefuelServerRpc();
                return;
            }
            if (_refuelerClientId.Value != NoRefueler) return;   // someone else already pouring

            var container = HeldContainer(player);
            if (container == null) return;
            var netObj = container.GetComponent<NetworkObject>();
            if (netObj == null || !netObj.IsSpawned) return;

            // Optimistic, and purely local - if the server refuses (tank filled a moment ago
            // on another peer's input, say), OnRefuelerChanged unwinds this the instant the
            // replicated value fails to become ours.
            LocalRefuelSource = container;
            LocalActive = this;

            if (IsServer) ServerStartRefuel(localId, netObj);
            else RequestStartRefuelServerRpc(netObj);
        }

        private static FuelContainer HeldContainer(NetworkPlayer player)
        {
            var pickup = player != null ? player.GetComponent<PhysicsPickup>() : null;
            var held = pickup != null ? pickup.HeldObject : null;
            return held != null ? held.GetComponent<FuelContainer>() : null;
        }

        private static string Label(FuelContainer container)
        {
            var item = container.GetComponent<WorldItem>();
            return item != null && item.itemData != null ? item.itemData.displayName : "fuel";
        }

        // ---------------- server: start/stop ----------------

        [ServerRpc(RequireOwnership = false)]
        private void RequestStartRefuelServerRpc(NetworkObjectReference containerRef, ServerRpcParams p = default)
        {
            if (containerRef.TryGet(out NetworkObject netObj))
                ServerStartRefuel(p.Receive.SenderClientId, netObj);
        }

        private void ServerStartRefuel(ulong clientId, NetworkObject containerObject)
        {
            if (!IsServer || _refuelerClientId.Value != NoRefueler) return;
            if (containerObject == null || !containerObject.IsSpawned) return;
            // Physics-carry already transferred ownership to whoever is holding the container
            // (see PhysicsPickup.TryGrab), so ownership IS "who is holding this" - the same
            // check WorldItemNetworkSync relies on for its own carry RPCs.
            if (containerObject.OwnerClientId != clientId) return;

            var container = containerObject.GetComponent<FuelContainer>();
            if (container == null || container.IsEmpty) return;
            if (_liters.Value >= capacityLiters - 0.01f) return;

            _serverContainer = container;
            _serverContainerObject = containerObject;
            _refuelerClientId.Value = clientId;
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestStopRefuelServerRpc(ServerRpcParams p = default)
        {
            if (_refuelerClientId.Value == p.Receive.SenderClientId) StopRefuel();
        }

        private void StopRefuel()
        {
            _refuelerClientId.Value = NoRefueler;
            _serverContainer = null;
            _serverContainerObject = null;
        }

        // ---------------- server: the pour itself ----------------

        private void Update()
        {
            if (!IsServer || _refuelerClientId.Value == NoRefueler) return;

            // Validated every tick rather than only at start: the container can be dropped,
            // thrown, emptied by this same transfer, or walked away from at any point, and any
            // of those should end the session quietly rather than leave it stuck open.
            if (_serverContainerObject == null || !_serverContainerObject.IsSpawned
                || _serverContainerObject.OwnerClientId != _refuelerClientId.Value
                || _serverContainer.IsEmpty
                || _liters.Value >= capacityLiters - 0.01f
                || Vector3.Distance(_serverContainerObject.transform.position, transform.position)
                   > MaxRefuelDistance)
            {
                StopRefuel();
                return;
            }

            float want = RefuelLitersPerSecond * Time.deltaTime;
            float room = capacityLiters - _liters.Value;
            float taken = _serverContainer.ServerDrain(Mathf.Min(want, room));
            _liters.Value += taken;

            if (_serverContainer.IsEmpty || _liters.Value >= capacityLiters - 0.01f) StopRefuel();
        }
    }
}
