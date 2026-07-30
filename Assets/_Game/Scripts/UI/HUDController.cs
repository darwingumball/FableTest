using Game.Net;
using Game.World;
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

        [Header("Refuelling")]
        [SerializeField] private GameObject refuelPanel;
        [SerializeField] private UnityEngine.UI.Image refuelContainerFill;
        [SerializeField] private TMP_Text refuelContainerLabel;
        [SerializeField] private UnityEngine.UI.Image refuelTankFill;
        [SerializeField] private TMP_Text refuelTankLabel;

        private NetworkPlayer _bound;

        private void OnEnable()
        {
            promptLabel.text = "";
            if (refuelPanel != null) refuelPanel.SetActive(false);
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

            UpdateRefuelMeter(uiOpen);
        }

        /// <summary>
        /// Polls <see cref="FuelTank.LocalActive"/> rather than listening for an event - there
        /// is only ever at most one active pour for the local player, and both the tank's own
        /// level and the container's are already replicated NetworkVariables, so reading them
        /// straight off the objects each frame is simpler than wiring a change notification for
        /// numbers that update continuously anyway while a pour is running.
        /// </summary>
        private void UpdateRefuelMeter(bool uiOpen)
        {
            if (refuelPanel == null) return;

            var tank = FuelTank.LocalActive;
            bool refueling = tank != null && !uiOpen;
            refuelPanel.SetActive(refueling);
            if (!refueling) return;

            var container = tank.LocalRefuelSource;
            if (refuelContainerFill != null)
                refuelContainerFill.fillAmount = container != null && container.Capacity > 0f
                    ? container.Liters / container.Capacity : 0f;
            if (refuelContainerLabel != null)
                refuelContainerLabel.text = container != null
                    ? $"Container: {container.Liters:0.0}/{container.Capacity:0} L" : "";

            if (refuelTankFill != null)
                refuelTankFill.fillAmount = tank.Fraction;
            if (refuelTankLabel != null)
                refuelTankLabel.text = $"Tank: {tank.Liters:0.0}/{tank.Capacity:0} L";
        }
    }
}
