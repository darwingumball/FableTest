using Game.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Editor
{
    /// <summary>
    /// Wires snow deformation: manager object in World, SnowDeformer on the player prefab.
    /// Menu: Game/Setup/Build Snow. Idempotent.
    /// </summary>
    public static class SnowBuilder
    {
        private const string WORLD_SCENE = "Assets/_Game/Scenes/World.unity";
        private const string PLAYER_PREFAB = "Assets/_Game/Prefabs/NetworkPlayer.prefab";

        [MenuItem("Game/Setup/Build Snow")]
        public static void Build()
        {
            var scene = EditorSceneManager.OpenScene(WORLD_SCENE, OpenSceneMode.Additive);

            GameObject manager = null;
            foreach (var go in scene.GetRootGameObjects())
                if (go.name == "SnowDeformation") manager = go;
            if (manager == null)
            {
                manager = new GameObject("SnowDeformation");
                SceneManager.MoveGameObjectToScene(manager, scene);
            }
            manager.transform.position = Vector3.zero; // region centers here
            if (manager.GetComponent<SnowDeformationManager>() == null)
                manager.AddComponent<SnowDeformationManager>();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, WORLD_SCENE);
            EditorSceneManager.CloseScene(scene, true);

            var player = PrefabUtility.LoadPrefabContents(PLAYER_PREFAB);
            if (player.GetComponent<SnowDeformer>() == null)
                player.AddComponent<SnowDeformer>();
            PrefabUtility.SaveAsPrefabAsset(player, PLAYER_PREFAB);
            PrefabUtility.UnloadPrefabContents(player);

            AssetDatabase.SaveAssets();
            Debug.Log("[SnowBuilder] Snow deformation wired (World manager + player deformer).");
        }
    }
}
