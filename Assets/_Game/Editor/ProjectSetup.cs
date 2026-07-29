using System.Collections.Generic;
using System.IO;
using Game.Interaction;
using Game.Net;
using Game.Player;
using Game.UI;
using Game.World;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

namespace Game.Editor
{
    /// <summary>
    /// One-shot project generator (idempotent, safe to re-run): player + NetworkManager
    /// prefabs, Boot/MainMenu/World scenes, build settings. After first generation the
    /// prefabs/scenes are the source of truth - this tool only exists so the project can
    /// be bootstrapped headlessly and reproducibly.
    ///
    /// Menu: Game/Setup/Full Project Setup
    /// Batch: Unity.exe -batchmode -executeMethod Game.Editor.ProjectSetup.RunAll
    /// </summary>
    public static class ProjectSetup
    {
        private const string PREFAB_FOLDER = "Assets/_Game/Prefabs";
        private const string PLAYER_PREFAB_PATH = PREFAB_FOLDER + "/NetworkPlayer.prefab";
        private const string NETWORK_MANAGER_PREFAB_PATH = PREFAB_FOLDER + "/NetworkManager.prefab";
        private const string SCENES_FOLDER = "Assets/_Game/Scenes";
        private const string BOOT_SCENE = SCENES_FOLDER + "/Boot.unity";
        private const string MAINMENU_SCENE = SCENES_FOLDER + "/MainMenu.unity";
        private const string WORLD_SCENE = SCENES_FOLDER + "/World.unity";
        private const string INPUT_ACTIONS_PATH = "Assets/_Game/Settings/GameInputActions.inputactions";
        private const string SKY_PROFILE_PATH = "Assets/Settings/SkyandFogSettingsProfile.asset";

        [MenuItem("Game/Setup/Full Project Setup")]
        public static void RunAll()
        {
            EnsureFolder(PREFAB_FOLDER);
            EnsureFolder(SCENES_FOLDER);

            var playerPrefab = CreatePlayerPrefab();
            var nmPrefab = CreateNetworkManagerPrefab(playerPrefab);
            CreateBootScene(nmPrefab);
            CreateMainMenuScene();
            CreateWorldScene();
            UpdateBuildSettings();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (string.IsNullOrEmpty(PlayerSettings.cloudProjectId))
                Debug.LogWarning("[ProjectSetup] Project is not linked to a Unity Cloud Project ID. " +
                    "Link it (Project Settings > Services) and enable Anonymous Auth + Lobby + Relay " +
                    "in the dashboard before testing co-op. Solo works offline.");

            Debug.Log("[ProjectSetup] Done. Open Assets/_Game/Scenes/Boot.unity and press Play.");
        }

        // ---------------- Player prefab ----------------

        private static GameObject CreatePlayerPrefab()
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(PLAYER_PREFAB_PATH);
            GameObject root = existing != null
                ? (GameObject)PrefabUtility.InstantiatePrefab(existing)
                : new GameObject("NetworkPlayer");
            if (PrefabUtility.IsPartOfPrefabInstance(root))
                PrefabUtility.UnpackPrefabInstance(root, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

            var cc = Ensure<CharacterController>(root);
            cc.height = 1.9f;
            cc.radius = 0.35f;
            cc.center = new Vector3(0f, 0.95f, 0f);

            // Visible placeholder body (collider removed - CharacterController is the collider).
            var body = FindChild(root, "Body");
            if (body == null)
            {
                body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                body.name = "Body";
                body.transform.SetParent(root.transform, false);
                body.transform.localPosition = new Vector3(0f, 0.95f, 0f);
                body.transform.localScale = new Vector3(0.7f, 0.95f, 0.7f);
                Object.DestroyImmediate(body.GetComponent<CapsuleCollider>());
            }

            var pivot = FindChild(root, "CameraPivot");
            if (pivot == null)
            {
                pivot = new GameObject("CameraPivot");
                pivot.transform.SetParent(root.transform, false);
                pivot.transform.localPosition = new Vector3(0f, 1.65f, 0f);
            }

            var camGO = FindChild(pivot, "PlayerCamera");
            if (camGO == null)
            {
                camGO = new GameObject("PlayerCamera");
                camGO.transform.SetParent(pivot.transform, false);
            }
            var cam = Ensure<Camera>(camGO);
            cam.nearClipPlane = 0.05f;
            Ensure<HDAdditionalCameraData>(camGO);
            var listener = Ensure<AudioListener>(camGO);
            camGO.SetActive(false); // NetworkPlayer activates it for the owner only.

            var netObj = Ensure<NetworkObject>(root);
            var netTransform = Ensure<NetworkTransform>(root);
            netTransform.AuthorityMode = NetworkTransform.AuthorityModes.Owner;
            netTransform.Interpolate = true;
            netTransform.InLocalSpace = false;
            // Riders on a boat are parented to its NetworkObject by BoatRiderCarry. This
            // makes NGO flip the replicated values into the parent's local space on the tick
            // the parent changes, and convert the in-flight interpolation with it - without
            // it the rider snaps once on boarding and, worse, keeps replicating world
            // position, which is the whole thing the parenting exists to avoid.
            netTransform.SwitchTransformSpaceWhenParented = true;
            // Mutually exclusive with the above; NGO reverts one of them at runtime if both
            // are set, and which one it picks depends on the order they were changed in.
            netTransform.UseUnreliableDeltas = false;
            netTransform.SyncScaleX = netTransform.SyncScaleY = netTransform.SyncScaleZ = false;

            var fpc = Ensure<FirstPersonController>(root);
            var stats = Ensure<PlayerStats>(root);
            var interaction = Ensure<InteractionSystem>(root);
            var netPlayer = Ensure<NetworkPlayer>(root);

            var inputActions = AssetDatabase.LoadAssetAtPath<InputActionAsset>(INPUT_ACTIONS_PATH);
            if (inputActions == null)
                Debug.LogError($"[ProjectSetup] Input actions asset missing at {INPUT_ACTIONS_PATH}.");

            // Wire private [SerializeField] references via SerializedObject.
            SetRef(fpc, "inputActions", inputActions);
            SetRef(fpc, "cameraPivot", pivot.transform);
            SetRef(interaction, "playerCamera", cam);
            SetRef(netPlayer, "firstPersonController", fpc);
            SetRef(netPlayer, "playerCamera", cam);
            SetRef(netPlayer, "audioListener", listener);
            SetRef(netPlayer, "interactionSystem", interaction);
            SetRef(netPlayer, "cameraPivot", pivot.transform);

            var saved = PrefabUtility.SaveAsPrefabAsset(root, PLAYER_PREFAB_PATH);
            Object.DestroyImmediate(root);
            Debug.Log($"[ProjectSetup] Player prefab written: {PLAYER_PREFAB_PATH}");
            return saved;
        }

        // ---------------- NetworkManager prefab ----------------

        private static GameObject CreateNetworkManagerPrefab(GameObject playerPrefab)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(NETWORK_MANAGER_PREFAB_PATH);
            GameObject root = existing != null
                ? (GameObject)PrefabUtility.InstantiatePrefab(existing)
                : new GameObject("NetworkManager");
            if (PrefabUtility.IsPartOfPrefabInstance(root))
                PrefabUtility.UnpackPrefabInstance(root, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

            var nm = Ensure<NetworkManager>(root);
            var utp = Ensure<UnityTransport>(root);
            nm.NetworkConfig ??= new NetworkConfig();
            nm.NetworkConfig.NetworkTransport = utp;
            nm.NetworkConfig.TickRate = 30;
            nm.NetworkConfig.EnableSceneManagement = true;
            nm.NetworkConfig.ConnectionApproval = true;
            if (playerPrefab != null)
                nm.NetworkConfig.PlayerPrefab = playerPrefab; // auto-registers in the prefab list

            var saved = PrefabUtility.SaveAsPrefabAsset(root, NETWORK_MANAGER_PREFAB_PATH);
            Object.DestroyImmediate(root);
            Debug.Log($"[ProjectSetup] NetworkManager prefab written: {NETWORK_MANAGER_PREFAB_PATH}");
            return saved;
        }

        // ---------------- Scenes ----------------

        private static void CreateBootScene(GameObject nmPrefab)
        {
            var scene = OpenOrCreateScene(BOOT_SCENE);

            var bootGO = EnsureRoot(scene, "NetworkBootstrap");
            var boot = Ensure<NetworkBootstrap>(bootGO);
            boot.mainMenuSceneName = "MainMenu";

            var sessionGO = EnsureRoot(scene, "NetworkSession");
            var session = Ensure<NetworkSessionManager>(sessionGO);
            Ensure<LobbyController>(sessionGO);
            session.networkManagerPrefab = nmPrefab;
            session.gameplaySceneName = "World";

            SaveAndClose(scene, BOOT_SCENE);
        }

        private static void CreateMainMenuScene()
        {
            var scene = OpenOrCreateScene(MAINMENU_SCENE);

            var camGO = EnsureRoot(scene, "MenuCamera");
            Ensure<Camera>(camGO);
            Ensure<HDAdditionalCameraData>(camGO);
            Ensure<AudioListener>(camGO);

            // The real menu lives in the MainMenuUI prefab (see MenuBuilder).

            SaveAndClose(scene, MAINMENU_SCENE);
        }

        private static void CreateWorldScene()
        {
            var scene = OpenOrCreateScene(WORLD_SCENE);

            var sunGO = EnsureRoot(scene, "Sun");
            var light = Ensure<Light>(sunGO);
            light.type = LightType.Directional;
            light.shadows = LightShadows.Soft;
            Ensure<HDAdditionalLightData>(sunGO);
            // HDRP directional intensity is photometric (lux); AddComponent defaults to 1
            // which is a moonless night. ~40k lux = overcast-ish daylight.
            light.intensity = 40000f;
            sunGO.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            var volumeGO = EnsureRoot(scene, "Sky and Fog Volume");
            var volume = Ensure<Volume>(volumeGO);
            volume.isGlobal = true;
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(SKY_PROFILE_PATH);
            if (profile != null) volume.sharedProfile = profile;
            else Debug.LogWarning($"[ProjectSetup] Sky profile not found at {SKY_PROFILE_PATH}; World will use pipeline defaults.");

            var groundGO = FindRoot(scene, "Ground");
            if (groundGO == null)
            {
                groundGO = GameObject.CreatePrimitive(PrimitiveType.Plane);
                groundGO.name = "Ground";
                SceneManager.MoveGameObjectToScene(groundGO, scene);
                groundGO.transform.localScale = new Vector3(10f, 1f, 10f);
            }

            // A few boxes so there is something to walk around and look at.
            for (int i = 0; i < 4; i++)
            {
                string name = $"TestCube_{i}";
                if (FindRoot(scene, name) != null) continue;
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = name;
                SceneManager.MoveGameObjectToScene(cube, scene);
                cube.transform.position = new Vector3(3f + i * 2.5f, 0.5f, 4f);
            }

            var spawnGO = EnsureRoot(scene, "PlayerSpawn");
            Ensure<PlayerSpawnPoint>(spawnGO);
            spawnGO.transform.position = new Vector3(0f, 0.1f, 0f);

            var stateGO = EnsureRoot(scene, "NetworkGameState");
            Ensure<NetworkObject>(stateGO);
            Ensure<NetworkGameState>(stateGO);

            SaveAndClose(scene, WORLD_SCENE);
        }

        private static void UpdateBuildSettings()
        {
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(BOOT_SCENE, true),
                new EditorBuildSettingsScene(MAINMENU_SCENE, true),
                new EditorBuildSettingsScene(WORLD_SCENE, true),
            };
            Debug.Log("[ProjectSetup] Build settings: Boot, MainMenu, World.");
        }

        // ---------------- Helpers ----------------

        private static Scene OpenOrCreateScene(string path)
        {
            if (File.Exists(path))
                return EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            EditorSceneManager.SaveScene(scene, path);
            return scene;
        }

        private static void SaveAndClose(Scene scene, string path)
        {
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, path);
            EditorSceneManager.CloseScene(scene, true);
            Debug.Log($"[ProjectSetup] Scene written: {path}");
        }

        private static GameObject FindRoot(Scene scene, string name)
        {
            foreach (var root in scene.GetRootGameObjects())
                if (root.name == name) return root;
            return null;
        }

        private static GameObject EnsureRoot(Scene scene, string name)
        {
            var go = FindRoot(scene, name);
            if (go == null)
            {
                go = new GameObject(name);
                SceneManager.MoveGameObjectToScene(go, scene);
            }
            return go;
        }

        private static GameObject FindChild(GameObject parent, string name)
        {
            var t = parent.transform.Find(name);
            return t != null ? t.gameObject : null;
        }

        private static T Ensure<T>(GameObject go) where T : Component
        {
            var comp = go.GetComponent<T>();
            if (comp == null) comp = go.AddComponent<T>();
            return comp;
        }

        /// <summary>Assigns a private [SerializeField] reference by property name.</summary>
        private static void SetRef(Component target, string fieldName, Object value)
        {
            var so = new SerializedObject(target);
            var prop = so.FindProperty(fieldName);
            if (prop == null)
            {
                Debug.LogError($"[ProjectSetup] {target.GetType().Name} has no serialized field '{fieldName}'.");
                return;
            }
            prop.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
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
