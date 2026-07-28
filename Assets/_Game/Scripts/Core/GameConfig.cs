using System;
using Unity.Collections;
using Unity.Netcode;

namespace Game.Core
{
    public enum GameDifficulty : byte { Easy, Normal, Hard, Hardcore }

    /// <summary>Who can see/join the session. Solo runs a loopback host with no lobby at all.</summary>
    public enum SessionVisibility : byte { Solo, Private, Public }

    /// <summary>
    /// Everything chosen on the GameSetup screen. One config drives both solo and
    /// multiplayer session creation - there is no separate singleplayer path.
    /// Serialized into save slot meta.json and replicated to clients via
    /// <see cref="Game.Net.NetworkGameState"/>.
    /// </summary>
    [Serializable]
    public class GameConfig
    {
        public string sessionName = "New Game";
        public GameDifficulty difficulty = GameDifficulty.Normal;
        public SessionVisibility visibility = SessionVisibility.Solo;
        public int maxPlayers = 4;
        public float dayLengthMinutes = 30f;
        public bool friendlyFire = false;

        public GameConfigNet ToNet()
        {
            return new GameConfigNet
            {
                SessionName = new FixedString64Bytes(sessionName ?? ""),
                Difficulty = (byte)difficulty,
                Visibility = (byte)visibility,
                MaxPlayers = maxPlayers,
                DayLengthMinutes = dayLengthMinutes,
                FriendlyFire = friendlyFire
            };
        }

        public static GameConfig FromNet(in GameConfigNet net)
        {
            return new GameConfig
            {
                sessionName = net.SessionName.ToString(),
                difficulty = (GameDifficulty)net.Difficulty,
                visibility = (SessionVisibility)net.Visibility,
                maxPlayers = net.MaxPlayers,
                dayLengthMinutes = net.DayLengthMinutes,
                friendlyFire = net.FriendlyFire
            };
        }
    }

    /// <summary>Wire format of <see cref="GameConfig"/> for NetworkVariable replication.</summary>
    public struct GameConfigNet : INetworkSerializable, IEquatable<GameConfigNet>
    {
        public FixedString64Bytes SessionName;
        public byte Difficulty;
        public byte Visibility;
        public int MaxPlayers;
        public float DayLengthMinutes;
        public bool FriendlyFire;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref SessionName);
            serializer.SerializeValue(ref Difficulty);
            serializer.SerializeValue(ref Visibility);
            serializer.SerializeValue(ref MaxPlayers);
            serializer.SerializeValue(ref DayLengthMinutes);
            serializer.SerializeValue(ref FriendlyFire);
        }

        public bool Equals(GameConfigNet other)
        {
            return SessionName.Equals(other.SessionName)
                && Difficulty == other.Difficulty
                && Visibility == other.Visibility
                && MaxPlayers == other.MaxPlayers
                && DayLengthMinutes.Equals(other.DayLengthMinutes)
                && FriendlyFire == other.FriendlyFire;
        }
    }
}
