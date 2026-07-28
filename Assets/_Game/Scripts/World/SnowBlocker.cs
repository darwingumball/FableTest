using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// Marks ground that snow must NOT lie on - building interiors, covered walkways,
    /// anything with a roof over it.
    ///
    /// The snow ground is one continuous mesh under the whole district, so without a mask
    /// it grows straight up through floors. Each blocker contributes an axis-aligned XZ
    /// footprint that <see cref="SnowDeformationManager"/> stamps into the snow mask; the
    /// snow shader multiplies its depth by that mask, so blocked ground stays flat at
    /// street level and the floor underneath is what you see.
    ///
    /// Put one on a building root and leave <see cref="useRendererBounds"/> on to derive
    /// the footprint from its geometry, or size it by hand for partial cover (an awning
    /// that should keep the pavement clear only under itself).
    /// </summary>
    public class SnowBlocker : MonoBehaviour
    {
        [Tooltip("Derive the XZ footprint from the combined bounds of child renderers.")]
        [SerializeField] private bool useRendererBounds = true;
        [Tooltip("Manual footprint size in metres (X,Z) when not using renderer bounds.")]
        [SerializeField] private Vector2 size = new(10f, 10f);
        [Tooltip("Shrink the footprint so exterior walls still catch snow against them. " +
                 "Applied to each edge, in metres.")]
        [SerializeField] private float inset = 0.15f;

        /// <summary>Footprint in world XZ as (minX, minZ, maxX, maxZ).</summary>
        public Vector4 GetFootprint()
        {
            Vector3 centre;
            Vector2 extents;

            if (useRendererBounds && TryGetWorldBounds(out var bounds))
            {
                centre = bounds.center;
                extents = new Vector2(bounds.extents.x, bounds.extents.z);
            }
            else
            {
                centre = transform.position;
                // Lossy scale so a scaled building root still masks the right area.
                var scale = transform.lossyScale;
                extents = new Vector2(size.x * Mathf.Abs(scale.x), size.y * Mathf.Abs(scale.z)) * 0.5f;
            }

            extents.x = Mathf.Max(extents.x - inset, 0.01f);
            extents.y = Mathf.Max(extents.y - inset, 0.01f);
            return new Vector4(centre.x - extents.x, centre.z - extents.y,
                               centre.x + extents.x, centre.z + extents.y);
        }

        private bool TryGetWorldBounds(out Bounds bounds)
        {
            bounds = default;
            var renderers = GetComponentsInChildren<Renderer>();
            bool any = false;
            foreach (var r in renderers)
            {
                // Particles and other non-geometry renderers would balloon the footprint.
                if (r is ParticleSystemRenderer) continue;
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return any;
        }

        private void OnEnable() => SnowDeformationManager.Instance?.MarkMaskDirty();
        private void OnDisable() => SnowDeformationManager.Instance?.MarkMaskDirty();

        private void OnDrawGizmosSelected()
        {
            var f = GetFootprint();
            var centre = new Vector3((f.x + f.z) * 0.5f, transform.position.y, (f.y + f.w) * 0.5f);
            var size3 = new Vector3(f.z - f.x, 0.05f, f.w - f.y);
            Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.6f);
            Gizmos.DrawWireCube(centre, size3);
        }
    }
}
