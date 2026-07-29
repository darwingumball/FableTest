using Game.Net;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// Drives the Sun and Moon from the replicated clock: rotation over the day,
    /// photometric intensity fading through dawn/dusk, and a handover to moonlight at
    /// night.
    ///
    /// Both bodies live here rather than in two components because exactly one of them is
    /// the key light at any moment, and the shaders that light themselves (the snow ground)
    /// need a single unambiguous answer for "where is the light coming from". Splitting it
    /// would mean a script execution order dependency to get that right.
    /// </summary>
    [RequireComponent(typeof(Light))]
    public class SunController : MonoBehaviour
    {
        [SerializeField] private float dayIntensityLux = 40000f;
        [SerializeField] private float yawDegrees = -30f;

        [Header("Moon")]
        [Tooltip("Assigned by Game/Setup/Build Sky. Optional - without it, night falls " +
                 "back to a dim sun so the world never goes fully black.")]
        [SerializeField] private Light moonLight;
        [Tooltip("Stylised moonlight. Physically ~0.25 lux, but the game has to stay " +
                 "readable at night - this plus the night exposure keeps nights moody, " +
                 "not blind.")]
        [SerializeField] private float moonIntensityLux = 450f;
        [Tooltip("Used only when there is no moon light assigned: the sun is held at this " +
                 "intensity below the horizon so the scene keeps some key light.")]
        [SerializeField] private float sunlessNightLux = 450f;

        private Light _light;

        private void Awake() => _light = GetComponent<Light>();

        /// <summary>
        /// 0 = night, 1 = full day, for a given hour. Shared with the exposure curve so
        /// lighting and exposure always agree.
        /// </summary>
        public static float DayBlend01(float hourOfDay)
        {
            float sunPitch = (hourOfDay - 6f) / 24f * 360f;
            float elevation = Mathf.Sin(sunPitch * Mathf.Deg2Rad);
            return Mathf.Clamp01(Mathf.InverseLerp(-0.08f, 0.25f, elevation));
        }

        private void Update()
        {
            var time = NetworkTimeSync.Instance;
            if (time == null) return;

            float hour = time.HourOfDay;
            // 06:00 sunrise at horizon, 12:00 zenith-ish, 18:00 sunset.
            float sunPitch = (hour - 6f) / 24f * 360f;
            transform.rotation = Quaternion.Euler(sunPitch, yawDegrees, 0f);

            float dayBlend = DayBlend01(hour);
            bool haveMoon = moonLight != null;

            float sunLux = haveMoon
                ? Mathf.Lerp(0f, dayIntensityLux, dayBlend)
                : Mathf.Lerp(sunlessNightLux, dayIntensityLux, dayBlend);
            _light.intensity = sunLux;
            _light.colorTemperature = Mathf.Lerp(2200f, 5800f, Mathf.Clamp01(dayBlend * 1.4f));

            float moonLux = 0f;
            if (haveMoon)
            {
                // Directly opposite the sun, and offset in yaw so the two never sit on one
                // axis - a moon that rises exactly where the sun set reads as a bug.
                moonLight.transform.rotation = Quaternion.Euler(sunPitch + 180f, yawDegrees + 25f, 0f);
                moonLux = Mathf.Lerp(moonIntensityLux, 0f, dayBlend);
                moonLight.intensity = moonLux;
                // SetActive rather than Light.enabled: the visible disc is a CHILD light
                // (see SkyBuilder - the disc and the illumination cannot come from one
                // light), and disabling only this component would leave a moon hanging in
                // the daytime sky. Setting a transform on an inactive object still works,
                // so the rotation above keeps tracking either way.
                if (moonLight.gameObject.activeSelf != moonLux > 0.01f)
                    moonLight.gameObject.SetActive(moonLux > 0.01f);
            }

            // Published for shaders that do their own simple shading (snow ground).
            // Lux matters: HDRP is scene-referred, so a custom shader must output real
            // luminance or it will be black/blown-out relative to lit geometry.
            //
            // Whichever body is brighter is the key light. At the crossover they are both
            // near zero, so switching rather than blending costs nothing visually and
            // keeps the published direction a real light direction at all times.
            bool moonIsKey = haveMoon && moonLux > sunLux;
            Transform key = moonIsKey ? moonLight.transform : transform;
            float keyLux = moonIsKey ? moonLux : sunLux;

            // The cloud dimmer has to be folded in here. HDRP applies it to everything it
            // lights itself, but a self-lighting shader would otherwise keep emitting full
            // sunlight while the exposure curve brightens for the overcast - so the snow
            // would blow out precisely when the sky clouds over.
            Shader.SetGlobalVector("_GameSunDirection", -key.forward);
            Shader.SetGlobalFloat("_GameSunLux", keyLux * Net.WeatherManager.CloudSunDimmer);
            Shader.SetGlobalFloat("_GameSunAmount", Mathf.Lerp(0.12f, 1f, dayBlend));
        }
    }
}
