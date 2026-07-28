using System.Collections.Generic;
using UnityEngine;

namespace Game.UI
{
    /// <summary>
    /// Stack navigation over the menu screens: Push to drill in, Back to return.
    /// Screens live as prefab children of the menu canvas; only the top one is active.
    /// </summary>
    public class MenuScreenManager : MonoBehaviour
    {
        [SerializeField] private MenuScreen initialScreen;
        [SerializeField] private SettingsScreen settingsScreen;

        private readonly Stack<MenuScreen> _stack = new();

        private void Start()
        {
            if (settingsScreen != null)
                settingsScreen.OnCloseRequested += Back;

            foreach (var screen in GetComponentsInChildren<MenuScreen>(true))
                screen.gameObject.SetActive(false);

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            if (initialScreen != null)
                Push(initialScreen);
        }

        public void Push(MenuScreen screen)
        {
            if (screen == null) return;
            if (_stack.Count > 0) Hide(_stack.Peek());
            _stack.Push(screen);
            Show(screen);
        }

        public void Back()
        {
            if (_stack.Count <= 1) return;
            Hide(_stack.Pop());
            Show(_stack.Peek());
        }

        private static void Show(MenuScreen screen)
        {
            screen.gameObject.SetActive(true);
            screen.OnShown();
        }

        private static void Hide(MenuScreen screen)
        {
            screen.OnHidden();
            screen.gameObject.SetActive(false);
        }
    }
}
