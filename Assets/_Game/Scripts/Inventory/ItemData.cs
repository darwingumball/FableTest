using UnityEngine;

namespace Game.Inventory
{
    /// <summary>
    /// One item type. Assets live in _Game/Resources/Items so both peers can resolve
    /// items by <see cref="itemId"/> over the network and in save files.
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Item", fileName = "item_new")]
    public class ItemData : ScriptableObject
    {
        [Tooltip("Stable id used in saves and RPCs. Defaults to the asset name.")]
        public string itemId;

        public string displayName = "Item";
        [TextArea] public string description = "";
        [Tooltip("Shown line-by-line in the inventory info panel.")]
        [TextArea] public string statsText = "";

        [Header("Grid")]
        [Min(1)] public int gridWidth = 1;
        [Min(1)] public int gridHeight = 1;
        [Min(1)] public int maxStack = 1;
        public float weightKg = 1f;

        [Header("Visuals")]
        public Sprite icon;
        [Tooltip("Tint for the placeholder icon block when no sprite is assigned.")]
        public Color iconTint = new(0.6f, 0.6f, 0.6f, 1f);
        [Tooltip("Networked physics prefab spawned when the item exists in the world.")]
        public GameObject worldPrefab;
        [Tooltip("Visual-only prefab for the 3D inventory preview. Falls back to worldPrefab.")]
        public GameObject previewPrefab;

        public string Id => string.IsNullOrEmpty(itemId) ? name : itemId;
        public GameObject PreviewPrefab => previewPrefab != null ? previewPrefab : worldPrefab;

        private void OnValidate()
        {
            if (string.IsNullOrEmpty(itemId)) itemId = name;
        }
    }
}
