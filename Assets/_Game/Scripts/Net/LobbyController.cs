using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;

namespace Game.Net
{
    /// <summary>
    /// Thin wrapper over the UGS Lobby service: create/query/join/leave plus the 15s host
    /// heartbeat (lobbies go inactive after 30s without one). Knows nothing about Relay
    /// or Netcode - that is <see cref="NetworkSessionManager"/>'s job.
    /// </summary>
    public class LobbyController : MonoBehaviour
    {
        /// <summary>Lobby data key holding the Relay join code for clients.</summary>
        public const string JOIN_CODE_KEY = "JoinCode";

        private const float HEARTBEAT_INTERVAL = 15f;

        public Lobby CurrentLobby { get; private set; }
        public string HostPlayerId => CurrentLobby?.HostId;
        public bool IsHost => CurrentLobby != null
            && HostPlayerId == AuthenticationService.Instance?.PlayerId;

        public event Action<Lobby> OnLobbyJoined;
        public event Action OnLobbyLeft;

        private float _heartbeatTimer;

        private void Update()
        {
            if (CurrentLobby == null || !IsHost) return;
            _heartbeatTimer -= Time.deltaTime;
            if (_heartbeatTimer <= 0f)
            {
                _heartbeatTimer = HEARTBEAT_INTERVAL;
                _ = SendHeartbeatSafe(CurrentLobby.Id);
            }
        }

        private static async Task SendHeartbeatSafe(string lobbyId)
        {
            try { await LobbyService.Instance.SendHeartbeatPingAsync(lobbyId); }
            catch (Exception ex) { Debug.LogWarning($"[LobbyController] Heartbeat failed: {ex.Message}"); }
        }

        /// <summary>
        /// Creates a lobby carrying the Relay join code. Private lobbies are joinable only
        /// by lobby code (the "Friends" visibility); public ones appear in QueryAsync.
        /// </summary>
        public async Task<Lobby> CreateAsync(string lobbyName, int maxPlayers, string relayJoinCode, bool isPrivate)
        {
            var options = new CreateLobbyOptions
            {
                IsPrivate = isPrivate,
                Data = new Dictionary<string, DataObject>
                {
                    { JOIN_CODE_KEY, new DataObject(DataObject.VisibilityOptions.Member, relayJoinCode) }
                }
            };
            var lobby = await LobbyService.Instance.CreateLobbyAsync(lobbyName, maxPlayers, options);
            CurrentLobby = lobby;
            _heartbeatTimer = HEARTBEAT_INTERVAL;
            OnLobbyJoined?.Invoke(lobby);
            return lobby;
        }

        public async Task<Lobby> JoinByIdAsync(string lobbyId)
        {
            var lobby = await LobbyService.Instance.JoinLobbyByIdAsync(lobbyId);
            CurrentLobby = lobby;
            OnLobbyJoined?.Invoke(lobby);
            return lobby;
        }

        public async Task<Lobby> JoinByCodeAsync(string lobbyCode)
        {
            var lobby = await LobbyService.Instance.JoinLobbyByCodeAsync(lobbyCode);
            CurrentLobby = lobby;
            OnLobbyJoined?.Invoke(lobby);
            return lobby;
        }

        /// <summary>Public lobbies with open slots. UI formats the response.</summary>
        public async Task<QueryResponse> QueryAsync(int count = 25)
        {
            var options = new QueryLobbiesOptions
            {
                Count = count,
                Filters = new List<QueryFilter>
                {
                    new QueryFilter(QueryFilter.FieldOptions.AvailableSlots, "0", QueryFilter.OpOptions.GT)
                }
            };
            return await LobbyService.Instance.QueryLobbiesAsync(options);
        }

        public string GetRelayJoinCode()
        {
            if (CurrentLobby?.Data == null) return null;
            return CurrentLobby.Data.TryGetValue(JOIN_CODE_KEY, out var obj) ? obj.Value : null;
        }

        /// <summary>Leaves (or deletes, if host) the current lobby. Best-effort.</summary>
        public async Task LeaveAsync()
        {
            if (CurrentLobby == null) return;
            var lobbyId = CurrentLobby.Id;
            var playerId = AuthenticationService.Instance?.PlayerId;
            var wasHost = IsHost;
            CurrentLobby = null;

            try
            {
                if (wasHost)
                    await LobbyService.Instance.DeleteLobbyAsync(lobbyId);
                else if (!string.IsNullOrEmpty(playerId))
                    await LobbyService.Instance.RemovePlayerAsync(lobbyId, playerId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LobbyController] Leave/Delete failed (ignored): {ex.Message}");
            }

            OnLobbyLeft?.Invoke();
        }

        /// <summary>
        /// Removes this player from any lobbies left over from a crash/hard-quit so UGS
        /// doesn't reject the next Create/Join with "already a member". Call before every
        /// create or join attempt.
        /// </summary>
        public async Task<int> LeaveAllJoinedAsync()
        {
            var playerId = AuthenticationService.Instance?.PlayerId;
            if (string.IsNullOrEmpty(playerId)) return 0;

            int cleaned = 0;
            try
            {
                var joined = await LobbyService.Instance.GetJoinedLobbiesAsync();
                if (joined == null) return 0;
                foreach (var lobbyId in joined)
                {
                    try
                    {
                        Lobby stale = null;
                        try { stale = await LobbyService.Instance.GetLobbyAsync(lobbyId); }
                        catch { /* 404 = already gone */ }

                        if (stale != null && stale.HostId == playerId)
                            await LobbyService.Instance.DeleteLobbyAsync(lobbyId);
                        else
                            await LobbyService.Instance.RemovePlayerAsync(lobbyId, playerId);
                        cleaned++;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[LobbyController] Could not clean stale lobby {lobbyId}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LobbyController] GetJoinedLobbies failed: {ex.Message}");
            }
            if (cleaned > 0)
                Debug.Log($"[LobbyController] Cleaned {cleaned} stale lobby membership(s).");
            return cleaned;
        }
    }
}
