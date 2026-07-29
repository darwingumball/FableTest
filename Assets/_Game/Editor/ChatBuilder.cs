using System.IO;
using Game.Net;
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
    /// Chat window into WorldUI, ChatRelay + VoiceChatService into World, and the Chat /
    /// Voice / ChatChannel actions into the input map.
    /// Menu: Game/Setup/Build Chat. Idempotent (regenerates the chat subtree).
    /// </summary>
    public static class ChatBuilder
    {
        private const string WORLD_UI_PREFAB = "Assets/_Game/UI/WorldUI.prefab";
        private const string WORLD_SCENE = "Assets/_Game/Scenes/World.unity";
        private const string INPUT_ACTIONS = "Assets/_Game/Settings/GameInputActions.inputactions";

        private static readonly Color PanelBg = new(0.02f, 0.02f, 0.03f, 0.55f);
        private static readonly Color FieldBg = new(0.10f, 0.10f, 0.12f, 0.95f);
        private static readonly Color TextCol = new(0.86f, 0.88f, 0.84f, 1f);

        [MenuItem("Game/Setup/Build Chat")]
        public static void Build()
        {
            PatchInputActions();
            PatchScene();
            PatchWorldUi();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[ChatBuilder] Chat built. Enter = type, Tab = switch channel, V = push to talk.");
        }

        /// <summary>
        /// Adds the actions if they are missing, then writes the asset back as JSON.
        /// Editing the .inputactions text by hand is how binding ids get duplicated, so the
        /// asset object is the one doing the authoring here.
        /// </summary>
        private static void PatchInputActions()
        {
            var asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(INPUT_ACTIONS);
            if (asset == null)
            {
                Debug.LogError("[ChatBuilder] Input actions asset not found.");
                return;
            }

            var map = asset.FindActionMap("Gameplay");
            if (map == null)
            {
                Debug.LogError("[ChatBuilder] 'Gameplay' action map not found.");
                return;
            }

            bool changed = false;
            changed |= EnsureAction(map, "Chat", "<Keyboard>/enter");
            // NOT tab - TabMenu already owns that, and a double-bound key would open the
            // inventory every time you switched channel.
            changed |= EnsureAction(map, "ChatChannel", "<Keyboard>/y");
            changed |= EnsureAction(map, "Voice", "<Keyboard>/v");
            if (!changed) return;

            File.WriteAllText(INPUT_ACTIONS, asset.ToJson());
            AssetDatabase.ImportAsset(INPUT_ACTIONS, ImportAssetOptions.ForceUpdate);
        }

        /// <summary>
        /// Adds the action, or corrects it if a previous run bound the wrong key. Only
        /// adding when missing would mean a bad binding, once written to the asset, could
        /// never be fixed by re-running the builder.
        /// </summary>
        private static bool EnsureAction(InputActionMap map, string name, string binding)
        {
            var existing = map.FindAction(name);
            if (existing != null)
            {
                if (existing.bindings.Count == 1 && existing.bindings[0].path == binding)
                    return false;

                // Remove and re-add rather than ChangeBinding().To(). A fresh InputBinding
                // carries no action name, so assigning one over an existing binding orphans
                // it - the map then throws on the next FindAction(null) while rebuilding
                // its lookup arrays, which corrupts the whole asset.
                map.Disable();
                existing.RemoveAction();
            }
            else
            {
                // The map must be disabled to be modified; a live map throws.
                map.Disable();
            }

            var action = map.AddAction(name, InputActionType.Button);
            action.AddBinding(binding, groups: "Keyboard&Mouse");
            return true;
        }

        private static void PatchScene()
        {
            var scene = EditorSceneManager.OpenScene(WORLD_SCENE, OpenSceneMode.Additive);

            GameObject relay = null, voice = null;
            foreach (var go in scene.GetRootGameObjects())
            {
                if (go.name == "ChatRelay") relay = go;
                if (go.name == "VoiceChatService") voice = go;
            }

            if (relay == null)
            {
                relay = new GameObject("ChatRelay");
                SceneManager.MoveGameObjectToScene(relay, scene);
            }
            if (relay.GetComponent<NetworkObject>() == null) relay.AddComponent<NetworkObject>();
            if (relay.GetComponent<ChatRelay>() == null) relay.AddComponent<ChatRelay>();

            // Voice is NOT a NetworkObject - Vivox carries its own audio, so this only has
            // to exist locally. It lives in World rather than Boot so leaving a session
            // tears it down with the scene.
            if (voice == null)
            {
                voice = new GameObject("VoiceChatService");
                SceneManager.MoveGameObjectToScene(voice, scene);
            }
            if (voice.GetComponent<VoiceChatService>() == null) voice.AddComponent<VoiceChatService>();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, WORLD_SCENE);
            EditorSceneManager.CloseScene(scene, true);
        }

        private static void PatchWorldUi()
        {
            var root = PrefabUtility.LoadPrefabContents(WORLD_UI_PREFAB);
            try
            {
                var old = root.transform.Find("ChatRoot");
                if (old != null) Object.DestroyImmediate(old.gameObject);
                var oldComp = root.GetComponent<ChatUI>();
                if (oldComp != null) Object.DestroyImmediate(oldComp);

                // Bottom-left, above the health bar, clear of the crosshair.
                var chatRoot = new GameObject("ChatRoot", typeof(Image), typeof(CanvasGroup));
                chatRoot.transform.SetParent(root.transform, false);
                chatRoot.GetComponent<Image>().color = PanelBg;
                var rt = chatRoot.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0, 0);
                rt.anchorMax = new Vector2(0, 0);
                rt.pivot = new Vector2(0, 0);
                rt.anchoredPosition = new Vector2(24, 110);
                rt.sizeDelta = new Vector2(720, 250);

                var logGO = new GameObject("Log", typeof(TextMeshProUGUI));
                logGO.transform.SetParent(chatRoot.transform, false);
                var logText = logGO.GetComponent<TextMeshProUGUI>();
                logText.fontSize = 16;
                logText.color = TextCol;
                logText.richText = true;
                logText.alignment = TextAlignmentOptions.BottomLeft;
                logText.overflowMode = TextOverflowModes.Truncate;
                var logRt = logText.rectTransform;
                logRt.anchorMin = Vector2.zero;
                logRt.anchorMax = Vector2.one;
                logRt.offsetMin = new Vector2(12, 44);
                logRt.offsetMax = new Vector2(-12, -10);

                // Voice indicator sits top-right of the panel so it is visible without
                // reading the log.
                var voiceGO = new GameObject("VoiceState", typeof(TextMeshProUGUI));
                voiceGO.transform.SetParent(chatRoot.transform, false);
                var voiceText = voiceGO.GetComponent<TextMeshProUGUI>();
                voiceText.fontSize = 15;
                voiceText.alignment = TextAlignmentOptions.TopRight;
                voiceText.text = string.Empty;
                var voiceRt = voiceText.rectTransform;
                voiceRt.anchorMin = new Vector2(1, 1);
                voiceRt.anchorMax = new Vector2(1, 1);
                voiceRt.pivot = new Vector2(1, 1);
                voiceRt.anchoredPosition = new Vector2(-12, -6);
                voiceRt.sizeDelta = new Vector2(220, 24);

                // Input row: hidden until Enter is pressed.
                var inputRow = new GameObject("InputRow", typeof(RectTransform));
                inputRow.transform.SetParent(chatRoot.transform, false);
                var rowRt = (RectTransform)inputRow.transform;
                rowRt.anchorMin = new Vector2(0, 0);
                rowRt.anchorMax = new Vector2(1, 0);
                rowRt.pivot = new Vector2(0.5f, 0);
                rowRt.anchoredPosition = new Vector2(0, 6);
                rowRt.sizeDelta = new Vector2(-24, 34);

                var chanGO = new GameObject("Channel", typeof(TextMeshProUGUI));
                chanGO.transform.SetParent(inputRow.transform, false);
                var chanText = chanGO.GetComponent<TextMeshProUGUI>();
                chanText.fontSize = 15;
                chanText.alignment = TextAlignmentOptions.MidlineLeft;
                var chanRt = chanText.rectTransform;
                chanRt.anchorMin = new Vector2(0, 0);
                chanRt.anchorMax = new Vector2(0, 1);
                chanRt.pivot = new Vector2(0, 0.5f);
                chanRt.sizeDelta = new Vector2(90, 0);

                var inputGO = TMP_DefaultControls.CreateInputField(new TMP_DefaultControls.Resources());
                inputGO.name = "Input";
                inputGO.transform.SetParent(inputRow.transform, false);
                var inRt = (RectTransform)inputGO.transform;
                inRt.anchorMin = new Vector2(0, 0);
                inRt.anchorMax = new Vector2(1, 1);
                inRt.offsetMin = new Vector2(94, 0);
                inRt.offsetMax = Vector2.zero;
                foreach (var img in inputGO.GetComponentsInChildren<Image>(true)) img.color = FieldBg;
                foreach (var t in inputGO.GetComponentsInChildren<TMP_Text>(true))
                {
                    t.color = TextCol;
                    t.fontSize = 16;
                }
                var input = inputGO.GetComponent<TMP_InputField>();
                if (input.placeholder is TMP_Text ph) ph.text = "say something, or /command...";
                input.lineType = TMP_InputField.LineType.SingleLine;

                var chat = root.AddComponent<ChatUI>();
                var so = new SerializedObject(chat);
                so.FindProperty("panelRoot").objectReferenceValue = chatRoot;
                so.FindProperty("inputRow").objectReferenceValue = inputRow;
                so.FindProperty("logText").objectReferenceValue = logText;
                so.FindProperty("channelLabel").objectReferenceValue = chanText;
                so.FindProperty("voiceLabel").objectReferenceValue = voiceText;
                so.FindProperty("inputField").objectReferenceValue = input;
                so.FindProperty("inputActions").objectReferenceValue =
                    AssetDatabase.LoadAssetAtPath<InputActionAsset>(INPUT_ACTIONS);
                so.ApplyModifiedPropertiesWithoutUndo();

                inputRow.SetActive(false);
                PrefabUtility.SaveAsPrefabAsset(root, WORLD_UI_PREFAB);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }
}
