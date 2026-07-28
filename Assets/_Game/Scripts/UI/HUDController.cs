using Game.Net;
using TMPro;
using UnityEngine;

namespace Game.UI
{
    /// <summary>Minimal HUD: crosshair + interact prompt. Binds to the local player on spawn.</summary>
    public class HUDController : MonoBehaviour
    {
        [SerializeField] private GameObject crosshair;
        [SerializeField] private TMP_Text promptLabel;
        [SerializeField] private UnityEngine.UI.Image healthFill;
        [SerializeField] private TMP_Text clockLabel;

        private NetworkPlayer _bound;

        private void OnEnable()
        {
            promptLabel.text = "";
            NetworkPlayer.OnLocalPlayerReady += Bind;
            if (NetworkPlayer.Local != null) Bind(NetworkPlayer.Local);
        }

        private void OnDisable()
        {
            NetworkPlayer.OnLocalPlayerReady -= Bind;
            Unbind();
        }

        private void Bind(NetworkPlayer player)
        {
            Unbind();
            _bound = player;
            if (_bound == null) return;
            if (_bound.Interaction != null)
                _bound.Interaction.OnPromptChanged += OnPrompt;
            if (_bound.Stats != null)
            {
                _bound.Stats.OnHealthChanged += OnHealth;
                OnHealth(_bound.Stats.Health, Player.PlayerStats.MAX_HEALTH);
            }
        }

        private void Unbind()
        {
            if (_bound != null)
            {
                if (_bound.Interaction != null) _bound.Interaction.OnPromptChanged -= OnPrompt;
                if (_bound.Stats != null) _bound.Stats.OnHealthChanged -= OnHealth;
            }
            _bound = null;
        }

        private void OnHealth(float current, float max)
        {
            if (healthFill != null) healthFill.fillAmount = max > 0f ? current / max : 0f;
        }

        private void OnPrompt(string prompt)
        {
            promptLabel.text = string.IsNullOrEmpty(prompt) ? "" : $"[E] {prompt}";
        }

        private void Update()
        {
            bool uiOpen = PauseMenu.IsOpen || TabMenuUI.IsOpen || ConsoleUI.IsOpen;
            if (crosshair != null) crosshair.SetActive(!uiOpen);

            if (clockLabel != null)
            {
                var time = Game.Net.NetworkTimeSync.Instance;
                if (time != null)
                {
                    float hour = time.HourOfDay;
                    clockLabel.text = $"Day {time.Day + 1}   {(int)hour:00}:{(int)((hour % 1f) * 60f):00}";
                }
            }
        }
    }
}
