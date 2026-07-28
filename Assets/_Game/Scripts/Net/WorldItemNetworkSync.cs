using Game.Interaction;
using Game.Inventory;
using Unity.Netcode;
using UnityEngine;

namespace Game.Net
{
    /// <summary>
    /// Server-routed pickup + physics-carry ownership transfer for world items.
    ///
    /// Pickup: client checks fit locally, calls <see cref="RequestPickup"/>; the server
    /// validates the item still exists, targets a GrantItemClientRpc at the picker, then
    /// despawns - every peer sees it vanish exactly once.
    ///
    /// Carry: <see cref="RequestOwnership"/>/<see cref="ReleaseOwnership"/> flip
    /// NetworkObject ownership so the carrier's Rigidbody becomes dynamic (owner-auth
    /// NetworkTransform/NetworkRigidbody) while held, and returns to server simulation
    /// on release.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    [RequireComponent(typeof(WorldItem))]
    public class WorldItemNetworkSync : NetworkBehaviour
    {
        /// <summary>Server-written; late joiners read stack size on spawn.</summary>
        private readonly NetworkVariable<int> _quantity = new(1,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private WorldItem _item;

        private void Awake()
        {
            _item = GetComponent<WorldItem>();
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer)
                _quantity.Value = _item.quantity;
            else
                _item.quantity = _quantity.Value;
            _quantity.OnValueChanged += (_, v) => _item.quantity = v;
        }

        // ---------------- pickup ----------------

        public void RequestPickup()
        {
            if (!IsSpawned) return;
            if (IsServer) ServerPickup(NetworkManager.Singleton.LocalClientId);
            else RequestPickupServerRpc();
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestPickupServerRpc(ServerRpcParams rpcParams = default)
        {
            ServerPickup(rpcParams.Receive.SenderClientId);
        }

        private void ServerPickup(ulong clientId)
        {
            if (_item.itemData == null || !NetworkObject.IsSpawned) return;

            if (clientId == NetworkManager.ServerClientId)
            {
                GrantLocally(_item.itemData.Id, _item.quantity);
            }
            else
            {
                GrantItemClientRpc(_item.itemData.Id, _item.quantity, new ClientRpcParams
                {
                    Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
                });
            }

            // Despawn AFTER the grant RPC - per-connection RPC order guarantees the
            // picker receives the item before the object disappears.
            NetworkObject.Despawn(true);
        }

        [ClientRpc]
        private void GrantItemClientRpc(string itemId, int quantity, ClientRpcParams _)
        {
            GrantLocally(itemId, quantity);
        }

        private static void GrantLocally(string itemId, int quantity)
        {
            var item = ItemDatabase.Get(itemId);
            var inv = NetworkPlayer.Local != null ? NetworkPlayer.Local.GetComponent<PlayerInventory>() : null;
            if (item == null || inv == null)
            {
                Debug.LogWarning($"[WorldItemNetworkSync] Could not grant '{itemId}' x{quantity}.");
                return;
            }
            int leftover = inv.AddItem(item, quantity);
            if (leftover > 0)
                Debug.LogWarning($"[WorldItemNetworkSync] {leftover}x '{itemId}' didn't fit after pickup (race).");
            Core.GameEventBus.Fire($"item_collected:{itemId}");
        }

        // ---------------- carry ownership ----------------

        public void RequestOwnership()
        {
            if (!IsSpawned || IsOwner) return;
            if (IsServer) NetworkObject.ChangeOwnership(NetworkManager.Singleton.LocalClientId);
            else RequestOwnershipServerRpc();
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestOwnershipServerRpc(ServerRpcParams rpcParams = default)
        {
            if (!NetworkObject.IsSpawned) return;
            NetworkObject.ChangeOwnership(rpcParams.Receive.SenderClientId);
        }

        public void ReleaseOwnership()
        {
            if (!IsSpawned) return;
            if (IsServer)
            {
                if (OwnerClientId != NetworkManager.ServerClientId)
                    NetworkObject.ChangeOwnership(NetworkManager.ServerClientId);
                return;
            }
            ReleaseOwnershipServerRpc();
        }

        [ServerRpc(RequireOwnership = false)]
        private void ReleaseOwnershipServerRpc()
        {
            if (!NetworkObject.IsSpawned) return;
            NetworkObject.ChangeOwnership(NetworkManager.ServerClientId);
        }
    }
}
