using System;
using System.IO;
using Game.Core;
using UnityEngine;

namespace Game.Save
{
    /// <summary>Slot metadata shown on the SaveSlots screen and stamped by autosaves.</summary>
    [Serializable]
    public class SaveSlotMeta
    {
        public int version = 1;
        public string sessionName = "";
        public int difficulty;
        public int maxPlayers = 4;
        public float dayLengthMinutes = 30f;
        public bool friendlyFire;
        public string createdUtc = "";
        public string lastPlayedUtc = "";
        public double playtimeSeconds;

        public GameConfig ToConfig(SessionVisibility visibility) => new GameConfig
        {
            sessionName = sessionName,
            difficulty = (GameDifficulty)difficulty,
            visibility = visibility,
            maxPlayers = maxPlayers,
            dayLengthMinutes = dayLengthMinutes,
            friendlyFire = friendlyFire,
        };

        public static SaveSlotMeta FromConfig(GameConfig config) => new SaveSlotMeta
        {
            sessionName = config.sessionName,
            difficulty = (int)config.difficulty,
            maxPlayers = config.maxPlayers,
            dayLengthMinutes = config.dayLengthMinutes,
            friendlyFire = config.friendlyFire,
            createdUtc = DateTime.UtcNow.ToString("o"),
            lastPlayedUtc = DateTime.UtcNow.ToString("o"),
        };
    }

    /// <summary>
    /// Slot-based save storage under persistentDataPath/Saves/Slot{N}/. Phase 12 adds
    /// world/quests/players payloads; for now only meta.json exists so the menu can
    /// create/list/delete games. Only the HOST ever touches this directory in a session.
    /// </summary>
    public static class SaveSystem
    {
        public const int SLOT_COUNT = 5;

        public static string SlotDir(int slot) =>
            Path.Combine(Application.persistentDataPath, "Saves", $"Slot{slot}");

        private static string MetaPath(int slot) => Path.Combine(SlotDir(slot), "meta.json");
        private static string WorldPath(int slot) => Path.Combine(SlotDir(slot), "world.json");
        private static string QuestsPath(int slot) => Path.Combine(SlotDir(slot), "quests.json");
        private static string PlayerPath(int slot, string authId) =>
            Path.Combine(SlotDir(slot), "players", SanitizeId(authId) + ".json");

        public static bool SlotExists(int slot) => File.Exists(MetaPath(slot));

        // ---------------- payload DTOs ----------------

        [Serializable]
        public class WorldSave
        {
            public int version = 1;
            public double worldHours = 8.0;
            public int weatherType;
            public float weatherIntensity = 1f;
            public string[] eventFlags = Array.Empty<string>();
        }

        [Serializable]
        public class QuestsSave
        {
            public int version = 1;
            /// <summary>Shared quest ids, parallel with <see cref="sharedStrides"/>/<see cref="sharedProgress"/>.</summary>
            public string[] sharedIds = Array.Empty<string>();
            public int[] sharedStrides = Array.Empty<int>();
            public int[] sharedProgress = Array.Empty<int>();
        }

        [Serializable]
        public class PlayerSave
        {
            public int version = 1;
            public string authPlayerId = "";
            public Vector3 position;
            public float health = 100f;
            public string inventoryJson = "";
            /// <summary>Serialized QuestManager.SaveState for this player's personal quests.</summary>
            public string questsJson = "";
        }

        // ---------------- payload IO (host only) ----------------

        public static void WriteWorld(int slot, WorldSave world) => WriteJson(WorldPath(slot), world);
        public static WorldSave LoadWorld(int slot) => ReadJson<WorldSave>(WorldPath(slot));

        public static void WriteQuests(int slot, QuestsSave quests) => WriteJson(QuestsPath(slot), quests);
        public static QuestsSave LoadQuests(int slot) => ReadJson<QuestsSave>(QuestsPath(slot));

        public static void WritePlayer(int slot, PlayerSave player)
        {
            if (string.IsNullOrEmpty(player?.authPlayerId)) return;
            WriteJson(PlayerPath(slot, player.authPlayerId), player);
        }

        public static PlayerSave LoadPlayer(int slot, string authId) =>
            string.IsNullOrEmpty(authId) ? null : ReadJson<PlayerSave>(PlayerPath(slot, authId));

        private static void WriteJson<T>(string path, T value)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonUtility.ToJson(value, true));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SaveSystem] Failed writing {path}: {ex.Message}");
            }
        }

        private static T ReadJson<T>(string path) where T : class
        {
            try
            {
                return File.Exists(path) ? JsonUtility.FromJson<T>(File.ReadAllText(path)) : null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SaveSystem] Corrupt file {path}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Auth ids are opaque strings; keep them filesystem-safe.</summary>
        private static string SanitizeId(string authId)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                authId = authId.Replace(c, '_');
            return authId;
        }

        public static SaveSlotMeta LoadMeta(int slot)
        {
            try
            {
                if (!SlotExists(slot)) return null;
                return JsonUtility.FromJson<SaveSlotMeta>(File.ReadAllText(MetaPath(slot)));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SaveSystem] Corrupt meta in slot {slot}: {ex.Message}");
                return null;
            }
        }

        public static void WriteMeta(int slot, SaveSlotMeta meta)
        {
            Directory.CreateDirectory(SlotDir(slot));
            File.WriteAllText(MetaPath(slot), JsonUtility.ToJson(meta, true));
        }

        public static SaveSlotMeta CreateNew(int slot, GameConfig config)
        {
            var meta = SaveSlotMeta.FromConfig(config);
            WriteMeta(slot, meta);
            return meta;
        }

        public static void TouchLastPlayed(int slot, double addedPlaytimeSeconds = 0)
        {
            var meta = LoadMeta(slot);
            if (meta == null) return;
            meta.lastPlayedUtc = DateTime.UtcNow.ToString("o");
            meta.playtimeSeconds += addedPlaytimeSeconds;
            WriteMeta(slot, meta);
        }

        public static void DeleteSlot(int slot)
        {
            try
            {
                if (Directory.Exists(SlotDir(slot)))
                    Directory.Delete(SlotDir(slot), true);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SaveSystem] Failed to delete slot {slot}: {ex.Message}");
            }
        }
    }
}
