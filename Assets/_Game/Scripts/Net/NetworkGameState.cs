using System;
using Game.Core;
using Unity.Netcode;
using UnityEngine;

namespace Game.Net
{
    /// <summary>
    /// Scene object in World that replicates the host's GameConfig to every client
    /// (including late joiners, via normal NetworkVariable spawn sync). Clients read
    /// difficulty/game rules from here, never from their own SessionContext.
    /// </summary>
    public class NetworkGameState : NetworkBehaviour
    {
        public static NetworkGameState Instance { get; private set; }

        private readonly NetworkVariable<GameConfigNet> _config = new(
            default,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        /// <summary>Deserialized copy of the replicated config. Valid after spawn.</summary>
        public GameConfig Config { get; private set; } = new GameConfig();

        public static event Action<GameConfig> OnConfigChanged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
            OnConfigChanged = null;
        }

        private void Awake()
        {
            Instance = this;
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer)
                _config.Value = SessionContext.Config.ToNet();

            ApplyConfig(_config.Value);
            _config.OnValueChanged += OnConfigNetChanged;
        }

        public override void OnNetworkDespawn()
        {
            _config.OnValueChanged -= OnConfigNetChanged;
        }

        public override void OnDestroy()
        {
            if (Instance == this) Instance = null;
            base.OnDestroy();
        }

        private void OnConfigNetChanged(GameConfigNet previous, GameConfigNet current) => ApplyConfig(current);

        private void ApplyConfig(in GameConfigNet net)
        {
            Config = GameConfig.FromNet(net);
            OnConfigChanged?.Invoke(Config);
        }

        /// <summary>Server-side config mutation (admin commands). Replicates automatically.</summary>
        public void ServerSetConfig(GameConfig config)
        {
            if (!IsServer) return;
            SessionContext.Config = config;
            _config.Value = config.ToNet();
        }
    }
}
