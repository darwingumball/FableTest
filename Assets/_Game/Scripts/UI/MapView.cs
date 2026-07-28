using System.Collections.Generic;
using Game.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// Map tab: a top-down orthographic camera renders the world region to a RenderTexture,
    /// with arrow icons overlaid for every player showing position and facing. The camera
    /// only renders while the tab is open.
    ///
    /// Screen mapping: the camera looks straight down with world +Z as screen-up and +X as
    /// screen-right, so world XZ maps linearly onto the image and a player's yaw becomes a
    /// simple -yaw rotation of the icon.
    /// </summary>
    public class MapView : MonoBehaviour
    {
        [SerializeField] private Camera mapCamera;
        [SerializeField] private RawImage mapImage;
        [SerializeField] private RectTransform iconLayer;
        [SerializeField] private TMP_Text iconPrefab;

        [Header("World region covered by the map")]
        [SerializeField] private Vector2 worldCenter = Vector2.zero;
        [SerializeField] private float worldSize = 120f;
        [SerializeField] private float cameraHeight = 80f;

        private RenderTexture _rt;
        private readonly Dictionary<ulong, TMP_Text> _icons = new();

        private void Awake()
        {
            if (iconPrefab != null) iconPrefab.gameObject.SetActive(false);
            if (mapCamera == null)
            {
                // Scene-only reference; missing means the World scene wasn't re-linked
                // (see MapAndToastBuilder.LinkSceneInstance).
                Debug.LogWarning("[MapView] No map camera assigned - map will render empty.");
                return;
            }
            _rt = new RenderTexture(1024, 1024, 24, RenderTextureFormat.ARGB32) { name = "MapRT" };
            mapCamera.targetTexture = _rt;
            mapCamera.orthographic = true;
            mapCamera.enabled = false;
            if (mapImage != null) mapImage.texture = _rt;
        }

        private void OnDestroy()
        {
            if (_rt != null) _rt.Release();
        }

        private void OnEnable()
        {
            if (mapCamera == null) return;
            PositionCamera();
            mapCamera.enabled = true;
        }

        private void OnDisable()
        {
            if (mapCamera != null) mapCamera.enabled = false;
        }

        private void PositionCamera()
        {
            mapCamera.transform.SetPositionAndRotation(
                new Vector3(worldCenter.x, cameraHeight, worldCenter.y),
                Quaternion.Euler(90f, 0f, 0f));
            mapCamera.orthographicSize = worldSize * 0.5f;
            mapCamera.farClipPlane = cameraHeight + 200f;
        }

        private void LateUpdate()
        {
            if (mapImage == null || iconPrefab == null) return;
            var seen = new HashSet<ulong>();
            var mapRect = mapImage.rectTransform.rect.size;

            foreach (var player in FindObjectsByType<NetworkPlayer>(FindObjectsSortMode.None))
            {
                ulong id = player.OwnerClientId;
                seen.Add(id);

                if (!_icons.TryGetValue(id, out var icon) || icon == null)
                {
                    icon = Instantiate(iconPrefab, iconLayer);
                    icon.gameObject.SetActive(true);
                    bool isLocal = player == NetworkPlayer.Local;
                    icon.text = "▲"; // filled up-triangle; rotation shows facing
                    icon.color = isLocal
                        ? new Color(0.60f, 0.78f, 0.55f, 1f)   // local player: pale green
                        : new Color(0.85f, 0.72f, 0.35f, 1f);  // teammates: amber
                    _icons[id] = icon;
                }

                Vector3 pos = player.transform.position;
                float u = (pos.x - worldCenter.x) / worldSize; // -0.5 .. 0.5
                float v = (pos.z - worldCenter.y) / worldSize;
                var rt = (RectTransform)icon.transform;
                rt.anchoredPosition = new Vector2(u * mapRect.x, v * mapRect.y);
                rt.localEulerAngles = new Vector3(0f, 0f, -player.transform.eulerAngles.y);

                bool onMap = Mathf.Abs(u) <= 0.5f && Mathf.Abs(v) <= 0.5f;
                icon.enabled = onMap;
            }

            // Drop icons for players who left.
            if (_icons.Count == seen.Count) return;
            var stale = new List<ulong>();
            foreach (var kvp in _icons)
                if (!seen.Contains(kvp.Key)) stale.Add(kvp.Key);
            foreach (var id in stale)
            {
                if (_icons[id] != null) Destroy(_icons[id].gameObject);
                _icons.Remove(id);
            }
        }
    }
}
