using Game.Net;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// Gives the snow ground physical collision that tracks the current snow depth.
    ///
    /// The snow mesh is displaced entirely in the vertex shader, so a MeshCollider would
    /// always describe the undisplaced plane. Instead a box spans the deformation region
    /// with its top face parked at the height a body actually rests at.
    ///
    /// That rest height is the snow surface MINUS <see cref="sinkDepth"/>: a walker
    /// compresses the snow under themselves (see <see cref="SnowDeformer"/>), so their feet
    /// belong at the bottom of their own footprint, not floating on the pristine surface.
    /// Keep <see cref="sinkDepth"/> equal to the snow material's _DepthMeters - both are
    /// written from one constant by SnowGroundBuilder.
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    public class SnowSurfaceCollider : MonoBehaviour
    {
        [Tooltip("World size of the snow area. Must match SnowDeformationManager.regionSize.")]
        [SerializeField] private float regionSize = 100f;
        [Tooltip("How far a body sinks into lying snow. Match the material's _DepthMeters.")]
        [SerializeField] private float sinkDepth = 0.35f;
        [Tooltip("Body thickness of the collision box. Only the top face matters; the rest " +
                 "extends down past the ground so nothing can slip underneath.")]
        [SerializeField] private float thickness = 6f;
        [Tooltip("Minimum height change before the collider is moved, in metres.")]
        [SerializeField] private float updateThreshold = 0.01f;

        /// <summary>World Y a body standing on the snow rests at.</summary>
        public float SurfaceHeight => transform.position.y + WalkHeightLocal();

        private BoxCollider _box;
        private float _appliedHeight = float.NaN;

        private void Awake()
        {
            _box = GetComponent<BoxCollider>();
            _box.size = new Vector3(regionSize, thickness, regionSize);
            Apply();
        }

        // LateUpdate so the depth read here is the one WeatherManager published this frame.
        private void LateUpdate()
        {
            if (Mathf.Abs(WalkHeightLocal() - _appliedHeight) > updateThreshold) Apply();
        }

        private float WalkHeightLocal()
        {
            float snowHeight = WeatherManager.Instance != null
                ? WeatherManager.Instance.SnowDepthMeters : 0f;
            // Never sink below the ground the snow is lying on.
            return snowHeight - Mathf.Min(sinkDepth, snowHeight);
        }

        private void Apply()
        {
            _appliedHeight = WalkHeightLocal();
            _box.center = new Vector3(0f, _appliedHeight - thickness * 0.5f, 0f);
        }
    }
}
