using System;
using System.Text;
using System.Threading.Tasks;
using Game.Core;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Lobbies.Models;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Net
{
    /// <summary>
    /// The single entry point for starting/ending play sessions. Callers never deal with
    /// Lobby, Relay, or Netcode ordering directly:
    ///
    ///   StartSessionAsync(config)  - host path. Solo visibility = loopback host with no
    ///                                lobby/relay; Private/Public = Relay + Lobby + host.
    ///                                One code path for singleplayer and multiplayer.
    ///   JoinByLobbyAsync/JoinByCodeAsync - client paths.
    ///   LeaveAsync                 - teardown from any state.
    ///
    /// Solo still runs NGO as a host so gameplay code can always rely on IsServer/IsClient
    /// without a "singleplayer mode" branch anywhere.
    ///
    /// Also owns connection approval: clients send {protocolVersion, authPlayerId, name}
    /// and the server fills <see cref="ServerPlayerRegistry"/> - the identity map that
    /// saves, quests, and admin permissions key on.
    /// </summary>
    [DefaultExecutionOrder(-500)]
    public class NetworkSessionManager : MonoBehaviour
    {
        public static NetworkSessionManager Instance { get; private set; }

        /// <summary>Bump when the connection payload or replication protocol changes incompatibly.</summary>
        public const int PROTOCOL_VERSION = 1;

        [Header("Networking")]
        [Tooltip("NetworkManager prefab with UnityTransport + player prefab. Created by Game/Setup.")]
        public GameObject networkManagerPrefab;

        [Header("Scenes")]
        [Tooltip("Scene loaded when a hosted session starts. Must be in Build Settings.")]
        public string gameplaySceneName = "World";

        public enum SessionMode { None, Solo, Host, Client }
        public SessionMode CurrentMode { get; private set; } = SessionMode.None;

        public LobbyController Lobby { get; private set; }
        public bool IsBusy { get; private set; }

        /// <summary>
        /// A string every peer in this session agrees on, and no other session shares.
        ///
        /// Used to name the voice channels (see <see cref="VoiceChatService"/>). The lobby
        /// id is the natural answer because every peer joined through it. Solo has no
        /// lobby, so it falls back to the auth player id - which is fine precisely because
        /// nobody else can be in a solo session to disagree with.
        /// Null before a session exists.
        /// </summary>
        public string SessionKey
        {
            get
            {
                string lobbyId = Lobby?.CurrentLobby?.Id;
                if (!string.IsNullOrEmpty(lobbyId)) return lobbyId;
                if (CurrentMode == SessionMode.None) return null;
                return "solo-" + (Unity.Services.Authentication.AuthenticationService.Instance
                    ?.PlayerId ?? "local");
            }
        }

        public event Action<SessionMode> OnSessionStarted;
        public event Action OnSessionEnded;

        [Serializable]
        private class ConnectionPayload
        {
            public int protocolVersion;
            public string authPlayerId;
            public string displayName;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            Lobby = GetComponent<LobbyController>();
            if (Lobby == null)
                Lobby = gameObject.AddComponent<LobbyController>();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// Best-effort cleanup on app close; can't await during teardown. Stale lobby
        /// memberships that slip through are cleaned by LeaveAllJoinedAsync on next start.
        /// </summary>
        private void OnApplicationQuit()
        {
            try
            {
                if (Lobby != null && Lobby.CurrentLobby != null)
                    _ = Lobby.LeaveAsync();
                if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
                    NetworkManager.Singleton.Shutdown();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NetworkSessionManager] Quit cleanup failed: {ex.Message}");
            }
        }

        // ---------------- Host paths ----------------

        /// <summary>
        /// Starts a session from a GameConfig - THE new-game/continue path for both solo
        /// and multiplayer. The config must already be in SessionContext (menu does this).
        /// </summary>
        public async Task<bool> StartSessionAsync(GameConfig config)
        {
            if (IsBusy || config == null) return false;
            IsBusy = true;
            try
            {
                SessionContext.Config = config;
                EnsureNetworkManager();
                PrepareServerSide();

                if (config.visibility == SessionVisibility.Solo)
                {
                    // Loopback, port 0 (OS-assigned) so we never collide with a stale
                    // socket or another local session. SetConnectionData also switches
                    // the transport out of relay mode if a coop host was attempted first.
                    if (NetworkManager.Singleton.NetworkConfig.NetworkTransport is UnityTransport utp)
                        utp.SetConnectionData("127.0.0.1", 0, "127.0.0.1");
                }
                else
                {
                    await NetworkBootstrap.EnsureInitializedAsync();
                    await Lobby.LeaveAllJoinedAsync();
                    string joinCode = await RelayConnector.HostAllocateAsync(config.maxPlayers);
                    bool isPrivate = config.visibility == SessionVisibility.Private;
                    await Lobby.CreateAsync(config.sessionName, config.maxPlayers, joinCode, isPrivate);
                }

                SetLocalConnectionPayload();
                if (!NetworkManager.Singleton.StartHost())
                {
                    Debug.LogError("[NetworkSessionManager] StartHost failed. See earlier NGO/Transport errors.");
                    await SafeLeave();
                    return false;
                }

                CurrentMode = config.visibility == SessionVisibility.Solo ? SessionMode.Solo : SessionMode.Host;
                OnSessionStarted?.Invoke(CurrentMode);
                LoadGameplayScene();
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NetworkSessionManager] StartSessionAsync failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                await SafeLeave();
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ---------------- Client paths ----------------

        /// <summary>Joins a lobby picked from the browser list.</summary>
        public Task<bool> JoinByLobbyAsync(Lobby lobby) =>
            JoinInternalAsync(lobby != null ? () => Lobby.JoinByIdAsync(lobby.Id) : null);

        /// <summary>Joins via a 6-char lobby code shared out of band (works for private lobbies).</summary>
        public Task<bool> JoinByCodeAsync(string lobbyCode) =>
            JoinInternalAsync(!string.IsNullOrWhiteSpace(lobbyCode)
                ? () => Lobby.JoinByCodeAsync(lobbyCode.Trim())
                : null);

        private async Task<bool> JoinInternalAsync(Func<Task<Lobby>> joinLobby)
        {
            if (IsBusy || joinLobby == null) return false;
            IsBusy = true;
            try
            {
                await NetworkBootstrap.EnsureInitializedAsync();
                EnsureNetworkManager();
                await Lobby.LeaveAllJoinedAsync();

                var joined = await joinLobby();
                string relayCode = joined.Data != null && joined.Data.TryGetValue(LobbyController.JOIN_CODE_KEY, out var obj)
                    ? obj.Value : null;
                await RelayConnector.ClientJoinAsync(relayCode);

                SetLocalConnectionPayload();
                if (!NetworkManager.Singleton.StartClient())
                {
                    Debug.LogError("[NetworkSessionManager] StartClient failed.");
                    await SafeLeave();
                    return false;
                }

                CurrentMode = SessionMode.Client;
                OnSessionStarted?.Invoke(CurrentMode);
                // No scene load here: the host's NetworkSceneManager pushes the gameplay
                // scene to us once connected.
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NetworkSessionManager] Join failed: {ex.Message}\n{ex.StackTrace}");
                await SafeLeave();
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ---------------- Teardown ----------------

        /// <summary>Tears down the session and returns to MainMenu. Safe in any state.</summary>
        public async Task LeaveAsync()
        {
            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.ConnectionApprovalCallback = null;
                NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
                if (NetworkManager.Singleton.IsListening)
                    NetworkManager.Singleton.Shutdown();
            }
            ServerPlayerRegistry.Clear();
            if (Lobby != null)
                await Lobby.LeaveAsync();

            CurrentMode = SessionMode.None;
            OnSessionEnded?.Invoke();

            if (SceneManager.GetActiveScene().name != "MainMenu")
                SceneManager.LoadScene("MainMenu");
        }

        private async Task SafeLeave()
        {
            try { await LeaveAsync(); }
            catch (Exception ex) { Debug.LogWarning($"[NetworkSessionManager] SafeLeave error: {ex.Message}"); }
        }

        // ---------------- Internals ----------------

        private void EnsureNetworkManager()
        {
            if (NetworkManager.Singleton != null) return;
            if (networkManagerPrefab == null)
                throw new InvalidOperationException("networkManagerPrefab not assigned. Run Game/Setup/Full Project Setup.");
            var go = Instantiate(networkManagerPrefab);
            go.name = "NetworkManager";
            DontDestroyOnLoad(go);
        }

        /// <summary>Hooks approval + disconnect callbacks. Host-only concepts; called before StartHost.</summary>
        private void PrepareServerSide()
        {
            ServerPlayerRegistry.Clear();
            var nm = NetworkManager.Singleton;
            nm.NetworkConfig.ConnectionApproval = true;
            nm.ConnectionApprovalCallback = OnConnectionApproval;
            nm.OnClientDisconnectCallback -= OnClientDisconnected;
            nm.OnClientDisconnectCallback += OnClientDisconnected;
        }

        /// <summary>
        /// The payload every connection (including the host's own local client) sends.
        /// Auth id may be empty in pure-solo offline play - the registry then falls back
        /// to a local identity so saves still work without UGS.
        /// </summary>
        private void SetLocalConnectionPayload()
        {
            string authId = null;
            try
            {
                if (AuthenticationService.Instance != null && AuthenticationService.Instance.IsSignedIn)
                    authId = AuthenticationService.Instance.PlayerId;
            }
            catch { /* Services not initialized (offline solo) - fine. */ }

            var payload = new ConnectionPayload
            {
                protocolVersion = PROTOCOL_VERSION,
                authPlayerId = string.IsNullOrEmpty(authId) ? $"local-{SystemInfo.deviceUniqueIdentifier}" : authId,
                displayName = Environment.UserName
            };
            NetworkManager.Singleton.NetworkConfig.ConnectionData =
                Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload));
        }

        private void OnConnectionApproval(
            NetworkManager.ConnectionApprovalRequest request,
            NetworkManager.ConnectionApprovalResponse response)
        {
            ConnectionPayload payload = null;
            try
            {
                if (request.Payload != null && request.Payload.Length > 0)
                    payload = JsonUtility.FromJson<ConnectionPayload>(Encoding.UTF8.GetString(request.Payload));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NetworkSessionManager] Bad connection payload from client {request.ClientNetworkId}: {ex.Message}");
            }

            if (payload == null || payload.protocolVersion != PROTOCOL_VERSION)
            {
                response.Approved = false;
                response.Reason = $"Version mismatch (host {PROTOCOL_VERSION}, client {payload?.protocolVersion.ToString() ?? "?"}).";
                return;
            }

            ServerPlayerRegistry.Register(request.ClientNetworkId, payload.authPlayerId, payload.displayName);
            response.Approved = true;
            response.CreatePlayerObject = true;
        }

        private void OnClientDisconnected(ulong clientId)
        {
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
                ServerPlayerRegistry.Unregister(clientId);
        }

        private void LoadGameplayScene()
        {
            if (string.IsNullOrEmpty(gameplaySceneName)) return;
            // Host always drives scene changes through NGO so clients follow.
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer
                && NetworkManager.Singleton.SceneManager != null)
            {
                NetworkManager.Singleton.SceneManager.LoadScene(gameplaySceneName, LoadSceneMode.Single);
            }
            else
            {
                SceneManager.LoadScene(gameplaySceneName);
            }
        }
    }
}
