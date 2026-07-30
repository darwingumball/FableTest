using Game.Interaction;
using Game.Net;
using Unity.Netcode;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// A refillable tank: a ship's engine, or a property's generator. Interact while carrying
    /// a <see cref="FuelContainer"/> (a jerry can, a fuel barrel) to pour it in; the container
    /// is consumed. Interact while carrying nothing just reads the gauge.
    ///
    /// Server-authoritative, like every other shared resource here: the level is one
    /// <c>NetworkVariable&lt;float&gt;</c>, written only by the server, so every peer agrees on
    /// how much fuel is left without the tank needing its own RPC round trip for every reader.
    ///
    /// <see cref="TryConsume"/> is the other half of the contract - the thing this tank feeds
    /// (a <see cref="Generator"/>, a boat's <c>BoatHelm</c>) calls it once a tick on the server
    /// and gets back whether there was fuel to draw. Neither consumer needs to know anything
    /// about litres, containers, or refuelling; they just get told yes or no.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class FuelTank : NetworkBehaviour, IInteractable
    {
        [SerializeField] private float capacityLiters = 100f;
        [Tooltip("What the tank holds when the scene starts. Not the capacity - most tanks " +
                 "should start partly used, so both running dry and refuelling are things a " +
                 "player can actually test without waiting.")]
        [SerializeField] private float startingLiters = 40f;

        private readonly NetworkVariable<float> _liters = new(0f,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public float Liters => _liters.Value;
        public float Capacity => capacityLiters;
        public float Fraction => capacityLiters > 0f ? _liters.Value / capacityLiters : 0f;

        public override void OnNetworkSpawn()
        {
            if (IsServer) _liters.Value = Mathf.Clamp(startingLiters, 0f, capacityLiters);
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
            if (!IsSpawned) return null;

            var container = HeldContainer(player);
            if (container != null)
            {
                if (_liters.Value >= capacityLiters - 0.01f)
                    return $"Fuel tank full ({_liters.Value:0}/{capacityLiters:0} L)";
                return $"Refuel with {Label(container)} (+{container.Liters:0} L)";
            }

            // Nothing to pour in - just read the gauge. Interact() is a safe no-op here, the
            // same shape as BoatHelm reporting "Helm in use": informative, not actionable.
            return $"Fuel tank: {_liters.Value:0}/{capacityLiters:0} L";
        }

        public void Interact(NetworkPlayer player)
        {
            var container = HeldContainer(player);
            if (container == null) return;

            var netObj = container.GetComponent<NetworkObject>();
            if (netObj == null || !netObj.IsSpawned) return;

            if (IsServer) ServerRefuel(netObj);
            else RequestRefuelServerRpc(netObj);
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

        [ServerRpc(RequireOwnership = false)]
        private void RequestRefuelServerRpc(NetworkObjectReference containerRef, ServerRpcParams p = default)
        {
            if (!containerRef.TryGet(out NetworkObject netObj)) return;
            // Physics-carry already transferred ownership to whoever is holding the container
            // (see PhysicsPickup.TryGrab), so ownership IS "who is holding this" - the same
            // check WorldItemNetworkSync relies on for its own carry RPCs.
            if (netObj.OwnerClientId != p.Receive.SenderClientId) return;
            ServerRefuel(netObj);
        }

        private void ServerRefuel(NetworkObject containerObject)
        {
            if (!IsServer || !containerObject.IsSpawned) return;
            var container = containerObject.GetComponent<FuelContainer>();
            if (container == null || _liters.Value >= capacityLiters - 0.01f) return;

            _liters.Value = Mathf.Min(capacityLiters, _liters.Value + container.Liters);
            containerObject.Despawn(true);
        }
    }
}
