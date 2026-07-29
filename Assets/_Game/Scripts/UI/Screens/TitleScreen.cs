using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    public class TitleScreen : MenuScreen
    {
        [SerializeField] private Button playButton;
        [SerializeField] private Button joinButton;
        [SerializeField] private Button settingsButton;
        [SerializeField] private Button quitButton;
        [SerializeField] private MenuScreen saveSlotsScreen;
        [SerializeField] private MenuScreen lobbyBrowserScreen;
        [SerializeField] private MenuScreen settingsScreen;

        private MenuScreenManager _manager;

        private void Awake()
        {
            _manager = GetComponentInParent<MenuScreenManager>(true);
            playButton.onClick.AddListener(() => _manager.Push(saveSlotsScreen));
            joinButton.onClick.AddListener(() => _manager.Push(lobbyBrowserScreen));
            settingsButton.onClick.AddListener(() => _manager.Push(settingsScreen));
            quitButton.onClick.AddListener(Quit);
        }

        private static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
