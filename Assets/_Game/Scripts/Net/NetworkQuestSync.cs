using System.Collections.Generic;
using Game.Quests;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Game.Net
{
    /// <summary>
    /// Replicates SHARED quests (scene object in World). Server owns the truth:
    /// accept/progress/turn-in requests hop to the server, get applied there, then fan
    /// out to everyone. Late joiners pull the full state via RequestSync (flattened
    /// arrays - string[] is not RPC-serializable).
    /// Personal quests never touch this class.
    /// </summary>
    public class NetworkQuestSync : NetworkBehaviour
    {
        public static NetworkQuestSync Instance { get; private set; }

        // Server-side truth for shared quests: questId -> per-objective progress.
        private readonly Dictionary<string, int[]> _serverShared = new();

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
            if (!IsServer)
                RequestSyncServerRpc();
        }

        // ---------------- accept ----------------

        public void RequestAccept(string questId)
        {
            if (IsServer) ServerAccept(questId);
            else RequestAcceptServerRpc(new FixedString64Bytes(questId));
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestAcceptServerRpc(FixedString64Bytes questId) => ServerAccept(questId.ToString());

        private void ServerAccept(string questId)
        {
            var quest = QuestManager.Get(questId);
            if (quest == null || !quest.sharedProgress || _serverShared.ContainsKey(questId)) return;
            _serverShared[questId] = new int[quest.objectives.Length];
            AcceptClientRpc(new FixedString64Bytes(questId));
        }

        [ClientRpc]
        private void AcceptClientRpc(FixedString64Bytes questId) => QuestManager.ApplyAccept(questId.ToString());

        // ---------------- progress ----------------

        public void ReportProgress(string questId, int objectiveIndex, int amount)
        {
            if (IsServer) ServerProgress(questId, objectiveIndex, amount);
            else ReportProgressServerRpc(new FixedString64Bytes(questId), objectiveIndex, amount);
        }

        [ServerRpc(RequireOwnership = false)]
        private void ReportProgressServerRpc(FixedString64Bytes questId, int objectiveIndex, int amount)
            => ServerProgress(questId.ToString(), objectiveIndex, amount);

        private void ServerProgress(string questId, int objectiveIndex, int amount)
        {
            var quest = QuestManager.Get(questId);
            if (quest == null || !_serverShared.TryGetValue(questId, out var progress)) return;
            if (objectiveIndex < 0 || objectiveIndex >= progress.Length || amount <= 0) return;

            int required = quest.objectives[objectiveIndex].requiredAmount;
            progress[objectiveIndex] = Mathf.Min(required, progress[objectiveIndex] + amount);
            ProgressClientRpc(new FixedString64Bytes(questId), objectiveIndex, progress[objectiveIndex]);
        }

        [ClientRpc]
        private void ProgressClientRpc(FixedString64Bytes questId, int objectiveIndex, int value)
            => QuestManager.ApplyProgress(questId.ToString(), objectiveIndex, value);

        // ---------------- late-join sync ----------------

        [ServerRpc(RequireOwnership = false)]
        private void RequestSyncServerRpc(ServerRpcParams rpcParams = default)
        {
            var ids = new List<FixedString64Bytes>();
            var strides = new List<int>();
            var flat = new List<int>();
            foreach (var kvp in _serverShared)
            {
                ids.Add(new FixedString64Bytes(kvp.Key));
                strides.Add(kvp.Value.Length);
                flat.AddRange(kvp.Value);
            }

            SyncClientRpc(ids.ToArray(), strides.ToArray(), flat.ToArray(), new ClientRpcParams
            {
                Send = new ClientRpcSendParams
                {
                    TargetClientIds = new[] { rpcParams.Receive.SenderClientId }
                }
            });
        }

        [ClientRpc]
        private void SyncClientRpc(FixedString64Bytes[] questIds, int[] strides, int[] flatProgress, ClientRpcParams _)
        {
            int cursor = 0;
            for (int i = 0; i < questIds.Length; i++)
            {
                string id = questIds[i].ToString();
                QuestManager.ApplyAccept(id);
                for (int o = 0; o < strides[i]; o++)
                    QuestManager.ApplyProgress(id, o, flatProgress[cursor + o]);
                cursor += strides[i];
            }
        }

        // ---------------- save bridge (Phase 12) ----------------

        public Dictionary<string, int[]> ServerGetSharedState() => new(_serverShared);

        public void ServerApplySharedState(Dictionary<string, int[]> state)
        {
            if (!IsServer || state == null) return;
            _serverShared.Clear();
            foreach (var kvp in state)
            {
                _serverShared[kvp.Key] = kvp.Value;
                AcceptClientRpc(new FixedString64Bytes(kvp.Key));
                for (int o = 0; o < kvp.Value.Length; o++)
                    ProgressClientRpc(new FixedString64Bytes(kvp.Key), o, kvp.Value[o]);
            }
        }
    }
}
