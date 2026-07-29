using System;
using Game.Admin;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Game.Net
{
    /// <summary>Which audience a chat line reaches.</summary>
    public enum ChatChannel : byte
    {
        /// <summary>Only players close enough to hear you.</summary>
        Local = 0,
        /// <summary>Everyone in the session.</summary>
        Global = 1,
        /// <summary>Server notices, command replies, join/leave. Never sent by players.</summary>
        System = 2,
    }

    /// <summary>
    /// Server-authoritative text chat with a proximity channel and a server-wide channel.
    ///
    /// The server decides who hears a local message, not the sender - a client that only
    /// asked "who is near me" could otherwise be modified to claim everyone is. It also
    /// resolves the display name from <see cref="ServerPlayerRegistry"/> rather than
    /// trusting a name in the payload, so nobody can speak as somebody else.
    ///
    /// Lines beginning with '/' are commands and go to <see cref="AdminService"/>, which
    /// already owns permission checks and server-side execution. Chat deliberately does not
    /// get its own command path: one place to audit is worth more than the convenience.
    /// </summary>
    public class ChatRelay : NetworkBehaviour
    {
        public static ChatRelay Instance { get; private set; }

        [Tooltip("Metres a Local message carries. Matched to the voice channel's audible " +
                 "distance so text and voice have the same reach.")]
        [SerializeField] private float localRange = 28f;
        [Tooltip("Minimum seconds between messages from one client. Server-enforced.")]
        [SerializeField] private float cooldownSeconds = 0.4f;
        [SerializeField] private int maxMessageLength = 240;

        /// <summary>Raised locally whenever a line should appear in the chat window.</summary>
        public static event Action<ChatChannel, string, string> OnMessage;

        private readonly System.Collections.Generic.Dictionary<ulong, float> _lastSend = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
            OnMessage = null;
        }

        public override void OnNetworkSpawn()
        {
            Instance = this;
            if (IsServer) NetworkManager.OnClientDisconnectCallback += ForgetClient;
        }

        public override void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (IsServer && NetworkManager != null)
                NetworkManager.OnClientDisconnectCallback -= ForgetClient;
            base.OnDestroy();
        }

        private void ForgetClient(ulong clientId) => _lastSend.Remove(clientId);

        /// <summary>
        /// Entry point for the chat box. Commands are split off here so a '/' line never
        /// reaches other players even if the command itself is rejected.
        /// </summary>
        public void SubmitLocal(string line, ChatChannel channel)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            line = line.Trim();

            if (line.StartsWith("/"))
            {
                string command = line.Substring(1);
                if (string.IsNullOrWhiteSpace(command)) return;
                if (AdminService.Instance != null) AdminService.Instance.Submit(command);
                else Local(ChatChannel.System, "Commands are unavailable right now.");
                return;
            }

            if (line.Length > maxMessageLength) line = line.Substring(0, maxMessageLength);
            SendChatServerRpc(new FixedString512Bytes(line), channel);
        }

        [ServerRpc(RequireOwnership = false)]
        private void SendChatServerRpc(FixedString512Bytes text, ChatChannel channel,
            ServerRpcParams p = default)
        {
            ulong sender = p.Receive.SenderClientId;

            // Rate limit on the server. A client-side cooldown is a courtesy, not a control.
            if (_lastSend.TryGetValue(sender, out float last) &&
                Time.time - last < cooldownSeconds) return;
            _lastSend[sender] = Time.time;

            // System is a server-only channel; a client asking for it is treated as Global.
            if (channel == ChatChannel.System) channel = ChatChannel.Global;

            string name = ServerPlayerRegistry.TryGet(sender, out var entry) &&
                          !string.IsNullOrWhiteSpace(entry.DisplayName)
                ? entry.DisplayName
                : $"Player {sender}";

            var payload = new FixedString512Bytes(text.ToString());
            var from = new FixedString64Bytes(Truncate(name, 60));

            if (channel == ChatChannel.Global)
            {
                ReceiveChatClientRpc(channel, from, payload);
                return;
            }

            // Proximity: the server works out the audience from actual positions.
            var listeners = ResolveNearby(sender);
            if (listeners.Length == 0) return;
            ReceiveChatClientRpc(channel, from, payload, new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = listeners },
            });
        }

        /// <summary>
        /// Client ids close enough to the speaker to hear them. The speaker is always
        /// included - not seeing your own message reads as the chat being broken.
        /// </summary>
        private ulong[] ResolveNearby(ulong speaker)
        {
            var result = new System.Collections.Generic.List<ulong> { speaker };

            if (!TryGetPlayerPosition(speaker, out Vector3 origin))
                return result.ToArray();

            float rangeSqr = localRange * localRange;
            foreach (var client in NetworkManager.ConnectedClientsIds)
            {
                if (client == speaker) continue;
                if (!TryGetPlayerPosition(client, out Vector3 other)) continue;
                if ((other - origin).sqrMagnitude <= rangeSqr) result.Add(client);
            }
            return result.ToArray();
        }

        private bool TryGetPlayerPosition(ulong clientId, out Vector3 position)
        {
            position = default;
            if (!NetworkManager.ConnectedClients.TryGetValue(clientId, out var client)) return false;
            var obj = client.PlayerObject;
            if (obj == null) return false;
            position = obj.transform.position;
            return true;
        }

        [ClientRpc]
        private void ReceiveChatClientRpc(ChatChannel channel, FixedString64Bytes from,
            FixedString512Bytes text, ClientRpcParams _ = default) =>
            OnMessage?.Invoke(channel, from.ToString(), text.ToString());

        /// <summary>Server-side announcement (joins, admin notices). Server only.</summary>
        public void Announce(string text)
        {
            if (!IsServer) return;
            ReceiveChatClientRpc(ChatChannel.System, new FixedString64Bytes("Server"),
                new FixedString512Bytes(Truncate(text, 500)));
        }

        /// <summary>Local-only line - command replies, errors. Never leaves this machine.</summary>
        public static void Local(ChatChannel channel, string text) =>
            OnMessage?.Invoke(channel, string.Empty, text);

        /// <summary>
        /// Text arriving from Vivox rather than Netcode (see <see cref="VoiceChatService"/>).
        /// Surfaced through the same event so there is one chat log, not two.
        /// </summary>
        public static void ReceiveExternal(string from, string text) =>
            OnMessage?.Invoke(ChatChannel.Global, from, text);

        private static string Truncate(string value, int max) =>
            string.IsNullOrEmpty(value) || value.Length <= max ? value : value.Substring(0, max);
    }
}
