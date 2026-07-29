using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>One save-slot row: label + New/Continue + Delete. Bound by SaveSlotsScreen.</summary>
    public class SlotRowUI : MonoBehaviour
    {
        [SerializeField] private TMP_Text label;
        [SerializeField] private Button primaryButton;
        [SerializeField] private TMP_Text primaryLabel;
        [SerializeField] private Button deleteButton;

        public void Bind(string text, string primaryText, bool canDelete, Action onPrimary, Action onDelete)
        {
            label.text = text;
            primaryLabel.text = primaryText;
            deleteButton.gameObject.SetActive(canDelete);

            primaryButton.onClick.RemoveAllListeners();
            primaryButton.onClick.AddListener(() => onPrimary?.Invoke());
            deleteButton.onClick.RemoveAllListeners();
            deleteButton.onClick.AddListener(() => onDelete?.Invoke());
        }
    }
}
