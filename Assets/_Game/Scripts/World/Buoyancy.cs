using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// Floats a rigidbody on the water using a few probe points.
    ///
    /// One probe at the centre would float a crate but never right it, so it would spin
    /// freely and read as weightless. Several probes spread across the body each push up
    /// independently, which produces the restoring torque that makes a box sit flat and
    /// bob with the swell - the same trick, and the same reason, as a real hull's buoyancy
    /// distribution.
    ///
    /// Only the owner simulates. World items are owner-authoritative (see
    /// <c>WorldItemNetworkSync</c>), so every peer applying forces would fight the
    /// transform sync.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class Buoyancy : MonoBehaviour
    {
        [Tooltip("Probe points in local space. Spread them out - probes clustered near the " +
                 "centre give no righting torque.")]
        [SerializeField] private Vector3[] probes =
        {
            new(-0.4f, 0f, -0.4f), new(0.4f, 0f, -0.4f),
            new(-0.4f, 0f, 0.4f), new(0.4f, 0f, 0.4f),
        };

        [Tooltip("How hard a fully submerged probe pushes up, as a multiple of gravity. " +
                 "Above 1 the object floats; at 1 it hovers neutrally buoyant.")]
        [SerializeField] private float buoyancy = 2.2f;
        [Tooltip("Depth over which the upward force ramps in. Without a ramp the force is " +
                 "a step function at the waterline and the object jitters against it.")]
        [SerializeField] private float submersionRamp = 0.6f;
        [Tooltip("Linear drag applied while submerged - water is not air.")]
        [SerializeField] private float waterDrag = 2.5f;
        [SerializeField] private float waterAngularDrag = 1.5f;

        private Rigidbody _body;
        private float _airDrag, _airAngularDrag;

        private void Awake()
        {
            _body = GetComponent<Rigidbody>();
            _airDrag = _body.linearDamping;
            _airAngularDrag = _body.angularDamping;
        }

        private void FixedUpdate()
        {
            var water = WaterVolume.Instance;
            if (water == null || _body.isKinematic) return;

            // Owner-only: see class summary.
            var netObj = GetComponent<Unity.Netcode.NetworkObject>();
            if (netObj != null && netObj.IsSpawned && !netObj.IsOwner) return;

            int submergedProbes = 0;
            float perProbe = buoyancy * -Physics.gravity.y * _body.mass / Mathf.Max(probes.Length, 1);

            foreach (var local in probes)
            {
                Vector3 world = transform.TransformPoint(local);
                if (!water.SampleSurface(world, out float surfaceY, out _)) continue;

                float depth = surfaceY - world.y;
                if (depth <= 0f) continue;
                submergedProbes++;

                // Ramp rather than step, so the body settles instead of buzzing.
                float strength = Mathf.Clamp01(depth / Mathf.Max(submersionRamp, 0.01f));
                _body.AddForceAtPosition(Vector3.up * (perProbe * strength), world,
                    ForceMode.Force);
            }

            bool inWater = submergedProbes > 0;
            _body.linearDamping = inWater ? waterDrag : _airDrag;
            _body.angularDamping = inWater ? waterAngularDrag : _airAngularDrag;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.cyan;
            foreach (var p in probes) Gizmos.DrawWireSphere(transform.TransformPoint(p), 0.12f);
        }
    }
}
