using UnityEngine;

namespace Game.UI
{
    /// <summary>
    /// Offscreen 3D stage rendering inventory items to a RenderTexture. Hover shows the
    /// item; clicking makes it spin slowly. The camera only renders while visible.
    /// </summary>
    public class ItemPreviewStage : MonoBehaviour
    {
        [SerializeField] private Camera stageCamera;
        [SerializeField] private Transform itemAnchor;
        [SerializeField] private float spinDegreesPerSecond = 40f;
        [SerializeField] private float framingMargin = 1.35f;

        public RenderTexture Texture { get; private set; }

        private GameObject _current;
        private bool _spinning;

        private void Awake()
        {
            Texture = new RenderTexture(512, 512, 16, RenderTextureFormat.ARGB32) { name = "ItemPreviewRT" };
            stageCamera.targetTexture = Texture;
            stageCamera.enabled = false;
        }

        private void OnDestroy()
        {
            if (Texture != null) Texture.Release();
        }

        public void Show(Inventory.ItemData item, bool spin)
        {
            _spinning = spin;
            if (item == null || item.PreviewPrefab == null)
            {
                Hide();
                return;
            }

            Clear();
            _current = Instantiate(item.PreviewPrefab, itemAnchor);
            _current.transform.localPosition = Vector3.zero;
            _current.transform.localRotation = Quaternion.identity;
            StripToVisual(_current);
            SetLayerRecursively(_current, gameObject.layer);
            FrameCurrent();
            stageCamera.enabled = true;
        }

        public void SetSpinning(bool spin) => _spinning = spin;

        public void Hide()
        {
            Clear();
            stageCamera.enabled = false;
        }

        private void Update()
        {
            if (_spinning && _current != null)
                itemAnchor.Rotate(0f, spinDegreesPerSecond * Time.deltaTime, 0f, Space.World);
        }

        private void Clear()
        {
            if (_current != null) Destroy(_current);
            _current = null;
            itemAnchor.rotation = Quaternion.identity;
        }

        /// <summary>World prefabs carry physics/network components - previews only need renderers.</summary>
        private static void StripToVisual(GameObject go)
        {
            foreach (var c in go.GetComponentsInChildren<Component>(true))
            {
                if (c is Transform || c is Renderer || c is MeshFilter) continue;
                Destroy(c);
            }
        }

        private void FrameCurrent()
        {
            var renderers = _current.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return;
            var bounds = renderers[0].bounds;
            foreach (var r in renderers) bounds.Encapsulate(r.bounds);

            itemAnchor.position += itemAnchor.position - bounds.center; // center item on anchor
            float radius = Mathf.Max(bounds.extents.magnitude, 0.05f);
            float distance = radius * framingMargin / Mathf.Tan(stageCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            stageCamera.transform.position = itemAnchor.position
                + (Quaternion.Euler(20f, -30f, 0f) * Vector3.back) * distance;
            stageCamera.transform.LookAt(itemAnchor.position);
        }

        private static void SetLayerRecursively(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform)
                SetLayerRecursively(child.gameObject, layer);
        }
    }
}
