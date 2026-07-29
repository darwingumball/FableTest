using System;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// One "Action ..... [Key] [Rebind]" row. Rebinds the first keyboard/mouse binding
    /// of the action via interactive rebinding; Escape cancels.
    /// </summary>
    public class RebindRow : MonoBehaviour
    {
        [SerializeField] private TMP_Text actionLabel;
        [SerializeField] private TMP_Text bindingLabel;
        [SerializeField] private Button rebindButton;

        private InputAction _action;
        private int _bindingIndex = -1;
        private Action _onRebound;
        private InputActionRebindingExtensions.RebindingOperation _operation;

        public void Bind(InputAction action, Action onRebound)
        {
            _action = action;
            _onRebound = onRebound;
            actionLabel.text = action.name;

            _bindingIndex = -1;
            for (int i = 0; i < action.bindings.Count; i++)
            {
                var b = action.bindings[i];
                if (b.isComposite || b.isPartOfComposite) continue;
                if (b.groups != null && b.groups.Contains("Keyboard&Mouse")) { _bindingIndex = i; break; }
                if (_bindingIndex < 0) _bindingIndex = i;
            }

            rebindButton.onClick.RemoveAllListeners();
            rebindButton.onClick.AddListener(StartRebind);
            RefreshLabel();
        }

        private void RefreshLabel()
        {
            bindingLabel.text = _bindingIndex >= 0
                ? _action.GetBindingDisplayString(_bindingIndex)
                : "-";
        }

        private void StartRebind()
        {
            if (_action == null || _bindingIndex < 0 || _operation != null) return;

            bindingLabel.text = "press key...";
            bool wasEnabled = _action.enabled;
            _action.Disable();

            _operation = _action.PerformInteractiveRebinding(_bindingIndex)
                .WithCancelingThrough("<Keyboard>/escape")
                .OnMatchWaitForAnother(0.1f)
                .OnComplete(op => FinishRebind(op, wasEnabled, true))
                .OnCancel(op => FinishRebind(op, wasEnabled, false))
                .Start();
        }

        private void FinishRebind(InputActionRebindingExtensions.RebindingOperation op, bool reEnable, bool changed)
        {
            op.Dispose();
            _operation = null;
            if (reEnable) _action.Enable();
            RefreshLabel();
            if (changed) _onRebound?.Invoke();
        }

        private void OnDisable()
        {
            _operation?.Cancel();
        }
    }
}
