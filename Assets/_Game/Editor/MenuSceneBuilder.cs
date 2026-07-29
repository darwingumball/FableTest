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
    /// The night harbour behind the main menu: water, a lit pier, a moored boat and a low
    /// silhouette of the town across the bay.
    ///
    /// Laid out for a menu that lives on the LEFT of the screen. The camera sits back and to
    /// the left so the pier leads away to the right and the boat sits in the right third,
    /// clear of the text. Everything of interest is on that side of frame on purpose.
    ///
    /// This is set dressing, not a level: no networking, no colliders, nothing to walk on.
    /// The far buildings are deliberately crude boxes - they read as a silhouette at this
    /// distance and are meant to be replaced.
    ///
    /// Menu: Game/Setup/Build Menu Scene. Idempotent.
    /// </summary>
    public static class MenuSceneBuilder
    {
        private const string MAINMENU_SCENE = "Assets/_Game/Scenes/MainMenu.unity";
        private const string ROOT_NAME = "MenuHarbour";
        private const string CAMERA_NAME = "MenuCamera";

        // Composition: camera left of the action, looking right and slightly down. The pier
        // and the boat live at positive X so they fall in the right two thirds of frame,
        // leaving the left third open water behind the menu column.
        private static readonly Vector3 CameraPosition = new(-4f, 4.6f, -15f);
        private static readonly Vector3 CameraLookAt = new(11f, 0.6f, 8f);
        private const float PIER_X = 15f;
        private static readonly Vector3 BoatMooring = new(9.4f, 0f, 6f);

        [MenuItem("Game/Setup/Build Menu Scene")]
        public static void Build()
        {
            if (!Ready("MenuSceneBuilder")) return;

            var deck = TestMaterials.Lit("MM_Deck", new Color(0.20f, 0.16f, 0.12f), 0.18f, 0f);
            var piling = TestMaterials.Lit("MM_Piling", new Color(0.10f, 0.09f, 0.08f), 0.25f, 0f);
            var metal = TestMaterials.Lit("MM_Metal", new Color(0.10f, 0.11f, 0.13f), 0.55f, 0.6f);
            var hull = TestMaterials.Lit("MM_Hull", new Color(0.24f, 0.09f, 0.08f), 0.42f, 0.1f);
            var town = TestMaterials.Lit("MM_Town", new Color(0.045f, 0.045f, 0.055f), 0.10f, 0f);

            var scene = EditorSceneManager.OpenScene(MAINMENU_SCENE, OpenSceneMode.Additive);
            foreach (var existing in scene.GetRootGameObjects())
                if (existing.name == ROOT_NAME)
                    Object.DestroyImmediate(existing);

            var root = new GameObject(ROOT_NAME);
            SceneManager.MoveGameObjectToScene(root, scene);

            BuildNight(root.transform);
            BuildWater(root.transform);
            BuildPier(root.transform, deck, piling, metal);
            BuildMooredBoat(root.transform, hull, deck, metal);
            BuildTownSilhouette(root.transform, town);

            // Every box here arrives with a collider from CreatePrimitive. Nothing in the
            // menu walks, swims or collides, so all of them are pure broadphase cost.
            foreach (var stray in root.GetComponentsInChildren<Collider>())
                Object.DestroyImmediate(stray);

            AimCamera(scene);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, MAINMENU_SCENE);
            EditorSceneManager.CloseScene(scene, true);
            AssetDatabase.SaveAssets();
            Debug.Log("[MenuSceneBuilder] Night harbour built in MainMenu. " +
                      "Play Boot to see it behind the menu.");
        }

        /// <summary>
        /// Refuses to run when the editor cannot be trusted to do what the caller expects.
        ///
        /// Compiling/updating is the old trap: the menu item runs the PREVIOUS build of this
        /// code and reports success. Play mode is a worse one, because EditorSceneManager
        /// throws outright - so the build does nothing at all while the scene on screen
        /// looks untouched and entirely plausible.
        /// </summary>
        internal static bool Ready(string who)
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning($"[{who}] Cannot build during play mode - scene editing is " +
                                 "blocked there. Exit play mode and run this again.");
                return false;
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                Debug.LogWarning($"[{who}] Editor is busy - wait for the compile to finish, " +
                                 "or this silently runs the previous build of this code.");
                return false;
            }
            return true;
        }

        // ---------------- lighting and sky ----------------

        /// <summary>
        /// Night, driven by a fixed exposure rather than the automatic one. A menu has no
        /// player to adapt around, and auto exposure would slowly brighten the whole scene
        /// while you sit reading the buttons.
        /// </summary>
        private static void BuildNight(Transform parent)
        {
            // TWO directional lights, and the split is the whole trick to a night sky.
            //
            // PhysicallyBasedSky takes ALL its atmospheric scattering from whichever light
            // is marked interactsWithSky. Point that light down from above the horizon and
            // you get a blue daytime sky no matter how dim it is. So the sky light is aimed
            // from BELOW the horizon - the sun has set - and a second light that the sky
            // ignores entirely provides the moonlight.
            var skyGO = new GameObject("MenuSkyLight");
            skyGO.transform.SetParent(parent, false);
            skyGO.transform.rotation = Quaternion.Euler(-9f, 200f, 0f);   // below the horizon

            var skyLight = skyGO.AddComponent<Light>();
            skyLight.type = LightType.Directional;
            skyLight.color = new Color(0.55f, 0.62f, 0.85f);
            skyLight.shadows = LightShadows.None;

            var skyData = skyGO.AddComponent<HDAdditionalLightData>();
            skyData.SetIntensity(0.02f, LightUnit.Lux);
            skyData.EnableColorTemperature(false);
            skyData.interactsWithSky = true;

            var moonGO = new GameObject("MenuMoon");
            moonGO.transform.SetParent(parent, false);
            moonGO.transform.rotation = Quaternion.Euler(34f, 125f, 0f);

            var moon = moonGO.AddComponent<Light>();
            moon.type = LightType.Directional;
            moon.color = new Color(0.66f, 0.75f, 1f);
            moon.shadows = LightShadows.Soft;

            var moonData = moonGO.AddComponent<HDAdditionalLightData>();
            moonData.SetIntensity(0.5f, LightUnit.Lux);
            moonData.EnableColorTemperature(false);
            moonData.interactsWithSky = false;
            moonData.volumetricDimmer = 1.3f;

            var volumeGO = new GameObject("MenuVolume");
            volumeGO.transform.SetParent(parent, false);
            var volume = volumeGO.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 100f;   // above anything the Boot scene carries over

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "MenuHarbourProfile";
            volume.sharedProfile = profile;

            var exposure = profile.Add<Exposure>(overrides: false);
            exposure.mode.overrideState = true;
            exposure.mode.value = ExposureMode.Fixed;
            exposure.fixedExposure.overrideState = true;
            exposure.fixedExposure.value = 9f;      // matches the world's night value

            var sky = profile.Add<PhysicallyBasedSky>(overrides: false);
            sky.groundTint.overrideState = true;
            sky.groundTint.value = new Color(0.05f, 0.05f, 0.06f);

            // Reuse the world's star field rather than generating a second one.
            var stars = AssetDatabase.LoadAssetAtPath<Cubemap>("Assets/_Game/Textures/StarField.asset");
            if (stars != null)
            {
                sky.spaceEmissionTexture.overrideState = true;
                sky.spaceEmissionTexture.value = stars;
                sky.spaceEmissionMultiplier.overrideState = true;
                sky.spaceEmissionMultiplier.value = 260f;
            }

            var visualEnv = profile.Add<VisualEnvironment>(overrides: false);
            visualEnv.skyType.overrideState = true;
            visualEnv.skyType.value = (int)SkyType.PhysicallyBased;

            var fog = profile.Add<Fog>(overrides: false);
            fog.enabled.overrideState = true;
            fog.enabled.value = true;
            fog.meanFreePath.overrideState = true;
            fog.meanFreePath.value = 120f;
            fog.albedo.overrideState = true;
            fog.albedo.value = new Color(0.42f, 0.47f, 0.55f);
            // Volumetrics are what make the pier lamps read as lamps in fog rather than as
            // flat discs, and this scene is cheap enough to afford them.
            fog.enableVolumetricFog.overrideState = true;
            fog.enableVolumetricFog.value = true;
            fog.anisotropy.overrideState = true;
            fog.anisotropy.value = 0.6f;

            AssetDatabase.CreateAsset(profile, "Assets/_Game/UI/MenuHarbourProfile.asset");
        }

        // ---------------- water ----------------

        private static void BuildWater(Transform parent)
        {
            var go = new GameObject("MenuWater");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localScale = new Vector3(600f, 1f, 600f);

            var surface = go.AddComponent<WaterSurface>();
            surface.surfaceType = WaterSurfaceType.OceanSeaLake;
            surface.geometryType = WaterGeometryType.Quad;
            surface.scriptInteractions = true;   // the moored boat samples height from this
            surface.tessellation = true;

            // Sheltered harbour: almost no swell, and what movement there is comes from the
            // ripples. A menu backdrop wants the water alive but not busy.
            surface.repetitionSize = 120f;
            surface.largeWindSpeed = 9f;
            surface.largeChaos = 0.85f;
            surface.largeOrientationValue = 40f;
            surface.ripples = true;
            surface.ripplesWindSpeed = 7f;
            surface.ripplesChaos = 0.8f;

            surface.refractionColor = new Color(0.04f, 0.10f, 0.12f);
            surface.scatteringColor = new Color(0.02f, 0.07f, 0.09f);
            surface.absorptionDistance = 3f;
            surface.caustics = false;   // nothing under this water to catch them

            go.AddComponent<WaterVolume>();
        }

        // ---------------- pier ----------------

        private static void BuildPier(Transform parent, Material deck, Material piling, Material metal)
        {
            var pier = TestMaterials.Node("Pier", parent, Vector3.zero);
            const float deckY = 1.5f;

            // Runs away from the camera toward the upper right of frame.
            TestMaterials.Box("Decking", pier, new Vector3(PIER_X, deckY, 8f),
                new Vector3(4.4f, 0.25f, 34f), deck);

            // Planking, just enough relief to catch the lamps.
            for (int i = 0; i < 17; i++)
                TestMaterials.Box($"Plank_{i}", pier,
                    new Vector3(PIER_X, deckY + 0.14f, -8f + i * 2f),
                    new Vector3(4.4f, 0.06f, 1.5f), deck);

            for (int i = 0; i < 9; i++)
                for (int side = -1; side <= 1; side += 2)
                    TestMaterials.Box($"Piling_{i}_{side}", pier,
                        new Vector3(PIER_X + side * 1.9f, -0.6f, -8f + i * 4f),
                        new Vector3(0.42f, 4.2f, 0.42f), piling);

            // Handrail down the far side only - the near side would fence off the view.
            TestMaterials.Box("Rail", pier, new Vector3(PIER_X + 2f, deckY + 1.05f, 8f),
                new Vector3(0.09f, 0.09f, 34f), metal);
            for (int i = 0; i < 9; i++)
                TestMaterials.Box($"RailPost_{i}", pier,
                    new Vector3(PIER_X + 2f, deckY + 0.55f, -8f + i * 4f),
                    new Vector3(0.08f, 1.1f, 0.08f), metal);

            // Lamps. Warm and low: they are the only real light in the frame, and they are
            // what gives the water something to reflect.
            for (int i = 0; i < 4; i++)
            {
                float z = -5f + i * 7.5f;
                var post = TestMaterials.Node($"Lamp_{i}", pier, new Vector3(PIER_X + 2f, deckY, z));
                TestMaterials.Box("Post", post, new Vector3(0f, 1.5f, 0f),
                    new Vector3(0.12f, 3f, 0.12f), metal);
                TestMaterials.Box("Head", post, new Vector3(-0.3f, 2.95f, 0f),
                    new Vector3(0.55f, 0.22f, 0.4f), metal);
                Lamp(post, new Vector3(-0.3f, 2.8f, 0f),
                     new Color(1f, 0.80f, 0.55f), 2600f, 14f);
            }
        }

        /// <summary>
        /// A practical light built directly rather than through <c>TestMaterials.PointLight</c>,
        /// which attaches the runtime light-group component the world scene drives. Nothing
        /// in MainMenu runs the weather or day/night systems that would feed it.
        /// </summary>
        private static void Lamp(Transform parent, Vector3 localPos, Color color,
                                 float lumens, float range)
        {
            var go = new GameObject("Light");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;

            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.range = range;
            light.shadows = LightShadows.None;   // scenery; shadows here buy nothing

            var data = go.AddComponent<HDAdditionalLightData>();
            data.SetIntensity(lumens, LightUnit.Lumen);
            data.range = range;
            data.volumetricDimmer = 1.6f;
            data.EnableColorTemperature(false);
        }

        // ---------------- boat ----------------

        /// <summary>
        /// The tugboat, moored alongside and left to move with the water. It reuses
        /// <see cref="BoatMotion"/> in its moored mode purely for the wave response - there
        /// is no helm, no rider carry, no collision and no NetworkObject on this one.
        /// </summary>
        private static void BuildMooredBoat(Transform parent, Material hullPaint,
                                            Material deckWood, Material trim)
        {
            var boat = new GameObject("MooredBoat");
            boat.transform.SetParent(parent, false);
            boat.transform.localPosition = BoatMooring;
            boat.transform.localRotation = Quaternion.Euler(0f, 8f, 0f);

            var visual = TestMaterials.Node("Hull", boat.transform, Vector3.zero);

            TestMaterials.Box("HullBody", visual, new Vector3(0f, -0.5f, 0f),
                new Vector3(5.2f, 2.2f, 13f), hullPaint);
            var bow = TestMaterials.Box("Bow", visual, new Vector3(0f, -0.4f, 7.1f),
                new Vector3(3.4f, 2f, 2.6f), hullPaint);
            bow.transform.localRotation = Quaternion.Euler(0f, 45f, 0f);
            TestMaterials.Box("DeckPlate", visual, new Vector3(0f, 0.72f, 0f),
                new Vector3(5.2f, 0.16f, 13f), deckWood);
            TestMaterials.Box("Wheelhouse", visual, new Vector3(0f, 2f, -3.2f),
                new Vector3(3.2f, 2.4f, 3.4f), trim);
            TestMaterials.Box("Funnel", visual, new Vector3(0f, 3.8f, -4.4f),
                new Vector3(1.1f, 1.6f, 1.1f), trim);
            for (int i = -1; i <= 1; i += 2)
                TestMaterials.Box($"Rail_{i}", visual, new Vector3(i * 2.6f, 1.4f, 2f),
                    new Vector3(0.14f, 1f, 8f), trim);

            Lamp(visual, new Vector3(0f, 3.4f, -3.2f), new Color(1f, 0.86f, 0.66f), 3200f, 12f);
            Lamp(visual, new Vector3(0f, 1.6f, 6.4f), new Color(0.65f, 0.88f, 1f), 1400f, 8f);

            var motion = boat.AddComponent<BoatMotion>();
            var so = new SerializedObject(motion);
            so.FindProperty("moored").boolValue = true;
            so.FindProperty("hullVisual").objectReferenceValue = visual;
            so.FindProperty("hullLength").floatValue = 6.5f;
            so.FindProperty("hullBeam").floatValue = 2.6f;
            so.FindProperty("freeboard").floatValue = 1.1f;
            // Alongside in a sheltered harbour: a slow, shallow working of the hull.
            so.FindProperty("waveFollow").floatValue = 0.85f;
            so.FindProperty("maxTiltDegrees").floatValue = 10f;
            so.FindProperty("heaveFollow").floatValue = 0.9f;
            so.FindProperty("heaveSmoothing").floatValue = 1.4f;
            so.FindProperty("smoothing").floatValue = 1.2f;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ---------------- background ----------------

        /// <summary>
        /// Rough boxes across the bay. Deliberately crude and deliberately unlit-dark: at
        /// 90 metres in fog they are a skyline, not architecture, and they are meant to be
        /// thrown away.
        /// </summary>
        private static void BuildTownSilhouette(Transform parent, Material dark)
        {
            var town = TestMaterials.Node("TownSilhouette", parent, Vector3.zero);
            var rng = new System.Random(20260728);

            for (int i = 0; i < 18; i++)
            {
                float x = -70f + i * 8.5f + (float)rng.NextDouble() * 4f;
                float h = 8f + (float)rng.NextDouble() * 22f;
                float w = 6f + (float)rng.NextDouble() * 7f;
                float z = 88f + (float)rng.NextDouble() * 26f;

                TestMaterials.Box($"Block_{i}", town, new Vector3(x, h * 0.5f - 1f, z),
                    new Vector3(w, h, w * 0.8f), dark);

                // One or two lit windows each, so the far shore is not a dead band.
                if (rng.NextDouble() < 0.7)
                    Lamp(town, new Vector3(x, h * 0.55f, z - w * 0.45f),
                         new Color(1f, 0.72f, 0.42f), 900f, 9f);
            }
        }

        // ---------------- camera ----------------

        private static void AimCamera(Scene scene)
        {
            Camera camera = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == CAMERA_NAME) camera = root.GetComponent<Camera>();
                if (camera == null) camera = root.GetComponentInChildren<Camera>();
                if (camera != null) break;
            }

            if (camera == null)
            {
                var go = new GameObject(CAMERA_NAME, typeof(Camera));
                SceneManager.MoveGameObjectToScene(go, scene);
                camera = go.GetComponent<Camera>();
            }

            camera.transform.SetPositionAndRotation(
                CameraPosition,
                Quaternion.LookRotation((CameraLookAt - CameraPosition).normalized, Vector3.up));
            // Wide enough to hold the pier and the boat while the menu occupies the left
            // third of the frame.
            camera.fieldOfView = 55f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 400f;

            var hd = camera.GetComponent<HDAdditionalCameraData>();
            if (hd == null) hd = camera.gameObject.AddComponent<HDAdditionalCameraData>();
            hd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Sky;
            hd.volumeLayerMask = ~0;
        }
    }
}
