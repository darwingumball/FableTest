using Game.Net;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// Carries the local player with a boat that both moves AND turns.
    ///
    /// <see cref="MovingPlatformCarry"/> only handles translation, which is all a lift or a
    /// shuttle needs. A turning deck is harder in two ways, and both have to be handled or
    /// the boat appears to slide out from under you:
    ///
    /// - POSITION. Standing off-centre on a rotating body means your world position must
    ///   swing around the boat's axis. Feeding only the boat's centre delta leaves you
    ///   drifting toward the stern as it turns.
    /// - FACING. Your view has to turn with the deck, or the boat rotates around you and
    ///   you end up walking backwards off the side. This goes through
    ///   <see cref="Game.Player.FirstPersonController.AddYaw"/> because the controller
    ///   rewrites rotation from its own tracked yaw every frame.
    ///
    /// Pitch and roll are deliberately NOT applied to the rider - they live on the boat's
    /// visual hull only. The player stays upright while the hull rolls under them.
    /// </summary>
    [RequireComponent(typeof(BoatMotion))]
    public class BoatRiderCarry : MonoBehaviour
    {
        [Tooltip("Deck volume in boat-local space. Players inside get carried.")]
        [SerializeField] private Vector3 deckCenter = new(0f, 1.4f, 0f);
        [SerializeField] private Vector3 deckSize = new(6f, 3.2f, 14f);
        [Tooltip("Deltas above this are treated as a teleport (late join, course reset) and " +
                 "swallowed rather than flung at the rider.")]
        [SerializeField] private float teleportThreshold = 6f;

        private BoatMotion _boat;

        private void Awake() => _boat = GetComponent<BoatMotion>();

        private void LateUpdate()
        {
            // After BoatMotion.LateUpdate has moved the boat this frame. Script execution
            // order within one component's LateUpdate is not guaranteed across components,
            // so this reads the delta the boat published rather than differencing itself.
            var local = NetworkPlayer.Local;
            if (local == null || local.Controller == null) return;

            Vector3 delta = _boat.LastFrameDelta;
            float yawDelta = _boat.LastFrameYawDelta;
            if (delta == Vector3.zero && Mathf.Abs(yawDelta) < 1e-4f) return;
            if (delta.magnitude > teleportThreshold) return;

            // Inside the deck volume? Test against the boat's CURRENT transform, which is
            // where the player is standing right now.
            Vector3 playerLocal = transform.InverseTransformPoint(local.transform.position);
            Vector3 offset = playerLocal - deckCenter;
            Vector3 half = deckSize * 0.5f;
            if (Mathf.Abs(offset.x) > half.x ||
                Mathf.Abs(offset.y) > half.y ||
                Mathf.Abs(offset.z) > half.z)
                return;

            // Translation from the boat's own movement, plus the arc the player is swung
            // through by its rotation. The arc term is what a translation-only carry misses.
            Vector3 fromAxis = local.transform.position - transform.position;
            Vector3 swung = Quaternion.AngleAxis(yawDelta, Vector3.up) * fromAxis;
            Vector3 rotationDelta = swung - fromAxis;

            local.Controller.ApplyCarry(delta + rotationDelta);
            if (Mathf.Abs(yawDelta) > 1e-4f) local.Controller.AddYaw(yawDelta);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.3f, 1f, 0.6f);
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(deckCenter, deckSize);
        }
    }
}
