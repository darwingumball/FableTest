using Unity.Netcode;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// Marks a world item as fuel and tracks how much is actually left in THIS one. Attached
    /// to the jerry can and fuel barrel prefabs by <c>ItemsBuilder</c>; <see cref="FuelTank"/>
    /// looks for this on whatever the player is currently physics-carrying when they interact
    /// with a tank.
    ///
    /// CAPACITY IS THE NOMINAL SIZE; <see cref="Liters"/> IS WHAT IS ACTUALLY IN IT. Pouring
    /// only ever drains what the receiving tank has room for - topping a nearly-full tank from
    /// a full jerry can leaves the can most of the way full rather than destroying it to fill
    /// the last litre. <see cref="ServerDrain"/> is the only way the level moves; nothing here
    /// decides how much to take, it just reports what it actually gave up.
    ///
    /// Server-authoritative like every other shared resource, for the same reason a tank's own
    /// level is: every peer needs to agree on the gauge without a round trip per reader.
    ///
    /// Deliberately its own component rather than a field on <c>ItemData</c>: capacity is a
    /// property of the physical prefab, not the shared catalog entry, and per-instance level is
    /// a property of THIS object, which a shared ScriptableObject asset cannot hold at all.
    ///
    /// KNOWN GAP: this level is only tracked while the can exists as a WORLD object (carried,
    /// dropped, sitting in a scatter). Picking it up into the bag (<c>WorldItem</c>'s "Pick up"
    /// interact, not <c>PhysicsPickup</c>'s carry) despawns this instance and grants a generic
    /// stack of `ItemData`, which has nowhere to remember a specific can's remaining litres - a
    /// partially-used can pocketed and pulled back out later comes back full. Fixing that
    /// needs per-instance payloads on inventory stacks, which is a real inventory feature, not
    /// a fuel one, and is out of scope here.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class FuelContainer : NetworkBehaviour
    {
        [SerializeField] private float capacityLiters = 20f;

        private readonly NetworkVariable<float> _liters = new(0f,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public float Capacity => capacityLiters;
        public float Liters => _liters.Value;
        public bool IsEmpty => _liters.Value <= 0.01f;

        public override void OnNetworkSpawn()
        {
            // A freshly spawned can always starts full - there is no path yet that would hand
            // one a specific starting level (see the class summary's known gap), so 0 can only
            // mean "never initialised" rather than "a can someone already used".
            if (IsServer && _liters.Value <= 0f) _liters.Value = capacityLiters;
        }

        /// <summary>Server-only. Removes up to <paramref name="amount"/> and reports how much
        /// was actually available to take.</summary>
        public float ServerDrain(float amount)
        {
            if (!IsServer || amount <= 0f) return 0f;
            float taken = Mathf.Min(amount, _liters.Value);
            _liters.Value -= taken;
            return taken;
        }
    }
}
