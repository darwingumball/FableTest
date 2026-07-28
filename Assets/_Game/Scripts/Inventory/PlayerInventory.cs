using System;
using UnityEngine;

namespace Game.Inventory
{
    /// <summary>
    /// The local player's bag: an 8x5 grid plus cash. Contents are authoritative on the
    /// OWNING client (co-op trust model - the server validates world item existence, not
    /// bag contents); the save system persists them via the host through GetState/ApplyState.
    /// </summary>
    public class PlayerInventory : MonoBehaviour
    {
        public const int GRID_WIDTH = 8;
        public const int GRID_HEIGHT = 5;

        public InventoryGrid Grid { get; private set; }
        public int Cash { get; private set; }

        public event Action CashChanged;

        private void Awake()
        {
            Grid = new InventoryGrid(GRID_WIDTH, GRID_HEIGHT);
        }

        /// <summary>Add items; returns the count that did NOT fit.</summary>
        public int AddItem(ItemData item, int count) => Grid.AutoAdd(item, count);

        public bool CanFit(ItemData item, int count) => Grid.CanFit(item, count);

        public void AddCash(int amount)
        {
            Cash = Mathf.Max(0, Cash + amount);
            CashChanged?.Invoke();
        }

        public bool TrySpendCash(int amount)
        {
            if (amount > Cash) return false;
            Cash -= amount;
            CashChanged?.Invoke();
            return true;
        }

        // ---------------- save DTO ----------------

        [Serializable]
        public class State
        {
            public int cash;
            public Entry[] entries = Array.Empty<Entry>();

            [Serializable]
            public struct Entry
            {
                public string itemId;
                public int count, x, y;
                public bool rotated;
            }
        }

        public State GetState()
        {
            var state = new State { cash = Cash, entries = new State.Entry[Grid.Stacks.Count] };
            for (int i = 0; i < Grid.Stacks.Count; i++)
            {
                var s = Grid.Stacks[i];
                state.entries[i] = new State.Entry
                {
                    itemId = s.item.Id, count = s.count, x = s.x, y = s.y, rotated = s.rotated,
                };
            }
            return state;
        }

        public void ApplyState(State state)
        {
            Grid.Clear();
            Cash = state?.cash ?? 0;
            CashChanged?.Invoke();
            if (state?.entries == null) return;
            foreach (var e in state.entries)
            {
                var item = ItemDatabase.Get(e.itemId);
                if (item == null)
                {
                    Debug.LogWarning($"[PlayerInventory] Unknown item '{e.itemId}' in save - skipped.");
                    continue;
                }
                var stack = new ItemStack { item = item, count = e.count };
                if (!Grid.TryPlace(stack, e.x, e.y, e.rotated))
                    Grid.AutoAdd(item, e.count); // layout conflict (item size changed) - refit
            }
        }
    }
}
