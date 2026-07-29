using System.Collections.Generic;
using Game.Net;
using Game.Player;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.UI
{
    /// <summary>
    /// Chat window: an always-on log that fades out when quiet, and an input line that
    /// only appears while you are actually typing.
    ///
    /// Opening the input has to take movement away from the player, or typing "swap" walks
    /// you into a wall. It also has to hand it back on close, including when the close
    /// happens because the message was sent rather than cancelled.
    /// </summary>
    public class ChatUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private GameObject inputRow;
        [SerializeField] private TextMeshProUGUI logText;
        [SerializeField] private TextMeshProUGUI channelLabel;
        [SerializeField] private TextMeshProUGUI voiceLabel;
        [SerializeField] private TMP_InputField inputField;
        [SerializeField] private InputActionAsset inputActions;

        [Tooltip("Lines kept in the scrollback.")]
        [SerializeField] private int maxLines = 40;
        [Tooltip("Seconds of silence before the log fades out. It never fades while typing.")]
        [SerializeField] private float fadeAfterSeconds = 9f;

        private readonly List<string> _lines = new();
        private InputAction _chatAction, _voiceAction, _channelAction;
        private ChatChannel _channel = ChatChannel.Local;
        private CanvasGroup _group;
        private float _lastActivity;
        private bool _typing;

        private void Awake()
        {
            _group = panelRoot != null ? panelRoot.GetComponent<CanvasGroup>() : null;
            if (inputRow != null) inputRow.SetActive(false);

            if (inputActions != null)
            {
                var map = inputActions.FindActionMap("Gameplay");
                _chatAction = map?.FindAction("Chat");
                _voiceAction = map?.FindAction("Voice");
                _channelAction = map?.FindAction("ChatChannel");
            }

            if (inputField != null) inputField.onSubmit.AddListener(OnSubmit);
            UpdateChannelLabel();
        }

        private void OnEnable() => ChatRelay.OnMessage += HandleMessage;
        private void OnDisable() => ChatRelay.OnMessage -= HandleMessage;

        private void Update()
        {
            HandleVoiceKey();

            if (_typing)
            {
                if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
                    CloseInput();
                if (_group != null) _group.alpha = 1f;
                return;
            }

            if (_chatAction != null && _chatAction.WasPressedThisFrame()) OpenInput();
            if (_channelAction != null && _channelAction.WasPressedThisFrame()) CycleChannel();

            // Fade the log out when nothing has happened for a while, so it stops competing
            // with the world. Any new message snaps it straight back to full.
            if (_group == null) return;
            float quiet = Time.unscaledTime - _lastActivity;
            _group.alpha = quiet < fadeAfterSeconds
                ? 1f
                : Mathf.Clamp01(1f - (quiet - fadeAfterSeconds) / 1.5f);
        }

        private void HandleVoiceKey()
        {
            var voice = VoiceChatService.Instance;
            if (voice == null || _voiceAction == null) return;

            // Push-to-talk must not fire while the chat box has focus, or every "v" typed
            // opens the mic.
            voice.SetTalkKeyHeld(!_typing && _voiceAction.IsPressed());

            if (voiceLabel == null) return;
            voiceLabel.text = !voice.IsReady ? string.Empty
                : voice.IsTransmitting ? "<color=#8CE99A>● TALKING</color>"
                : voice.SelfMuted ? "<color=#FF8787>muted</color>"
                : string.Empty;
        }

        private void OpenInput()
        {
            _typing = true;
            _lastActivity = Time.unscaledTime;
            if (inputRow != null) inputRow.SetActive(true);
            if (inputField != null)
            {
                inputField.text = string.Empty;
                inputField.ActivateInputField();
                inputField.Select();
            }
            SetPlayerControl(false);
        }

        private void CloseInput()
        {
            _typing = false;
            if (inputField != null)
            {
                inputField.text = string.Empty;
                inputField.DeactivateInputField();
            }
            if (inputRow != null) inputRow.SetActive(false);
            SetPlayerControl(true);
        }

        private void OnSubmit(string value)
        {
            // onSubmit fires on Enter. Send first, then close - closing first would drop
            // the text we were about to send.
            if (!string.IsNullOrWhiteSpace(value))
            {
                if (ChatRelay.Instance != null) ChatRelay.Instance.SubmitLocal(value, _channel);
                else ChatRelay.Local(ChatChannel.System, "Chat is not connected.");
            }
            CloseInput();
        }

        private void CycleChannel()
        {
            _channel = _channel == ChatChannel.Local ? ChatChannel.Global : ChatChannel.Local;
            UpdateChannelLabel();

            // Text channel and voice channel move together: having your typing go server-wide
            // while your voice stays local would be a quiet way to say the wrong thing to the
            // wrong people.
            var voice = VoiceChatService.Instance;
            if (voice != null) _ = voice.SetGlobalVoiceAsync(_channel == ChatChannel.Global);
        }

        private void UpdateChannelLabel()
        {
            if (channelLabel == null) return;
            channelLabel.text = _channel == ChatChannel.Global
                ? "<color=#FFD43B>[ALL]</color>"
                : "<color=#74C0FC>[LOCAL]</color>";
        }

        private static void SetPlayerControl(bool enabled)
        {
            var player = NetworkPlayer.Local;
            if (player == null) return;
            var controller = player.GetComponent<FirstPersonController>();
            if (controller == null) return;
            controller.SetMoveControl(enabled);
            controller.SetLookControl(enabled);
            Cursor.lockState = enabled ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !enabled;
        }

        private void HandleMessage(ChatChannel channel, string from, string text)
        {
            string prefix = channel switch
            {
                ChatChannel.Global => "<color=#FFD43B>[ALL]</color> ",
                ChatChannel.System => "<color=#909296>",
                _ => "<color=#74C0FC>[L]</color> ",
            };
            string suffix = channel == ChatChannel.System ? "</color>" : string.Empty;
            string who = string.IsNullOrEmpty(from) ? string.Empty : $"<b>{from}</b>: ";

            _lines.Add(prefix + who + text + suffix);
            if (_lines.Count > maxLines) _lines.RemoveRange(0, _lines.Count - maxLines);

            if (logText != null) logText.text = string.Join("\n", _lines);
            _lastActivity = Time.unscaledTime;
        }
    }
}
