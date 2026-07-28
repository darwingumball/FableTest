using System;
using UnityEngine;

namespace Game.Core
{
    /// <summary>
    /// Lightweight string-id event bus used by quests ("onCompleteEvents"), interactables,
    /// and world triggers. Server-authoritative systems fire events; QuestManager records
    /// fired ids as flags for prerequisite checks.
    /// </summary>
    public static class GameEventBus
    {
        public static event Action<string> OnGameEvent;

        public static void Fire(string eventId)
        {
            if (string.IsNullOrEmpty(eventId)) return;
            OnGameEvent?.Invoke(eventId);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            OnGameEvent = null;
        }
    }
}
