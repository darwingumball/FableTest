using Game.UI;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.Editor
{
    /// <summary>
    /// Patches the WorldUI prefab with the quest toast stack (top-right HUD) and the live
    /// map tab, plus the map camera in the World scene.
    /// Menu: Game/Setup/Build Map And Toasts. Idempotent; does not regenerate WorldUI.
    /// </summary>
    public static class MapAndToastBuilder
    {
        private const string WORLD_UI_PREFAB = "Assets/_Game/UI/WorldUI.prefab";
        private const string WORLD_SCENE = "Assets/_Game/Scenes/World.unity";
        private const string MAP_CAMERA_NAME = "MapCamera";

        private static readonly Color TextCol = new(0.85f, 0.85f, 0.80f, 1f);

        [MenuItem("Game/Setup/Build Map And Toasts")]
        public static void Build()
        {
            var mapCamera = BuildMapCameraInScene();
            PatchWorldUi(mapCamera);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[MapAndToastBuilder] Quest toasts + map built.");
        }

        /// <summary>
        /// The map camera lives in the scene (it must see scene geometry). The UI prefab
        /// instance in the scene gets its reference re-linked here.
        /// </summary>
        private static Camera BuildMapCameraInScene()
        {
            var scene = EditorSceneManager.OpenScene(WORLD_SCENE, OpenSceneMode.Additive);
            GameObject camGO = null;
            foreach (var go in scene.GetRootGameObjects())
                if (go.name == MAP_CAMERA_NAME) camGO = go;
            if (camGO == null)
            {
                camGO = new GameObject(MAP_CAMERA_NAME);
                SceneManager.MoveGameObjectToScene(camGO, scene);
            }

            var cam = camGO.GetComponent<Camera>();
            if (cam == null) cam = camGO.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 60f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 300f;
            cam.enabled = false;
            camGO.transform.SetPositionAndRotation(new Vector3(0f, 80f, 0f), Quaternion.Euler(90f, 0f, 0f));

            var hd = camGO.GetComponent<HDAdditionalCameraData>();
            if (hd == null) hd = camGO.AddComponent<HDAdditionalCameraData>();
            hd.volumeLayerMask = 0;          // no fog/exposure volumes on the map
            hd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
            hd.backgroundColorHDR = new Color(0.04f, 0.04f, 0.05f, 1f);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, WORLD_SCENE);

            // Re-link the scene's WorldUI instance to this camera after the prefab is saved.
            EditorSceneManager.CloseScene(scene, true);
            return cam;
        }

        private static void PatchWorldUi(Camera sceneMapCamera)
        {
            var root = PrefabUtility.LoadPrefabContents(WORLD_UI_PREFAB);
            try
            {
                BuildToasts(root);
                BuildMap(root);
                PrefabUtility.SaveAsPrefabAsset(root, WORLD_UI_PREFAB);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            LinkSceneInstance(sceneMapCamera);
        }

        private static void BuildToasts(GameObject root)
        {
            var hud = root.transform.Find("HUD");
            if (hud == null) { Debug.LogError("[MapAndToastBuilder] HUD missing."); return; }

            var old = hud.Find("QuestToasts");
            if (old != null) Object.DestroyImmediate(old.gameObject);

            var stack = new GameObject("QuestToasts",
                typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            stack.transform.SetParent(hud, false);
            // Without a size fitter the container stays 0-high and toasts draw on top of
            // each other instead of stacking.
            stack.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var rt = stack.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(1, 1);
            rt.anchoredPosition = new Vector2(-40, -76); // below the clock
            rt.sizeDelta = new Vector2(420, 0);
            var layout = stack.GetComponent<VerticalLayoutGroup>();
            layout.childAlignment = TextAnchor.UpperRight;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.spacing = 6;

            // Template toast (kept inactive; cloned at runtime).
            var toastGO = new GameObject("ToastTemplate", typeof(TextMeshProUGUI), typeof(LayoutElement));
            toastGO.transform.SetParent(stack.transform, false);
            var toast = toastGO.GetComponent<TextMeshProUGUI>();
            toast.fontSize = 21;
            toast.color = TextCol;
            toast.alignment = TextAlignmentOptions.TopRight;
            toast.raycastTarget = false;
            toast.textWrappingMode = TextWrappingModes.Normal;
            toastGO.GetComponent<LayoutElement>().preferredHeight = 56;
            toastGO.SetActive(false);

            var toastUi = hud.GetComponent<QuestToastUI>();
            if (toastUi == null) toastUi = hud.gameObject.AddComponent<QuestToastUI>();
            var so = new SerializedObject(toastUi);
            so.FindProperty("container").objectReferenceValue = rt;
            so.FindProperty("toastPrefab").objectReferenceValue = toast;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void BuildMap(GameObject root)
        {
            var panel = root.transform.Find("TabMenuRoot/Window/Content/MapPanel");
            if (panel == null)
            {
                Debug.LogError("[MapAndToastBuilder] MapPanel missing - run Build Inventory UI first.");
                return;
            }

            var placeholder = panel.Find("Placeholder");
            if (placeholder != null) Object.DestroyImmediate(placeholder.gameObject);
            var oldView = panel.Find("MapView");
            if (oldView != null) Object.DestroyImmediate(oldView.gameObject);
            var oldComp = panel.GetComponent<MapView>();
            if (oldComp != null) Object.DestroyImmediate(oldComp);

            var view = new GameObject("MapView", typeof(RectTransform));
            view.transform.SetParent(panel, false);
            var viewRt = view.GetComponent<RectTransform>();
            viewRt.anchorMin = new Vector2(0.5f, 0.5f);
            viewRt.anchorMax = new Vector2(0.5f, 0.5f);
            viewRt.pivot = new Vector2(0.5f, 0.5f);
            viewRt.sizeDelta = new Vector2(660, 660);

            var imageGO = new GameObject("MapImage", typeof(RawImage));
            imageGO.transform.SetParent(view.transform, false);
            var image = imageGO.GetComponent<RawImage>();
            image.raycastTarget = false;
            var imgRt = image.rectTransform;
            imgRt.anchorMin = Vector2.zero;
            imgRt.anchorMax = Vector2.one;
            imgRt.offsetMin = Vector2.zero;
            imgRt.offsetMax = Vector2.zero;

            var iconLayer = new GameObject("Icons", typeof(RectTransform));
            iconLayer.transform.SetParent(view.transform, false);
            var iconRt = iconLayer.GetComponent<RectTransform>();
            iconRt.anchorMin = iconRt.anchorMax = new Vector2(0.5f, 0.5f);
            iconRt.pivot = new Vector2(0.5f, 0.5f);
            iconRt.sizeDelta = Vector2.zero;

            var iconGO = new GameObject("IconTemplate", typeof(TextMeshProUGUI));
            iconGO.transform.SetParent(iconLayer.transform, false);
            var icon = iconGO.GetComponent<TextMeshProUGUI>();
            icon.text = "▲";
            icon.fontSize = 26;
            icon.alignment = TextAlignmentOptions.Center;
            icon.raycastTarget = false;
            icon.rectTransform.sizeDelta = new Vector2(32, 32);
            iconGO.SetActive(false);

            var hint = new GameObject("Hint", typeof(TextMeshProUGUI));
            hint.transform.SetParent(panel, false);
            var hintText = hint.GetComponent<TextMeshProUGUI>();
            hintText.text = "green = you    amber = crew";
            hintText.fontSize = 16;
            hintText.color = new Color(0.55f, 0.55f, 0.52f, 1f);
            hintText.alignment = TextAlignmentOptions.Center;
            var hintRt = hintText.rectTransform;
            hintRt.anchorMin = new Vector2(0.5f, 0);
            hintRt.anchorMax = new Vector2(0.5f, 0);
            hintRt.pivot = new Vector2(0.5f, 0);
            hintRt.anchoredPosition = new Vector2(0, 18);
            hintRt.sizeDelta = new Vector2(600, 24);

            var mapView = panel.gameObject.AddComponent<MapView>();
            var so = new SerializedObject(mapView);
            so.FindProperty("mapImage").objectReferenceValue = image;
            so.FindProperty("iconLayer").objectReferenceValue = iconRt;
            so.FindProperty("iconPrefab").objectReferenceValue = icon;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// mapCamera is a scene object, so the reference can only live on the scene's
        /// WorldUI instance (prefabs cannot reference scene objects).
        /// </summary>
        private static void LinkSceneInstance(Camera sceneMapCamera)
        {
            var scene = EditorSceneManager.OpenScene(WORLD_SCENE, OpenSceneMode.Additive);
            Camera cam = null;
            foreach (var go in scene.GetRootGameObjects())
                if (go.name == MAP_CAMERA_NAME) cam = go.GetComponent<Camera>();

            foreach (var go in scene.GetRootGameObjects())
            {
                if (go.name != "WorldUI") continue;
                var view = go.GetComponentInChildren<MapView>(true);
                if (view == null || cam == null) continue;
                var so = new SerializedObject(view);
                so.FindProperty("mapCamera").objectReferenceValue = cam;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(go);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, WORLD_SCENE);
            EditorSceneManager.CloseScene(scene, true);
        }
    }
}
