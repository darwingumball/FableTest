using System.Collections.Generic;
using Game.Net;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// Makes surfaces reflective when it rains and dry off slowly afterwards.
    ///
    /// Wet ground is what actually sells rain: neon and street lights only smear into
    /// reflections if the surface is smooth. This raises smoothness and darkens base colour
    /// (water absorbs, so wet asphalt reads darker) as rain falls, then reverses on a much
    /// slower curve once it stops.
    ///
    /// Operates on material INSTANCES so the source assets are never dirtied. Also publishes
    /// _GameWetness globally for hand-written shaders to use.
    /// </summary>
    public class SurfaceWetness : MonoBehaviour
    {
        [Header("Look")]
        [Tooltip("Smoothness multiplier applied on top of each material's authored value.")]
        [SerializeField, Range(0f, 1f)] private float wetSmoothness = 0.93f;
        [Tooltip("How much darker a fully soaked surface reads. 0 = no darkening.")]
        [SerializeField, Range(0f, 1f)] private float wetDarkening = 0.35f;

        [Header("Response")]
        [Tooltip("Rain rate that counts as a full downpour (matches WeatherPreset.rainRate).")]
        [SerializeField] private float rainRateForFullWet = 250f;
        [Tooltip("Wetness gained per second in a full downpour.")]
        [SerializeField] private float wettingPerSecond = 0.15f;
        [Tooltip("Wetness lost per second when dry. Deliberately much slower than wetting.")]
        [SerializeField] private float dryingPerSecond = 0.03f;

        [Header("Scope")]
        [SerializeField] private bool includeChildren = true;
        [Tooltip("Renderers whose materials are emissive signage etc. and should stay dry.")]
        [SerializeField] private Renderer[] exclude;

        /// <summary>0 = bone dry, 1 = soaked.</summary>
        public float Wetness => _wetness;

        private struct Target
        {
            public Material Material;
            public float DrySmoothness;
            public Color DryColor;
        }

        private readonly List<Target> _targets = new();
        private float _wetness;
        private float _applied = -1f;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
        private static readonly int GlobalWetnessId = Shader.PropertyToID("_GameWetness");

        private void Awake()
        {
            var renderers = includeChildren
                ? GetComponentsInChildren<Renderer>(true)
                : GetComponents<Renderer>();

            foreach (var renderer in renderers)
            {
                if (System.Array.IndexOf(exclude, renderer) >= 0) continue;
                // .materials returns per-renderer instances, so the shared assets on disk
                // are left untouched.
                foreach (var mat in renderer.materials)
                {
                    if (mat == null || !mat.HasProperty(SmoothnessId)) continue;
                    _targets.Add(new Target
                    {
                        Material = mat,
                        DrySmoothness = mat.GetFloat(SmoothnessId),
                        DryColor = mat.HasProperty(BaseColorId) ? mat.GetColor(BaseColorId) : Color.white,
                    });
                }
            }
        }

        private void OnDestroy()
        {
            foreach (var target in _targets)
                if (target.Material != null) Destroy(target.Material);
            _targets.Clear();
        }

        private void Update()
        {
            float rain = WeatherManager.Instance != null ? WeatherManager.Instance.RainRate : 0f;
            float soak = Mathf.Clamp01(rain / Mathf.Max(rainRateForFullWet, 1f));

            float delta = soak > 0.01f
                ? wettingPerSecond * soak
                : -dryingPerSecond;
            _wetness = Mathf.Clamp01(_wetness + delta * Time.deltaTime);

            Shader.SetGlobalFloat(GlobalWetnessId, _wetness);

            // Material writes are not free; skip imperceptible changes.
            if (Mathf.Abs(_wetness - _applied) < 0.005f) return;
            _applied = _wetness;

            foreach (var target in _targets)
            {
                if (target.Material == null) continue;
                target.Material.SetFloat(SmoothnessId,
                    Mathf.Lerp(target.DrySmoothness, wetSmoothness, _wetness));
                if (target.Material.HasProperty(BaseColorId))
                {
                    float darken = Mathf.Lerp(1f, 1f - wetDarkening, _wetness);
                    var c = target.DryColor;
                    target.Material.SetColor(BaseColorId,
                        new Color(c.r * darken, c.g * darken, c.b * darken, c.a));
                }
            }
        }
    }
}
