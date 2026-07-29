using Game.Net;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// The Tab overlay: Inventory / Quests / Map tabs. Frees the cursor and blocks
    /// player control while open. Settings intentionally lives in the Esc menu only.
    /// </summary>
    public class TabMenuUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private Button inventoryTabButton;
        [SerializeField] private Button questsTabButton;
        [SerializeField] private Button mapTabButton;
        [SerializeField] private GameObject inventoryPanel;
        [SerializeField] private GameObject questsPanel;
        [SerializeField] private GameObject mapPanel;
        [SerializeField] private InputActionAsset inputActions;

        public static bool IsOpen { get; private set; }

        private InputAction _tabAction;
        private InputAction _mapAction;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => IsOpen = false;

        private void Awake()
        {
            inventoryTabButton.onClick.AddListener(() => ShowTab(inventoryPanel));
            questsTabButton.onClick.AddListener(() => ShowTab(questsPanel));
            mapTabButton.onClick.AddListener(() => ShowTab(mapPanel));

            _tabAction = inputActions.FindActionMap("Gameplay")?.FindAction("TabMenu");
            _mapAction = inputActions.FindActionMap("Gameplay")?.FindAction("Map");
            panelRoot.SetActive(false);
        }

        private void Update()
        {
            if (PauseMenu.IsOpen) return;

            if (_tabAction != null && _tabAction.WasPressedThisFrame())
            {
                if (IsOpen) Close();
                else Open(inventoryPanel);
            }
            else if (_mapAction != null && _mapAction.WasPressedThisFrame())
            {
                if (IsOpen) Close();
                else Open(mapPanel);
            }
        }

        public void Open(GameObject tab)
        {
            IsOpen = true;
            panelRoot.SetActive(true);
            ShowTab(tab != null ? tab : inventoryPanel);
            SetPlayerControl(false);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        public void Close()
        {
            IsOpen = false;
            panelRoot.SetActive(false);
            SetPlayerControl(true);
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void ShowTab(GameObject tab)
        {
            inventoryPanel.SetActive(tab == inventoryPanel);
            questsPanel.SetActive(tab == questsPanel);
            mapPanel.SetActive(tab == mapPanel);
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
