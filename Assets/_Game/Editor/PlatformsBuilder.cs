using Game.Interaction;
using Game.World;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Editor
{
    /// <summary>
    /// Adds a test shuttle (metro stand-in) and a 2-floor elevator with call button to the
    /// World scene. Menu: Game/Setup/Build Platforms. Idempotent.
    /// </summary>
    public static class PlatformsBuilder
    {
        private const string WORLD_SCENE = "Assets/_Game/Scenes/World.unity";

        [MenuItem("Game/Setup/Build Platforms")]
        public static void Build()
        {
            var scene = EditorSceneManager.OpenScene(WORLD_SCENE, OpenSceneMode.Additive);

            BuildShuttle(scene);
            BuildElevator(scene);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, WORLD_SCENE);
            EditorSceneManager.CloseScene(scene, true);
            AssetDatabase.SaveAssets();
            Debug.Log("[PlatformsBuilder] Shuttle + elevator added to World.");
        }

        private static void BuildShuttle(Scene scene)
        {
            var shuttle = FindOrCreateRoot(scene, "Shuttle");
            shuttle.transform.position = new Vector3(-12f, 0.6f, -10f);

            if (shuttle.transform.Find("Deck") == null)
            {
                var deck = GameObject.CreatePrimitive(PrimitiveType.Cube);
                deck.name = "Deck";
                deck.transform.SetParent(shuttle.transform, false);
                deck.transform.localScale = new Vector3(3f, 0.3f, 8f);

                var wallL = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wallL.name = "WallL";
                wallL.transform.SetParent(shuttle.transform, false);
                wallL.transform.localPosition = new Vector3(-1.4f, 1.1f, 0f);
                wallL.transform.localScale = new Vector3(0.2f, 2f, 8f);

                var wallR = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wallR.name = "WallR";
                wallR.transform.SetParent(shuttle.transform, false);
                wallR.transform.localPosition = new Vector3(1.4f, 1.1f, 0f);
                wallR.transform.localScale = new Vector3(0.2f, 2f, 8f);
            }

            Ensure<NetworkObject>(shuttle);
            Ensure<ShuttlePlatform>(shuttle);
            var carry = Ensure<MovingPlatformCarry>(shuttle);
            var so = new SerializedObject(carry);
            so.FindProperty("cabinCenter").vector3Value = new Vector3(0f, 1.6f, 0f);
            so.FindProperty("cabinSize").vector3Value = new Vector3(2.6f, 3f, 8f);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void BuildElevator(Scene scene)
        {
            var elevator = FindOrCreateRoot(scene, "Elevator");
            elevator.transform.position = new Vector3(10f, 0.15f, -8f);

            if (elevator.transform.Find("Cab") == null)
            {
                var cab = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cab.name = "Cab";
                cab.transform.SetParent(elevator.transform, false);
                cab.transform.localScale = new Vector3(2.5f, 0.3f, 2.5f);
            }

            Ensure<NetworkObject>(elevator);
            Ensure<ElevatorPlatform>(elevator);
            var carry = Ensure<MovingPlatformCarry>(elevator);
            var so = new SerializedObject(carry);
            so.FindProperty("cabinCenter").vector3Value = new Vector3(0f, 1.5f, 0f);
            so.FindProperty("cabinSize").vector3Value = new Vector3(2.4f, 3f, 2.4f);
            so.ApplyModifiedPropertiesWithoutUndo();

            var button = FindOrCreateRoot(scene, "ElevatorButton");
            if (button.GetComponent<MeshFilter>() == null)
            {
                var visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
                visual.name = "ButtonVisual";
                visual.transform.SetParent(button.transform, false);
                visual.transform.localScale = new Vector3(0.25f, 0.25f, 0.1f);
                visual.transform.localPosition = new Vector3(0f, 1.2f, 0f);
            }
            button.transform.position = new Vector3(8.2f, 0f, -8f);
            if (button.GetComponent<Collider>() == null)
            {
                var col = button.AddComponent<BoxCollider>();
                col.center = new Vector3(0f, 1.2f, 0f);
                col.size = new Vector3(0.4f, 0.4f, 0.3f);
            }
            var interact = Ensure<ElevatorButtonInteractable>(button);
            var iso = new SerializedObject(interact);
            iso.FindProperty("elevator").objectReferenceValue = elevator.GetComponent<ElevatorPlatform>();
            iso.ApplyModifiedPropertiesWithoutUndo();
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
    }
}
