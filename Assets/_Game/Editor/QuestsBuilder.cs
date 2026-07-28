using Game.Interaction;
using Game.Net;
using Game.Quests;
using Game.UI;
using TMPro;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Editor
{
    /// <summary>
    /// Starter quest content + wiring: two quest assets (one shared, one personal),
    /// NetworkQuestSync scene object, a QuestBoard in the World, and the QuestLogUI in
    /// the WorldUI Quests tab. Menu: Game/Setup/Build Quests. Idempotent.
    /// </summary>
    public static class QuestsBuilder
    {
        private const string QUESTS_RES_FOLDER = "Assets/_Game/Resources/Quests";
        private const string WORLD_SCENE = "Assets/_Game/Scenes/World.unity";
        private const string WORLD_UI_PREFAB = "Assets/_Game/UI/WorldUI.prefab";

        [MenuItem("Game/Setup/Build Quests")]
        public static void Build()
        {
            EnsureFolder(QUESTS_RES_FOLDER);
            BuildQuestAssets();
            PatchWorldScene();
            PatchQuestsTab();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[QuestsBuilder] Quest content, sync object, board, and log UI built.");
        }

        private static void BuildQuestAssets()
        {
            var shared = LoadOrCreate("first_supplies");
            shared.questId = "first_supplies";
            shared.title = "First Supplies";
            shared.description = "The crew needs food before nightfall. Scrounge up canned rations.";
            shared.sharedProgress = true;
            shared.availableFromStart = true;
            shared.objectives = new[]
            {
                new QuestData.Objective
                {
                    description = "Collect canned rations", type = ObjectiveType.Collect,
                    targetId = "ration_can", requiredAmount = 3,
                },
            };
            shared.rewardCash = 50;
            shared.onCompleteEvents = new[] { "prologue_done" };
            EditorUtility.SetDirty(shared);

            var personal = LoadOrCreate("tools_of_trade");
            personal.questId = "tools_of_trade";
            personal.title = "Tools of the Trade";
            personal.description = "Everyone carries their own weight here. Find yourself a wrench.";
            personal.sharedProgress = false;
            personal.availableFromStart = true;
            personal.objectives = new[]
            {
                new QuestData.Objective
                {
                    description = "Find a heavy wrench", type = ObjectiveType.Collect,
                    targetId = "wrench_large", requiredAmount = 1,
                },
            };
            personal.rewardCash = 25;
            EditorUtility.SetDirty(personal);
        }

        private static QuestData LoadOrCreate(string id)
        {
            string path = $"{QUESTS_RES_FOLDER}/{id}.asset";
            var asset = AssetDatabase.LoadAssetAtPath<QuestData>(path);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<QuestData>();
                AssetDatabase.CreateAsset(asset, path);
            }
            return asset;
        }

        private static void PatchWorldScene()
        {
            var scene = EditorSceneManager.OpenScene(WORLD_SCENE, OpenSceneMode.Additive);

            var syncGO = FindOrCreateRoot(scene, "NetworkQuestSync");
            Ensure<NetworkObject>(syncGO);
            Ensure<NetworkQuestSync>(syncGO);

            var board = FindOrCreateRoot(scene, "QuestBoard");
            if (board.GetComponent<MeshFilter>() == null)
            {
                var visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
                visual.name = "BoardVisual";
                visual.transform.SetParent(board.transform, false);
                visual.transform.localScale = new Vector3(1.4f, 1f, 0.1f);
                visual.transform.localPosition = new Vector3(0f, 1.2f, 0f);
            }
            board.transform.position = new Vector3(-2.5f, 0f, 3f);
            Ensure<QuestBoardInteractable>(board);
            if (board.GetComponent<Collider>() == null)
            {
                var col = board.AddComponent<BoxCollider>();
                col.center = new Vector3(0f, 1.2f, 0f);
                col.size = new Vector3(1.4f, 1f, 0.3f);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, WORLD_SCENE);
            EditorSceneManager.CloseScene(scene, true);
        }

        private static void PatchQuestsTab()
        {
            var root = PrefabUtility.LoadPrefabContents(WORLD_UI_PREFAB);
            try
            {
                var panel = root.transform.Find("TabMenuRoot/Window/Content/QuestsPanel");
                if (panel == null)
                {
                    Debug.LogError("[QuestsBuilder] QuestsPanel not found - run Build Inventory UI first.");
                    return;
                }

                var placeholder = panel.Find("Placeholder");
                if (placeholder != null) Object.DestroyImmediate(placeholder.gameObject);

                var textTr = panel.Find("QuestLog");
                TextMeshProUGUI text;
                if (textTr == null)
                {
                    var go = new GameObject("QuestLog", typeof(TextMeshProUGUI));
                    go.transform.SetParent(panel, false);
                    text = go.GetComponent<TextMeshProUGUI>();
                    var rt = text.rectTransform;
                    rt.anchorMin = Vector2.zero;
                    rt.anchorMax = Vector2.one;
                    rt.offsetMin = new Vector2(60, 30);
                    rt.offsetMax = new Vector2(-60, -30);
                    text.fontSize = 20;
                    text.alignment = TextAlignmentOptions.TopLeft;
                    text.color = new Color(0.85f, 0.85f, 0.80f, 1f);
                }
                else
                {
                    text = textTr.GetComponent<TextMeshProUGUI>();
                }

                var log = panel.GetComponent<QuestLogUI>();
                if (log == null) log = panel.gameObject.AddComponent<QuestLogUI>();
                var so = new SerializedObject(log);
                so.FindProperty("logText").objectReferenceValue = text;
                so.ApplyModifiedPropertiesWithoutUndo();

                PrefabUtility.SaveAsPrefabAsset(root, WORLD_UI_PREFAB);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static GameObject FindOrCreateRoot(Scene scene, string name)
        {
            foreach (var go in scene.GetRootGameObjects())
                if (go.name == name) return go;
            var created = new GameObject(name);
            SceneManager.MoveGameObjectToScene(created, scene);
            return created;
        }

        private static T Ensure<T>(GameObject go) where T : Component
        {
            var comp = go.GetComponent<T>();
            if (comp == null) comp = go.AddComponent<T>();
            return comp;
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            var parts = folder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
