using System.Collections.Generic;
using Game.Inventory;
using Game.Net;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// The inventory tab: paints the local player's grid, handles drag/drop with R to
    /// rotate, dropping outside the grid throws the item into the world, hover shows the
    /// 3D preview, click locks it spinning with the info panel.
    /// </summary>
    public class InventoryGridUI : MonoBehaviour
    {
        public const float CELL_SIZE = 72f;

        [Header("Grid")]
        [SerializeField] private RectTransform gridContainer;
        [SerializeField] private RectTransform itemLayer;
        [SerializeField] private RectTransform dragLayer;
        [SerializeField] private ItemWidgetUI itemWidgetPrefab;

        [Header("Info panel")]
        [SerializeField] private GameObject infoRoot;
        [SerializeField] private RawImage previewImage;
        [SerializeField] private TMP_Text infoName;
        [SerializeField] private TMP_Text infoStats;
        [SerializeField] private TMP_Text infoDescription;
        [SerializeField] private Button dropButton;
        [SerializeField] private TMP_Text weightLabel;
        [SerializeField] private ItemPreviewStage previewStagePrefab;

        private ItemPreviewStage previewStage;
        private PlayerInventory _inventory;
        private readonly List<ItemWidgetUI> _widgets = new();

        private ItemWidgetUI _dragged;
        private bool _dragRotated;
        private ItemStack _selected;

        private void Awake()
        {
            dropButton.onClick.AddListener(DropSelected);
        }

        private void OnEnable()
        {
            if (previewStage == null && previewStagePrefab != null)
                previewStage = Instantiate(previewStagePrefab, new Vector3(0f, -500f, 0f), Quaternion.identity);
            BindInventory();
            ShowInfo(null, false);
            Rebuild();
        }

        private void OnDisable()
        {
            if (_inventory != null) _inventory.Grid.Changed -= Rebuild;
            _inventory = null;
            if (previewStage != null) previewStage.Hide();
        }

        private void BindInventory()
        {
            var local = NetworkPlayer.Local;
            _inventory = local != null ? local.GetComponent<PlayerInventory>() : null;
            if (_inventory != null)
                _inventory.Grid.Changed += Rebuild;
        }

        private void Rebuild()
        {
            foreach (var w in _widgets) if (w != null) Destroy(w.gameObject);
            _widgets.Clear();
            if (_inventory == null) return;

            foreach (var stack in _inventory.Grid.Stacks)
            {
                var widget = Instantiate(itemWidgetPrefab, itemLayer);
                widget.Bind(this, stack, CELL_SIZE);
                _widgets.Add(widget);
            }

            weightLabel.text = $"{_inventory.Grid.TotalWeight():0.0} kg";

            if (_selected != null && !((IList<ItemStack>)_inventory.Grid.Stacks).Contains(_selected))
                ShowInfo(null, false);
        }

        // ---------------- hover / click / info ----------------

        public void OnWidgetHover(ItemWidgetUI widget, bool entered)
        {
            if (_dragged != null) return;
            if (_selected != null) return; // a clicked selection owns the preview + info

            if (entered)
            {
                previewStage.Show(widget.Stack.item, spin: false);
                previewImage.texture = previewStage.Texture;
                previewImage.enabled = true;
            }
            else
            {
                previewStage.Hide();
                previewImage.enabled = false;
            }
        }

        public void OnWidgetClick(ItemWidgetUI widget)
        {
            // Clicking the selected stack again deselects it.
            ShowInfo(_selected == widget.Stack ? null : widget.Stack, true);
        }

        private void ShowInfo(ItemStack stack, bool spin)
        {
            _selected = stack;
            infoRoot.SetActive(stack != null);
            if (stack == null)
            {
                previewStage.Hide();
                previewImage.enabled = false;
                return;
            }
            infoName.text = stack.count > 1 ? $"{stack.item.displayName} x{stack.count}" : stack.item.displayName;
            infoStats.text = BuildStats(stack.item);
            infoDescription.text = stack.item.description;
            previewStage.Show(stack.item, spin);
            previewImage.texture = previewStage.Texture;
            previewImage.enabled = true;
        }

        private static string BuildStats(ItemData item)
        {
            string size = $"Size: {item.gridWidth}x{item.gridHeight}";
            string weight = $"Weight: {item.weightKg:0.0} kg";
            string stack = item.maxStack > 1 ? $"Stacks to {item.maxStack}" : null;
            var lines = new List<string> { size, weight };
            if (stack != null) lines.Add(stack);
            if (!string.IsNullOrEmpty(item.statsText)) lines.Add(item.statsText);
            return string.Join("\n", lines);
        }

        // ---------------- drag / drop ----------------

        public void OnWidgetBeginDrag(ItemWidgetUI widget, PointerEventData e)
        {
            _dragged = widget;
            _dragRotated = widget.Stack.rotated;
            widget.transform.SetParent(dragLayer, true);
        }

        public void OnWidgetDrag(ItemWidgetUI widget, PointerEventData e)
        {
            if (_dragged != widget) return;

            if (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame)
            {
                _dragRotated = !_dragRotated;
                var rt = (RectTransform)widget.transform;
                int w = _dragRotated ? widget.Stack.item.gridHeight : widget.Stack.item.gridWidth;
                int h = _dragRotated ? widget.Stack.item.gridWidth : widget.Stack.item.gridHeight;
                rt.sizeDelta = new Vector2(w * CELL_SIZE, h * CELL_SIZE);
            }

            var rect = (RectTransform)widget.transform;
            rect.position = e.position;

            bool valid = TryGetCell(e, out int cx, out int cy)
                && _inventory.Grid.CanPlace(widget.Stack.item, cx, cy, _dragRotated, widget.Stack);
            widget.SetDragTint(valid || !TryGetCell(e, out _, out _)); // outside grid = drop-to-world, shown as valid
        }

        public void OnWidgetEndDrag(ItemWidgetUI widget, PointerEventData e)
        {
            if (_dragged != widget) return;
            _dragged = null;
            widget.ResetTint();

            if (TryGetCell(e, out int cx, out int cy))
            {
                _inventory.Grid.TryPlace(widget.Stack, cx, cy, _dragRotated);
                Rebuild(); // always repaint (revert or move)
            }
            else
            {
                DropStackToWorld(widget.Stack);
            }
        }

        private bool TryGetCell(PointerEventData e, out int cx, out int cy)
        {
            cx = cy = 0;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    gridContainer, e.position, e.pressEventCamera, out var local))
                return false;
            // gridContainer pivot is top-left.
            cx = Mathf.FloorToInt(local.x / CELL_SIZE);
            cy = Mathf.FloorToInt(-local.y / CELL_SIZE);
            return cx >= 0 && cy >= 0 && cx < _inventory.Grid.Width && cy < _inventory.Grid.Height;
        }

        private void DropSelected()
        {
            if (_selected != null) DropStackToWorld(_selected);
        }

        private void DropStackToWorld(ItemStack stack)
        {
            var local = NetworkPlayer.Local;
            if (local == null || WorldItemManager.Instance == null || _inventory == null) { Rebuild(); return; }

            var cam = local.PlayerCamera != null ? local.PlayerCamera.transform : local.transform;
            Vector3 pos = cam.position + cam.forward * 1.2f;
            Vector3 impulse = cam.forward * 2.5f;

            string itemId = stack.item.Id;
            int count = stack.count;
            _inventory.Grid.Remove(stack);
            WorldItemManager.Instance.RequestDrop(itemId, count, pos, impulse);
            ShowInfo(null, false);
        }
    }
}
