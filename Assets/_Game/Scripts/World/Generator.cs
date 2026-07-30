using Game.Core;
using Game.Interaction;
using Game.Net;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// An on/off switch that draws from a <see cref="FuelTank"/> while running and powers a
    /// set of objects for as long as it does. Refusing to start with an empty tank, and
    /// stopping itself the instant one runs dry, is the whole point: fuel becomes a real
    /// constraint on whatever the generator is feeding rather than a cosmetic gauge.
    ///
    /// Server-authoritative running state; every peer applies <see cref="poweredObjects"/>
    /// locally from the replicated flag, so the lights or whatever else this drives look the
    /// same to everyone without their own network traffic.
    ///
    /// Firing <see cref="GameEventBus"/> events works differently here than everywhere else
    /// that fires one: the bus is a per-process static, so a plain <c>GameEventBus.Fire</c> on
    /// the server would only ever satisfy the SERVER's own quest flags. A generator switching
    /// on is world state every peer should be able to react to (a shared quest, a future
    /// objective), so the event is broadcast with a ClientRpc and fired locally on every peer
    /// including the host - the same problem <c>WorldItemNetworkSync.GrantItemClientRpc</c>
    /// solves for pickups, generalised because this state is shared rather than per-picker.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class Generator : NetworkBehaviour, IInteractable
    {
        [SerializeField] private FuelTank tank;
        [Tooltip("Consumption while running. Litres per hour reads more naturally than a raw " +
                 "rate for something meant to run for a long time, even though test instances " +
                 "are tuned faster so a play session can actually see it run dry.")]
        [SerializeField] private float litersPerHour = 3f;
        [Tooltip("Stable id for the events below. Defaults to this object's name.")]
        [SerializeField] private string generatorId = "";
        [Tooltip("Enabled while running, disabled the instant it stops - lights, powered " +
                 "props, whatever this generator is for.")]
        [SerializeField] private GameObject[] poweredObjects = System.Array.Empty<GameObject>();

        private readonly NetworkVariable<bool> _running = new(false,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private int _lastToggleFrame = -1;

        public bool IsRunning => _running.Value;
        public string Id => string.IsNullOrEmpty(generatorId) ? name : generatorId;

        public override void OnNetworkSpawn()
        {
            _running.OnValueChanged += OnRunningChanged;
            // OnValueChanged only fires on a CHANGE, so a late joiner reading an already-true
            // value would otherwise never apply the powered objects at all.
            ApplyPowered(_running.Value);
        }

        public override void OnNetworkDespawn()
        {
            _running.OnValueChanged -= OnRunningChanged;
        }

        private void OnRunningChanged(bool previous, bool current) => ApplyPowered(current);

        private void ApplyPowered(bool on)
        {
            foreach (var go in poweredObjects)
                if (go != null) go.SetActive(on);
        }

        private void Update()
        {
            if (!IsServer || !_running.Value || tank == null) return;
            if (!tank.TryConsume(litersPerHour / 3600f, Time.deltaTime))
                SetRunning(false);
        }

        // ---------------- interaction ----------------

        public string GetPrompt(NetworkPlayer player)
        {
            if (!IsSpawned) return null;
            if (_running.Value) return "Turn off generator";
            if (tank != null && tank.Liters > 0f) return "Turn on generator";
            return tank != null
                ? $"Generator: no fuel ({tank.Liters:0}/{tank.Capacity:0} L)"
                : "Generator: no fuel";
        }

        public void Interact(NetworkPlayer player)
        {
            // Same once-per-frame guard as the ladder and the helm: this is reachable both
            // from the interaction raycast and could otherwise double-fire in one frame.
            if (_lastToggleFrame == Time.frameCount) return;
            _lastToggleFrame = Time.frameCount;

            if (!IsSpawned) return;
            if (IsServer) ServerToggle();
            else RequestToggleServerRpc();
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestToggleServerRpc() => ServerToggle();

        private void ServerToggle()
        {
            if (_running.Value) { SetRunning(false); return; }
            if (tank == null || tank.Liters <= 0f) return;   // no fuel - refuse to start
            SetRunning(true);
        }

        private void SetRunning(bool on)
        {
            if (_running.Value == on) return;
            _running.Value = on;
            BroadcastEvent(on ? $"generator_powered:{Id}" : $"generator_stopped:{Id}");
        }

        // ---------------- cross-peer quest flags ----------------

        private void BroadcastEvent(string eventId)
        {
            if (!IsServer) return;
            FireEventClientRpc(new FixedString128Bytes(eventId));
        }

        [ClientRpc]
        private void FireEventClientRpc(FixedString128Bytes eventId) =>
            GameEventBus.Fire(eventId.ToString());
    }
}
