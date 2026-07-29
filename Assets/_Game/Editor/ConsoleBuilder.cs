using Game.Admin;
using Game.UI;
using TMPro;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.Editor
{
    /// <summary>
    /// Admin console UI into WorldUI + AdminService object into World.
    /// Menu: Game/Setup/Build Console. Idempotent (regenerates the console subtree).
    /// </summary>
    public static class ConsoleBuilder
    {
        private const string WORLD_UI_PREFAB = "Assets/_Game/UI/WorldUI.prefab";
        private const string WORLD_SCENE = "Assets/_Game/Scenes/World.unity";
        private const string INPUT_ACTIONS = "Assets/_Game/Settings/GameInputActions.inputactions";

        private static readonly Color PanelBg = new(0.03f, 0.03f, 0.04f, 0.94f);
        private static readonly Color FieldBg = new(0.10f, 0.10f, 0.12f, 1f);
        private static readonly Color TextCol = new(0.82f, 0.84f, 0.78f, 1f);

        [MenuItem("Game/Setup/Build Console")]
        public static void Build()
        {
            PatchScene();
            PatchWorldUi();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[ConsoleBuilder] Admin console built (backquote to open).");
        }

        private static void PatchScene()
        {
            var scene = EditorSceneManager.OpenScene(WORLD_SCENE, OpenSceneMode.Additive);
            GameObject admin = null;
            foreach (var go in scene.GetRootGameObjects())
                if (go.name == "AdminService") admin = go;
            if (admin == null)
            {
                admin = new GameObject("AdminService");
                SceneManager.MoveGameObjectToScene(admin, scene);
            }
            if (admin.GetComponent<NetworkObject>() == null) admin.AddComponent<NetworkObject>();
            if (admin.GetComponent<AdminService>() == null) admin.AddComponent<AdminService>();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, WORLD_SCENE);
            EditorSceneManager.CloseScene(scene, true);
        }

        private static void PatchWorldUi()
        {
            var root = PrefabUtility.LoadPrefabContents(WORLD_UI_PREFAB);
            try
            {
                var old = root.transform.Find("ConsoleRoot");
                if (old != null) Object.DestroyImmediate(old.gameObject);
                var oldComp = root.GetComponent<ConsoleUI>();
                if (oldComp != null) Object.DestroyImmediate(oldComp);

                var consoleRoot = new GameObject("ConsoleRoot", typeof(Image));
                consoleRoot.transform.SetParent(root.transform, false);
                consoleRoot.GetComponent<Image>().color = PanelBg;
                var rt = consoleRoot.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0, 1);
                rt.anchorMax = new Vector2(1, 1);
                rt.pivot = new Vector2(0.5f, 1);
                rt.sizeDelta = new Vector2(0, 460);

                // Output (scrolling text block, newest at bottom)
                var outGO = new GameObject("Output", typeof(TextMeshProUGUI));
                outGO.transform.SetParent(consoleRoot.transform, false);
                var outText = outGO.GetComponent<TextMeshProUGUI>();
                outText.fontSize = 17;
                outText.color = TextCol;
                outText.alignment = TextAlignmentOptions.BottomLeft;
                outText.overflowMode = TextOverflowModes.Truncate;
                var outRt = outText.rectTransform;
                outRt.anchorMin = Vector2.zero;
                outRt.anchorMax = Vector2.one;
                outRt.offsetMin = new Vector2(16, 54);
                outRt.offsetMax = new Vector2(-16, -12);

                // Input line
                var inputGO = TMP_DefaultControls.CreateInputField(new TMP_DefaultControls.Resources());
                inputGO.name = "Input";
                inputGO.transform.SetParent(consoleRoot.transform, false);
                var inRt = (RectTransform)inputGO.transform;
                inRt.anchorMin = new Vector2(0, 0);
                inRt.anchorMax = new Vector2(1, 0);
                inRt.pivot = new Vector2(0.5f, 0);
                inRt.anchoredPosition = new Vector2(0, 8);
                inRt.sizeDelta = new Vector2(-32, 38);
                foreach (var img in inputGO.GetComponentsInChildren<Image>(true)) img.color = FieldBg;
                foreach (var t in inputGO.GetComponentsInChildren<TMP_Text>(true))
                {
                    t.color = TextCol;
                    t.fontSize = 17;
                }
                var input = inputGO.GetComponent<TMP_InputField>();
                if (input.placeholder is TMP_Text ph) ph.text = "command...";
                input.lineType = TMP_InputField.LineType.SingleLine;

                var console = root.AddComponent<ConsoleUI>();
                var so = new SerializedObject(console);
                so.FindProperty("panelRoot").objectReferenceValue = consoleRoot;
                so.FindProperty("outputText").objectReferenceValue = outText;
                so.FindProperty("inputField").objectReferenceValue = input;
                so.FindProperty("inputActions").objectReferenceValue =
                    AssetDatabase.LoadAssetAtPath<InputActionAsset>(INPUT_ACTIONS);
                so.ApplyModifiedPropertiesWithoutUndo();

                consoleRoot.SetActive(false);
                PrefabUtility.SaveAsPrefabAsset(root, WORLD_UI_PREFAB);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }
}
