using Game.Save;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Editor
{
    /// <summary>Adds the SaveService object to the World scene. Menu: Game/Setup/Build Save.</summary>
    public static class SaveBuilder
    {
        private const string WORLD_SCENE = "Assets/_Game/Scenes/World.unity";

        [MenuItem("Game/Setup/Build Save")]
        public static void Build()
        {
            var scene = EditorSceneManager.OpenScene(WORLD_SCENE, OpenSceneMode.Additive);

            GameObject go = null;
            foreach (var root in scene.GetRootGameObjects())
                if (root.name == "SaveService") go = root;
            if (go == null)
            {
                go = new GameObject("SaveService");
                SceneManager.MoveGameObjectToScene(go, scene);
            }
            if (go.GetComponent<NetworkObject>() == null) go.AddComponent<NetworkObject>();
            if (go.GetComponent<SaveService>() == null) go.AddComponent<SaveService>();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, WORLD_SCENE);
            EditorSceneManager.CloseScene(scene, true);
            AssetDatabase.SaveAssets();
            Debug.Log("[SaveBuilder] SaveService added to World.");
        }
    }
}
