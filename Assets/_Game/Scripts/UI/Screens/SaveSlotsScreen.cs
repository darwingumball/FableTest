using System;
using Game.Save;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// Lists the save slots. Empty slot -> New (GameSetup with defaults); existing slot ->
    /// Continue (GameSetup prefilled from meta, difficulty locked) or Delete.
    /// </summary>
    public class SaveSlotsScreen : MenuScreen
    {
        [SerializeField] private Transform slotContainer;
        [SerializeField] private SlotRowUI slotRowPrefab;
        [SerializeField] private Button backButton;
        [SerializeField] private GameSetupScreen gameSetupScreen;

        private MenuScreenManager _manager;

        private void Awake()
        {
            _manager = GetComponentInParent<MenuScreenManager>(true);
            backButton.onClick.AddListener(() => _manager.Back());
        }

        public override void OnShown() => Rebuild();

        private void Rebuild()
        {
            for (int i = slotContainer.childCount - 1; i >= 0; i--)
                Destroy(slotContainer.GetChild(i).gameObject);

            for (int slot = 0; slot < SaveSystem.SLOT_COUNT; slot++)
            {
                var row = Instantiate(slotRowPrefab, slotContainer);
                var meta = SaveSystem.LoadMeta(slot);
                int s = slot;

                if (meta == null)
                {
                    row.Bind($"Slot {slot + 1} — empty", "New", false,
                        onPrimary: () => OpenSetup(s, null), onDelete: null);
                }
                else
                {
                    string played = "";
                    if (DateTime.TryParse(meta.lastPlayedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                        played = dt.ToLocalTime().ToString("g");
                    row.Bind(
                        $"Slot {slot + 1} — {meta.sessionName}  ({(Core.GameDifficulty)meta.difficulty}, {played})",
                        "Continue", true,
                        onPrimary: () => OpenSetup(s, meta),
                        onDelete: () => { SaveSystem.DeleteSlot(s); Rebuild(); });
                }
            }
        }

        private void OpenSetup(int slot, SaveSlotMeta meta)
        {
            gameSetupScreen.Configure(slot, meta);
            _manager.Push(gameSetupScreen);
        }
    }
}
