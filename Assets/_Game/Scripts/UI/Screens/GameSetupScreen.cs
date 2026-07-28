using Game.Core;
using Game.Net;
using Game.Save;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// THE game-creation screen, identical for solo and multiplayer: name, difficulty,
    /// visibility (Solo / Friends / Public), player count, day length, friendly fire.
    /// Continue mode locks the persistent fields (name/difficulty) and only lets the
    /// per-session ones (visibility/players) change.
    /// </summary>
    public class GameSetupScreen : MenuScreen
    {
        [SerializeField] private TMP_InputField nameInput;
        [SerializeField] private TMP_Dropdown difficultyDropdown;
        [SerializeField] private TMP_Dropdown visibilityDropdown;
        [SerializeField] private Slider maxPlayersSlider;
        [SerializeField] private TMP_Text maxPlayersLabel;
        [SerializeField] private Slider dayLengthSlider;
        [SerializeField] private TMP_Text dayLengthLabel;
        [SerializeField] private Toggle friendlyFireToggle;
        [SerializeField] private Button startButton;
        [SerializeField] private TMP_Text startLabel;
        [SerializeField] private TMP_Text statusLabel;
        [SerializeField] private Button backButton;

        private MenuScreenManager _manager;
        private int _slot;
        private bool _isContinue;

        private void Awake()
        {
            _manager = GetComponentInParent<MenuScreenManager>(true);
            backButton.onClick.AddListener(() => _manager.Back());
            startButton.onClick.AddListener(OnStartClicked);
            maxPlayersSlider.onValueChanged.AddListener(v => maxPlayersLabel.text = $"Max players: {(int)v}");
            dayLengthSlider.onValueChanged.AddListener(v => dayLengthLabel.text = $"Day length: {(int)v} min");
        }

        public void Configure(int slot, SaveSlotMeta meta)
        {
            _slot = slot;
            _isContinue = meta != null;

            var config = meta?.ToConfig(SessionVisibility.Solo) ?? new GameConfig { sessionName = $"Game {slot + 1}" };

            nameInput.text = config.sessionName;
            difficultyDropdown.value = (int)config.difficulty;
            visibilityDropdown.value = (int)config.visibility;
            maxPlayersSlider.value = config.maxPlayers;
            dayLengthSlider.value = config.dayLengthMinutes;
            friendlyFireToggle.isOn = config.friendlyFire;

            // Persistent world facts don't change on continue.
            nameInput.interactable = !_isContinue;
            difficultyDropdown.interactable = !_isContinue;
            dayLengthSlider.interactable = !_isContinue;

            maxPlayersLabel.text = $"Max players: {(int)maxPlayersSlider.value}";
            dayLengthLabel.text = $"Day length: {(int)dayLengthSlider.value} min";
            startLabel.text = _isContinue ? "Continue" : "Start";
            statusLabel.text = "";
            SetBusy(false);
        }

        private async void OnStartClicked()
        {
            var config = new GameConfig
            {
                sessionName = string.IsNullOrWhiteSpace(nameInput.text) ? $"Game {_slot + 1}" : nameInput.text.Trim(),
                difficulty = (GameDifficulty)difficultyDropdown.value,
                visibility = (SessionVisibility)visibilityDropdown.value,
                maxPlayers = (int)maxPlayersSlider.value,
                dayLengthMinutes = dayLengthSlider.value,
                friendlyFire = friendlyFireToggle.isOn,
            };

            if (_isContinue)
                SaveSystem.TouchLastPlayed(_slot);
            else
                SaveSystem.CreateNew(_slot, config);

            SessionContext.SelectedSaveSlot = _slot;
            SessionContext.IsContinue = _isContinue;

            SetBusy(true);
            statusLabel.text = config.visibility == SessionVisibility.Solo
                ? "Starting..." : "Creating lobby...";

            bool ok = await NetworkSessionManager.Instance.StartSessionAsync(config);
            if (!ok)
            {
                statusLabel.text = "Failed to start session — see console.";
                SetBusy(false);
            }
            // On success the scene changes and this UI is destroyed.
        }

        private void SetBusy(bool busy)
        {
            startButton.interactable = !busy;
            backButton.interactable = !busy;
        }
    }
}
