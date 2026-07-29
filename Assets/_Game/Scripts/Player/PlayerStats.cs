using System;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player
{
    /// <summary>
    /// Health scaffold - the hook the future medical system builds on. Server-authoritative:
    /// only the server mutates health; clients observe. Nothing kills or respawns yet.
    /// </summary>
    public class PlayerStats : NetworkBehaviour
    {
        public const float MAX_HEALTH = 100f;

        private readonly NetworkVariable<float> _health = new(
            MAX_HEALTH,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public float Health => _health.Value;
        public float HealthNormalized => _health.Value / MAX_HEALTH;
        public bool IsAlive => _health.Value > 0f;

        /// <summary>(current, max) - fired on every replicated change, on all peers.</summary>
        public event Action<float, float> OnHealthChanged;

        public override void OnNetworkSpawn()
        {
            _health.OnValueChanged += HandleHealthChanged;
            HandleHealthChanged(_health.Value, _health.Value);
        }

        public override void OnNetworkDespawn()
        {
            _health.OnValueChanged -= HandleHealthChanged;
        }

        private void HandleHealthChanged(float previous, float current) =>
            OnHealthChanged?.Invoke(current, MAX_HEALTH);

        /// <summary>Server-only. Friendly-fire rules etc. are the caller's responsibility.</summary>
        public void ServerApplyDamage(float amount)
        {
            if (!IsServer || amount <= 0f) return;
            _health.Value = Mathf.Max(0f, _health.Value - amount);
        }

        /// <summary>Server-only.</summary>
        public void ServerHeal(float amount)
        {
            if (!IsServer || amount <= 0f) return;
            _health.Value = Mathf.Min(MAX_HEALTH, _health.Value + amount);
        }
    }
}
