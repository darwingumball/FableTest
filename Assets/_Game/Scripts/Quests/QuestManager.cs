using System;
using System.Collections.Generic;
using System.Linq;
using Game.Core;
using Game.Inventory;
using Game.Net;
using UnityEngine;

namespace Game.Quests
{
    [Serializable]
    public class QuestState
    {
        public string questId;
        public int[] progress = Array.Empty<int>();
        public bool completed;
    }

    /// <summary>
    /// Local quest brain. Personal quests live entirely here; shared quests are mirrored
    /// from the server via <see cref="NetworkQuestSync"/> - all mutations to shared quests
    /// route through the server (this fixes the old project's Talk-objective gap where the
    /// triggering client applied progress locally).
    ///
    /// Gameplay reports through <see cref="Report"/>; UI listens to <see cref="Changed"/>.
    /// </summary>
    public static class QuestManager
    {
        private static Dictionary<string, QuestData> _catalog;
        private static readonly Dictionary<string, QuestState> _active = new();
        private static readonly HashSet<string> _completed = new();
        private static readonly HashSet<string> _flags = new();

        public static event Action Changed;
        public static event Action<QuestData> QuestAccepted;
        public static event Action<QuestData> QuestCompleted;

        public static IReadOnlyDictionary<string, QuestState> Active => _active;
        public static IReadOnlyCollection<string> CompletedQuests => _completed;
        public static IReadOnlyCollection<string> Flags => _flags;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _catalog = null;
            _active.Clear();
            _completed.Clear();
            _flags.Clear();
            Changed = null;
            QuestAccepted = null;
            QuestCompleted = null;
            GameEventBus.OnGameEvent -= OnGameEvent;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Hook()
        {
            GameEventBus.OnGameEvent += OnGameEvent;
        }

        private static void EnsureCatalog()
        {
            if (_catalog != null) return;
            _catalog = new Dictionary<string, QuestData>();
            foreach (var q in Resources.LoadAll<QuestData>("Quests"))
                if (!_catalog.TryAdd(q.Id, q))
                    Debug.LogError($"[QuestManager] Duplicate questId '{q.Id}'.");
        }

        public static QuestData Get(string questId)
        {
            EnsureCatalog();
            return questId != null && _catalog.TryGetValue(questId, out var q) ? q : null;
        }

        public static IEnumerable<QuestData> Catalog
        {
            get { EnsureCatalog(); return _catalog.Values; }
        }

        public static bool IsAvailable(QuestData quest)
        {
            if (quest == null || _active.ContainsKey(quest.Id) || _completed.Contains(quest.Id)) return false;
            if (quest.prerequisiteQuestIds.Any(p => !_completed.Contains(p))) return false;
            if (quest.requiredEventFlags.Any(f => !_flags.Contains(f))) return false;
            return true;
        }

        // ---------------- accept / progress / turn-in ----------------

        /// <summary>Gameplay-facing accept. Shared quests hop to the server first.</summary>
        public static void Accept(string questId)
        {
            var quest = Get(questId);
            if (quest == null || !IsAvailable(quest)) return;
            if (quest.sharedProgress && NetworkQuestSync.Instance != null)
                NetworkQuestSync.Instance.RequestAccept(questId);
            else
                ApplyAccept(questId);
        }

        /// <summary>Actually activates the quest locally (called by sync for shared quests).</summary>
        public static void ApplyAccept(string questId)
        {
            var quest = Get(questId);
            if (quest == null || _active.ContainsKey(questId) || _completed.Contains(questId)) return;
            _active[questId] = new QuestState
            {
                questId = questId,
                progress = new int[quest.objectives.Length],
            };
            QuestAccepted?.Invoke(quest);
            Changed?.Invoke();
        }

        /// <summary>
        /// Gameplay reports something happened (collected item X, talked to Y...).
        /// Personal quests advance locally; shared quests route via the server.
        /// </summary>
        public static void Report(ObjectiveType type, string targetId, int amount = 1)
        {
            foreach (var state in _active.Values.ToList())
            {
                var quest = Get(state.questId);
                if (quest == null || state.completed) continue;
                for (int i = 0; i < quest.objectives.Length; i++)
                {
                    var obj = quest.objectives[i];
                    if (obj.type != type || obj.targetId != targetId) continue;
                    if (state.progress[i] >= obj.requiredAmount) continue;

                    if (quest.sharedProgress && NetworkQuestSync.Instance != null)
                        NetworkQuestSync.Instance.ReportProgress(state.questId, i, amount);
                    else
                        ApplyProgress(state.questId, i, state.progress[i] + amount);
                }
            }
        }

        /// <summary>Sets absolute progress for one objective (called locally or by sync).</summary>
        public static void ApplyProgress(string questId, int objectiveIndex, int value)
        {
            if (!_active.TryGetValue(questId, out var state)) return;
            var quest = Get(questId);
            if (quest == null || objectiveIndex < 0 || objectiveIndex >= state.progress.Length) return;

            state.progress[objectiveIndex] =
                Mathf.Clamp(value, 0, quest.objectives[objectiveIndex].requiredAmount);

            if (!state.completed && IsObjectivesMet(quest, state))
                Complete(questId);
            else
                Changed?.Invoke();
        }

        public static bool IsObjectivesMet(QuestData quest, QuestState state)
        {
            for (int i = 0; i < quest.objectives.Length; i++)
                if (state.progress[i] < quest.objectives[i].requiredAmount)
                    return false;
            return true;
        }

        private static void Complete(string questId)
        {
            var quest = Get(questId);
            if (quest == null || !_active.TryGetValue(questId, out var state) || state.completed) return;
            state.completed = true;
            _active.Remove(questId);
            _completed.Add(questId);

            // Rewards go to the local player (shared quests reward everyone - co-op).
            var inv = NetworkPlayer.Local != null ? NetworkPlayer.Local.GetComponent<PlayerInventory>() : null;
            if (inv != null)
            {
                if (quest.rewardCash > 0) inv.AddCash(quest.rewardCash);
                foreach (var reward in quest.rewardItems)
                {
                    var item = ItemDatabase.Get(reward.itemId);
                    if (item != null) inv.AddItem(item, reward.count);
                }
            }

            foreach (var evt in quest.onCompleteEvents) GameEventBus.Fire(evt);
            if (!string.IsNullOrEmpty(quest.nextQuestId)) Accept(quest.nextQuestId);

            QuestCompleted?.Invoke(quest);
            Changed?.Invoke();
        }

        private static void OnGameEvent(string eventId)
        {
            _flags.Add(eventId);
            // "item_collected:<itemId>" from pickups feeds Collect objectives.
            if (eventId.StartsWith("item_collected:"))
                Report(ObjectiveType.Collect, eventId.Substring("item_collected:".Length));
        }

        // ---------------- save DTO (Phase 12 wires this to disk) ----------------

        [Serializable]
        public class SaveState
        {
            public QuestState[] active = Array.Empty<QuestState>();
            public string[] completed = Array.Empty<string>();
            public string[] flags = Array.Empty<string>();
        }

        public static SaveState GetState() => new()
        {
            active = _active.Values.ToArray(),
            completed = _completed.ToArray(),
            flags = _flags.ToArray(),
        };

        public static void ApplyState(SaveState state)
        {
            _active.Clear();
            _completed.Clear();
            _flags.Clear();
            if (state != null)
            {
                foreach (var s in state.active) _active[s.questId] = s;
                foreach (var c in state.completed) _completed.Add(c);
                foreach (var f in state.flags) _flags.Add(f);
            }
            Changed?.Invoke();
        }
    }
}
