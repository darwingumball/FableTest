using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// Marks a world item as fuel and says how much it holds. Attached to the jerry can and
    /// fuel barrel prefabs by <c>ItemsBuilder</c>; <see cref="FuelTank"/> looks for this on
    /// whatever the player is currently physics-carrying when they interact with a tank.
    ///
    /// Deliberately its own component rather than a field on <c>ItemData</c>: the item asset
    /// is shared, network-resolved data (id, grid size, weight); how many litres a specific
    /// prefab represents is a property of the physical object in the world, not of the
    /// catalog entry, and keeping it here means the fuel system does not have to touch the
    /// inventory/save data model at all.
    /// </summary>
    public class FuelContainer : MonoBehaviour
    {
        [SerializeField] private float liters = 20f;

        public float Liters => liters;
    }
}
