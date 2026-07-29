using Game.Interaction;
using Game.Net;
using Game.Player;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.World
{
    /// <summary>
    /// A ladder you lock onto with Interact, climb with W/S, and leave with Interact again
    /// or by reaching the top.
    ///
    /// Entirely local: while climbing, the position written here is the player's own
    /// transform, which their owner-authoritative NetworkTransform already replicates. There
    /// is nothing to agree on between peers, so there is nothing to network.
    ///
    /// The climb track is expressed in LADDER-LOCAL space and resolved every LateUpdate.
    /// That is what makes this work on the boat: the rungs heave, surge and yaw underneath
    /// you, and re-deriving the world position from the current ladder transform carries you
    /// with them for free - no carry component, no drift.
    /// </summary>
    public class Ladder : MonoBehaviour, IInteractable
    {
        [Tooltip("Height of the climbable track, measured up from this transform's origin.")]
        [SerializeField] private float climbHeight = 3.6f;
        [SerializeField] private float climbSpeed = 2.4f;
        [Tooltip("Where the body sits relative to the rungs, in local space. +Z is out from " +
                 "the ladder face, so the player hangs off the front rather than inside it.")]
        [SerializeField] private Vector3 standOffset = new(0f, 0f, 0.45f);
        [Tooltip("Where the player is placed when they reach the top, in local space. This " +
                 "must clear the gunwale, or they pop out still inside the hull collider.")]
        [SerializeField] private Vector3 topExitLocal = new(0f, 3.9f, -0.9f);

        private static Ladder _active;

        private FirstPersonController _climber;
        private InputAction _interactAction;
        private float _t;                 // 0 = bottom rung, 1 = top
        private float _lastLadderYaw;
        private int _lastToggleFrame = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _active = null;

        public bool IsInUse => _climber != null;

        // ---------------- interaction ----------------

        public string GetPrompt(NetworkPlayer player) =>
            _climber != null ? "Release ladder" : "Climb ladder";

        public void Interact(NetworkPlayer player)
        {
            // Interact reaches this from two places on the same frame - the raycast in
            // InteractionSystem, and the direct read below that keeps you from being
            // stranded if you look away from the rungs. Toggling twice would mount and
            // instantly dismount, so only the first call in a frame counts.
            if (_lastToggleFrame == Time.frameCount) return;
            _lastToggleFrame = Time.frameCount;

            if (_climber != null) Release(atTop: false);
            else Mount(player);
        }

        private void Mount(NetworkPlayer player)
        {
            var fpc = player != null ? player.Controller : null;
            if (fpc == null) return;

            // One ladder at a time, even if two are within arm's reach.
            if (_active != null && _active != this) _active.Release(atTop: false);

            _climber = fpc;
            _active = this;
            _interactAction ??= fpc.InputActions?.FindActionMap("Gameplay")?.FindAction("Interact");

            // Start from the height they grabbed it at, so catching the ladder halfway up
            // from the deck does not drop them back to the waterline.
            float localY = transform.InverseTransformPoint(fpc.transform.position).y;
            _t = Mathf.Clamp01(localY / Mathf.Max(climbHeight, 0.01f));

            _lastLadderYaw = transform.eulerAngles.y;
            fpc.SetClimbing(true);
            // Face the rungs. standOffset is +Z out from the ladder face, so "into the
            // ladder" is -forward.
            fpc.SetYaw(Quaternion.LookRotation(-transform.forward, Vector3.up).eulerAngles.y);
            fpc.TeleportTo(TrackPoint(_t));
        }

        private void Release(bool atTop)
        {
            if (_climber == null) return;

            // Stepping off at the top has to land them on the deck, not on the top rung
            // where the next frame of gravity drops them straight back into the water.
            if (atTop) _climber.TeleportTo(transform.TransformPoint(topExitLocal));

            _climber.SetClimbing(false);
            _climber = null;
            if (_active == this) _active = null;
        }

        // ---------------- climbing ----------------

        private void LateUpdate()
        {
            if (_climber == null) return;

            // LateUpdate, after BoatMotion has moved the hull this frame. Reading the ladder
            // transform in Update would pin the climber to where the boat was last frame,
            // which shows up as a visible stutter along the rungs.
            _t = Mathf.Clamp01(_t + _climber.MoveInput.y * climbSpeed * Time.deltaTime
                                    / Mathf.Max(climbHeight, 0.01f));

            _climber.TeleportTo(TrackPoint(_t));

            // Keep facing the rungs as the boat turns under the ladder. Only the ladder's
            // own yaw change is applied, so the player keeps full free look on top of it.
            float yaw = transform.eulerAngles.y;
            float yawDelta = Mathf.DeltaAngle(_lastLadderYaw, yaw);
            if (Mathf.Abs(yawDelta) > 1e-4f) _climber.AddYaw(yawDelta);
            _lastLadderYaw = yaw;

            if (_interactAction != null && _interactAction.WasPressedThisFrame())
            {
                // Fallback for looking away from the rungs mid-climb; shares the
                // once-per-frame guard with the raycast path.
                Interact(NetworkPlayer.Local);
                return;
            }

            if (_t >= 1f) Release(atTop: true);
        }

        private Vector3 TrackPoint(float t) =>
            transform.TransformPoint(standOffset + Vector3.up * (t * climbHeight));

        private void OnDisable() => Release(atTop: false);

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.8f, 0.2f);
            Gizmos.DrawLine(transform.TransformPoint(standOffset),
                            transform.TransformPoint(standOffset + Vector3.up * climbHeight));
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(transform.TransformPoint(topExitLocal), 0.25f);
        }
    }
}
