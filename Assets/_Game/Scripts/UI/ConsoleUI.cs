using System.Collections.Generic;
using System.Text;
using Game.Admin;
using Game.Net;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.UI
{
    /// <summary>
    /// Backquote console. Sends lines to <see cref="AdminService"/> (server-executed) and
    /// prints replies. Local history with up/down arrows.
    /// </summary>
    public class ConsoleUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private TMP_Text outputText;
        [SerializeField] private TMP_InputField inputField;
        [SerializeField] private InputActionAsset inputActions;

        public static bool IsOpen { get; private set; }

        private readonly List<string> _history = new();
        private readonly StringBuilder _output = new();
        private int _historyIndex = -1;
        private InputAction _toggleAction;
        private AdminService _boundService;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => IsOpen = false;

        private void Awake()
        {
            _toggleAction = inputActions.FindActionMap("Gameplay")?.FindAction("Console");
            inputField.onSubmit.AddListener(Submit);
            panelRoot.SetActive(false);
            Print("Console ready. Type help.");
        }

        private void Update()
        {
            if (_toggleAction != null && _toggleAction.WasPressedThisFrame())
            {
                if (IsOpen) Close();
                else Open();
            }

            if (!IsOpen) return;

            BindService();

            if (Keyboard.current == null) return;
            if (Keyboard.current.upArrowKey.wasPressedThisFrame) StepHistory(-1);
            else if (Keyboard.current.downArrowKey.wasPressedThisFrame) StepHistory(1);
            else if (Keyboard.current.escapeKey.wasPressedThisFrame) Close();
        }

        private void BindService()
        {
            var service = AdminService.Instance;
            if (service == _boundService) return;
            if (_boundService != null) _boundService.OnReply -= Print;
            _boundService = service;
            if (_boundService != null) _boundService.OnReply += Print;
        }

        private void OnDestroy()
        {
            if (_boundService != null) _boundService.OnReply -= Print;
        }

        private void Open()
        {
            IsOpen = true;
            panelRoot.SetActive(true);
            BindService();
            SetPlayerControl(false);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            inputField.text = "";
            inputField.ActivateInputField();
        }

        private void Close()
        {
            IsOpen = false;
            panelRoot.SetActive(false);
            if (!PauseMenu.IsOpen && !TabMenuUI.IsOpen)
            {
                SetPlayerControl(true);
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        private void Submit(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                inputField.ActivateInputField();
                return;
            }

            Print($"<color=#94A88F>> {line}</color>");
            _history.Add(line);
            _historyIndex = _history.Count;

            if (AdminService.Instance != null) AdminService.Instance.Submit(line);
            else Print("Not in a session.");

            inputField.text = "";
            inputField.ActivateInputField();
        }

        private void StepHistory(int direction)
        {
            if (_history.Count == 0) return;
            _historyIndex = Mathf.Clamp(_historyIndex + direction, 0, _history.Count);
            inputField.text = _historyIndex < _history.Count ? _history[_historyIndex] : "";
            inputField.caretPosition = inputField.text.Length;
        }

        private void Print(string message)
        {
            _output.AppendLine(message);
            if (_output.Length > 8000) _output.Remove(0, _output.Length - 8000);
            outputText.text = _output.ToString();
        }

        private static void SetPlayerControl(bool enabled)
        {
            var local = NetworkPlayer.Local;
            if (local == null) return;
            if (local.Controller != null) local.Controller.SetControl(enabled);
            if (local.Interaction != null) local.Interaction.enabled = enabled;
        }
    }
}
