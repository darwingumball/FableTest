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
    /// A three-storey apartment block for testing interior lighting and the networked
    /// elevator together: enclosed rooms with warm practicals, emissive windows that read
    /// from the street, and an open shaft the elevator runs up.
    ///
    /// The elevator reuses <see cref="ElevatorPlatform"/>, so it stays a pure function of
    /// server time and a late joiner arriving mid-travel sees the cab in the same place.
    ///
    /// Menu: Game/Setup/Build Apartment. Idempotent (rebuilds from scratch).
    /// </summary>
    public static class ApartmentBuilder
    {
        private const string WORLD_SCENE = "Assets/_Game/Scenes/World.unity";
        private const string ROOT_NAME = "TestApartment";

        private static readonly Vector3 Origin = new(0f, 0f, 34f);

        private const float FLOOR_HEIGHT = 3.5f;
        private const int FLOORS = 3;
        private const float HALF_X = 8f;   // building spans x -8..8
        private const float HALF_Z = 6f;   // building spans z -6..6
        private const float WALL = 0.2f;

        // Shaft occupies the left end of the plan; floor slabs are built around it.
        private const float SHAFT_MIN_X = -7f;
        private const float SHAFT_MAX_X = -4f;
        private const float SHAFT_HALF_Z = 1.5f;

        [MenuItem("Game/Setup/Build Apartment")]
        public static void Build()
        {
            var concrete = TestMaterials.Lit("TB_ApartConcrete", new Color(0.26f, 0.25f, 0.24f), 0.20f, 0f);
            var interior = TestMaterials.Lit("TB_ApartInterior", new Color(0.42f, 0.39f, 0.35f), 0.25f, 0f);
            var trim = TestMaterials.Lit("TB_Trim", new Color(0.12f, 0.13f, 0.16f), 0.55f, 0.6f);
            var windowGlow = TestMaterials.Emissive("TB_ApartWindow", new Color(1f, 0.78f, 0.48f), 320f);

            var scene = EditorSceneManager.OpenScene(WORLD_SCENE, OpenSceneMode.Additive);
            foreach (var existing in scene.GetRootGameObjects())
                if (existing.name == ROOT_NAME) Object.DestroyImmediate(existing);

            var parent = new GameObject(ROOT_NAME);
            SceneManager.MoveGameObjectToScene(parent, scene);
            parent.transform.position = Origin;
            var root = parent.transform;

            float totalHeight = FLOORS * FLOOR_HEIGHT;

            BuildShell(root, concrete, totalHeight);
            for (int floor = 0; floor < FLOORS; floor++)
                BuildFloor(root, floor, interior, trim, windowGlow);

            BuildElevator(scene, root, trim);

            // The whole footprint is roofed, so no snow indoors.
            TestMaterials.SnowBlock(root, "SnowBlock_Interior", Vector3.zero,
                new Vector2(HALF_X * 2f, HALF_Z * 2f));

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, WORLD_SCENE);
            EditorSceneManager.CloseScene(scene, true);
            AssetDatabase.SaveAssets();
            Debug.Log($"[ApartmentBuilder] {FLOORS}-storey apartment built at {Origin}.");
        }

        private static void BuildShell(Transform root, Material concrete, float height)
        {
            // Ground slab, then perimeter. The front (+Z) face is left to BuildFloor so it
            // can leave window and door openings per storey.
            TestMaterials.Box("GroundSlab", root, new Vector3(0f, 0.1f, 0f),
                new Vector3(HALF_X * 2f, 0.2f, HALF_Z * 2f), concrete);
            TestMaterials.Box("WallBack", root, new Vector3(0f, height * 0.5f, -HALF_Z),
                new Vector3(HALF_X * 2f, height, WALL), concrete);
            TestMaterials.Box("WallLeft", root, new Vector3(-HALF_X, height * 0.5f, 0f),
                new Vector3(WALL, height, HALF_Z * 2f), concrete);
            TestMaterials.Box("WallRight", root, new Vector3(HALF_X, height * 0.5f, 0f),
                new Vector3(WALL, height, HALF_Z * 2f), concrete);
            TestMaterials.Box("Roof", root, new Vector3(0f, height + 0.1f, 0f),
                new Vector3(HALF_X * 2f + 0.4f, 0.2f, HALF_Z * 2f + 0.4f), concrete);
            TestMaterials.Box("Parapet", root, new Vector3(0f, height + 0.6f, HALF_Z + 0.1f),
                new Vector3(HALF_X * 2f + 0.4f, 0.8f, 0.2f), concrete);
        }

        private static void BuildFloor(Transform parent, int floor, Material interior,
            Material trim, Material windowGlow)
        {
            float y = floor * FLOOR_HEIGHT;
            var root = TestMaterials.Node($"Floor{floor}", parent, new Vector3(0f, y, 0f));

            // Upper storeys need a slab, built as four pieces so the lift shaft stays open.
            if (floor > 0)
            {
                float leftWidth = SHAFT_MIN_X - (-HALF_X);
                TestMaterials.Box("SlabLeft", root,
                    new Vector3(-HALF_X + leftWidth * 0.5f, 0.1f, 0f),
                    new Vector3(leftWidth, 0.2f, HALF_Z * 2f), interior);

                float rightWidth = HALF_X - SHAFT_MAX_X;
                TestMaterials.Box("SlabRight", root,
                    new Vector3(SHAFT_MAX_X + rightWidth * 0.5f, 0.1f, 0f),
                    new Vector3(rightWidth, 0.2f, HALF_Z * 2f), interior);

                float shaftWidth = SHAFT_MAX_X - SHAFT_MIN_X;
                float sideDepth = HALF_Z - SHAFT_HALF_Z;
                for (int s = -1; s <= 1; s += 2)
                    TestMaterials.Box($"SlabShaftSide{s}", root,
                        new Vector3((SHAFT_MIN_X + SHAFT_MAX_X) * 0.5f, 0.1f,
                                    s * (SHAFT_HALF_Z + sideDepth * 0.5f)),
                        new Vector3(shaftWidth, 0.2f, sideDepth), interior);
            }

            // Front facade: piers between window bays, with a door gap on the ground floor.
            BuildFacade(root, floor, interior, windowGlow);

            // Partition splitting the storey into two rooms, with a doorway through it.
            TestMaterials.Box("Partition", root, new Vector3(2f, FLOOR_HEIGHT * 0.5f, -3.2f),
                new Vector3(0.15f, FLOOR_HEIGHT, 5.6f), interior);

            // Shaft surround so the opening reads as a lift, not a hole.
            TestMaterials.Box("ShaftWall", root,
                new Vector3(SHAFT_MAX_X, FLOOR_HEIGHT * 0.5f, 0f),
                new Vector3(0.15f, FLOOR_HEIGHT, SHAFT_HALF_Z * 2f + 0.3f), trim);

            // Interiors are lit whatever the hour, so nightBoost stays near 1 - unlike the
            // street fixtures, which have to fight daylight exposure.
            TestMaterials.PointLight("CeilingA", root, new Vector3(-1.5f, FLOOR_HEIGHT - 0.4f, 2f),
                new Color(1f, 0.82f, 0.62f), 22500f, 9f, volumetric: 1.2f,
                group: "apartment", nightBoost: 1.15f);
            TestMaterials.PointLight("CeilingB", root, new Vector3(5f, FLOOR_HEIGHT - 0.4f, -2.5f),
                new Color(1f, 0.78f, 0.56f), 17500f, 8f, volumetric: 1.2f,
                group: "apartment", nightBoost: 1.15f);
            TestMaterials.PointLight("LandingLight", root,
                new Vector3(-3f, FLOOR_HEIGHT - 0.4f, 0f),
                new Color(0.85f, 0.90f, 1f), 10000f, 7f, volumetric: 1.6f,
                group: "apartment", nightBoost: 1.3f);
        }

        private static void BuildFacade(Transform root, int floor, Material concrete, Material windowGlow)
        {
            const float sill = 0.9f;
            const float windowHeight = 1.7f;
            float headerHeight = FLOOR_HEIGHT - sill - windowHeight;

            // Bay centres across the front, skipping the doorway bay on the ground floor.
            float[] bayCentres = { -6f, -2f, 2f, 6f };
            const float bayHalf = 1.2f;

            // Continuous spandrel under the windows, split by the ground-floor doorway.
            if (floor == 0)
            {
                TestMaterials.Box("SillLeft", root, new Vector3(-4.75f, sill * 0.5f, HALF_Z),
                    new Vector3(6.5f, sill, WALL), concrete);
                TestMaterials.Box("SillRight", root, new Vector3(4.75f, sill * 0.5f, HALF_Z),
                    new Vector3(6.5f, sill, WALL), concrete);
            }
            else
            {
                TestMaterials.Box("Sill", root, new Vector3(0f, sill * 0.5f, HALF_Z),
                    new Vector3(HALF_X * 2f, sill, WALL), concrete);
            }

            TestMaterials.Box("Header", root,
                new Vector3(0f, sill + windowHeight + headerHeight * 0.5f, HALF_Z),
                new Vector3(HALF_X * 2f, headerHeight, WALL), concrete);

            // Piers between bays.
            float previousEdge = -HALF_X;
            for (int i = 0; i < bayCentres.Length; i++)
            {
                float bayStart = bayCentres[i] - bayHalf;
                float pierWidth = bayStart - previousEdge;
                if (pierWidth > 0.01f)
                    TestMaterials.Box($"Pier{i}", root,
                        new Vector3(previousEdge + pierWidth * 0.5f, sill + windowHeight * 0.5f, HALF_Z),
                        new Vector3(pierWidth, windowHeight, WALL), concrete);
                previousEdge = bayCentres[i] + bayHalf;

                // Glowing pane so the block reads as occupied from the street. Ground floor
                // keeps its middle bays open as the entrance.
                bool isDoorway = floor == 0 && (i == 1 || i == 2);
                if (!isDoorway)
                    TestMaterials.Box($"Window{i}", root,
                        new Vector3(bayCentres[i], sill + windowHeight * 0.5f, HALF_Z),
                        new Vector3(bayHalf * 2f, windowHeight, 0.06f), windowGlow);
            }

            float lastPier = HALF_X - previousEdge;
            if (lastPier > 0.01f)
                TestMaterials.Box("PierEnd", root,
                    new Vector3(previousEdge + lastPier * 0.5f, sill + windowHeight * 0.5f, HALF_Z),
                    new Vector3(lastPier, windowHeight, WALL), concrete);
        }

        /// <summary>
        /// Elevator cab plus its call button, reusing the networked platform so the ride is
        /// derived from server time rather than replicated per-frame.
        /// </summary>
        private static void BuildElevator(Scene scene, Transform parent, Material trim)
        {
            float shaftX = (SHAFT_MIN_X + SHAFT_MAX_X) * 0.5f;

            // Kept as a scene ROOT, not a child of the building: NGO scene objects are
            // simplest to reason about unparented, and the platform writes world transforms.
            var elevator = new GameObject("ApartmentElevator");
            SceneManager.MoveGameObjectToScene(elevator, scene);
            elevator.transform.position = parent.position + new Vector3(shaftX, 0.25f, 0f);

            var cab = TestMaterials.Box("Cab", elevator.transform, Vector3.zero,
                new Vector3(2.6f, 0.3f, 2.6f), trim);
            cab.transform.localPosition = Vector3.zero;

            elevator.AddComponent<NetworkObject>();
            var platform = elevator.AddComponent<ElevatorPlatform>();
            var pso = new SerializedObject(platform);
            var offsets = pso.FindProperty("floorOffsets");
            offsets.arraySize = FLOORS;
            for (int i = 0; i < FLOORS; i++)
                offsets.GetArrayElementAtIndex(i).floatValue = i * FLOOR_HEIGHT;
            pso.ApplyModifiedPropertiesWithoutUndo();

            var carry = elevator.AddComponent<MovingPlatformCarry>();
            var cso = new SerializedObject(carry);
            cso.FindProperty("cabinCenter").vector3Value = new Vector3(0f, 1.5f, 0f);
            cso.FindProperty("cabinSize").vector3Value = new Vector3(2.6f, 3f, 2.6f);
            cso.ApplyModifiedPropertiesWithoutUndo();

            // Call button just outside the shaft on the ground floor.
            var button = new GameObject("ApartmentElevatorButton");
            SceneManager.MoveGameObjectToScene(button, scene);
            button.transform.position = parent.position + new Vector3(SHAFT_MAX_X + 0.4f, 0f, 2f);

            var visual = TestMaterials.Box("ButtonVisual", button.transform,
                new Vector3(0f, 1.2f, 0f), new Vector3(0.25f, 0.25f, 0.1f), trim);
            visual.transform.localPosition = new Vector3(0f, 1.2f, 0f);

            var col = button.AddComponent<BoxCollider>();
            col.center = new Vector3(0f, 1.2f, 0f);
            col.size = new Vector3(0.4f, 0.4f, 0.3f);

            var interact = button.AddComponent<ElevatorButtonInteractable>();
            var iso = new SerializedObject(interact);
            iso.FindProperty("elevator").objectReferenceValue = platform;
            iso.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
