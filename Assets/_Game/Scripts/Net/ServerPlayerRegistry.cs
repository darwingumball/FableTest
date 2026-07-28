using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Net
{
    /// <summary>
    /// Server-side map of NGO clientId -> stable Unity Auth playerId, built during
    /// connection approval. This is the identity the save system, per-player quest
    /// state, and admin permissions key on - clientIds are transient per session,
    /// auth ids survive reconnects.
    /// Only meaningful on the server; empty on pure clients.
    /// </summary>
    public static class ServerPlayerRegistry
    {
        public struct Entry
        {
            public string AuthPlayerId;
            public string DisplayName;
        }

        private static readonly Dictionary<ulong, Entry> _byClientId = new();

        public static event Action<ulong, Entry> OnPlayerRegistered;
        public static event Action<ulong, Entry> OnPlayerUnregistered;

        public static IReadOnlyDictionary<ulong, Entry> All => _byClientId;

        public static void Register(ulong clientId, string authPlayerId, string displayName)
        {
            var entry = new Entry { AuthPlayerId = authPlayerId, DisplayName = displayName };
            _byClientId[clientId] = entry;
            Debug.Log($"[ServerPlayerRegistry] client {clientId} = auth {authPlayerId} ('{displayName}')");
            OnPlayerRegistered?.Invoke(clientId, entry);
        }

        public static void Unregister(ulong clientId)
        {
            if (_byClientId.Remove(clientId, out var entry))
                OnPlayerUnregistered?.Invoke(clientId, entry);
        }

        public static bool TryGet(ulong clientId, out Entry entry) => _byClientId.TryGetValue(clientId, out entry);

        public static string GetAuthId(ulong clientId) =>
            _byClientId.TryGetValue(clientId, out var e) ? e.AuthPlayerId : null;

        public static bool TryGetClientId(string authPlayerId, out ulong clientId)
        {
            foreach (var kvp in _byClientId)
            {
                if (kvp.Value.AuthPlayerId == authPlayerId)
                {
                    clientId = kvp.Key;
                    return true;
                }
            }
            clientId = 0;
            return false;
        }

        public static void Clear() => _byClientId.Clear();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _byClientId.Clear();
            OnPlayerRegistered = null;
            OnPlayerUnregistered = null;
        }
    }
}
