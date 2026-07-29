using Game.Net;
using Unity.Netcode;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// Keeps players standing on a boat, locally and across the network.
    ///
    /// TWO MECHANISMS, and they cover different problems.
    ///
    /// 1. NETWORK PARENTING (server). Riders inside the deck volume are parented to the
    ///    boat's NetworkObject, which flips their NetworkTransform into local space. This is
    ///    the part that makes other people on the deck look right. A NetworkTransform
    ///    replicates WORLD position: a boat at 9 m/s covers most of a metre between ticks,
    ///    so every remote rider arrives a tick or two stale and visibly slides around the
    ///    deck, however well they are interpolated. Replicating deck-RELATIVE position
    ///    instead means the only thing being interpolated is their walking - a couple of
    ///    metres per second, and never the boat. The boat's own pose is a pure function of
    ///    server time (see <see cref="BoatMotion"/>), so every peer reconstructs the same
    ///    world position from that local offset exactly.
    ///
    /// 2. LOCAL CARRY (every peer, own player only). Applied only while NOT parented -
    ///    during the round trip it takes the server to notice you have stepped aboard, and
    ///    in any session with no network at all. Once the parent lands, the hierarchy moves
    ///    the rider and applying a carry on top would double every metre the boat travels.
    ///
    /// FACING is separate from both. It cannot ride on the hierarchy, because
    /// <see cref="Game.Player.FirstPersonController"/> rewrites its world rotation from its
    /// own tracked yaw every frame - a parented player would be turned by the deck and then
    /// immediately turned back. So the boat's yaw delta always goes through
    /// <see cref="Game.Player.FirstPersonController.AddYaw"/>, parented or not.
    ///
    /// Pitch and roll are deliberately NOT applied to riders - they live on the boat's
    /// visual hull only. The player stays upright while the hull rolls under them.
    /// </summary>
    [RequireComponent(typeof(BoatMotion))]
    public class BoatRiderCarry : MonoBehaviour
    {
        [Tooltip("Deck volume in boat-local space. Players inside get carried.")]
        [SerializeField] private Vector3 deckCenter = new(0f, 1.4f, 0f);
        [SerializeField] private Vector3 deckSize = new(6f, 3.2f, 14f);
        [Tooltip("Metres added to the deck volume for the test that keeps an existing rider " +
                 "aboard. Boarding uses the plain volume, leaving uses the padded one, so " +
                 "standing on the boundary cannot fire a parent change every frame - each " +
                 "one is a network message.")]
        [SerializeField] private float boardingHysteresis = 1.2f;
        [Tooltip("Deltas above this are treated as a teleport (late join, course reset) and " +
                 "swallowed rather than flung at the rider.")]
        [SerializeField] private float teleportThreshold = 6f;
        [Tooltip("Off falls back to the local carry alone, which is correct for the player " +
                 "at the keyboard but leaves remote riders sliding about on the deck.")]
        [SerializeField] private bool networkParentRiders = true;

        private BoatMotion _boat;
        private NetworkObject _self;

        private void Awake()
        {
            _boat = GetComponent<BoatMotion>();
            _self = GetComponent<NetworkObject>();
        }

        private void LateUpdate()
        {
            // After BoatMotion.LateUpdate has moved the boat this frame. Script execution
            // order within one component's LateUpdate is not guaranteed across components,
            // so this reads the delta the boat published rather than differencing itself.
            UpdateRiderParenting();
            CarryLocalRider();
        }

        // ---------------- server: who is aboard ----------------

        /// <summary>
        /// Parents and unparents riders. Server only - <c>TrySetParent</c> is authority-only
        /// in client/server mode, and it has to be, or two peers could disagree about whose
        /// deck someone is standing on.
        /// </summary>
        private void UpdateRiderParenting()
        {
            if (!networkParentRiders) return;

            var net = NetworkManager.Singleton;
            if (net == null || !net.IsListening || !net.IsServer) return;
            if (_self == null || !_self.IsSpawned) return;

            foreach (var client in net.ConnectedClientsList)
            {
                var player = client.PlayerObject;
                if (player == null || !player.IsSpawned) continue;

                Vector3 position = player.transform.position;

                if (player.transform.parent == transform)
                {
                    if (!Contains(position, boardingHysteresis))
                        player.TrySetParent((NetworkObject)null, worldPositionStays: true);
                }
                else if (player.transform.parent == null && Contains(position, 0f))
                {
                    // Only ever claim an unparented player. Stepping between two boats then
                    // costs one frame with no parent, which the local carry covers - whereas
                    // letting boats claim each other's riders lets two overlapping deck
                    // volumes trade the same player back and forth every frame.
                    player.TrySetParent(_self, worldPositionStays: true);
                }
            }
        }

        // ---------------- every peer: your own body ----------------

        private void CarryLocalRider()
        {
            var local = NetworkPlayer.Local;
            if (local == null || local.Controller == null) return;

            // A ladder owns the climber outright: it pins them to a track in ladder-local
            // space and applies the ladder's own yaw. Since the ladder is bolted to this
            // hull those are the same degrees, and applying them here too would spin the
            // climber at twice the boat's rate.
            if (local.Controller.IsClimbing) return;

            Vector3 delta = _boat.LastFrameDelta;
            float yawDelta = _boat.LastFrameYawDelta;
            if (delta == Vector3.zero && Mathf.Abs(yawDelta) < 1e-4f) return;
            if (delta.magnitude > teleportThreshold) return;

            // Parented riders are aboard by definition - the hierarchy is the authority on
            // that, not a volume test that could disagree with the server's.
            bool parented = local.transform.parent == transform;
            if (!parented && !Contains(local.transform.position, 0f)) return;

            if (!parented)
            {
                // Translation from the boat's own movement, plus the arc the player is swung
                // through by its rotation. The arc term is what a translation-only carry
                // misses: standing off-centre on a turning deck, your world position has to
                // swing around the boat's axis or you drift steadily toward the stern.
                Vector3 fromAxis = local.transform.position - transform.position;
                Vector3 swung = Quaternion.AngleAxis(yawDelta, Vector3.up) * fromAxis;
                local.Controller.ApplyCarry(delta + (swung - fromAxis));
            }

            if (Mathf.Abs(yawDelta) > 1e-4f) local.Controller.AddYaw(yawDelta);
        }

        // ---------------- shared ----------------

        private bool Contains(Vector3 worldPosition, float padding)
        {
            Vector3 offset = transform.InverseTransformPoint(worldPosition) - deckCenter;
            Vector3 half = deckSize * 0.5f + Vector3.one * padding;
            return Mathf.Abs(offset.x) <= half.x
                && Mathf.Abs(offset.y) <= half.y
                && Mathf.Abs(offset.z) <= half.z;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(0.3f, 1f, 0.6f);
            Gizmos.DrawWireCube(deckCenter, deckSize);
            Gizmos.color = new Color(0.3f, 1f, 0.6f, 0.35f);
            Gizmos.DrawWireCube(deckCenter, deckSize + Vector3.one * (boardingHysteresis * 2f));
        }
    }
}
