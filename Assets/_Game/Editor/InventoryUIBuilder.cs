using Game.Inventory;
using Game.UI;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.UI;

namespace Game.Editor
{
    /// <summary>
    /// Builds the inventory-related UI: ItemWidget prefab, ItemPreviewStage prefab
    /// (offscreen 3D render rig), and the Tab menu (Inventory/Quests/Map) patched into
    /// the WorldUI prefab. Idempotent by regeneration of the TabMenu subtree.
    ///
    /// Menu: Game/Setup/Build Inventory UI
    /// </summary>
    public static class InventoryUIBuilder
    {
        private const string UI_FOLDER = "Assets/_Game/UI";
        private const string WORLD_UI_PREFAB = UI_FOLDER + "/WorldUI.prefab";
        private const string ITEM_WIDGET_PREFAB = UI_FOLDER + "/ItemWidget.prefab";
        private const string PREVIEW_STAGE_PREFAB = "Assets/_Game/Prefabs/ItemPreviewStage.prefab";
        private const string INPUT_ACTIONS_PATH = "Assets/_Game/Settings/GameInputActions.inputactions";

        private static readonly Color PanelBg = new(0.075f, 0.075f, 0.090f, 1f);
        private static readonly Color WindowBg = new(0.045f, 0.045f, 0.055f, 0.99f);
        private static readonly Color CellBg = new(0.11f, 0.11f, 0.13f, 1f);
        private static readonly Color ControlBg = new(0.125f, 0.125f, 0.150f, 1f);
        private static readonly Color TextCol = new(0.85f, 0.85f, 0.80f, 1f);
        private static readonly Color DimText = new(0.55f, 0.55f, 0.52f, 1f);

        [MenuItem("Game/Setup/Build Inventory UI")]
        public static void Build()
        {
            var widgetPrefab = BuildItemWidgetPrefab();
            var stagePrefab = BuildPreviewStagePrefab();
            PatchWorldUi(widgetPrefab, stagePrefab);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[InventoryUIBuilder] Inventory UI built into WorldUI prefab.");
        }

        // ---------------- item widget ----------------

        private static GameObject BuildItemWidgetPrefab()
        {
            var root = new GameObject("ItemWidget", typeof(Image));
            root.GetComponent<Image>().color = new Color(0.3f, 0.3f, 0.3f, 0.95f);

            var iconGO = new GameObject("Icon", typeof(Image));
            iconGO.transform.SetParent(root.transform, false);
            var iconRt = iconGO.GetComponent<RectTransform>();
            iconRt.anchorMin = Vector2.zero;
            iconRt.anchorMax = Vector2.one;
            iconRt.offsetMin = new Vector2(6, 6);
            iconRt.offsetMax = new Vector2(-6, -6);
            iconGO.GetComponent<Image>().raycastTarget = false;

            var nameLabel = MakeText(root.transform, "Name", "", 14, TextAlignmentOptions.Center);
            Stretch(nameLabel.rectTransform);
            nameLabel.raycastTarget = false;
            nameLabel.textWrappingMode = TextWrappingModes.Normal;

            var countLabel = MakeText(root.transform, "Count", "", 16, TextAlignmentOptions.BottomRight);
            Stretch(countLabel.rectTransform);
            countLabel.rectTransform.offsetMin = new Vector2(4, 2);
            countLabel.rectTransform.offsetMax = new Vector2(-6, -2);
            countLabel.raycastTarget = false;

            var widget = root.AddComponent<ItemWidgetUI>();
            Wire(widget, "background", root.GetComponent<Image>());
            Wire(widget, "icon", iconGO.GetComponent<Image>());
            Wire(widget, "countLabel", countLabel);
            Wire(widget, "nameLabel", nameLabel);

            var saved = PrefabUtility.SaveAsPrefabAsset(root, ITEM_WIDGET_PREFAB);
            Object.DestroyImmediate(root);
            return saved;
        }

        // ---------------- preview stage ----------------

        private static GameObject BuildPreviewStagePrefab()
        {
            int layer = ItemsBuilder.EnsureLayer(ItemsBuilder.PREVIEW_LAYER);

            var root = new GameObject("ItemPreviewStage");
            root.layer = layer;

            var anchor = new GameObject("Anchor");
            anchor.transform.SetParent(root.transform, false);
            anchor.layer = layer;

            var camGO = new GameObject("StageCamera");
            camGO.transform.SetParent(root.transform, false);
            camGO.layer = layer;
            var cam = camGO.AddComponent<Camera>();
            cam.fieldOfView = 30f;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 50f;
            cam.cullingMask = 1 << layer;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.05f, 0.05f, 0.06f, 1f);
            var hd = camGO.AddComponent<HDAdditionalCameraData>();
            hd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
            hd.backgroundColorHDR = new Color(0.05f, 0.05f, 0.06f, 1f);
            hd.volumeLayerMask = 0; // no scene volumes (fog/exposure) on the preview

            var lightGO = new GameObject("KeyLight");
            lightGO.transform.SetParent(root.transform, false);
            lightGO.transform.localPosition = new Vector3(1.5f, 2f, -1.5f);
            lightGO.transform.rotation = Quaternion.Euler(40f, 35f, 0f);
            lightGO.layer = layer;
            var light = lightGO.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = 12f;
            light.intensity = 800f; // lumens (HDRP photometric)
            lightGO.AddComponent<HDAdditionalLightData>();
            light.cullingMask = 1 << layer;

            var stage = root.AddComponent<ItemPreviewStage>();
            Wire(stage, "stageCamera", cam);
            Wire(stage, "itemAnchor", anchor.transform);

            var saved = PrefabUtility.SaveAsPrefabAsset(root, PREVIEW_STAGE_PREFAB);
            Object.DestroyImmediate(root);
            return saved;
        }

        // ---------------- tab menu into WorldUI ----------------

        private static void PatchWorldUi(GameObject widgetPrefab, GameObject stagePrefab)
        {
            var root = PrefabUtility.LoadPrefabContents(WORLD_UI_PREFAB);
            try
            {
                var old = root.transform.Find("TabMenuRoot");
                if (old != null) Object.DestroyImmediate(old.gameObject);
                var oldComp = root.GetComponent<TabMenuUI>();
                if (oldComp != null) Object.DestroyImmediate(oldComp);

                var actions = AssetDatabase.LoadAssetAtPath<InputActionAsset>(INPUT_ACTIONS_PATH);

                // Screen root
                var tabRoot = new GameObject("TabMenuRoot", typeof(Image));
                tabRoot.transform.SetParent(root.transform, false);
                tabRoot.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.6f);
                Stretch(tabRoot.GetComponent<RectTransform>());
                // Keep above HUD, below pause: insert before PausePanel.
                var pause = root.transform.Find("PausePanel");
                if (pause != null) tabRoot.transform.SetSiblingIndex(pause.GetSiblingIndex());

                var window = new GameObject("Window", typeof(Image));
                window.transform.SetParent(tabRoot.transform, false);
                window.GetComponent<Image>().color = WindowBg;
                var winRt = window.GetComponent<RectTransform>();
                winRt.sizeDelta = new Vector2(1360, 760);

                // Tab bar
                var tabBar = new GameObject("TabBar", typeof(RectTransform), typeof(HorizontalLayoutGroup));
                tabBar.transform.SetParent(window.transform, false);
                var barRt = tabBar.GetComponent<RectTransform>();
                barRt.anchorMin = new Vector2(0, 1);
                barRt.anchorMax = new Vector2(1, 1);
                barRt.pivot = new Vector2(0.5f, 1);
                barRt.sizeDelta = new Vector2(0, 52);
                var barLayout = tabBar.GetComponent<HorizontalLayoutGroup>();
                barLayout.childControlWidth = true;
                barLayout.childControlHeight = true;
                barLayout.childForceExpandWidth = true;
                barLayout.spacing = 4;
                barLayout.padding = new RectOffset(8, 8, 6, 6);

                var invTab = MakeButton(tabBar.transform, "InventoryTab", "Inventory");
                var questTab = MakeButton(tabBar.transform, "QuestsTab", "Quests");
                var mapTab = MakeButton(tabBar.transform, "MapTab", "Map");

                // Content area
                var content = new GameObject("Content", typeof(RectTransform));
                content.transform.SetParent(window.transform, false);
                var contentRt = content.GetComponent<RectTransform>();
                Stretch(contentRt);
                contentRt.offsetMax = new Vector2(0, -56);

                var inventoryPanel = BuildInventoryPanel(content.transform, widgetPrefab, stagePrefab);
                var questsPanel = MakePlaceholderPanel(content.transform, "QuestsPanel", "Quest log arrives in Phase 7.");
                var mapPanel = MakePlaceholderPanel(content.transform, "MapPanel", "Map arrives later.");

                // Drag layer on very top of the window
                var dragLayer = new GameObject("DragLayer", typeof(RectTransform));
                dragLayer.transform.SetParent(tabRoot.transform, false);
                Stretch(dragLayer.GetComponent<RectTransform>());

                var gridUi = inventoryPanel.GetComponent<InventoryGridUI>();
                Wire(gridUi, "dragLayer", dragLayer.GetComponent<RectTransform>());

                var tabMenu = root.AddComponent<TabMenuUI>();
                Wire(tabMenu, "panelRoot", tabRoot);
                Wire(tabMenu, "inventoryTabButton", invTab);
                Wire(tabMenu, "questsTabButton", questTab);
                Wire(tabMenu, "mapTabButton", mapTab);
                Wire(tabMenu, "inventoryPanel", inventoryPanel);
                Wire(tabMenu, "questsPanel", questsPanel);
                Wire(tabMenu, "mapPanel", mapPanel);
                Wire(tabMenu, "inputActions", actions);

                tabRoot.SetActive(false);
                PrefabUtility.SaveAsPrefabAsset(root, WORLD_UI_PREFAB);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static GameObject BuildInventoryPanel(Transform parent, GameObject widgetPrefab, GameObject stagePrefab)
        {
            var panel = new GameObject("InventoryPanel", typeof(RectTransform));
            panel.transform.SetParent(parent, false);
            Stretch(panel.GetComponent<RectTransform>());

            // --- left: grid ---
            float gridW = PlayerInventory.GRID_WIDTH * InventoryGridUI.CELL_SIZE;   // 576
            float gridH = PlayerInventory.GRID_HEIGHT * InventoryGridUI.CELL_SIZE;  // 360

            var gridContainer = new GameObject("GridContainer", typeof(Image));
            gridContainer.transform.SetParent(panel.transform, false);
            gridContainer.GetComponent<Image>().color = PanelBg;
            var gridRt = gridContainer.GetComponent<RectTransform>();
            gridRt.anchorMin = gridRt.anchorMax = new Vector2(0, 1);
            gridRt.pivot = new Vector2(0, 1);
            gridRt.anchoredPosition = new Vector2(50, -60);
            gridRt.sizeDelta = new Vector2(gridW, gridH);

            for (int y = 0; y < PlayerInventory.GRID_HEIGHT; y++)
                for (int x = 0; x < PlayerInventory.GRID_WIDTH; x++)
                {
                    var cell = new GameObject($"Cell_{x}_{y}", typeof(Image));
                    cell.transform.SetParent(gridContainer.transform, false);
                    cell.GetComponent<Image>().color = CellBg;
                    cell.GetComponent<Image>().raycastTarget = false;
                    var cellRt = cell.GetComponent<RectTransform>();
                    cellRt.anchorMin = cellRt.anchorMax = new Vector2(0, 1);
                    cellRt.pivot = new Vector2(0, 1);
                    cellRt.anchoredPosition = new Vector2(x * InventoryGridUI.CELL_SIZE + 1, -(y * InventoryGridUI.CELL_SIZE + 1));
                    cellRt.sizeDelta = new Vector2(InventoryGridUI.CELL_SIZE - 2, InventoryGridUI.CELL_SIZE - 2);
                }

            var itemLayer = new GameObject("ItemLayer", typeof(RectTransform));
            itemLayer.transform.SetParent(gridContainer.transform, false);
            Stretch(itemLayer.GetComponent<RectTransform>());

            var weightLabel = MakeText(panel.transform, "WeightLabel", "0.0 kg", 18, TextAlignmentOptions.MidlineLeft);
            var wRt = weightLabel.rectTransform;
            wRt.anchorMin = wRt.anchorMax = new Vector2(0, 1);
            wRt.pivot = new Vector2(0, 1);
            wRt.anchoredPosition = new Vector2(50, -20);
            wRt.sizeDelta = new Vector2(gridW, 30);
            weightLabel.color = DimText;

            // --- right: info + preview ---
            var info = new GameObject("InfoPanel", typeof(Image));
            info.transform.SetParent(panel.transform, false);
            info.GetComponent<Image>().color = PanelBg;
            var infoRt = info.GetComponent<RectTransform>();
            infoRt.anchorMin = infoRt.anchorMax = new Vector2(1, 1);
            infoRt.pivot = new Vector2(1, 1);
            infoRt.anchoredPosition = new Vector2(-50, -20);
            infoRt.sizeDelta = new Vector2(560, 660);

            var preview = new GameObject("Preview", typeof(RawImage));
            preview.transform.SetParent(info.transform, false);
            var pvRt = preview.GetComponent<RectTransform>();
            pvRt.anchorMin = new Vector2(0.5f, 1);
            pvRt.anchorMax = new Vector2(0.5f, 1);
            pvRt.pivot = new Vector2(0.5f, 1);
            pvRt.anchoredPosition = new Vector2(0, -16);
            pvRt.sizeDelta = new Vector2(360, 360);
            preview.GetComponent<RawImage>().enabled = false;

            var infoName = MakeText(info.transform, "Name", "", 26, TextAlignmentOptions.Center);
            SetTop(infoName.rectTransform, -392, 34);
            var infoStats = MakeText(info.transform, "Stats", "", 18, TextAlignmentOptions.TopLeft);
            SetTop(infoStats.rectTransform, -436, 120);
            infoStats.rectTransform.offsetMin = new Vector2(28, infoStats.rectTransform.offsetMin.y);
            infoStats.rectTransform.offsetMax = new Vector2(-28, infoStats.rectTransform.offsetMax.y);
            infoStats.color = new Color(0.58f, 0.66f, 0.55f, 1f);
            var infoDesc = MakeText(info.transform, "Description", "", 17, TextAlignmentOptions.TopLeft);
            SetTop(infoDesc.rectTransform, -560, 60);
            infoDesc.rectTransform.offsetMin = new Vector2(28, infoDesc.rectTransform.offsetMin.y);
            infoDesc.rectTransform.offsetMax = new Vector2(-28, infoDesc.rectTransform.offsetMax.y);
            infoDesc.color = DimText;
            infoDesc.fontStyle = FontStyles.Italic;

            var dropBtn = MakeButton(info.transform, "DropButton", "Drop");
            var dropRt = (RectTransform)dropBtn.transform;
            dropRt.anchorMin = new Vector2(0.5f, 0);
            dropRt.anchorMax = new Vector2(0.5f, 0);
            dropRt.pivot = new Vector2(0.5f, 0);
            dropRt.anchoredPosition = new Vector2(0, 14);
            dropRt.sizeDelta = new Vector2(220, 44);

            var gridUi = panel.AddComponent<InventoryGridUI>();
            Wire(gridUi, "gridContainer", gridRt);
            Wire(gridUi, "itemLayer", itemLayer.GetComponent<RectTransform>());
            Wire(gridUi, "itemWidgetPrefab", widgetPrefab.GetComponent<ItemWidgetUI>());
            Wire(gridUi, "infoRoot", info);
            Wire(gridUi, "previewImage", preview.GetComponent<RawImage>());
            Wire(gridUi, "infoName", infoName);
            Wire(gridUi, "infoStats", infoStats);
            Wire(gridUi, "infoDescription", infoDesc);
            Wire(gridUi, "dropButton", dropBtn);
            Wire(gridUi, "weightLabel", weightLabel);
            Wire(gridUi, "previewStagePrefab", stagePrefab.GetComponent<ItemPreviewStage>());

            return panel;
        }

        private static GameObject MakePlaceholderPanel(Transform parent, string name, string text)
        {
            var panel = new GameObject(name, typeof(RectTransform));
            panel.transform.SetParent(parent, false);
            Stretch(panel.GetComponent<RectTransform>());
            var label = MakeText(panel.transform, "Placeholder", text, 22, TextAlignmentOptions.Center);
            Stretch(label.rectTransform);
            label.color = DimText;
            panel.SetActive(false);
            return panel;
        }

        // ---------------- helpers ----------------

        private static TextMeshProUGUI MakeText(Transform parent, string name, string text, float size, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.color = TextCol;
            tmp.alignment = align;
            return tmp;
        }

        private static Button MakeButton(Transform parent, string name, string label)
        {
            var go = new GameObject(name, typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = ControlBg;
            var text = MakeText(go.transform, "Label", label, 20, TextAlignmentOptions.Center);
            Stretch(text.rectTransform);
            text.raycastTarget = false;
            return go.GetComponent<Button>();
        }

        private static void SetTop(RectTransform rt, float y, float height)
        {
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(0.5f, 1);
            rt.anchoredPosition = new Vector2(0, y);
            rt.sizeDelta = new Vector2(0, height);
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static void Wire(Component target, string field, Object value)
        {
            var so = new SerializedObject(target);
            var prop = so.FindProperty(field);
            if (prop == null)
            {
                Debug.LogError($"[InventoryUIBuilder] {target.GetType().Name} missing field '{field}'.");
                return;
            }
            prop.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
