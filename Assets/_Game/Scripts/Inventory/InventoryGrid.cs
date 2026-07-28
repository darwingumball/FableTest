using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Inventory
{
    /// <summary>A stack of one item type occupying a rectangle of grid cells.</summary>
    [Serializable]
    public class ItemStack
    {
        public ItemData item;
        public int count;
        public int x, y;
        public bool rotated;

        public int Width => rotated ? item.gridHeight : item.gridWidth;
        public int Height => rotated ? item.gridWidth : item.gridHeight;
    }

    /// <summary>
    /// Tarkov-style spatial grid: stacks occupy WxH rectangles, can rotate 90 degrees,
    /// same-item stacks merge up to maxStack. Pure model - UI listens to Changed.
    /// </summary>
    public class InventoryGrid
    {
        public int Width { get; }
        public int Height { get; }

        private readonly ItemStack[,] _cells;
        private readonly List<ItemStack> _stacks = new();

        public IReadOnlyList<ItemStack> Stacks => _stacks;
        public event Action Changed;

        public InventoryGrid(int width, int height)
        {
            Width = width;
            Height = height;
            _cells = new ItemStack[width, height];
        }

        public ItemStack StackAt(int x, int y) =>
            x >= 0 && y >= 0 && x < Width && y < Height ? _cells[x, y] : null;

        public bool CanPlace(ItemData item, int x, int y, bool rotated, ItemStack ignore = null)
        {
            int w = rotated ? item.gridHeight : item.gridWidth;
            int h = rotated ? item.gridWidth : item.gridHeight;
            if (x < 0 || y < 0 || x + w > Width || y + h > Height) return false;
            for (int cx = x; cx < x + w; cx++)
                for (int cy = y; cy < y + h; cy++)
                    if (_cells[cx, cy] != null && _cells[cx, cy] != ignore)
                        return false;
            return true;
        }

        /// <summary>Move/place an existing stack at a specific spot (drag-drop). </summary>
        public bool TryPlace(ItemStack stack, int x, int y, bool rotated)
        {
            if (!CanPlace(stack.item, x, y, rotated, stack)) return false;
            if (_stacks.Contains(stack)) ClearCells(stack);
            stack.x = x;
            stack.y = y;
            stack.rotated = rotated;
            if (!_stacks.Contains(stack)) _stacks.Add(stack);
            FillCells(stack);
            Changed?.Invoke();
            return true;
        }

        /// <summary>Merge into existing stacks then first-fit place. Returns leftover count.</summary>
        public int AutoAdd(ItemData item, int count)
        {
            if (item == null || count <= 0) return count;

            if (item.maxStack > 1)
            {
                foreach (var stack in _stacks)
                {
                    if (stack.item != item || stack.count >= item.maxStack) continue;
                    int take = Mathf.Min(item.maxStack - stack.count, count);
                    stack.count += take;
                    count -= take;
                    if (count <= 0) { Changed?.Invoke(); return 0; }
                }
            }

            bool placedAny = false;
            while (count > 0)
            {
                if (!FindFreeSpot(item, out int x, out int y, out bool rot)) break;
                var stack = new ItemStack
                {
                    item = item,
                    count = Mathf.Min(count, item.maxStack),
                    x = x, y = y, rotated = rot,
                };
                count -= stack.count;
                _stacks.Add(stack);
                FillCells(stack);
                placedAny = true;
            }

            if (placedAny) Changed?.Invoke();
            return count;
        }

        public bool CanFit(ItemData item, int count)
        {
            if (item.maxStack > 1)
            {
                foreach (var stack in _stacks)
                    if (stack.item == item && stack.count < item.maxStack)
                        count -= item.maxStack - stack.count;
                if (count <= 0) return true;
            }
            return FindFreeSpot(item, out _, out _, out _);
        }

        public void Remove(ItemStack stack)
        {
            if (!_stacks.Remove(stack)) return;
            ClearCells(stack);
            Changed?.Invoke();
        }

        /// <summary>Take up to <paramref name="count"/> from a stack; removes it when emptied.</summary>
        public int TakeFrom(ItemStack stack, int count)
        {
            int take = Mathf.Min(count, stack.count);
            stack.count -= take;
            if (stack.count <= 0) Remove(stack);
            else Changed?.Invoke();
            return take;
        }

        public void Clear()
        {
            _stacks.Clear();
            Array.Clear(_cells, 0, _cells.Length);
            Changed?.Invoke();
        }

        public float TotalWeight()
        {
            float w = 0f;
            foreach (var s in _stacks) w += s.item.weightKg * s.count;
            return w;
        }

        public void RaiseChanged() => Changed?.Invoke();

        private bool FindFreeSpot(ItemData item, out int x, out int y, out bool rotated)
        {
            for (int cy = 0; cy < Height; cy++)
                for (int cx = 0; cx < Width; cx++)
                {
                    if (CanPlace(item, cx, cy, false)) { x = cx; y = cy; rotated = false; return true; }
                    if (item.gridWidth != item.gridHeight && CanPlace(item, cx, cy, true)) { x = cx; y = cy; rotated = true; return true; }
                }
            x = y = 0;
            rotated = false;
            return false;
        }

        private void FillCells(ItemStack stack)
        {
            for (int cx = stack.x; cx < stack.x + stack.Width; cx++)
                for (int cy = stack.y; cy < stack.y + stack.Height; cy++)
                    _cells[cx, cy] = stack;
        }

        private void ClearCells(ItemStack stack)
        {
            for (int cx = 0; cx < Width; cx++)
                for (int cy = 0; cy < Height; cy++)
                    if (_cells[cx, cy] == stack)
                        _cells[cx, cy] = null;
        }
    }
}
