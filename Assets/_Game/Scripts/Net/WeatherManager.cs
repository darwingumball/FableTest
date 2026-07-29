using System;
using Game.World;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Game.Net
{
    /// <summary>
    /// Server-authoritative weather. State is one NetworkVariable; the transition blend is
    /// computed deterministically from ServerTime on every peer, so a client joining
    /// mid-transition lands on exactly the same sky as everyone else.
    ///
    /// Presentation: a single runtime Volume whose Fog and VolumetricClouds overrides are
    /// numerically LERPed between the two <see cref="WeatherPreset"/>s. Blending numbers
    /// (rather than crossfading two profiles) means fog density and cloud cover move
    /// smoothly and every value stays tunable in one inspector per weather type.
    /// </summary>
    public class WeatherManager : NetworkBehaviour
    {
        public static WeatherManager Instance { get; private set; }

        public struct WeatherNetState : INetworkSerializable, IEquatable<WeatherNetState>
        {
            public byte Current;
            public byte Target;
            public double TransitionStartServerTime;
            public float TransitionDuration;
            public float Intensity;

            public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
            {
                serializer.SerializeValue(ref Current);
                serializer.SerializeValue(ref Target);
                serializer.SerializeValue(ref TransitionStartServerTime);
                serializer.SerializeValue(ref TransitionDuration);
                serializer.SerializeValue(ref Intensity);
            }

            public bool Equals(WeatherNetState other) =>
                Current == other.Current && Target == other.Target
                && TransitionStartServerTime.Equals(other.TransitionStartServerTime)
                && TransitionDuration.Equals(other.TransitionDuration)
                && Intensity.Equals(other.Intensity);
        }

        private readonly NetworkVariable<WeatherNetState> _state = new(
            new WeatherNetState { Current = 0, Target = 0, TransitionDuration = 1f, Intensity = 1f },
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        [SerializeField] private Volume weatherVolume;

        /// <summary>0..1 progress of the current transition (1 = fully in target weather).</summary>
        public float Blend { get; private set; } = 1f;
        public WeatherType CurrentType => (WeatherType)_state.Value.Current;
        public WeatherType TargetType => (WeatherType)_state.Value.Target;
        public float Intensity => _state.Value.Intensity;

        /// <summary>Blended values consumed by presentation systems.</summary>
        public float RainRate { get; private set; }
        public float SnowRate { get; private set; }
        public float SnowAccumulation { get; private set; }
        public float WindStrength { get; private set; }

        [Header("Exposure curve (EV100 - higher is darker)")]
        [Tooltip("Exposure at full day. Auto-exposure was rejected here: a big bright " +
                 "surface (snow) drags it dark and washes out the authored look.\n\n" +
                 "Derived, not eyeballed: HDRP maps luminance up to ~1.2 * 2^EV. Sunlit " +
                 "snow is albedo 0.9 under 40000 lux = 0.9 * 40000 / PI ~ 11500 cd/m^2, " +
                 "which needs EV ~13.2. Anything lower clips the snow to flat white and " +
                 "takes the trails, the deformation normals and the sky with it.")]
        [SerializeField] private float dayExposure = 13.2f;
        [SerializeField] private float nightExposure = 8.0f;
        [Tooltip("How much heavy cloud cover brightens the image to stay readable.")]
        [SerializeField] private float cloudExposureCompensation = 1.3f;

        [Header("Snow depth (accumulates while snowing, melts in sun)")]
        [Tooltip("Snow depth in metres at full coverage. Tuned for STREETS: shallow enough " +
                 "that a footprint carves through to the road and the player walks on the " +
                 "road surface, not on top of the snow. Raise for open/rural regions - but " +
                 "keep SnowGroundBuilder.TRAIL_DEPTH >= this or trails stop short.")]
        [SerializeField] private float maxSnowDepth = 0.2f;
        [Tooltip("Coverage gained per second at full snowfall (1 = empty to full).")]
        [SerializeField] private float snowGainPerSecond = 0.004f;
        [Tooltip("Coverage lost per second in full sun with no snowfall.")]
        [SerializeField] private float snowMeltPerSecond = 0.006f;

        /// <summary>Server-owned so late joiners inherit the current depth exactly.</summary>
        private readonly NetworkVariable<float> _snowCoverage = new(0f,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>0..1 how covered the world currently is.</summary>
        public float SnowCoverage => _snowCoverage.Value;

        /// <summary>
        /// Current depth of lying snow in metres. The snow shader lifts its mesh by this,
        /// and <see cref="Game.World.SnowSurfaceCollider"/> raises collision to match, so
        /// they must read the same number.
        /// </summary>
        public float SnowDepthMeters => _snowCoverage.Value * maxSnowDepth;

        [Header("Sky bounce")]
        [Tooltip("Ground albedo the sky model bounces back up. This is the ONLY thing " +
                 "lighting a facade that faces away from the sun, so a near-black ground " +
                 "tint is why unlit walls read as flat black at noon.")]
        [SerializeField] private Color bareGroundTint = new(0.16f, 0.15f, 0.14f);
        [Tooltip("Ground tint at full snow cover. Lying snow really does throw most of the " +
                 "sunlight back up, and it is what makes a snowy day read as bright.")]
        [SerializeField] private Color snowGroundTint = new(0.62f, 0.64f, 0.68f);

        private Fog _fog;
        private VolumetricClouds _clouds;
        private Exposure _exposure;
        private PhysicallyBasedSky _sky;
        private static readonly int SnowHeightId = Shader.PropertyToID("_SnowHeightMeters");

        /// <summary>
        /// Cloud cover's dimming of the sun, 1 = clear sky. HDRP applies this to lit
        /// geometry automatically, but shaders that light themselves from
        /// <c>_GameSunLux</c> (the snow ground) have to fold it in by hand - otherwise
        /// overcast brightens the exposure while the snow keeps outputting full sunlight,
        /// and the snow clips to flat white exactly when the weather turns.
        /// Defaults to 1 so a scene with no WeatherManager still lights correctly.
        /// </summary>
        public static float CloudSunDimmer { get; private set; } = 1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
            CloudSunDimmer = 1f;
        }

        private void Awake()
        {
            Instance = this;
            EnsureVolume();
        }

        public override void OnDestroy()
        {
            if (Instance == this) Instance = null;
            base.OnDestroy();
        }

        /// <summary>
        /// Builds a private runtime profile so weather never mutates a shared asset
        /// (which would leak edits back into the project in the editor).
        /// </summary>
        private void EnsureVolume()
        {
            if (weatherVolume == null) weatherVolume = GetComponent<Volume>();
            if (weatherVolume == null) weatherVolume = gameObject.AddComponent<Volume>();
            weatherVolume.isGlobal = true;
            weatherVolume.priority = 20;
            weatherVolume.weight = 1f;

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "WeatherRuntimeProfile";
            weatherVolume.profile = profile;

            _fog = profile.Add<Fog>(overrides: true);
            _fog.enabled.overrideState = true;
            _fog.meanFreePath.overrideState = true;
            _fog.enableVolumetricFog.overrideState = true;
            _fog.depthExtent.overrideState = true;
            _fog.albedo.overrideState = true;
            _fog.anisotropy.overrideState = true;
            _fog.maximumHeight.overrideState = true;

            _clouds = profile.Add<VolumetricClouds>(overrides: true);
            _clouds.enable.overrideState = true;
            _clouds.cloudControl.overrideState = true;
            _clouds.cloudControl.value = VolumetricClouds.CloudControl.Advanced;
            _clouds.densityMultiplier.overrideState = true;
            _clouds.shapeFactor.overrideState = true;
            _clouds.erosionFactor.overrideState = true;
            _clouds.bottomAltitude.overrideState = true;
            _clouds.altitudeRange.overrideState = true;
            _clouds.sunLightDimmer.overrideState = true;
            _clouds.globalWindSpeed.overrideState = true;

            _exposure = profile.Add<Exposure>(overrides: true);
            _exposure.mode.overrideState = true;
            _exposure.mode.value = ExposureMode.Fixed;
            _exposure.fixedExposure.overrideState = true;
            _exposure.fixedExposure.value = dayExposure;

            // Only the ground tint is overridden - the rest of the sky (atmosphere, sun
            // disc, aerosols) stays authored in the scene profile.
            _sky = profile.Add<PhysicallyBasedSky>(overrides: false);
            _sky.groundTint.overrideState = true;
            _sky.groundTint.value = bareGroundTint;
        }

        private void Update()
        {
            if (!IsSpawned || _fog == null) return;
            var s = _state.Value;

            double now = NetworkManager.ServerTime.Time;
            Blend = s.TransitionDuration <= 0f ? 1f
                : Mathf.Clamp01((float)((now - s.TransitionStartServerTime) / s.TransitionDuration));

            var from = WeatherPreset.Get((WeatherType)s.Current);
            var to = WeatherPreset.Get((WeatherType)s.Target);
            if (from == null && to == null) return;
            from ??= to;
            to ??= from;

            float k = Blend;
            float intensity = Mathf.Clamp(s.Intensity, 0f, 2f);

            // --- fog ---
            _fog.enabled.value = true;
            // Thicker fog = shorter mean free path, so intensity divides it.
            float meanFreePath = Mathf.Lerp(from.fogMeanFreePath, to.fogMeanFreePath, k);
            _fog.meanFreePath.value = Mathf.Max(1f, meanFreePath / Mathf.Max(0.05f, intensity));
            _fog.enableVolumetricFog.value = k < 0.5f ? from.volumetricFog : to.volumetricFog;
            _fog.depthExtent.value = Mathf.Lerp(from.fogDepthExtent, to.fogDepthExtent, k);
            _fog.albedo.value = Color.Lerp(from.fogAlbedo, to.fogAlbedo, k);
            _fog.anisotropy.value = Mathf.Lerp(from.fogAnisotropy, to.fogAnisotropy, k);
            _fog.maximumHeight.value = Mathf.Lerp(from.fogMaximumHeight, to.fogMaximumHeight, k);

            // --- clouds ---
            bool cloudsOn = (k < 0.5f ? from.cloudsEnabled : to.cloudsEnabled)
                            && !Admin.PerfProbe.SuppressClouds;
            _clouds.enable.value = cloudsOn;
            if (cloudsOn)
            {
                _clouds.densityMultiplier.value =
                    Mathf.Clamp01(Mathf.Lerp(from.cloudDensity, to.cloudDensity, k) * intensity);
                _clouds.shapeFactor.value = Mathf.Lerp(from.cloudShapeFactor, to.cloudShapeFactor, k);
                _clouds.erosionFactor.value = Mathf.Lerp(from.cloudErosion, to.cloudErosion, k);
                _clouds.bottomAltitude.value = Mathf.Lerp(from.cloudAltitude, to.cloudAltitude, k);
                _clouds.altitudeRange.value = Mathf.Lerp(from.cloudThickness, to.cloudThickness, k);
                _clouds.sunLightDimmer.value = Mathf.Lerp(from.cloudSunDimmer, to.cloudSunDimmer, k);
                // Wind speed is a mode+value struct, not a bare float.
                _clouds.globalWindSpeed.value = new WindParameter.WindParamaterValue
                {
                    mode = WindParameter.WindOverrideMode.Custom,
                    customValue = Mathf.Lerp(from.cloudWindSpeed, to.cloudWindSpeed, k),
                };
            }

            // --- exposure: follows the same day curve as the sun, brightened under cloud ---
            if (_exposure != null && NetworkTimeSync.Instance != null)
            {
                float dayBlend = World.SunController.DayBlend01(NetworkTimeSync.Instance.HourOfDay);

                // Daytime exposure is a property of the WEATHER, not one number for the whole
                // game. A single global value has to be a compromise between clear noon and a
                // storm, and the compromise reads as permanently overcast. Presets that leave
                // it at 0 fall back to the old behaviour.
                float fromDay = from.dayExposure > 0f ? from.dayExposure : dayExposure;
                float toDay = to.dayExposure > 0f ? to.dayExposure : dayExposure;
                float ev = Mathf.Lerp(nightExposure, Mathf.Lerp(fromDay, toDay, k), dayBlend);
                float dimmer = cloudsOn ? _clouds.sunLightDimmer.value : 1f;
                CloudSunDimmer = dimmer;
                ev -= (1f - dimmer) * cloudExposureCompensation;
                _exposure.fixedExposure.value = ev + ExposureBias;
            }

            // --- sky bounce: snow cover decides how much light comes back off the ground ---
            if (_sky != null)
                _sky.groundTint.value = Color.Lerp(bareGroundTint, snowGroundTint, _snowCoverage.Value);

            // --- presentation outputs ---
            RainRate = Mathf.Lerp(from.rainRate, to.rainRate, k) * intensity;
            SnowRate = Mathf.Lerp(from.snowRate, to.snowRate, k) * intensity;
            SnowAccumulation = Mathf.Lerp(from.snowAccumulationRate, to.snowAccumulationRate, k) * intensity;
            WindStrength = Mathf.Lerp(from.windStrength, to.windStrength, k);

            UpdateSnowCoverage();
        }

        /// <summary>
        /// Snow builds while it falls and melts in sunlight. The server integrates and
        /// replicates the result, so everyone (including late joiners) sees the same depth
        /// instead of each client integrating its own drifting value.
        /// </summary>
        private void UpdateSnowCoverage()
        {
            if (IsServer)
            {
                bool snowing = SnowRate > 1f;
                float dayBlend = NetworkTimeSync.Instance != null
                    ? World.SunController.DayBlend01(NetworkTimeSync.Instance.HourOfDay) : 1f;
                // Melting needs sun AND clear-ish sky; heavy cloud slows it right down.
                float sunExposure = cloudsOn(_clouds) ? _clouds.sunLightDimmer.value : 1f;

                float delta = snowing
                    ? snowGainPerSecond * Mathf.Clamp01(SnowRate / 500f)
                    : -snowMeltPerSecond * dayBlend * sunExposure;

                float next = Mathf.Clamp01(_snowCoverage.Value + delta * Time.deltaTime);
                // Throttle writes: NetworkVariables shouldn't churn every frame.
                if (Mathf.Abs(next - _snowCoverage.Value) > 0.002f || next == 0f || next == 1f)
                    _snowCoverage.Value = next;
            }

            Shader.SetGlobalFloat(SnowHeightId, SnowDepthMeters);
        }

        private static bool cloudsOn(VolumetricClouds c) => c != null && c.enable.value;

        // ---------------- control (server) ----------------

        /// <summary>Server-only. Called by the admin console, event triggers, or the cycle.</summary>
        public void ServerSetWeather(WeatherType type, float transitionSeconds, float intensity = 1f)
        {
            if (!IsServer) return;
            var s = _state.Value;
            _state.Value = new WeatherNetState
            {
                Current = s.Target, // whatever we were heading to becomes the origin
                Target = (byte)type,
                TransitionStartServerTime = NetworkManager.ServerTime.Time,
                TransitionDuration = Mathf.Max(0.1f, transitionSeconds),
                Intensity = Mathf.Clamp(intensity, 0f, 2f),
            };
        }

        public (WeatherType type, float intensity) GetSaveState() => (TargetType, Intensity);

        public void ServerApplySaveState(WeatherType type, float intensity) =>
            ServerSetWeather(type, 0.1f, intensity);

        /// <summary>
        /// Jumps snow depth straight to a coverage without waiting out accumulation
        /// (minutes of real time). Console `snow` command. Replicates like any other
        /// coverage change, so late joiners and remote clients agree.
        /// </summary>
        public void ServerSetSnowCoverage(float coverage01)
        {
            if (!IsServer) return;
            _snowCoverage.Value = Mathf.Clamp01(coverage01);
        }

        /// <summary>
        /// Debug EV100 offset added to the time-driven exposure. NEGATIVE brightens.
        /// Local-only presentation - the console broadcasts it so every client matches.
        /// </summary>
        public static float ExposureBias { get; set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetExposureBias() => ExposureBias = 0f;
    }
}
