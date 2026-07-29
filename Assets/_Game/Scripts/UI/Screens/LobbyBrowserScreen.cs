using Game.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>Public lobby list + join-by-code (works for private/friends lobbies too).</summary>
    public class LobbyBrowserScreen : MenuScreen
    {
        [SerializeField] private Transform listContainer;
        [SerializeField] private LobbyRowUI lobbyRowPrefab;
        [SerializeField] private Button refreshButton;
        [SerializeField] private TMP_InputField codeInput;
        [SerializeField] private Button joinByCodeButton;
        [SerializeField] private TMP_Text statusLabel;
        [SerializeField] private Button backButton;

        private MenuScreenManager _manager;
        private bool _joining;

        private void Awake()
        {
            _manager = GetComponentInParent<MenuScreenManager>(true);
            backButton.onClick.AddListener(() => _manager.Back());
            refreshButton.onClick.AddListener(() => _ = RefreshAsync());
            joinByCodeButton.onClick.AddListener(OnJoinByCode);
        }

        public override void OnShown()
        {
            statusLabel.text = "";
            _ = RefreshAsync();
        }

        private async System.Threading.Tasks.Task RefreshAsync()
        {
            ClearList();
            statusLabel.text = "Searching for games...";
            try
            {
                await NetworkBootstrap.EnsureInitializedAsync();
                var response = await NetworkSessionManager.Instance.Lobby.QueryAsync();
                ClearList();
                if (response.Results == null || response.Results.Count == 0)
                {
                    statusLabel.text = "No public games found.";
                    return;
                }
                statusLabel.text = "";
                foreach (var lobby in response.Results)
                {
                    var row = Instantiate(lobbyRowPrefab, listContainer);
                    var captured = lobby;
                    row.Bind($"{lobby.Name}   {lobby.Players.Count}/{lobby.MaxPlayers}",
                        () => Join(() => NetworkSessionManager.Instance.JoinByLobbyAsync(captured)));
                }
            }
            catch (System.Exception ex)
            {
                statusLabel.text = "Lobby search failed — check connection / UGS setup.";
                Debug.LogWarning($"[LobbyBrowser] Query failed: {ex.Message}");
            }
        }

        private void OnJoinByCode()
        {
            if (string.IsNullOrWhiteSpace(codeInput.text)) return;
            Join(() => NetworkSessionManager.Instance.JoinByCodeAsync(codeInput.text));
        }

        private async void Join(System.Func<System.Threading.Tasks.Task<bool>> joinFunc)
        {
            if (_joining) return;
            _joining = true;
            statusLabel.text = "Joining...";
            bool ok = await joinFunc();
            if (!ok)
            {
                statusLabel.text = "Join failed — lobby may be gone or code invalid.";
                _joining = false;
            }
            // On success NGO scene sync takes us to the host's world.
        }

        private void ClearList()
        {
            for (int i = listContainer.childCount - 1; i >= 0; i--)
                Destroy(listContainer.GetChild(i).gameObject);
        }
    }
}
