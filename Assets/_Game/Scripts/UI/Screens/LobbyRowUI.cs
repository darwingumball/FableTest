using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    public class LobbyRowUI : MonoBehaviour
    {
        [SerializeField] private TMP_Text label;
        [SerializeField] private Button joinButton;

        public void Bind(string text, Action onJoin)
        {
            label.text = text;
            joinButton.onClick.RemoveAllListeners();
            joinButton.onClick.AddListener(() => onJoin?.Invoke());
        }
    }
}
