using System.Collections.Generic;
using Game.Core;
using Game.Inventory;
using Game.Net;
using Game.Quests;
using Game.World;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Game.Save
{
    /// <summary>
    /// Runtime save orchestrator (scene object in World). Only the HOST touches disk.
    ///
    /// Saving: host writes world + shared quests immediately; each client is asked for
    /// its inventory/quest snapshot via RPC and the host writes it under the client's
    /// Unity Auth playerId, so a returning player gets their own character back.
    ///
    /// Loading: on spawn the host restores world/quest state; per-player records are
    /// pushed to each client as they connect (including reconnects mid-session).
    /// </summary>
    public class SaveService : NetworkBehaviour
    {
        public static SaveService Instance { get; private set; }

        [SerializeField] private float autosaveIntervalSeconds = 180f;

        private float _autosaveTimer;
        private int Slot => SessionContext.SelectedSaveSlot;
        private bool HostCanSave => IsServer && Slot >= 0;

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
            if (IsServer)
            {
                // Scene NetworkObjects spawn in arbitrary order and TimeSync/Weather set
                // their own defaults on spawn - restore a frame later so we win.
                if (SessionContext.IsContinue && Slot >= 0) StartCoroutine(RestoreWorldDeferred());
                NetworkManager.OnClientConnectedCallback += OnClientConnected;
                // The host's own player is already here.
                RestorePlayer(NetworkManager.ServerClientId);
            }
            _autosaveTimer = autosaveIntervalSeconds;
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer && NetworkManager != null)
                NetworkManager.OnClientConnectedCallback -= OnClientConnected;
        }

        private void Update()
        {
            if (!HostCanSave || autosaveIntervalSeconds <= 0f) return;
            _autosaveTimer -= Time.deltaTime;
            if (_autosaveTimer <= 0f)
            {
                _autosaveTimer = autosaveIntervalSeconds;
                SaveAll();
            }
        }

        // ---------------- saving ----------------

        /// <summary>Host-only full save. Safe to call from UI.</summary>
        public void SaveAll()
        {
            if (!HostCanSave) return;

            var world = new SaveSystem.WorldSave
            {
                worldHours = NetworkTimeSync.Instance != null ? NetworkTimeSync.Instance.WorldHours : 8.0,
                eventFlags = new List<string>(QuestManager.Flags).ToArray(),
            };
            if (WeatherManager.Instance != null)
            {
                var (type, intensity) = WeatherManager.Instance.GetSaveState();
                world.weatherType = (int)type;
                world.weatherIntensity = intensity;
            }
            SaveSystem.WriteWorld(Slot, world);

            if (NetworkQuestSync.Instance != null)
            {
                var shared = NetworkQuestSync.Instance.ServerGetSharedState();
                var ids = new List<string>();
                var strides = new List<int>();
                var flat = new List<int>();
                foreach (var kvp in shared)
                {
                    ids.Add(kvp.Key);
                    strides.Add(kvp.Value.Length);
                    flat.AddRange(kvp.Value);
                }
                SaveSystem.WriteQuests(Slot, new SaveSystem.QuestsSave
                {
                    sharedIds = ids.ToArray(),
                    sharedStrides = strides.ToArray(),
                    sharedProgress = flat.ToArray(),
                });
            }

            // Host's own player record is gathered locally; remote clients are asked.
            StorePlayerRecord(NetworkManager.ServerClientId, CaptureLocalPlayer());
            RequestPlayerSnapshotClientRpc();

            SaveSystem.TouchLastPlayed(Slot);
            Debug.Log($"[SaveService] Saved slot {Slot}.");
        }

        /// <summary>Every client (except the host, which captured locally) uploads its state.</summary>
        [ClientRpc]
        private void RequestPlayerSnapshotClientRpc()
        {
            if (IsServer) return;
            var snapshot = CaptureLocalPlayer();
            SubmitPlayerSnapshotServerRpc(
                new FixedString4096Bytes(snapshot.inventoryJson),
                new FixedString4096Bytes(snapshot.questsJson),
                snapshot.position, snapshot.health);
        }

        [ServerRpc(RequireOwnership = false)]
        private void SubmitPlayerSnapshotServerRpc(
            FixedString4096Bytes inventoryJson, FixedString4096Bytes questsJson,
            Vector3 position, float health, ServerRpcParams p = default)
        {
            StorePlayerRecord(p.Receive.SenderClientId, new SaveSystem.PlayerSave
            {
                inventoryJson = inventoryJson.ToString(),
                questsJson = questsJson.ToString(),
                position = position,
                health = health,
            });
        }

        private void StorePlayerRecord(ulong clientId, SaveSystem.PlayerSave record)
        {
            if (!HostCanSave || record == null) return;
            string authId = ServerPlayerRegistry.GetAuthId(clientId);
            if (string.IsNullOrEmpty(authId)) return;
            record.authPlayerId = authId;
            SaveSystem.WritePlayer(Slot, record);
        }

        private static SaveSystem.PlayerSave CaptureLocalPlayer()
        {
            var local = NetworkPlayer.Local;
            var record = new SaveSystem.PlayerSave
            {
                questsJson = JsonUtility.ToJson(QuestManager.GetState()),
            };
            if (local != null)
            {
                record.position = local.transform.position;
                if (local.Stats != null) record.health = local.Stats.Health;
                var inv = local.GetComponent<PlayerInventory>();
                if (inv != null) record.inventoryJson = JsonUtility.ToJson(inv.GetState());
            }
            return record;
        }

        // ---------------- loading ----------------

        private System.Collections.IEnumerator RestoreWorldDeferred()
        {
            // Wait until the systems we write into exist and are spawned.
            float timeout = 5f;
            while (timeout > 0f
                   && (NetworkTimeSync.Instance == null || !NetworkTimeSync.Instance.IsSpawned
                       || WeatherManager.Instance == null || !WeatherManager.Instance.IsSpawned
                       || NetworkQuestSync.Instance == null || !NetworkQuestSync.Instance.IsSpawned))
            {
                timeout -= Time.deltaTime;
                yield return null;
            }
            yield return null; // let their own OnNetworkSpawn defaults land first
            RestoreWorld();
        }

        private void RestoreWorld()
        {
            var world = SaveSystem.LoadWorld(Slot);
            if (world != null)
            {
                if (NetworkTimeSync.Instance != null)
                    NetworkTimeSync.Instance.ServerSetTime(world.worldHours);
                if (WeatherManager.Instance != null)
                    WeatherManager.Instance.ServerApplySaveState((WeatherType)world.weatherType, world.weatherIntensity);
            }

            var quests = SaveSystem.LoadQuests(Slot);
            if (quests != null && NetworkQuestSync.Instance != null)
            {
                var shared = new Dictionary<string, int[]>();
                int cursor = 0;
                for (int i = 0; i < quests.sharedIds.Length; i++)
                {
                    int stride = quests.sharedStrides[i];
                    var progress = new int[stride];
                    for (int o = 0; o < stride; o++) progress[o] = quests.sharedProgress[cursor + o];
                    cursor += stride;
                    shared[quests.sharedIds[i]] = progress;
                }
                NetworkQuestSync.Instance.ServerApplySharedState(shared);
            }
        }

        private void OnClientConnected(ulong clientId) => RestorePlayer(clientId);

        /// <summary>Host reads this client's record (if any) and pushes it to them.</summary>
        private void RestorePlayer(ulong clientId)
        {
            if (!HostCanSave || !SessionContext.IsContinue) return;
            string authId = ServerPlayerRegistry.GetAuthId(clientId);
            if (string.IsNullOrEmpty(authId)) return;

            var record = SaveSystem.LoadPlayer(Slot, authId);
            if (record == null) return; // brand-new player joins with a fresh character

            ApplyPlayerClientRpc(
                new FixedString4096Bytes(record.inventoryJson ?? ""),
                new FixedString4096Bytes(record.questsJson ?? ""),
                record.position,
                new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } } });

            if (NetworkManager.ConnectedClients.TryGetValue(clientId, out var client)
                && client.PlayerObject != null)
            {
                var stats = client.PlayerObject.GetComponent<Player.PlayerStats>();
                if (stats != null)
                {
                    stats.ServerApplyDamage(Player.PlayerStats.MAX_HEALTH - record.health);
                }
            }
        }

        [ClientRpc]
        private void ApplyPlayerClientRpc(
            FixedString4096Bytes inventoryJson, FixedString4096Bytes questsJson,
            Vector3 position, ClientRpcParams _)
        {
            var local = NetworkPlayer.Local;
            if (local == null) return;

            string invJson = inventoryJson.ToString();
            if (!string.IsNullOrEmpty(invJson))
            {
                var inv = local.GetComponent<PlayerInventory>();
                if (inv != null) inv.ApplyState(JsonUtility.FromJson<PlayerInventory.State>(invJson));
            }

            string qJson = questsJson.ToString();
            if (!string.IsNullOrEmpty(qJson))
                QuestManager.ApplyState(JsonUtility.FromJson<QuestManager.SaveState>(qJson));

            var cc = local.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            // Snow depth at save time is not snow depth at load time, so the stored Y can
            // land inside the snow volume. Re-seat on the surface that exists now.
            local.transform.position = GroundProbe.ResolveStandingPosition(position);
            if (cc != null) cc.enabled = true;
        }
    }
}
