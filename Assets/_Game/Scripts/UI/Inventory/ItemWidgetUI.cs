using Game.Inventory;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// The draggable rectangle representing one <see cref="ItemStack"/> in the grid.
    /// All placement rules live in <see cref="InventoryGridUI"/>; this just forwards
    /// pointer events and paints itself.
    /// </summary>
    public class ItemWidgetUI : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler,
        IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        [SerializeField] private Image background;
        [SerializeField] private Image icon;
        [SerializeField] private TMP_Text countLabel;
        [SerializeField] private TMP_Text nameLabel;

        public ItemStack Stack { get; private set; }

        private InventoryGridUI _grid;
        private static readonly Color ValidTint = new(0.35f, 0.55f, 0.35f, 0.95f);
        private static readonly Color InvalidTint = new(0.6f, 0.25f, 0.25f, 0.95f);
        private Color _baseColor;

        public void Bind(InventoryGridUI grid, ItemStack stack, float cellSize)
        {
            _grid = grid;
            Stack = stack;

            var rt = (RectTransform)transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.sizeDelta = new Vector2(stack.Width * cellSize, stack.Height * cellSize);
            rt.anchoredPosition = new Vector2(stack.x * cellSize, -stack.y * cellSize);

            _baseColor = new Color(
                stack.item.iconTint.r * 0.45f + 0.08f,
                stack.item.iconTint.g * 0.45f + 0.08f,
                stack.item.iconTint.b * 0.45f + 0.08f, 0.95f);
            background.color = _baseColor;

            if (stack.item.icon != null)
            {
                icon.enabled = true;
                icon.sprite = stack.item.icon;
                nameLabel.text = "";
            }
            else
            {
                icon.enabled = false;
                nameLabel.text = stack.item.displayName;
            }
            countLabel.text = stack.count > 1 ? stack.count.ToString() : "";
        }

        public void SetDragTint(bool valid) => background.color = valid ? ValidTint : InvalidTint;
        public void ResetTint() => background.color = _baseColor;

        public void OnPointerEnter(PointerEventData e) => _grid.OnWidgetHover(this, true);
        public void OnPointerExit(PointerEventData e) => _grid.OnWidgetHover(this, false);
        public void OnPointerClick(PointerEventData e)
        {
            if (e.button == PointerEventData.InputButton.Left && !e.dragging)
                _grid.OnWidgetClick(this);
        }

        public void OnBeginDrag(PointerEventData e) => _grid.OnWidgetBeginDrag(this, e);
        public void OnDrag(PointerEventData e) => _grid.OnWidgetDrag(this, e);
        public void OnEndDrag(PointerEventData e) => _grid.OnWidgetEndDrag(this, e);
    }
}
