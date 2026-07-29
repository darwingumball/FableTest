using Game.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

namespace Game.Editor
{
    /// <summary>
    /// Builds two lighting testbeds in the World scene, one for each end of the intended
    /// art direction:
    ///   - Storefront: neon pink/blue, wet reflective forecourt, glow through the window.
    ///   - Warehouse: dark, grim, red work lights punching beams through volumetric fog.
    ///
    /// Everything is box primitives and HDRP Lit materials so the numbers stay legible and
    /// tweakable - this exists to judge exposure, emissive levels, fog and reflections
    /// before any real art is authored.
    ///
    /// Menu: Game/Setup/Build Test Buildings. Idempotent (rebuilds from scratch each run).
    /// </summary>
    public static class TestBuildingsBuilder
    {
        private const string WORLD_SCENE = "Assets/_Game/Scenes/World.unity";
        private const string MAT_FOLDER = "Assets/_Game/Materials/Test";
        private const string ROOT_NAME = "TestBuildings";

        // Placed clear of the metro/deck content near the origin, inside the 100m ground.
        private static readonly Vector3 StorefrontOrigin = new(-26f, 0f, 18f);
        private static readonly Vector3 WarehouseOrigin = new(26f, 0f, -20f);

        // Emissive levels in nits. Daylight exposure sits around EV 11.8, so signage has to
        // be genuinely bright to read at noon while not blowing out at night.
        private const float NEON_NITS = 2600f;
        private const float STRIP_NITS = 900f;

        [MenuItem("Game/Setup/Build Test Buildings")]
        public static void Build()
        {
            TestMaterials.EnsureFolder(MAT_FOLDER);

            var mats = new Palette
            {
                Concrete = TestMaterials.Lit("TB_Concrete", new Color(0.30f, 0.30f, 0.32f), 0.18f, 0f),
                StoreWall = TestMaterials.Lit("TB_StoreWall", new Color(0.38f, 0.36f, 0.40f), 0.32f, 0f),
                Trim = TestMaterials.Lit("TB_Trim", new Color(0.12f, 0.13f, 0.16f), 0.55f, 0.6f),
                Asphalt = TestMaterials.Lit("TB_Forecourt", new Color(0.10f, 0.10f, 0.11f), 0.25f, 0f),
                Glass = TestMaterials.Lit("TB_Glass", new Color(0.03f, 0.04f, 0.05f), 0.96f, 0f),
                // Metallic near 1 leaves almost no diffuse response, which reads as pure
                // black under practical lighting. Rusted steel is mostly oxide anyway.
                RustMetal = TestMaterials.Lit("TB_RustMetal", new Color(0.22f, 0.19f, 0.17f), 0.35f, 0.25f),
                DarkConcrete = TestMaterials.Lit("TB_DarkConcrete", new Color(0.20f, 0.20f, 0.21f), 0.15f, 0f),
                Crate = TestMaterials.Lit("TB_Crate", new Color(0.30f, 0.24f, 0.17f), 0.22f, 0f),
                NeonPink = TestMaterials.Emissive("TB_NeonPink", new Color(1f, 0.16f, 0.62f), NEON_NITS),
                NeonBlue = TestMaterials.Emissive("TB_NeonBlue", new Color(0.18f, 0.65f, 1f), NEON_NITS),
                NeonRed = TestMaterials.Emissive("TB_NeonRed", new Color(1f, 0.10f, 0.06f), STRIP_NITS),
                WindowGlow = TestMaterials.Emissive("TB_WindowGlow", new Color(1f, 0.72f, 0.42f), 260f),
            };

            var scene = EditorSceneManager.OpenScene(WORLD_SCENE, OpenSceneMode.Additive);

            foreach (var root in scene.GetRootGameObjects())
                if (root.name == ROOT_NAME) Object.DestroyImmediate(root);

            var parent = new GameObject(ROOT_NAME);
            SceneManager.MoveGameObjectToScene(parent, scene);

            BuildStorefront(parent.transform, mats);
            BuildWarehouse(parent.transform, mats);
            BuildQualityVolume(parent.transform);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, WORLD_SCENE);
            EditorSceneManager.CloseScene(scene, true);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[TestBuildingsBuilder] Storefront + warehouse built.");
        }

        private struct Palette
        {
            public Material Concrete, StoreWall, Trim, Asphalt, Glass, RustMetal;
            public Material DarkConcrete, Crate, NeonPink, NeonBlue, NeonRed, WindowGlow;
        }

        // ---------------------------------------------------------------- storefront

        private static void BuildStorefront(Transform parent, Palette m)
        {
            var root = TestMaterials.Node("Storefront", parent, StorefrontOrigin);

            // Wet forecourt. Dark and smooth so neon has something to smear across; the
            // SurfaceWetness component pushes it glassy while it rains.
            TestMaterials.Box("Forecourt", root, new Vector3(0f, 0.04f, 9f), new Vector3(16f, 0.08f, 12f), m.Asphalt);
            root.gameObject.AddComponent<SurfaceWetness>();

            // Shell. 0.2m plinth keeps the interior floor clear of light snow.
            TestMaterials.Box("Floor", root, new Vector3(0f, 0.1f, 0f), new Vector3(8f, 0.2f, 6f), m.Concrete);
            TestMaterials.Box("WallBack", root, new Vector3(0f, 2.2f, -3.1f), new Vector3(8f, 4f, 0.2f), m.StoreWall);
            TestMaterials.Box("WallLeft", root, new Vector3(-3.9f, 2.2f, 0f), new Vector3(0.2f, 4f, 6f), m.StoreWall);
            TestMaterials.Box("WallRight", root, new Vector3(3.9f, 2.2f, 0f), new Vector3(0.2f, 4f, 6f), m.StoreWall);
            TestMaterials.Box("Roof", root, new Vector3(0f, 4.3f, 0f), new Vector3(8.4f, 0.2f, 6.4f), m.Concrete);

            // Facade: display window on the left, walk-in doorway on the right.
            TestMaterials.Box("PillarLeft", root, new Vector3(-3.65f, 2.2f, 3f), new Vector3(0.7f, 4f, 0.2f), m.StoreWall);
            TestMaterials.Box("PillarRight", root, new Vector3(3.65f, 2.2f, 3f), new Vector3(0.7f, 4f, 0.2f), m.StoreWall);
            TestMaterials.Box("Header", root, new Vector3(0f, 3.85f, 3f), new Vector3(8f, 0.7f, 0.2f), m.StoreWall);
            TestMaterials.Box("WindowSill", root, new Vector3(-1.55f, 0.35f, 3f), new Vector3(3.5f, 0.5f, 0.3f), m.Trim);
            TestMaterials.Box("WindowGlass", root, new Vector3(-1.55f, 1.95f, 3f), new Vector3(3.5f, 2.7f, 0.06f), m.Glass);
            TestMaterials.Box("DoorMullion", root, new Vector3(0.45f, 1.85f, 3f), new Vector3(0.3f, 3.3f, 0.22f), m.Trim);

            // Awning + signage.
            TestMaterials.Box("Awning", root, new Vector3(0f, 3.42f, 3.75f), new Vector3(8.4f, 0.16f, 1.7f), m.Trim);
            TestMaterials.Box("SignBacking", root, new Vector3(0f, 4.05f, 3.8f), new Vector3(5.2f, 1.1f, 0.16f), m.Trim);
            TestMaterials.Box("SignFacePink", root, new Vector3(0f, 4.05f, 3.9f), new Vector3(4.6f, 0.62f, 0.06f), m.NeonPink);
            TestMaterials.Box("SignTubeBlueL", root, new Vector3(-2.35f, 4.05f, 3.9f), new Vector3(0.16f, 0.95f, 0.06f), m.NeonBlue);
            TestMaterials.Box("SignTubeBlueR", root, new Vector3(2.35f, 4.05f, 3.9f), new Vector3(0.16f, 0.95f, 0.06f), m.NeonBlue);
            TestMaterials.Box("AwningUnderglow", root, new Vector3(0f, 3.32f, 3.6f), new Vector3(7.6f, 0.08f, 0.32f), m.NeonBlue);

            // Interior glow so the window reads as a lit shop from outside.
            TestMaterials.Box("InteriorSign", root, new Vector3(0f, 2.5f, -2.95f), new Vector3(4.5f, 0.14f, 0.06f), m.NeonPink);
            TestMaterials.Box("CeilingPanel", root, new Vector3(0f, 4.15f, 0.5f), new Vector3(3.4f, 0.08f, 2.2f), m.WindowGlow);

            // Lights. Volumetrics on so the glow carries into fog instead of stopping at
            // the surfaces it hits.
            TestMaterials.PointLight("NeonGlowPink", root, new Vector3(0f, 4.05f, 4.3f),
                new Color(1f, 0.20f, 0.66f), 30000f, 18f, volumetric: 3f,
                group: "neon", nightBoost: 2f);
            TestMaterials.PointLight("NeonGlowBlue", root, new Vector3(0f, 3.25f, 4.0f),
                new Color(0.22f, 0.68f, 1f), 20000f, 16f, volumetric: 3f,
                group: "neon", nightBoost: 2f);
            TestMaterials.PointLight("InteriorFill", root, new Vector3(0f, 3.1f, -0.4f),
                new Color(1f, 0.80f, 0.58f), 20000f, 14f, volumetric: 1.2f,
                group: "storefront", nightBoost: 1.4f);
            TestMaterials.PointLight("InteriorPink", root, new Vector3(-1.6f, 2.1f, 1.4f),
                new Color(1f, 0.28f, 0.70f), 8000f, 10f, volumetric: 1.5f,
                group: "storefront", nightBoost: 1.4f);

            // Spot raking the wet forecourt - the clearest read on reflection quality.
            TestMaterials.SpotLight("ForecourtWash", root, new Vector3(0f, 4.6f, 5.5f),
                new Vector3(62f, 0f, 0f), new Color(0.65f, 0.80f, 1f), 50000f, 24f, 70f,
                volumetric: 2.5f, shadows: true, group: "storefront", nightBoost: 2.5f);

            ReflectionProbeAt("StorefrontProbe", root, new Vector3(0f, 2.5f, 4f),
                new Vector3(26f, 10f, 26f));

            // Keep snow off the shop floor and out from under the awning. Sized by hand
            // rather than from renderer bounds: the bounds would include the forecourt
            // slab, and the forecourt SHOULD collect snow.
            TestMaterials.SnowBlock(root, "SnowBlock_Interior", new Vector3(0f, 0f, 0f), new Vector2(8f, 6f));
            TestMaterials.SnowBlock(root, "SnowBlock_Awning", new Vector3(0f, 0f, 3.9f), new Vector2(8.4f, 1.9f));
        }

        // ---------------------------------------------------------------- warehouse

        private static void BuildWarehouse(Transform parent, Palette m)
        {
            var root = TestMaterials.Node("Warehouse", parent, WarehouseOrigin);
            root.gameObject.AddComponent<SurfaceWetness>();

            TestMaterials.Box("Floor", root, new Vector3(0f, 0.1f, 0f), new Vector3(16f, 0.2f, 12f), m.DarkConcrete);
            TestMaterials.Box("WallBack", root, new Vector3(0f, 3.7f, -6.1f), new Vector3(16f, 7f, 0.2f), m.RustMetal);
            TestMaterials.Box("WallLeft", root, new Vector3(-7.9f, 3.7f, 0f), new Vector3(0.2f, 7f, 12f), m.RustMetal);
            TestMaterials.Box("WallRight", root, new Vector3(7.9f, 3.7f, 0f), new Vector3(0.2f, 7f, 12f), m.RustMetal);
            TestMaterials.Box("Roof", root, new Vector3(0f, 7.3f, 0f), new Vector3(16.4f, 0.2f, 12.4f), m.DarkConcrete);

            // Open bay door: two side panels and a header leave a 6m x 5m mouth.
            TestMaterials.Box("FrontLeft", root, new Vector3(-5.5f, 3.7f, 6f), new Vector3(5f, 7f, 0.2f), m.RustMetal);
            TestMaterials.Box("FrontRight", root, new Vector3(5.5f, 3.7f, 6f), new Vector3(5f, 7f, 0.2f), m.RustMetal);
            TestMaterials.Box("FrontHeader", root, new Vector3(0f, 6.1f, 6f), new Vector3(6f, 2.2f, 0.2f), m.RustMetal);

            // Loading dock lip + ramp so the doorway reads as industrial.
            TestMaterials.Box("DockLip", root, new Vector3(0f, 0.15f, 6.6f), new Vector3(6f, 0.3f, 1.4f), m.DarkConcrete);

            // Structure for the beams to cut across.
            TestMaterials.Box("ColumnL", root, new Vector3(-4f, 3.7f, -1f), new Vector3(0.4f, 7f, 0.4f), m.RustMetal);
            TestMaterials.Box("ColumnR", root, new Vector3(4f, 3.7f, -1f), new Vector3(0.4f, 7f, 0.4f), m.RustMetal);
            TestMaterials.Box("Gantry", root, new Vector3(0f, 6.9f, -1f), new Vector3(9f, 0.3f, 0.4f), m.RustMetal);

            // Clutter to catch light and cast shadows.
            TestMaterials.Box("Crate0", root, new Vector3(-5.2f, 0.9f, -3.4f), new Vector3(1.6f, 1.4f, 1.6f), m.Crate);
            TestMaterials.Box("Crate1", root, new Vector3(-5.2f, 2.3f, -3.4f), new Vector3(1.3f, 1.2f, 1.3f), m.Crate);
            TestMaterials.Box("Crate2", root, new Vector3(5.6f, 0.8f, -4.2f), new Vector3(1.8f, 1.2f, 1.4f), m.Crate);
            TestMaterials.Box("Crate3", root, new Vector3(3.0f, 0.7f, 2.6f), new Vector3(1.5f, 1.0f, 1.5f), m.Crate);
            TestMaterials.Box("Pallet", root, new Vector3(-2.4f, 0.32f, 3.2f), new Vector3(2.2f, 0.24f, 1.4f), m.Crate);

            // Red strip lights along the ceiling, plus the fixtures that emit them.
            for (int i = 0; i < 3; i++)
            {
                float z = -3.5f + i * 3.5f;
                TestMaterials.Box($"StripHousing{i}", root, new Vector3(0f, 6.85f, z), new Vector3(9f, 0.3f, 0.4f), m.Trim);
                TestMaterials.Box($"StripRed{i}", root, new Vector3(0f, 6.68f, z), new Vector3(8.4f, 0.06f, 0.28f), m.NeonRed);
                TestMaterials.SpotLight($"WorkLight{i}", root, new Vector3(0f, 6.55f, z), new Vector3(90f, 0f, 0f),
                    new Color(1f, 0.13f, 0.08f), 60000f, 22f, 110f, volumetric: 4f, shadows: i == 1,
                    group: "warehouse", nightBoost: 3.5f);
            }

            // Emergency light over the bay: the one thing visible from outside.
            TestMaterials.Box("BayLampHousing", root, new Vector3(0f, 5.4f, 5.7f), new Vector3(0.8f, 0.4f, 0.5f), m.Trim);
            TestMaterials.Box("BayLampLens", root, new Vector3(0f, 5.4f, 5.42f), new Vector3(0.6f, 0.26f, 0.06f), m.NeonRed);
            TestMaterials.SpotLight("BayWash", root, new Vector3(0f, 5.3f, 5.4f), new Vector3(28f, 0f, 0f),
                new Color(1f, 0.16f, 0.10f), 70000f, 26f, 95f, volumetric: 4f, shadows: true,
                group: "warehouse", nightBoost: 3.5f);

            // A single cold source at the back so the red has something to contrast against.
            TestMaterials.PointLight("BackdoorLeak", root, new Vector3(-6.5f, 2.2f, -5.6f),
                new Color(0.55f, 0.72f, 1f), 15000f, 10f, volumetric: 2f,
                group: "warehouse", nightBoost: 2f);

            ReflectionProbeAt("WarehouseProbe", root, new Vector3(0f, 3f, 0f),
                new Vector3(20f, 14f, 16f));

            // Whole footprint is roofed; the dock lip outside stays open to the weather.
            TestMaterials.SnowBlock(root, "SnowBlock_Interior", new Vector3(0f, 0f, 0f), new Vector2(16f, 12f));
        }

        // ---------------------------------------------------------------- quality volume

        /// <summary>
        /// Screen space reflections, on its own low-priority global volume so it never
        /// fights the runtime profile WeatherManager builds.
        /// </summary>
        private static void BuildQualityVolume(Transform parent)
        {
            var go = TestMaterials.Node("GraphicsQualityVolume", parent, Vector3.zero);
            var volume = go.gameObject.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = -10f;

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "TestGraphicsQuality";

            // Add(overrides: true) flips every parameter's overrideState on, so only the
            // values need setting below.
            var ssr = profile.Add<ScreenSpaceReflection>(true);
            ssr.enabled.value = true;
            // These are quality-preset-backed properties: the getter ignores the local
            // value unless the quality level is explicitly marked as an override.
            ssr.quality.levelAndOverride = ((int)ScalableSettingLevelParameter.Level.High, true);
            // Rough surfaces get nothing useful out of SSR; start it where wet asphalt and
            // glass live so the cost goes somewhere visible.
            ssr.minSmoothness = 0.4f;
            ssr.smoothnessFadeStart = 0.5f;

            var ao = profile.Add<ScreenSpaceAmbientOcclusion>(true);
            ao.intensity.overrideState = true;
            ao.intensity.value = 0.65f;

            volume.sharedProfile = profile;

            const string path = MAT_FOLDER + "/TestGraphicsQuality.asset";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(profile, path);
            volume.sharedProfile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
        }

        // ---------------------------------------------------------------- helpers
        // Geometry, materials and light authoring live in TestMaterials so the street and
        // apartment builders share exactly the same rules (notably the HDRP intensity
        // ordering, which is easy to get wrong in isolation).

        private static void ReflectionProbeAt(string name, Transform parent, Vector3 localPos, Vector3 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;

            var probe = go.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Realtime;
            probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.IndividualFaces;
            probe.size = size;
            probe.boxProjection = true;
            probe.hdr = true;
            probe.resolution = 128;
            // Cull the snow ground. A probe re-renders everything it can see once per face,
            // and the snow mesh alone was turning 1.5M triangles a frame into 15.9M while
            // adding essentially nothing to a 128px cubemap.
            int snowLayer = LayerMask.NameToLayer(SnowGroundBuilder.SNOW_LAYER);
            if (snowLayer >= 0) probe.cullingMask = ~(1 << snowLayer);

            if (go.GetComponent<HDAdditionalReflectionData>() == null)
                go.AddComponent<HDAdditionalReflectionData>();
            go.AddComponent<PeriodicReflectionProbe>();
        }
    }
}
