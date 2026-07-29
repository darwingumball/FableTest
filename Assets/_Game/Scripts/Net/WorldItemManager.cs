using Game.Inventory;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Game.Net
{
    /// <summary>
    /// Scene singleton (World) that spawns world item objects server-side: initial test
    /// scatter, inventory drops, and admin "give"-style spawns. Clients request drops via
    /// ServerRpc (classic RequireOwnership=false on a scene NetworkObject - the pattern
    /// proven by the old project's NetworkQuestSync).
    /// </summary>
    public class WorldItemManager : NetworkBehaviour
    {
        public static WorldItemManager Instance { get; private set; }

        [System.Serializable]
        public struct ScatterEntry
        {
            public string itemId;
            public int count;
            public Vector3 position;
        }

        [Tooltip("Server spawns these once when the scene comes up (test content).")]
        public ScatterEntry[] initialScatter;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Instance = null;

        private void Awake() => Instance = this;

        public override void OnDestroy()
        {
            if (Instance == this) Instance = null;
            base.OnDestroy();
        }

        public override void OnNetworkSpawn()
        {
            if (!IsServer || initialScatter == null) return;
            foreach (var entry in initialScatter)
                ServerSpawnItem(entry.itemId, entry.count, entry.position, Vector3.zero);
        }

        /// <summary>Server-only: spawn a networked world item. Returns the instance or null.</summary>
        public GameObject ServerSpawnItem(string itemId, int count, Vector3 position, Vector3 impulse)
        {
            if (!IsServer) return null;
            var item = ItemDatabase.Get(itemId);
            if (item == null || item.worldPrefab == null)
            {
                Debug.LogWarning($"[WorldItemManager] Cannot spawn '{itemId}' (missing item or worldPrefab).");
                return null;
            }

            var go = Instantiate(item.worldPrefab, position, Random.rotation);
            var worldItem = go.GetComponent<Interaction.WorldItem>();
            if (worldItem != null) worldItem.quantity = Mathf.Max(1, count);

            go.GetComponent<NetworkObject>().Spawn(true);

            if (impulse != Vector3.zero && go.TryGetComponent<Rigidbody>(out var rb))
                rb.AddForce(impulse, ForceMode.VelocityChange);
            return go;
        }

        /// <summary>Client path for dropping items out of the inventory.</summary>
        public void RequestDrop(string itemId, int count, Vector3 position, Vector3 impulse)
        {
            if (IsServer) ServerSpawnItem(itemId, count, position, impulse);
            else RequestDropServerRpc(new FixedString64Bytes(itemId), count, position, impulse);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestDropServerRpc(FixedString64Bytes itemId, int count, Vector3 position, Vector3 impulse)
        {
            ServerSpawnItem(itemId.ToString(), Mathf.Clamp(count, 1, 999), position, impulse);
        }
    }
}
