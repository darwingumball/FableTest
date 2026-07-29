using Game.Interaction;
using Game.Inventory;
using Game.Net;
using Game.World;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Editor
{
    /// <summary>
    /// Generates starter item content: ItemData assets (Resources/Items so ids resolve
    /// over the network), primitive networked world prefabs, the ItemPreview layer,
    /// player prefab inventory components, and the World scene's WorldItemManager with
    /// a test scatter. Idempotent.
    ///
    /// Menu: Game/Setup/Build Items
    /// </summary>
    public static class ItemsBuilder
    {
        public const string PREVIEW_LAYER = "ItemPreview";
        private const string ITEMS_RES_FOLDER = "Assets/_Game/Resources/Items";
        private const string ITEM_PREFAB_FOLDER = "Assets/_Game/Prefabs/Items";
        private const string MATERIALS_FOLDER = "Assets/_Game/Materials";
        private const string PLAYER_PREFAB_PATH = "Assets/_Game/Prefabs/NetworkPlayer.prefab";
        private const string WORLD_SCENE = "Assets/_Game/Scenes/World.unity";

        private struct ItemDef
        {
            public string id, name, desc, stats;
            public int w, h, stack;
            public float mass;
            public Color color;
            public PrimitiveType primitive;
            public Vector3 scale;
            /// <summary>Gets a <see cref="Buoyancy"/>. A wrench does not.</summary>
            public bool floats;
        }

        private static readonly ItemDef[] Defs =
        {
            new() { id = "crate_small", name = "Supply Crate", desc = "A battered supply crate. Something rattles inside.",
                    stats = "Sturdy", w = 2, h = 2, stack = 1, mass = 6f,
                    color = new Color(0.45f, 0.33f, 0.20f), primitive = PrimitiveType.Cube, scale = new Vector3(0.4f, 0.4f, 0.4f),
                    floats = true },
            new() { id = "ration_can", name = "Canned Rations", desc = "Expired long before the incident. Edible, technically.",
                    stats = "Restores hunger (later)", w = 1, h = 1, stack = 5, mass = 0.5f,
                    color = new Color(0.42f, 0.45f, 0.28f), primitive = PrimitiveType.Cylinder, scale = new Vector3(0.12f, 0.09f, 0.12f) },
            new() { id = "wrench_large", name = "Heavy Wrench", desc = "Opens bolts, doors, and skulls.",
                    stats = "Tool", w = 2, h = 1, stack = 1, mass = 2f,
                    color = new Color(0.55f, 0.55f, 0.58f), primitive = PrimitiveType.Capsule, scale = new Vector3(0.08f, 0.22f, 0.08f) },
            new() { id = "fuel_barrel", name = "Fuel Barrel", desc = "Half full. Sloshes ominously when carried.",
                    stats = "Heavy - slows you down\nFlammable", w = 2, h = 2, stack = 1, mass = 20f,
                    color = new Color(0.55f, 0.16f, 0.13f), primitive = PrimitiveType.Cylinder, scale = new Vector3(0.4f, 0.3f, 0.4f),
                    floats = true },
            // The small fuel container, alongside the barrel. Sealed, so it floats - dropping
            // one over the side should be a recoverable mistake rather than a lost tank of
            // diesel. The fuel SYSTEM (generators, ship tanks, consumption) is not built yet;
            // this is the item it will consume.
            new() { id = "jerry_can", name = "Jerry Can", desc = "Twenty litres of diesel and a bent spout.",
                    stats = "Fuel: 20 L\nFlammable", w = 2, h = 2, stack = 1, mass = 18f,
                    color = new Color(0.24f, 0.30f, 0.20f), primitive = PrimitiveType.Cube, scale = new Vector3(0.19f, 0.46f, 0.34f),
                    floats = true },
            // Deliberately the biggest thing in the list. The crane needs a load that reads as
            // a load from the wheelhouse roof twenty metres away, and a 40 cm crate does not -
            // a pot you can see swinging is the whole point of watching a crane work.
            new() { id = "crab_trap", name = "Crab Pot", desc = "Steel frame, tarred netting, one bait jar. Smells accordingly.",
                    stats = "Bulky - crane or two hands\nFloats, just about", w = 3, h = 3, stack = 1, mass = 34f,
                    color = new Color(0.28f, 0.30f, 0.26f), primitive = PrimitiveType.Cube, scale = new Vector3(0.95f, 0.6f, 0.95f),
                    floats = true },
        };

        [MenuItem("Game/Setup/Build Items")]
        public static void Build()
        {
            EnsureFolder(ITEMS_RES_FOLDER);
            EnsureFolder(ITEM_PREFAB_FOLDER);
            EnsureFolder(MATERIALS_FOLDER);
            int previewLayer = EnsureLayer(PREVIEW_LAYER);

            foreach (var def in Defs)
                BuildItem(def);

            PatchPlayerPrefab(previewLayer);
            PatchWorldScene();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[ItemsBuilder] Built {Defs.Length} items, patched player prefab and World scene.");
        }

        private static void BuildItem(ItemDef def)
        {
            // 1. ItemData asset (created first so the prefab can reference it).
            string dataPath = $"{ITEMS_RES_FOLDER}/{def.id}.asset";
            var data = AssetDatabase.LoadAssetAtPath<ItemData>(dataPath);
            if (data == null)
            {
                data = ScriptableObject.CreateInstance<ItemData>();
                AssetDatabase.CreateAsset(data, dataPath);
            }
            data.itemId = def.id;
            data.displayName = def.name;
            data.description = def.desc;
            data.statsText = def.stats;
            data.gridWidth = def.w;
            data.gridHeight = def.h;
            data.maxStack = def.stack;
            data.weightKg = def.mass;
            data.iconTint = def.color;

            // 2. Material.
            string matPath = $"{MATERIALS_FOLDER}/{def.id}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("HDRP/Lit"));
                AssetDatabase.CreateAsset(mat, matPath);
            }
            mat.SetColor("_BaseColor", def.color);

            // 3. World prefab.
            string prefabPath = $"{ITEM_PREFAB_FOLDER}/{def.id}.prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            GameObject root;
            if (existing != null)
            {
                root = (GameObject)PrefabUtility.InstantiatePrefab(existing);
                PrefabUtility.UnpackPrefabInstance(root, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            }
            else
            {
                root = GameObject.CreatePrimitive(def.primitive);
                root.name = def.id;
            }
            root.transform.localScale = def.scale;
            root.GetComponent<Renderer>().sharedMaterial = mat;

            var rb = Ensure<Rigidbody>(root);
            rb.mass = def.mass;
            rb.interpolation = RigidbodyInterpolation.Interpolate;

            var netObject = Ensure<NetworkObject>(root);
            // CargoAttachment parents cargo to a deck or a hook OUTSIDE of NGO, deliberately -
            // see the class summary for why. NGO would otherwise try to replicate that
            // parenting and then undo it, and log a warning per crate while it did.
            netObject.AutoObjectParentSync = false;

            var nt = Ensure<NetworkTransform>(root);
            nt.AuthorityMode = NetworkTransform.AuthorityModes.Owner;
            nt.Interpolate = true;
            nt.SyncScaleX = nt.SyncScaleY = nt.SyncScaleZ = false;
            Ensure<NetworkRigidbody>(root);

            var worldItem = Ensure<WorldItem>(root);
            worldItem.itemData = data;
            worldItem.quantity = 1;
            Ensure<WorldItemNetworkSync>(root);

            // Every item can be lashed down or hung off a hook - including the small ones,
            // because the same mechanism is what will let handheld things be set on a desk.
            Ensure<CargoAttachment>(root);

            if (def.floats) Ensure<Buoyancy>(root);
            else
            {
                var stray = root.GetComponent<Buoyancy>();
                if (stray != null) Object.DestroyImmediate(stray);
            }

            var saved = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            Object.DestroyImmediate(root);

            // 4. Close the loop: asset points at its prefab.
            data.worldPrefab = saved;
            EditorUtility.SetDirty(data);
        }

        private static void PatchPlayerPrefab(int previewLayer)
        {
            var root = PrefabUtility.LoadPrefabContents(PLAYER_PREFAB_PATH);
            Ensure<PlayerInventory>(root);
            Ensure<PhysicsPickup>(root);

            var cam = root.GetComponentInChildren<Camera>(true);
            if (cam != null && previewLayer >= 0)
                cam.cullingMask &= ~(1 << previewLayer);

            PrefabUtility.SaveAsPrefabAsset(root, PLAYER_PREFAB_PATH);
            PrefabUtility.UnloadPrefabContents(root);
        }

        private static void PatchWorldScene()
        {
            var scene = EditorSceneManager.OpenScene(WORLD_SCENE, OpenSceneMode.Additive);

            GameObject managerGO = null;
            foreach (var rootGO in scene.GetRootGameObjects())
                if (rootGO.name == "WorldItemManager") { managerGO = rootGO; break; }
            if (managerGO == null)
            {
                managerGO = new GameObject("WorldItemManager");
                SceneManager.MoveGameObjectToScene(managerGO, scene);
            }
            Ensure<NetworkObject>(managerGO);
            var manager = Ensure<WorldItemManager>(managerGO);
            manager.initialScatter = new[]
            {
                new WorldItemManager.ScatterEntry { itemId = "crate_small", count = 1, position = new Vector3(2f, 0.6f, 3f) },
                new WorldItemManager.ScatterEntry { itemId = "ration_can", count = 3, position = new Vector3(2.8f, 0.5f, 3.4f) },
                new WorldItemManager.ScatterEntry { itemId = "ration_can", count = 2, position = new Vector3(3.4f, 0.5f, 2.6f) },
                new WorldItemManager.ScatterEntry { itemId = "wrench_large", count = 1, position = new Vector3(4.2f, 0.5f, 3.8f) },
                new WorldItemManager.ScatterEntry { itemId = "fuel_barrel", count = 1, position = new Vector3(5f, 0.7f, 2.8f) },

                // Gear in the water either side of the crab boat (moored at x=11, z=80), well
                // inside the crane's reach. Dropped a little above the surface so they settle
                // and float rather than starting half-sunk. Fishing these aboard is the crane
                // test; see CrabBoatBuilder.
                new WorldItemManager.ScatterEntry { itemId = "crab_trap", count = 1, position = new Vector3(5.5f, -2.4f, 77.5f) },
                new WorldItemManager.ScatterEntry { itemId = "crab_trap", count = 1, position = new Vector3(4.5f, -2.4f, 82f) },
                new WorldItemManager.ScatterEntry { itemId = "crab_trap", count = 1, position = new Vector3(17.5f, -2.4f, 79f) },
                new WorldItemManager.ScatterEntry { itemId = "crate_small", count = 1, position = new Vector3(17f, -2.4f, 84f) },
                new WorldItemManager.ScatterEntry { itemId = "fuel_barrel", count = 1, position = new Vector3(6f, -2.4f, 85f) },
                new WorldItemManager.ScatterEntry { itemId = "jerry_can", count = 1, position = new Vector3(3.9f, 0.5f, 2.2f) },
            };

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, WORLD_SCENE);
            EditorSceneManager.CloseScene(scene, true);
        }

        // ---------------- helpers ----------------

        public static int EnsureLayer(string layerName)
        {
            int existing = LayerMask.NameToLayer(layerName);
            if (existing >= 0) return existing;

            var tagManager = new SerializedObject(
                AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layers = tagManager.FindProperty("layers");
            for (int i = 8; i < layers.arraySize; i++)
            {
                var prop = layers.GetArrayElementAtIndex(i);
                if (string.IsNullOrEmpty(prop.stringValue))
                {
                    prop.stringValue = layerName;
                    tagManager.ApplyModifiedPropertiesWithoutUndo();
                    Debug.Log($"[ItemsBuilder] Added layer '{layerName}' at index {i}.");
                    return i;
                }
            }
            Debug.LogError("[ItemsBuilder] No free layer slot for ItemPreview.");
            return -1;
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
