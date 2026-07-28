using Game.Net;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// Drives the Sun from the replicated clock: rotation over the day, photometric
    /// intensity fading through dawn/dusk into a dim moon at night.
    /// </summary>
    [RequireComponent(typeof(Light))]
    public class SunController : MonoBehaviour
    {
        [SerializeField] private float dayIntensityLux = 40000f;
        [Tooltip("Stylised moonlight floor. Physically ~1 lux, but the game has to stay " +
                 "readable at night - this plus auto-exposure keeps nights moody, not blind.")]
        [SerializeField] private float nightIntensityLux = 450f;
        [SerializeField] private float yawDegrees = -30f;

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
            _light.intensity = Mathf.Lerp(nightIntensityLux, dayIntensityLux, dayBlend);
            _light.colorTemperature = Mathf.Lerp(2200f, 5800f, Mathf.Clamp01(dayBlend * 1.4f));

            // Published for shaders that do their own simple shading (snow ground).
            // Lux matters: HDRP is scene-referred, so a custom shader must output real
            // luminance or it will be black/blown-out relative to lit geometry.
            Shader.SetGlobalVector("_GameSunDirection", -transform.forward);
            Shader.SetGlobalFloat("_GameSunLux", _light.intensity);
            Shader.SetGlobalFloat("_GameSunAmount", Mathf.Lerp(0.12f, 1f, dayBlend));
        }
    }
}
