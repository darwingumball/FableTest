using Game.Core;
using Game.Net;
using Game.Save;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// Escape menu in the World scene: Resume / Settings / Save (host) / Leave.
    /// Doesn't pause time (multiplayer), just frees the cursor and blocks player input.
    /// </summary>
    public class PauseMenu : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private Button resumeButton;
        [SerializeField] private Button settingsButton;
        [SerializeField] private Button saveButton;
        [SerializeField] private TMP_Text statusLabel;
        [SerializeField] private Button leaveButton;
        [SerializeField] private SettingsScreen settingsScreen;
        [SerializeField] private InputActionAsset inputActions;

        public static bool IsOpen { get; private set; }

        private InputAction _pauseAction;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => IsOpen = false;

        private void Awake()
        {
            resumeButton.onClick.AddListener(Close);
            settingsButton.onClick.AddListener(OpenSettings);
            saveButton.onClick.AddListener(SaveGame);
            leaveButton.onClick.AddListener(Leave);
            settingsScreen.OnCloseRequested += CloseSettings;

            _pauseAction = inputActions.FindActionMap("Gameplay")?.FindAction("Pause");

            panelRoot.SetActive(false);
            settingsScreen.gameObject.SetActive(false);
        }

        private void Update()
        {
            if (_pauseAction != null && _pauseAction.WasPressedThisFrame())
            {
                if (settingsScreen.gameObject.activeSelf) CloseSettings();
                else if (IsOpen) Close();
                else Open();
            }
        }

        private void Open()
        {
            IsOpen = true;
            panelRoot.SetActive(true);
            statusLabel.text = "";

            bool isHost = NetworkSessionManager.Instance != null
                && NetworkSessionManager.Instance.CurrentMode != NetworkSessionManager.SessionMode.Client;
            saveButton.gameObject.SetActive(isHost && SessionContext.SelectedSaveSlot >= 0);

            SetPlayerControl(false);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void Close()
        {
            IsOpen = false;
            panelRoot.SetActive(false);
            settingsScreen.gameObject.SetActive(false);
            SetPlayerControl(true);
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void OpenSettings()
        {
            panelRoot.SetActive(false);
            settingsScreen.gameObject.SetActive(true);
            settingsScreen.OnShown();
        }

        private void CloseSettings()
        {
            settingsScreen.gameObject.SetActive(false);
            panelRoot.SetActive(true);
        }

        private void SaveGame()
        {
            if (SaveService.Instance == null)
            {
                statusLabel.text = "Save unavailable.";
                return;
            }
            SaveService.Instance.SaveAll();
            statusLabel.text = "Saved.";
        }

        private async void Leave()
        {
            SetPlayerControl(true);
            if (NetworkSessionManager.Instance != null)
                await NetworkSessionManager.Instance.LeaveAsync();
        }

        private static void SetPlayerControl(bool enabled)
        {
            var local = NetworkPlayer.Local;
            if (local != null && local.Controller != null)
                local.Controller.SetControl(enabled);
            if (local != null && local.Interaction != null)
                local.Interaction.enabled = enabled;
        }
    }
}
