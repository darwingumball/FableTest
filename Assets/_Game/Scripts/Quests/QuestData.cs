using System;
using UnityEngine;

namespace Game.Quests
{
    public enum ObjectiveType : byte { Collect, GoTo, Interact, Talk, TurnIn }

    /// <summary>
    /// One quest definition. Assets live in _Game/Resources/Quests so every peer resolves
    /// the same catalog by questId.
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Quest", fileName = "quest_new")]
    public class QuestData : ScriptableObject
    {
        [Tooltip("Stable id (defaults to asset name). Used in RPCs and saves.")]
        public string questId;
        public string title = "Quest";
        [TextArea] public string description = "";

        [Tooltip("Shared = one progress state for the whole session, server-owned. " +
                 "Otherwise each player runs their own copy.")]
        public bool sharedProgress;

        [Serializable]
        public struct Objective
        {
            public string description;
            public ObjectiveType type;
            [Tooltip("Item id for Collect, trigger/NPC id for GoTo/Interact/Talk.")]
            public string targetId;
            [Min(1)] public int requiredAmount;
        }

        public Objective[] objectives = Array.Empty<Objective>();

        [Header("Availability")]
        public string[] prerequisiteQuestIds = Array.Empty<string>();
        public string[] requiredEventFlags = Array.Empty<string>();
        [Tooltip("Auto-available from the start of a session.")]
        public bool availableFromStart;

        [Header("Completion")]
        public int rewardCash;
        [Serializable] public struct ItemReward { public string itemId; public int count; }
        public ItemReward[] rewardItems = Array.Empty<ItemReward>();
        public string nextQuestId = "";
        public string[] onCompleteEvents = Array.Empty<string>();

        public string Id => string.IsNullOrEmpty(questId) ? name : questId;

        private void OnValidate()
        {
            if (string.IsNullOrEmpty(questId)) questId = name;
        }
    }
}
