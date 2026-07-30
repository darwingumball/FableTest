using System;
using System.Collections.Generic;
using System.Globalization;
using Game.Inventory;
using Game.Net;
using Game.Quests;
using Game.World;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Game.Admin
{
    /// <summary>
    /// Cheat/admin commands. Every command executes on the SERVER after a permission
    /// check, so effects (weather, time, spawns) are authoritative and replicate normally.
    /// The host is always an admin; others must be granted.
    ///
    /// Scene object in World. Clients send raw command lines via ServerRpc and get a
    /// targeted reply back for their console.
    /// </summary>
    public class AdminService : NetworkBehaviour
    {
        public static AdminService Instance { get; private set; }

        private readonly NetworkList<ulong> _admins = new();

        /// <summary>Raised on the local machine when a reply arrives (console prints it).</summary>
        public event Action<string> OnReply;

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
            if (IsServer && !_admins.Contains(NetworkManager.ServerClientId))
                _admins.Add(NetworkManager.ServerClientId);
        }

        public bool LocalIsAdmin =>
            IsSpawned && _admins.Contains(NetworkManager.Singleton.LocalClientId);

        /// <summary>Console entry point.</summary>
        public void Submit(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            // perf is the one command that must NOT go to the server. Frame time is a
            // property of the machine looking at the scene, and its toggles affect only the
            // local renderer - running it on the host would measure the wrong computer and
            // change everyone's picture to answer one person's question.
            var args = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (args.Length > 0 && args[0].ToLowerInvariant() == "perf")
            {
                OnReply?.Invoke(PerfProbe.Execute(args));
                return;
            }

            if (IsServer) Execute(line, NetworkManager.ServerClientId);
            else SubmitServerRpc(new FixedString512Bytes(line));
        }

        [ServerRpc(RequireOwnership = false)]
        private void SubmitServerRpc(FixedString512Bytes line, ServerRpcParams p = default)
        {
            Execute(line.ToString(), p.Receive.SenderClientId);
        }

        private void Reply(ulong clientId, string message)
        {
            if (clientId == NetworkManager.ServerClientId) OnReply?.Invoke(message);
            else ReplyClientRpc(new FixedString512Bytes(message), new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
            });
        }

        [ClientRpc]
        private void ReplyClientRpc(FixedString512Bytes message, ClientRpcParams _) =>
            OnReply?.Invoke(message.ToString());

        // ---------------- execution ----------------

        private void Execute(string line, ulong sender)
        {
            var args = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (args.Length == 0) return;
            string cmd = args[0].ToLowerInvariant();

            if (cmd is "help" or "?")
            {
                Reply(sender, CommandHelp);
                return;
            }

            if (!_admins.Contains(sender))
            {
                Reply(sender, "Permission denied. Ask the host for admin.");
                return;
            }

            try
            {
                Reply(sender, Run(cmd, args, sender));
            }
            catch (Exception ex)
            {
                Reply(sender, $"Error: {ex.Message}");
            }
        }

        private string Run(string cmd, string[] args, ulong sender)
        {
            switch (cmd)
            {
                case "weather":
                {
                    if (args.Length < 2) return "usage: weather <clear|overcast|fog|rain|storm|snow> [seconds] [intensity]";
                    if (!Enum.TryParse<WeatherType>(args[1], true, out var type))
                        return $"Unknown weather '{args[1]}'.";
                    float seconds = args.Length > 2 ? ParseFloat(args[2], 10f) : 10f;
                    float intensity = args.Length > 3 ? ParseFloat(args[3], 1f) : 1f;
                    if (WeatherManager.Instance == null) return "Weather system missing.";
                    WeatherManager.Instance.ServerSetWeather(type, seconds, intensity);
                    return $"Weather -> {type} over {seconds:0.#}s (intensity {intensity:0.##}).";
                }

                case "time":
                {
                    if (args.Length < 2) return "usage: time <hour 0-24> [dayLengthMinutes]";
                    if (NetworkTimeSync.Instance == null) return "Time system missing.";
                    float hour = ParseFloat(args[1], 12f);
                    float dayLength = args.Length > 2 ? ParseFloat(args[2], -1f) : -1f;
                    double baseDay = Math.Floor(NetworkTimeSync.Instance.WorldHours / 24.0) * 24.0;
                    NetworkTimeSync.Instance.ServerSetTime(baseDay + hour, dayLength);
                    return $"Time set to {hour:00.##}:00.";
                }

                case "give":
                {
                    if (args.Length < 2) return "usage: give <itemId> [count]";
                    int count = args.Length > 2 ? ParseInt(args[2], 1) : 1;
                    if (ItemDatabase.Get(args[1]) == null) return $"Unknown item '{args[1]}'.";
                    if (WorldItemManager.Instance == null) return "World item manager missing.";
                    if (!TryGetPlayer(sender, out var player)) return "No player object.";
                    var drop = player.transform.position + player.transform.forward * 1.5f + Vector3.up;
                    WorldItemManager.Instance.ServerSpawnItem(args[1], count, drop, Vector3.zero);
                    return $"Spawned {args[1]} x{count} in front of you.";
                }

                case "tp":
                {
                    if (args.Length < 4) return "usage: tp <x> <y> <z>";
                    if (!TryGetPlayer(sender, out var player)) return "No player object.";
                    var pos = new Vector3(ParseFloat(args[1], 0f), ParseFloat(args[2], 0f), ParseFloat(args[3], 0f));
                    TeleportClientRpc(pos, new ClientRpcParams
                    {
                        Send = new ClientRpcSendParams { TargetClientIds = new[] { sender } }
                    });
                    return $"Teleporting to {pos}.";
                }

                case "heal":
                {
                    if (!TryGetPlayer(sender, out var player)) return "No player object.";
                    var stats = player.GetComponent<Player.PlayerStats>();
                    if (stats == null) return "No stats component.";
                    stats.ServerHeal(Player.PlayerStats.MAX_HEALTH);
                    return "Healed to full.";
                }

                case "quest":
                {
                    if (args.Length < 3) return "usage: quest <accept|complete> <questId>";
                    var quest = QuestManager.Get(args[2]);
                    if (quest == null) return $"Unknown quest '{args[2]}'.";
                    QuestDebugClientRpc(new FixedString64Bytes(args[2]), args[1] == "complete");
                    return $"Quest '{args[2]}' {args[1]}.";
                }

                case "admin":
                {
                    if (!IsServer || sender != NetworkManager.ServerClientId)
                        return "Only the host can change admins.";
                    if (args.Length < 3) return "usage: admin <grant|revoke> <clientId>";
                    if (!ulong.TryParse(args[2], out var target)) return "clientId must be a number.";
                    if (args[1] == "grant")
                    {
                        if (!_admins.Contains(target)) _admins.Add(target);
                        Reply(target, "You have been granted admin.");
                        return $"Granted admin to client {target}.";
                    }
                    _admins.Remove(target);
                    return $"Revoked admin from client {target}.";
                }

                case "snow":
                {
                    if (args.Length < 2) return "usage: snow <0-1>   (0 = bare ground, 1 = full depth)";
                    if (WeatherManager.Instance == null) return "Weather system missing.";
                    float coverage = Mathf.Clamp01(ParseFloat(args[1], 0f));
                    WeatherManager.Instance.ServerSetSnowCoverage(coverage);
                    return $"Snow coverage -> {coverage:0.##} " +
                           $"({WeatherManager.Instance.SnowDepthMeters:0.##}m deep). " +
                           "Melts normally from here unless it is snowing.";
                }

                case "wet":
                {
                    if (args.Length < 2) return "usage: wet <0-1|auto>";
                    float value = args[1].Equals("auto", StringComparison.OrdinalIgnoreCase)
                        ? -1f : Mathf.Clamp01(ParseFloat(args[1], 0f));
                    DebugVisualClientRpc(VisualCmd.Wetness, value);
                    return value < 0f
                        ? "Surface wetness follows the weather again."
                        : $"Surface wetness pinned to {value:0.##}.";
                }

                case "exposure":
                {
                    if (args.Length < 2) return "usage: exposure <evBias>   negative = brighter, 0 = default";
                    float bias = ParseFloat(args[1], 0f);
                    DebugVisualClientRpc(VisualCmd.ExposureBias, bias);
                    return $"Exposure bias -> {bias:0.##} EV ({(bias < 0f ? "brighter" : bias > 0f ? "darker" : "default")}).";
                }

                case "dither":
                {
                    if (args.Length < 2) return "usage: dither <0-2>   0 = off (default)";
                    float amount = Mathf.Clamp(ParseFloat(args[1], 0f), 0f, 2f);
                    DebugVisualClientRpc(VisualCmd.Dither, amount);
                    return $"PSX dither -> {amount:0.##}.";
                }

                case "res":
                {
                    if (args.Length < 2) return "usage: res <height|native>   e.g. 240, 360, 480, native";
                    int height = args[1].Equals("native", StringComparison.OrdinalIgnoreCase)
                        ? 0 : ParseInt(args[1], 360);
                    DebugVisualClientRpc(VisualCmd.InternalRes, height);
                    return height <= 0 ? "PSX internal resolution -> native."
                                       : $"PSX internal resolution -> {height}p.";
                }

                case "light":
                {
                    if (args.Length < 2) return "usage: light <group|all> <multiplier>  |  light list";
                    if (args[1].Equals("list", StringComparison.OrdinalIgnoreCase))
                        return World.PracticalLight.DescribeGroups();
                    if (args.Length < 3) return "usage: light <group|all> <multiplier>";
                    float scale = Mathf.Clamp(ParseFloat(args[2], 1f), 0f, 20f);
                    LightScaleClientRpc(new FixedString32Bytes(args[1]), scale);
                    return $"Light group '{args[1]}' -> x{scale:0.##} (all clients).";
                }

                case "goto":
                {
                    if (args.Length < 2)
                        return "usage: goto <storefront|warehouse|street|shore|sea|" +
                               "tug|crabboat|spawn>";
                    Vector3 target;
                    switch (args[1].ToLowerInvariant())
                    {
                        case "storefront": target = new Vector3(-26f, 1f, 28f); break;
                        case "warehouse": target = new Vector3(26f, 1f, -10f); break;
                        case "street": target = new Vector3(0f, 1f, -35f); break;
                        // Fixed vantage points for repeatable perf comparisons: standing on
                        // the beach, and well out on the water looking back at the land.
                        case "shore": target = new Vector3(0f, 1f, 60f); break;
                        case "sea": target = new Vector3(0f, -2f, 250f); break;
                        // Straight onto the working deck of each boat, aft of anything you
                        // could land on top of. These are fixed world points computed from the
                        // moorings in WaterBuilder and CrabBoatBuilder: drive a boat away and
                        // its deck goes with it, which is what the boarding ladders are for.
                        // Y is water level (-3.2) plus freeboard plus deck height plus a step.
                        case "tug": target = new Vector3(-12.8f, -0.7f, 75.1f); break;
                        case "crabboat": target = new Vector3(12.9f, -0.6f, 72.2f); break;
                        case "spawn": target = new Vector3(0f, 1f, 0f); break;
                        default: return $"Unknown location '{args[1]}'.";
                    }
                    TeleportClientRpc(target, new ClientRpcParams
                    {
                        Send = new ClientRpcSendParams { TargetClientIds = new[] { sender } }
                    });
                    return $"Teleporting to {args[1]}.";
                }

                case "speed":
                {
                    if (args.Length < 2) return "usage: speed <multiplier>   1 = normal";
                    float mult = Mathf.Clamp(ParseFloat(args[1], 1f), 0.1f, 2f);
                    SpeedClientRpc(mult, new ClientRpcParams
                    {
                        Send = new ClientRpcSendParams { TargetClientIds = new[] { sender } }
                    });
                    return $"Move speed x{mult:0.##}.";
                }

                case "players":
                {
                    var sb = new System.Text.StringBuilder("Connected:");
                    foreach (var kvp in ServerPlayerRegistry.All)
                        sb.Append($"\n  {kvp.Key}: {kvp.Value.DisplayName} ({kvp.Value.AuthPlayerId})"
                                  + (_admins.Contains(kvp.Key) ? " [admin]" : ""));
                    return sb.ToString();
                }

                default:
                    return $"Unknown command '{cmd}'. Type help.";
            }
        }

        /// <summary>
        /// Presentation-only debug knobs. These are local rendering state, not simulation,
        /// but they broadcast so a host tuning the look sees the same frame everyone else
        /// does - otherwise co-op screenshots and bug reports disagree.
        /// </summary>
        private enum VisualCmd { Wetness, ExposureBias, Dither, InternalRes }

        [ClientRpc]
        private void DebugVisualClientRpc(VisualCmd which, float value)
        {
            switch (which)
            {
                case VisualCmd.Wetness:
                    World.SurfaceWetness.DebugOverride = value;
                    break;
                case VisualCmd.ExposureBias:
                    WeatherManager.ExposureBias = value;
                    break;
                case VisualCmd.Dither:
                    Core.SettingsService.Data.ditherStrength = value;
                    Core.SettingsService.Save();
                    break;
                case VisualCmd.InternalRes:
                    Core.SettingsService.Data.psxInternalHeight = Mathf.RoundToInt(value);
                    Core.SettingsService.Save();
                    break;
            }
        }

        [ClientRpc]
        private void LightScaleClientRpc(FixedString32Bytes group, float scale) =>
            World.PracticalLight.SetGroupScale(group.ToString(), scale);

        [ClientRpc]
        private void SpeedClientRpc(float multiplier, ClientRpcParams _)
        {
            var player = NetworkPlayer.Local;
            if (player != null && player.Controller != null)
                player.Controller.SetSpeedMultiplier(multiplier);
        }

        [ClientRpc]
        private void TeleportClientRpc(Vector3 position, ClientRpcParams _)
        {
            var local = NetworkPlayer.Local;
            if (local == null) return;
            var cc = local.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            local.transform.position = position;
            if (cc != null) cc.enabled = true;
        }

        [ClientRpc]
        private void QuestDebugClientRpc(FixedString64Bytes questId, bool complete)
        {
            string id = questId.ToString();
            QuestManager.ApplyAccept(id);
            if (!complete) return;
            var quest = QuestManager.Get(id);
            if (quest == null) return;
            for (int i = 0; i < quest.objectives.Length; i++)
                QuestManager.ApplyProgress(id, i, quest.objectives[i].requiredAmount);
        }

        private static bool TryGetPlayer(ulong clientId, out GameObject player)
        {
            player = null;
            if (!NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var client)
                || client.PlayerObject == null) return false;
            player = client.PlayerObject.gameObject;
            return true;
        }

        private static float ParseFloat(string s, float fallback) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        private static int ParseInt(string s, int fallback) =>
            int.TryParse(s, out var v) ? v : fallback;

        private const string CommandHelp =
            "World:\n" +
            "  weather <type> [seconds] [intensity]   clear|overcast|fog|rain|storm|snow\n" +
            "  time <hour> [dayLengthMinutes]\n" +
            "  snow <0-1>                             jump snow depth (skips accumulation)\n" +
            "Look (broadcast to everyone):\n" +
            "  wet <0-1|auto>                         pin surface wetness for reflections\n" +
            "  exposure <evBias>                      negative = brighter, 0 = default\n" +
            "  dither <0-2>                           PSX dither, 0 = off (default)\n" +
            "  res <height|native>                    PSX internal res: 240/360/480/native\n" +
            "  light <group|all> <multiplier>         scale a light group live\n" +
            "  light list                             groups, counts, current scale\n" +
            "Performance (local only - measures YOUR machine, changes only YOUR picture):\n" +
            "  perf                                   frame ms, draws, tris, live lights\n" +
            "  perf <lights|vlights|shadows|fog|clouds|post|sky|water|snow> on|off\n" +
            "  perf reset                             put everything back\n" +
            "Player:\n" +
            "  goto <storefront|warehouse|street|shore|sea|tug|crabboat|spawn>\n" +
            "  tp <x> <y> <z>\n" +
            "  speed <multiplier>                     1 = normal\n" +
            "  heal\n" +
            "  give <itemId> [count]                  crate_small|ration_can|wrench_large|\n" +
            "                                         fuel_barrel|jerry_can|crab_trap|\n" +
            "                                         couch|chair\n" +
            "Session:\n" +
            "  quest <accept|complete> <questId>\n" +
            "  players\n" +
            "  admin <grant|revoke> <clientId>        (host only)";
    }
}
