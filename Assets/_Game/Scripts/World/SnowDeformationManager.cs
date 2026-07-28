using System.Collections.Generic;
using Game.Net;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// Owns the snow deformation render texture mapped over a world-space region.
    /// <see cref="SnowDeformer"/> agents (players - remote ones included, their transforms
    /// replicate) stamp depressions; snowfall (WeatherManager.SnowAccumulation) refills.
    /// Purely local presentation state - identical on every peer because the inputs are.
    ///
    /// Shader globals for the snow material (Shader Graph, Phase 13):
    ///   _SnowDeformRT (R: 0 pristine .. 1 compressed)
    ///   _SnowRegionParams (xy: region origin XZ, zw: 1/region size)
    /// </summary>
    public class SnowDeformationManager : MonoBehaviour
    {
        public static SnowDeformationManager Instance { get; private set; }

        [Tooltip("World size of the deformable area. Must match the snow ground mesh.")]
        [SerializeField] private float regionSize = 100f;
        [Tooltip("Deformation texture resolution. 2048 over 100m = ~5cm per texel.")]
        [SerializeField] private int resolution = 2048;

        public RenderTexture DeformRT { get; private set; }

        private Material _opsMaterial;
        private readonly List<SnowDeformer> _deformers = new();

        private static readonly int StampDataId = Shader.PropertyToID("_StampData");
        private static readonly int RefillId = Shader.PropertyToID("_RefillAmount");
        private static readonly int GlobalRtId = Shader.PropertyToID("_SnowDeformRT");
        private static readonly int GlobalParamsId = Shader.PropertyToID("_SnowRegionParams");
        private static readonly int GlobalTexelId = Shader.PropertyToID("_SnowDeformTexel");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Instance = null;

        private void Awake()
        {
            Instance = this;
            // R8 (unsigned normalized), NOT RFloat: the refill pass uses reverse-subtract
            // blending, and a float target has no clamping, so depth marches negative and
            // the snow mesh gets displaced UPWARD into the camera. Fixed-point formats
            // clamp to [0,1] in hardware.
            DeformRT = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.R8)
            {
                name = "SnowDeformRT",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear, // explicit: point filtering shows as stair-stepped trails
            };
            DeformRT.Create();
            var prev = RenderTexture.active;
            RenderTexture.active = DeformRT;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = prev;

            var shader = Resources.Load<Shader>("Shaders/SnowDeform");
            _opsMaterial = new Material(shader);

            Shader.SetGlobalTexture(GlobalRtId, DeformRT);
            UpdateRegionGlobals();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (DeformRT != null) DeformRT.Release();
        }

        private void UpdateRegionGlobals()
        {
            Vector3 origin = transform.position - new Vector3(regionSize, 0f, regionSize) * 0.5f;
            Shader.SetGlobalVector(GlobalParamsId,
                new Vector4(origin.x, origin.z, 1f / regionSize, 1f / regionSize));
            // One texel in UV. Normal reconstruction MUST step by this, not by
            // 1/regionSize (which is a whole metre and shades a ring of ghost circles).
            Shader.SetGlobalFloat(GlobalTexelId, 1f / resolution);
        }

        public void Register(SnowDeformer deformer)
        {
            if (!_deformers.Contains(deformer)) _deformers.Add(deformer);
        }

        public void Unregister(SnowDeformer deformer) => _deformers.Remove(deformer);

        private void LateUpdate()
        {
            if (_opsMaterial == null) return;

            foreach (var deformer in _deformers)
            {
                if (deformer == null || !deformer.TryConsumeStamp(out Vector3 pos)) continue;
                Vector2 uv = WorldToRegionUV(pos);
                if (uv.x < 0f || uv.x > 1f || uv.y < 0f || uv.y > 1f) continue;

                _opsMaterial.SetVector(StampDataId,
                    new Vector4(uv.x, uv.y, deformer.radius / regionSize, deformer.depth));
                Graphics.Blit(null, DeformRT, _opsMaterial, 0);
            }

            float accumulation = WeatherManager.Instance != null ? WeatherManager.Instance.SnowAccumulation : 0f;
            if (accumulation > 0f)
            {
                _opsMaterial.SetFloat(RefillId, accumulation * Time.deltaTime);
                Graphics.Blit(null, DeformRT, _opsMaterial, 1);
            }
        }

        private Vector2 WorldToRegionUV(Vector3 world)
        {
            Vector3 origin = transform.position - new Vector3(regionSize, 0f, regionSize) * 0.5f;
            return new Vector2((world.x - origin.x) / regionSize, (world.z - origin.z) / regionSize);
        }
    }
}
