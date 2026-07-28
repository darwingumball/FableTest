using System.Collections.Generic;
using UnityEngine;

namespace Game.Inventory
{
    /// <summary>Id -> ItemData lookup over everything in Resources/Items.</summary>
    public static class ItemDatabase
    {
        private static Dictionary<string, ItemData> _byId;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _byId = null;

        private static void EnsureLoaded()
        {
            if (_byId != null) return;
            _byId = new Dictionary<string, ItemData>();
            foreach (var item in Resources.LoadAll<ItemData>("Items"))
            {
                if (!_byId.TryAdd(item.Id, item))
                    Debug.LogError($"[ItemDatabase] Duplicate itemId '{item.Id}' ({item.name}).");
            }
            Debug.Log($"[ItemDatabase] Loaded {_byId.Count} items.");
        }

        public static ItemData Get(string itemId)
        {
            EnsureLoaded();
            return itemId != null && _byId.TryGetValue(itemId, out var item) ? item : null;
        }

        public static IEnumerable<ItemData> All
        {
            get { EnsureLoaded(); return _byId.Values; }
        }
    }
}
