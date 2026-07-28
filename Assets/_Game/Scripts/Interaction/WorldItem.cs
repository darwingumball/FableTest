using Game.Inventory;
using Game.Net;
using UnityEngine;

namespace Game.Interaction
{
    /// <summary>
    /// An item lying in the world. Pressing Interact routes the pickup through the
    /// server (<see cref="WorldItemNetworkSync"/>) so it disappears for everyone.
    /// </summary>
    public class WorldItem : MonoBehaviour, IInteractable
    {
        public ItemData itemData;
        [Min(1)] public int quantity = 1;

        private WorldItemNetworkSync _sync;

        private void Awake()
        {
            _sync = GetComponent<WorldItemNetworkSync>();
        }

        public string GetPrompt(NetworkPlayer player)
        {
            if (itemData == null) return null;
            var inv = player != null ? player.GetComponent<PlayerInventory>() : null;
            if (inv != null && !inv.CanFit(itemData, quantity)) return $"{itemData.displayName} (inventory full)";
            return quantity > 1 ? $"Pick up {itemData.displayName} x{quantity}" : $"Pick up {itemData.displayName}";
        }

        public void Interact(NetworkPlayer player)
        {
            if (itemData == null) return;
            var inv = player != null ? player.GetComponent<PlayerInventory>() : null;
            if (inv == null || !inv.CanFit(itemData, quantity)) return;

            if (_sync != null && _sync.IsSpawned)
            {
                _sync.RequestPickup();
            }
            else
            {
                // Offline/unspawned fallback (should not happen in normal flow).
                inv.AddItem(itemData, quantity);
                Destroy(gameObject);
            }
        }
    }
}
