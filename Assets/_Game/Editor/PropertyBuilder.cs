using Game.World;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Editor
{
    /// <summary>
    /// Wires the placement system onto a hand-built property floor, and adds a small test
    /// generator so the fuel system has somewhere on land to prove itself.
    ///
    /// The floor itself is NOT generated here - "Apartment Floor" is geometry Evan built by
    /// hand in the World scene (three ProBuilder slabs). This only reads its collider bounds
    /// and adds what makes it usable: a <see cref="NetworkObject"/> (a <c>CargoAnchor</c> names
    /// itself relative to the nearest one above it), a <see cref="PlacementZone"/> sized to
    /// whatever the floor actually is, and a generator in one corner.
    ///
    /// Reading the bounds off the floor's own colliders rather than hardcoding numbers means
    /// this re-fits itself if the floor is edited, and the same method serves the next
    /// property Evan builds - just a different floor name in <see cref="Build"/>.
    ///
    /// Menu: Game/Setup/Build Property. Idempotent.
    /// </summary>
    public static class PropertyBuilder
    {
        private const string WORLD_SCENE = "Assets/_Game/Scenes/World.unity";

        // Ceiling height for the placement region above the floor.
        private const float HEADROOM = 3f;
        // Finer than a boat deck's grid - furniture is fussier about exactly where it lands
        // than a crate is.
        private const float CELL_SIZE = 0.15f;
        private const float CORNER_MARGIN = 1.3f;

        [MenuItem("Game/Setup/Build Property")]
        public static void Build()
        {
            if (!MenuSceneBuilder.Ready("PropertyBuilder")) return;

            var housing = TestMaterials.Lit("TB_GeneratorHousing",
                new Color(0.22f, 0.22f, 0.24f), 0.4f, 0.3f);
            var trim = TestMaterials.Lit("TB_Trim", new Color(0.12f, 0.13f, 0.16f), 0.55f, 0.6f);
            var outlineMaterial = CargoBuilder.GhostMaterial("Zone_Outline",
                new Color(0.35f, 1f, 0.55f, 0.16f));

            var scene = EditorSceneManager.OpenScene(WORLD_SCENE, OpenSceneMode.Additive);

            BuildOnFloor(scene, "Apartment Floor", housing, trim, outlineMaterial);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, WORLD_SCENE);
            EditorSceneManager.CloseScene(scene, true);
            AssetDatabase.SaveAssets();
        }

        private static void BuildOnFloor(Scene scene, string floorName, Material housing,
            Material trim, Material outlineMaterial)
        {
            GameObject floor = null;
            foreach (var root in scene.GetRootGameObjects())
                if (root.name == floorName) { floor = root; break; }
            if (floor == null)
            {
                Debug.LogError($"[PropertyBuilder] No '{floorName}' object in the World scene - " +
                                "nothing to wire.");
                return;
            }

            Ensure<NetworkObject>(floor);

            var bounds = ComputeFloorBounds(floor);
            if (Quaternion.Angle(floor.transform.rotation, Quaternion.identity) > 0.5f)
                Debug.LogWarning($"[PropertyBuilder] '{floorName}' is rotated - the placement " +
                                 "zone assumes an axis-aligned floor and may not fit it exactly.");

            int zoneIndex = BuildPlacementZone(floor.transform, bounds, outlineMaterial);

            // Corner of the floor, inset so nothing clips through the outer edge. World-space
            // math converted to the floor's local space at the end, so this works whatever the
            // floor's own position turns out to be.
            Vector3 cornerWorld = new(bounds.min.x + CORNER_MARGIN, bounds.max.y,
                                      bounds.min.z + CORNER_MARGIN);

            var tank = FuelSystemBuilder.BuildFuelTank(floor.transform,
                floor.transform.InverseTransformPoint(cornerWorld),
                capacityLiters: 60f, startingLiters: 0f, trim);

            Vector3 generatorWorld = cornerWorld + new Vector3(0f, 0f, 0.8f);
            var light1 = BuildTestLight(floor.transform,
                floor.transform.InverseTransformPoint(
                    new Vector3(Mathf.Lerp(bounds.min.x, bounds.max.x, 0.25f),
                                bounds.max.y + 2.1f, bounds.center.z)),
                "PropertyLight_1");
            var light2 = BuildTestLight(floor.transform,
                floor.transform.InverseTransformPoint(
                    new Vector3(Mathf.Lerp(bounds.min.x, bounds.max.x, 0.75f),
                                bounds.max.y + 2.1f, bounds.center.z)),
                "PropertyLight_2");

            // Off and empty by construction: a generator that needs refuelling and switching
            // on should not already be lighting the room before anyone has touched it.
            light1.SetActive(false);
            light2.SetActive(false);

            FuelSystemBuilder.BuildGenerator(floor.transform,
                floor.transform.InverseTransformPoint(generatorWorld), localYaw: 0f,
                tank, litersPerHour: 90f, generatorId: $"{floorName}_generator",
                poweredObjects: new[] { light1, light2 }, housing, trim);

            Debug.Log($"[PropertyBuilder] '{floorName}' wired: floor bounds {bounds.size:F1}, " +
                      $"placement zone anchor index {zoneIndex}, generator empty and off, " +
                      "tank empty.");
        }

        private static int BuildPlacementZone(Transform floor, Bounds worldBounds, Material outlineMaterial)
        {
            var zoneGO = new GameObject("PlacementZone");
            zoneGO.transform.SetParent(floor, false);
            zoneGO.transform.position = new Vector3(worldBounds.center.x,
                worldBounds.max.y + HEADROOM * 0.5f, worldBounds.center.z);

            var size = new Vector3(worldBounds.size.x, HEADROOM, worldBounds.size.z);
            var outline = CargoBuilder.BuildZoneOutline(zoneGO.transform, Vector3.zero, size,
                0.35f, outlineMaterial);

            var zone = zoneGO.AddComponent<PlacementZone>();
            var so = new SerializedObject(zone);
            // attachRoot left unassigned deliberately - it defaults to this transform, which
            // is exactly right for a floor that never moves. Boats point it at a rolling twin
            // instead; see CrabBoatBuilder.
            so.FindProperty("center").vector3Value = Vector3.zero;
            so.FindProperty("size").vector3Value = size;
            so.FindProperty("cellSize").floatValue = CELL_SIZE;
            so.FindProperty("outline").objectReferenceValue = outline;
            so.ApplyModifiedPropertiesWithoutUndo();
            return zone.Index;
        }

        /// <summary>
        /// A property light the generator can switch. Built through TestMaterials.PointLight
        /// (which does not return the object it creates) and then fetched back by name, so it
        /// still gets PracticalLight's day/night response and console group scaling like every
        /// other light in the scene.
        /// </summary>
        private static GameObject BuildTestLight(Transform parent, Vector3 localPos, string name)
        {
            TestMaterials.PointLight(name, parent, localPos, new Color(1f, 0.92f, 0.75f), 6000f,
                10f, volumetric: 0.6f, group: "property");
            return parent.Find(name).gameObject;
        }

        private static Bounds ComputeFloorBounds(GameObject floor)
        {
            var colliders = floor.GetComponentsInChildren<Collider>(true);
            if (colliders.Length == 0) return new Bounds(floor.transform.position, Vector3.one);

            var bounds = colliders[0].bounds;
            for (int i = 1; i < colliders.Length; i++) bounds.Encapsulate(colliders[i].bounds);
            return bounds;
        }

        private static T Ensure<T>(GameObject go) where T : Component
        {
            var comp = go.GetComponent<T>();
            if (comp == null) comp = go.AddComponent<T>();
            return comp;
        }
    }
}
