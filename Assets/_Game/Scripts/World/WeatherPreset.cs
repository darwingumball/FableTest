using UnityEngine;

namespace Game.World
{
    public enum WeatherType : byte { Clear, Overcast, Fog, Rain, Storm, Snow }

    /// <summary>
    /// Designer-tunable look for one weather type. Everything here is a plain number so
    /// <see cref="Game.Net.WeatherManager"/> can LERP between two presets during a
    /// transition and write the blended values straight into the HDRP volume overrides -
    /// no per-preset profile assets to keep in sync.
    ///
    /// Assets live in _Game/Resources/Weather (one per <see cref="type"/>).
    /// Tune these in the inspector; the admin console ("weather rain 20 0.7") and
    /// <see cref="WeatherEventTrigger"/> drive which preset is active.
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Weather Preset", fileName = "weather_new")]
    public class WeatherPreset : ScriptableObject
    {
        public WeatherType type;

        [Header("Fog")]
        [Tooltip("Visibility distance in metres. Smaller = thicker fog.")]
        [Min(1f)] public float fogMeanFreePath = 400f;
        public bool volumetricFog = true;
        [Tooltip("How far volumetric fog is computed from the camera (metres).")]
        [Min(1f)] public float fogDepthExtent = 90f;
        public Color fogAlbedo = new(0.75f, 0.78f, 0.82f);
        [Range(-1f, 1f)] public float fogAnisotropy;
        [Tooltip("Height where fog starts thinning out.")]
        public float fogMaximumHeight = 60f;

        [Header("Clouds")]
        public bool cloudsEnabled = true;
        [Tooltip("0 = wispy, 1 = solid overcast.")]
        [Range(0f, 1f)] public float cloudDensity = 0.4f;
        [Range(0f, 1f)] public float cloudShapeFactor = 0.9f;
        [Range(0f, 1f)] public float cloudErosion = 0.8f;
        [Tooltip("Cloud deck height in metres.")]
        [Min(100f)] public float cloudAltitude = 1200f;
        [Min(100f)] public float cloudThickness = 2000f;
        [Tooltip("Darkens the clouds for storm looks.")]
        [Range(0f, 1f)] public float cloudSunDimmer = 1f;
        [Tooltip("Cloud drift speed (km/h).")]
        public float cloudWindSpeed = 20f;

        [Header("Precipitation (particles/sec around the player)")]
        public float rainRate;
        public float snowRate;

        [Header("Environment")]
        [Range(0f, 1f)] public float windStrength = 0.1f;
        [Tooltip("Snow depth recovered per second at full intensity (fills in trails).")]
        public float snowAccumulationRate;
        public bool lightning;

        private static WeatherPreset[] _all;

        public static WeatherPreset Get(WeatherType type)
        {
            _all ??= Resources.LoadAll<WeatherPreset>("Weather");
            foreach (var p in _all)
                if (p != null && p.type == type) return p;
            return null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _all = null;
    }
}
