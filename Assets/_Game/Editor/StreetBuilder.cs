using Game.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Editor
{
    /// <summary>
    /// A length of road with kerbed pavements and sodium lamp posts, for judging weather:
    /// wet asphalt reflections, snow lying on the pavement and getting carved back to the
    /// road, and how far lamp light carries through volumetric fog.
    ///
    /// Surfaces sit BELOW the street snow depth (0.2m) on purpose - road at 6cm, pavement
    /// at 16cm - so snow covers them and footprints reveal them again.
    ///
    /// Menu: Game/Setup/Build Street. Idempotent (rebuilds from scratch).
    /// </summary>
    public static class StreetBuilder
    {
        private const string WORLD_SCENE = "Assets/_Game/Scenes/World.unity";
        private const string MAT_FOLDER = "Assets/_Game/Materials/Test";
        private const string ROOT_NAME = "TestStreet";

        // South of everything else: the warehouse's back wall is at z = -26.
        private const float CENTRE_Z = -35f;
        private const float LENGTH = 90f;
        private const float ROAD_HALF_WIDTH = 4f;
        private const float PAVEMENT_WIDTH = 3f;
        private const float ROAD_TOP = 0.06f;
        private const float KERB_TOP = 0.16f;
        private const float LAMP_SPACING = 14f;

        [MenuItem("Game/Setup/Build Street")]
        public static void Build()
        {
            var asphalt = TestMaterials.Lit("TB_Asphalt", new Color(0.13f, 0.13f, 0.14f), 0.25f, 0f);
            var pavement = TestMaterials.Lit("TB_Pavement", new Color(0.33f, 0.33f, 0.34f), 0.22f, 0f);
            var kerb = TestMaterials.Lit("TB_Kerb", new Color(0.34f, 0.34f, 0.35f), 0.20f, 0f);
            var paint = TestMaterials.Lit("TB_RoadPaint", new Color(0.55f, 0.52f, 0.40f), 0.30f, 0f);
            var poleMat = TestMaterials.Lit("TB_LampPole", new Color(0.09f, 0.09f, 0.10f), 0.45f, 0.5f);
            var lampLens = TestMaterials.Emissive("TB_LampLens", new Color(1f, 0.72f, 0.36f), 4000f);

            var scene = EditorSceneManager.OpenScene(WORLD_SCENE, OpenSceneMode.Additive);
            foreach (var existing in scene.GetRootGameObjects())
                if (existing.name == ROOT_NAME) Object.DestroyImmediate(existing);

            var parent = new GameObject(ROOT_NAME);
            SceneManager.MoveGameObjectToScene(parent, scene);
            parent.transform.position = new Vector3(0f, 0f, CENTRE_Z);
            // Road and pavement go glassy in the rain; the lamps are what they reflect.
            parent.AddComponent<SurfaceWetness>();

            var root = parent.transform;

            TestMaterials.Box("Road", root, new Vector3(0f, ROAD_TOP * 0.5f, 0f),
                new Vector3(LENGTH, ROAD_TOP, ROAD_HALF_WIDTH * 2f), asphalt);

            // Centre line, dashed, so wet reflections have something with contrast.
            for (float x = -LENGTH * 0.5f + 3f; x < LENGTH * 0.5f - 3f; x += 6f)
                TestMaterials.Box($"Line_{x:0}", root, new Vector3(x + 1.5f, ROAD_TOP + 0.005f, 0f),
                    new Vector3(2.4f, 0.01f, 0.16f), paint);

            for (int side = -1; side <= 1; side += 2)
            {
                float kerbZ = side * (ROAD_HALF_WIDTH + 0.1f);
                float pavementZ = side * (ROAD_HALF_WIDTH + 0.2f + PAVEMENT_WIDTH * 0.5f);

                TestMaterials.Box($"Kerb_{side}", root, new Vector3(0f, KERB_TOP * 0.5f, kerbZ),
                    new Vector3(LENGTH, KERB_TOP, 0.2f), kerb);
                TestMaterials.Box($"Pavement_{side}", root,
                    new Vector3(0f, KERB_TOP * 0.5f, pavementZ),
                    new Vector3(LENGTH, KERB_TOP, PAVEMENT_WIDTH), pavement);
            }

            // Lamps alternate sides so the road gets even coverage from half the fixtures.
            int index = 0;
            for (float x = -LENGTH * 0.5f + 7f; x < LENGTH * 0.5f; x += LAMP_SPACING, index++)
            {
                int side = (index % 2 == 0) ? 1 : -1;
                float z = side * (ROAD_HALF_WIDTH + 1.2f);
                BuildLamp(root, $"Lamp_{index}", new Vector3(x, KERB_TOP, z), -side, poleMat, lampLens);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, WORLD_SCENE);
            EditorSceneManager.CloseScene(scene, true);
            AssetDatabase.SaveAssets();
            Debug.Log($"[StreetBuilder] Street built at z={CENTRE_Z} with {index} lamps.");
        }

        /// <summary>Pole, gooseneck arm and a downward sodium head leaning over the road.</summary>
        private static void BuildLamp(Transform parent, string name, Vector3 basePos,
            int reachDirection, Material poleMat, Material lensMat)
        {
            var lamp = new GameObject(name);
            lamp.transform.SetParent(parent, false);
            lamp.transform.localPosition = basePos;
            var root = lamp.transform;

            const float height = 6.5f;
            const float reach = 1.8f;
            TestMaterials.Box("Pole", root, new Vector3(0f, height * 0.5f, 0f),
                new Vector3(0.16f, height, 0.16f), poleMat);
            TestMaterials.Box("Arm", root, new Vector3(0f, height, reachDirection * reach * 0.5f),
                new Vector3(0.12f, 0.12f, reach), poleMat);

            var headZ = reachDirection * reach;
            TestMaterials.Box("Head", root, new Vector3(0f, height - 0.1f, headZ),
                new Vector3(0.4f, 0.2f, 0.8f), poleMat);
            TestMaterials.Box("Lens", root, new Vector3(0f, height - 0.22f, headZ),
                new Vector3(0.32f, 0.06f, 0.66f), lensMat);

            // Tilted slightly back toward the road, as street lighting actually is.
            TestMaterials.SpotLight("LampLight", root,
                new Vector3(0f, height - 0.25f, headZ),
                new Vector3(75f, reachDirection > 0 ? 0f : 180f, 0f),
                new Color(1f, 0.76f, 0.45f),
                lumens: 54000f, range: 22f, angle: 110f, volumetric: 3.5f, shadows: false,
                group: "street", nightBoost: 3f);
        }
    }
}
