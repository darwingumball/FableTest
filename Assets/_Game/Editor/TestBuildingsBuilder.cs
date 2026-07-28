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
            EnsureFolder(MAT_FOLDER);

            var mats = new Palette
            {
                Concrete = Lit("TB_Concrete", new Color(0.30f, 0.30f, 0.32f), 0.18f, 0f),
                StoreWall = Lit("TB_StoreWall", new Color(0.38f, 0.36f, 0.40f), 0.32f, 0f),
                Trim = Lit("TB_Trim", new Color(0.12f, 0.13f, 0.16f), 0.55f, 0.6f),
                Asphalt = Lit("TB_Asphalt", new Color(0.06f, 0.06f, 0.07f), 0.25f, 0f),
                Glass = Lit("TB_Glass", new Color(0.03f, 0.04f, 0.05f), 0.96f, 0f),
                // Metallic near 1 leaves almost no diffuse response, which reads as pure
                // black under practical lighting. Rusted steel is mostly oxide anyway.
                RustMetal = Lit("TB_RustMetal", new Color(0.22f, 0.19f, 0.17f), 0.35f, 0.25f),
                DarkConcrete = Lit("TB_DarkConcrete", new Color(0.20f, 0.20f, 0.21f), 0.15f, 0f),
                Crate = Lit("TB_Crate", new Color(0.30f, 0.24f, 0.17f), 0.22f, 0f),
                NeonPink = Emissive("TB_NeonPink", new Color(1f, 0.16f, 0.62f), NEON_NITS),
                NeonBlue = Emissive("TB_NeonBlue", new Color(0.18f, 0.65f, 1f), NEON_NITS),
                NeonRed = Emissive("TB_NeonRed", new Color(1f, 0.10f, 0.06f), STRIP_NITS),
                WindowGlow = Emissive("TB_WindowGlow", new Color(1f, 0.72f, 0.42f), 260f),
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
            var root = Node("Storefront", parent, StorefrontOrigin);

            // Wet forecourt. Dark and smooth so neon has something to smear across; the
            // SurfaceWetness component pushes it glassy while it rains.
            Box("Forecourt", root, new Vector3(0f, 0.04f, 9f), new Vector3(16f, 0.08f, 12f), m.Asphalt);
            root.gameObject.AddComponent<SurfaceWetness>();

            // Shell. 0.2m plinth keeps the interior floor clear of light snow.
            Box("Floor", root, new Vector3(0f, 0.1f, 0f), new Vector3(8f, 0.2f, 6f), m.Concrete);
            Box("WallBack", root, new Vector3(0f, 2.2f, -3.1f), new Vector3(8f, 4f, 0.2f), m.StoreWall);
            Box("WallLeft", root, new Vector3(-3.9f, 2.2f, 0f), new Vector3(0.2f, 4f, 6f), m.StoreWall);
            Box("WallRight", root, new Vector3(3.9f, 2.2f, 0f), new Vector3(0.2f, 4f, 6f), m.StoreWall);
            Box("Roof", root, new Vector3(0f, 4.3f, 0f), new Vector3(8.4f, 0.2f, 6.4f), m.Concrete);

            // Facade: display window on the left, walk-in doorway on the right.
            Box("PillarLeft", root, new Vector3(-3.65f, 2.2f, 3f), new Vector3(0.7f, 4f, 0.2f), m.StoreWall);
            Box("PillarRight", root, new Vector3(3.65f, 2.2f, 3f), new Vector3(0.7f, 4f, 0.2f), m.StoreWall);
            Box("Header", root, new Vector3(0f, 3.85f, 3f), new Vector3(8f, 0.7f, 0.2f), m.StoreWall);
            Box("WindowSill", root, new Vector3(-1.55f, 0.35f, 3f), new Vector3(3.5f, 0.5f, 0.3f), m.Trim);
            Box("WindowGlass", root, new Vector3(-1.55f, 1.95f, 3f), new Vector3(3.5f, 2.7f, 0.06f), m.Glass);
            Box("DoorMullion", root, new Vector3(0.45f, 1.85f, 3f), new Vector3(0.3f, 3.3f, 0.22f), m.Trim);

            // Awning + signage.
            Box("Awning", root, new Vector3(0f, 3.42f, 3.75f), new Vector3(8.4f, 0.16f, 1.7f), m.Trim);
            Box("SignBacking", root, new Vector3(0f, 4.05f, 3.8f), new Vector3(5.2f, 1.1f, 0.16f), m.Trim);
            Box("SignFacePink", root, new Vector3(0f, 4.05f, 3.9f), new Vector3(4.6f, 0.62f, 0.06f), m.NeonPink);
            Box("SignTubeBlueL", root, new Vector3(-2.35f, 4.05f, 3.9f), new Vector3(0.16f, 0.95f, 0.06f), m.NeonBlue);
            Box("SignTubeBlueR", root, new Vector3(2.35f, 4.05f, 3.9f), new Vector3(0.16f, 0.95f, 0.06f), m.NeonBlue);
            Box("AwningUnderglow", root, new Vector3(0f, 3.32f, 3.6f), new Vector3(7.6f, 0.08f, 0.32f), m.NeonBlue);

            // Interior glow so the window reads as a lit shop from outside.
            Box("InteriorSign", root, new Vector3(0f, 2.5f, -2.95f), new Vector3(4.5f, 0.14f, 0.06f), m.NeonPink);
            Box("CeilingPanel", root, new Vector3(0f, 4.15f, 0.5f), new Vector3(3.4f, 0.08f, 2.2f), m.WindowGlow);

            // Lights. Volumetrics on so the glow carries into fog instead of stopping at
            // the surfaces it hits.
            PointLight("NeonGlowPink", root, new Vector3(0f, 4.05f, 4.3f),
                new Color(1f, 0.20f, 0.66f), 30000f, 18f, volumetric: 3f);
            PointLight("NeonGlowBlue", root, new Vector3(0f, 3.25f, 4.0f),
                new Color(0.22f, 0.68f, 1f), 20000f, 16f, volumetric: 3f);
            PointLight("InteriorFill", root, new Vector3(0f, 3.1f, -0.4f),
                new Color(1f, 0.80f, 0.58f), 20000f, 14f, volumetric: 1.2f);
            PointLight("InteriorPink", root, new Vector3(-1.6f, 2.1f, 1.4f),
                new Color(1f, 0.28f, 0.70f), 8000f, 10f, volumetric: 1.5f);

            // Spot raking the wet forecourt - the clearest read on reflection quality.
            SpotLight("ForecourtWash", root, new Vector3(0f, 4.6f, 5.5f),
                new Vector3(62f, 0f, 0f), new Color(0.65f, 0.80f, 1f), 50000f, 24f, 70f,
                volumetric: 2.5f, shadows: true);

            ReflectionProbeAt("StorefrontProbe", root, new Vector3(0f, 2.5f, 4f),
                new Vector3(26f, 10f, 26f));
        }

        // ---------------------------------------------------------------- warehouse

        private static void BuildWarehouse(Transform parent, Palette m)
        {
            var root = Node("Warehouse", parent, WarehouseOrigin);
            root.gameObject.AddComponent<SurfaceWetness>();

            Box("Floor", root, new Vector3(0f, 0.1f, 0f), new Vector3(16f, 0.2f, 12f), m.DarkConcrete);
            Box("WallBack", root, new Vector3(0f, 3.7f, -6.1f), new Vector3(16f, 7f, 0.2f), m.RustMetal);
            Box("WallLeft", root, new Vector3(-7.9f, 3.7f, 0f), new Vector3(0.2f, 7f, 12f), m.RustMetal);
            Box("WallRight", root, new Vector3(7.9f, 3.7f, 0f), new Vector3(0.2f, 7f, 12f), m.RustMetal);
            Box("Roof", root, new Vector3(0f, 7.3f, 0f), new Vector3(16.4f, 0.2f, 12.4f), m.DarkConcrete);

            // Open bay door: two side panels and a header leave a 6m x 5m mouth.
            Box("FrontLeft", root, new Vector3(-5.5f, 3.7f, 6f), new Vector3(5f, 7f, 0.2f), m.RustMetal);
            Box("FrontRight", root, new Vector3(5.5f, 3.7f, 6f), new Vector3(5f, 7f, 0.2f), m.RustMetal);
            Box("FrontHeader", root, new Vector3(0f, 6.1f, 6f), new Vector3(6f, 2.2f, 0.2f), m.RustMetal);

            // Loading dock lip + ramp so the doorway reads as industrial.
            Box("DockLip", root, new Vector3(0f, 0.15f, 6.6f), new Vector3(6f, 0.3f, 1.4f), m.DarkConcrete);

            // Structure for the beams to cut across.
            Box("ColumnL", root, new Vector3(-4f, 3.7f, -1f), new Vector3(0.4f, 7f, 0.4f), m.RustMetal);
            Box("ColumnR", root, new Vector3(4f, 3.7f, -1f), new Vector3(0.4f, 7f, 0.4f), m.RustMetal);
            Box("Gantry", root, new Vector3(0f, 6.9f, -1f), new Vector3(9f, 0.3f, 0.4f), m.RustMetal);

            // Clutter to catch light and cast shadows.
            Box("Crate0", root, new Vector3(-5.2f, 0.9f, -3.4f), new Vector3(1.6f, 1.4f, 1.6f), m.Crate);
            Box("Crate1", root, new Vector3(-5.2f, 2.3f, -3.4f), new Vector3(1.3f, 1.2f, 1.3f), m.Crate);
            Box("Crate2", root, new Vector3(5.6f, 0.8f, -4.2f), new Vector3(1.8f, 1.2f, 1.4f), m.Crate);
            Box("Crate3", root, new Vector3(3.0f, 0.7f, 2.6f), new Vector3(1.5f, 1.0f, 1.5f), m.Crate);
            Box("Pallet", root, new Vector3(-2.4f, 0.32f, 3.2f), new Vector3(2.2f, 0.24f, 1.4f), m.Crate);

            // Red strip lights along the ceiling, plus the fixtures that emit them.
            for (int i = 0; i < 3; i++)
            {
                float z = -3.5f + i * 3.5f;
                Box($"StripHousing{i}", root, new Vector3(0f, 6.85f, z), new Vector3(9f, 0.3f, 0.4f), m.Trim);
                Box($"StripRed{i}", root, new Vector3(0f, 6.68f, z), new Vector3(8.4f, 0.06f, 0.28f), m.NeonRed);
                SpotLight($"WorkLight{i}", root, new Vector3(0f, 6.55f, z), new Vector3(90f, 0f, 0f),
                    new Color(1f, 0.13f, 0.08f), 60000f, 22f, 110f, volumetric: 4f, shadows: true);
            }

            // Emergency light over the bay: the one thing visible from outside.
            Box("BayLampHousing", root, new Vector3(0f, 5.4f, 5.7f), new Vector3(0.8f, 0.4f, 0.5f), m.Trim);
            Box("BayLampLens", root, new Vector3(0f, 5.4f, 5.42f), new Vector3(0.6f, 0.26f, 0.06f), m.NeonRed);
            SpotLight("BayWash", root, new Vector3(0f, 5.3f, 5.4f), new Vector3(28f, 0f, 0f),
                new Color(1f, 0.16f, 0.10f), 70000f, 26f, 95f, volumetric: 4f, shadows: true);

            // A single cold source at the back so the red has something to contrast against.
            PointLight("BackdoorLeak", root, new Vector3(-6.5f, 2.2f, -5.6f),
                new Color(0.55f, 0.72f, 1f), 15000f, 10f, volumetric: 2f);

            ReflectionProbeAt("WarehouseProbe", root, new Vector3(0f, 3f, 0f),
                new Vector3(20f, 14f, 16f));
        }

        // ---------------------------------------------------------------- quality volume

        /// <summary>
        /// Screen space reflections, on its own low-priority global volume so it never
        /// fights the runtime profile WeatherManager builds.
        /// </summary>
        private static void BuildQualityVolume(Transform parent)
        {
            var go = Node("GraphicsQualityVolume", parent, Vector3.zero);
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

        // ---------------------------------------------------------------- primitives

        private static Transform Node(string name, Transform parent, Vector3 localPos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            return go.transform;
        }

        private static GameObject Box(string name, Transform parent, Vector3 localPos,
            Vector3 scale, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        private static void PointLight(string name, Transform parent, Vector3 localPos,
            Color color, float lumens, float range, float volumetric)
        {
            MakeLight(name, parent, localPos, Vector3.zero, LightType.Point, color, lumens,
                range, spotAngle: 0f, volumetric, shadows: false);
        }

        private static void SpotLight(string name, Transform parent, Vector3 localPos,
            Vector3 euler, Color color, float lumens, float range, float angle,
            float volumetric, bool shadows)
        {
            MakeLight(name, parent, localPos, euler, LightType.Spot, color, lumens, range,
                angle, volumetric, shadows);
        }

        private static void MakeLight(string name, Transform parent, Vector3 localPos,
            Vector3 euler, LightType type, Color color, float lumens, float range,
            float spotAngle, float volumetric, bool shadows)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localEulerAngles = euler;

            var light = go.AddComponent<Light>();
            light.type = type;
            light.color = color;
            light.shadows = shadows ? LightShadows.Soft : LightShadows.None;

            var hd = go.GetComponent<HDAdditionalLightData>();
            if (hd == null) hd = go.AddComponent<HDAdditionalLightData>();

            // Order matters here. HDRP re-derives intensity whenever the emitting shape
            // changes, so range and cone angle must be final BEFORE the value is written.
            // Calling SetIntensity() first and SetSpotAngle() after runs the
            // lumen->candela conversion a second time and lands ~160x too dim.
            hd.range = range;
            if (type == LightType.Spot) hd.SetSpotAngle(spotAngle);
            hd.lightUnit = LightUnit.Lumen;
            hd.intensity = lumens;

            // Without this, lights illuminate surfaces but leave the fog untouched - no
            // beams, no haze around the neon, which is most of the look we are after.
            hd.affectsVolumetric = true;
            hd.volumetricDimmer = volumetric;
        }

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

            if (go.GetComponent<HDAdditionalReflectionData>() == null)
                go.AddComponent<HDAdditionalReflectionData>();
            go.AddComponent<PeriodicReflectionProbe>();
        }

        // ---------------------------------------------------------------- materials

        private static Material Lit(string name, Color baseColor, float smoothness, float metallic)
        {
            var mat = LoadOrCreate(name);
            mat.SetColor("_BaseColor", baseColor);
            mat.SetFloat("_Smoothness", smoothness);
            mat.SetFloat("_Metallic", metallic);
            HDMaterial.ValidateMaterial(mat);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material Emissive(string name, Color color, float nits)
        {
            var mat = LoadOrCreate(name);
            // Neon tubes read as the light source itself, so the base colour is nearly
            // black - all the visible energy comes from emission.
            mat.SetColor("_BaseColor", color * 0.08f);
            mat.SetFloat("_Smoothness", 0.6f);
            mat.SetFloat("_Metallic", 0f);
            HDMaterial.SetUseEmissiveIntensity(mat, true);
            HDMaterial.SetEmissiveColor(mat, color);
            HDMaterial.SetEmissiveIntensity(mat, nits, EmissiveIntensityUnit.Nits);
            HDMaterial.ValidateMaterial(mat);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material LoadOrCreate(string name)
        {
            string path = $"{MAT_FOLDER}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            var shader = Shader.Find("HDRP/Lit");
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.shader = shader;
            return mat;
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            var parts = folder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
