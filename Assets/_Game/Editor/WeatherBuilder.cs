using Game.Net;
using Game.World;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Game.Editor
{
    /// <summary>
    /// Authors the six WeatherPreset assets (all values are inspector-tunable afterwards)
    /// and wires the Environment rig in the World scene: NetworkTimeSync, WeatherManager
    /// with its runtime Volume, and the precipitation rig.
    /// Menu: Game/Setup/Build Weather. Idempotent — but it DOES reset preset values to the
    /// defaults below, so tune presets in the inspector and avoid re-running, or edit the
    /// defaults here.
    /// </summary>
    public static class WeatherBuilder
    {
        private const string WEATHER_RES = "Assets/_Game/Resources/Weather";
        private const string WORLD_SCENE = "Assets/_Game/Scenes/World.unity";

        private struct Def
        {
            public WeatherType type;
            public float fog, depthExtent, fogHeight;
            public bool volumetric;
            public Color fogAlbedo;
            public bool clouds;
            public float density, shape, erosion, altitude, thickness, sunDimmer, windSpeed;
            public float rain, snow, wind, accumulation;
            public bool lightning;
        }

        private static readonly Def[] Defs =
        {
            new() { type = WeatherType.Clear,
                    fog = 1500f, depthExtent = 80f, fogHeight = 40f, volumetric = true,
                    fogAlbedo = new Color(0.80f, 0.85f, 0.92f),
                    clouds = true, density = 0.15f, shape = 0.85f, erosion = 0.8f,
                    altitude = 1800f, thickness = 1500f, sunDimmer = 1f, windSpeed = 15f,
                    wind = 0.05f },

            new() { type = WeatherType.Overcast,
                    fog = 600f, depthExtent = 90f, fogHeight = 60f, volumetric = true,
                    fogAlbedo = new Color(0.72f, 0.74f, 0.78f),
                    clouds = true, density = 0.75f, shape = 0.92f, erosion = 0.6f,
                    altitude = 1100f, thickness = 2400f, sunDimmer = 0.55f, windSpeed = 28f,
                    wind = 0.2f },

            new() { type = WeatherType.Fog,
                    fog = 45f, depthExtent = 110f, fogHeight = 80f, volumetric = true,
                    fogAlbedo = new Color(0.70f, 0.72f, 0.74f),
                    clouds = true, density = 0.5f, shape = 0.9f, erosion = 0.7f,
                    altitude = 900f, thickness = 1800f, sunDimmer = 0.6f, windSpeed = 8f,
                    wind = 0.05f },

            new() { type = WeatherType.Rain,
                    fog = 220f, depthExtent = 100f, fogHeight = 70f, volumetric = true,
                    fogAlbedo = new Color(0.66f, 0.70f, 0.76f),
                    clouds = true, density = 0.85f, shape = 0.95f, erosion = 0.55f,
                    altitude = 900f, thickness = 2600f, sunDimmer = 0.4f, windSpeed = 40f,
                    rain = 900f, wind = 0.4f },

            new() { type = WeatherType.Storm,
                    fog = 110f, depthExtent = 120f, fogHeight = 90f, volumetric = true,
                    fogAlbedo = new Color(0.55f, 0.58f, 0.64f),
                    clouds = true, density = 1f, shape = 1f, erosion = 0.45f,
                    altitude = 700f, thickness = 3200f, sunDimmer = 0.22f, windSpeed = 75f,
                    rain = 1800f, wind = 0.85f, lightning = true },

            new() { type = WeatherType.Snow,
                    fog = 160f, depthExtent = 100f, fogHeight = 70f, volumetric = true,
                    fogAlbedo = new Color(0.82f, 0.85f, 0.90f),
                    clouds = true, density = 0.8f, shape = 0.9f, erosion = 0.5f,
                    altitude = 800f, thickness = 2400f, sunDimmer = 0.45f, windSpeed = 25f,
                    snow = 700f, wind = 0.3f, accumulation = 0.05f },
        };

        [MenuItem("Game/Setup/Build Weather")]
        public static void Build()
        {
            EnsureFolder(WEATHER_RES);
            foreach (var def in Defs) BuildPreset(def);
            PatchWorldScene();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[WeatherBuilder] Weather presets (fog + clouds + precipitation) and scene rig built.");
        }

        private static void BuildPreset(Def def)
        {
            string path = $"{WEATHER_RES}/weather_{def.type}.asset";
            var preset = AssetDatabase.LoadAssetAtPath<WeatherPreset>(path);
            if (preset == null)
            {
                preset = ScriptableObject.CreateInstance<WeatherPreset>();
                AssetDatabase.CreateAsset(preset, path);
            }

            preset.type = def.type;
            preset.fogMeanFreePath = def.fog;
            preset.volumetricFog = def.volumetric;
            preset.fogDepthExtent = def.depthExtent;
            preset.fogAlbedo = def.fogAlbedo;
            preset.fogMaximumHeight = def.fogHeight;
            preset.cloudsEnabled = def.clouds;
            preset.cloudDensity = def.density;
            preset.cloudShapeFactor = def.shape;
            preset.cloudErosion = def.erosion;
            preset.cloudAltitude = def.altitude;
            preset.cloudThickness = def.thickness;
            preset.cloudSunDimmer = def.sunDimmer;
            preset.cloudWindSpeed = def.windSpeed;
            preset.rainRate = def.rain;
            preset.snowRate = def.snow;
            preset.windStrength = def.wind;
            preset.snowAccumulationRate = def.accumulation;
            preset.lightning = def.lightning;
            EditorUtility.SetDirty(preset);
        }

        private static void PatchWorldScene()
        {
            var scene = EditorSceneManager.OpenScene(WORLD_SCENE, OpenSceneMode.Additive);

            foreach (var go in scene.GetRootGameObjects())
                if (go.name == "Sun") Ensure<SunController>(go);

            var env = FindOrCreateRoot(scene, "Environment");
            Ensure<NetworkObject>(env);
            Ensure<NetworkTimeSync>(env);
            var weather = Ensure<WeatherManager>(env);

            // Single volume driven numerically by WeatherManager; drop the old A/B rig.
            foreach (var stale in new[] { "WeatherVolumeA", "WeatherVolumeB" })
            {
                var t = env.transform.Find(stale);
                if (t != null) Object.DestroyImmediate(t.gameObject);
            }

            var volume = Ensure<Volume>(env);
            volume.isGlobal = true;
            volume.priority = 20;
            var so = new SerializedObject(weather);
            so.FindProperty("weatherVolume").objectReferenceValue = volume;
            so.ApplyModifiedPropertiesWithoutUndo();

            var precip = FindOrCreateChild(env, "Precipitation");
            Ensure<PrecipitationController>(precip);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, WORLD_SCENE);
            EditorSceneManager.CloseScene(scene, true);
        }

        private static GameObject FindOrCreateRoot(Scene scene, string name)
        {
            foreach (var go in scene.GetRootGameObjects())
                if (go.name == name) return go;
            var created = new GameObject(name);
            SceneManager.MoveGameObjectToScene(created, scene);
            return created;
        }

        private static GameObject FindOrCreateChild(GameObject parent, string name)
        {
            var t = parent.transform.Find(name);
            if (t != null) return t.gameObject;
            var created = new GameObject(name);
            created.transform.SetParent(parent.transform, false);
            return created;
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
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
