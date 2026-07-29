using Unity.Netcode;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// A boat you can walk around on, without a rigidbody anywhere near it.
    ///
    /// Two decisions carry this whole component:
    ///
    /// 1. NOT PHYSICS. The hull pose is a pure function of <c>ServerTime.Time</c> plus the
    ///    water height under it, so every peer computes the same boat with nothing
    ///    replicated and a late joiner is correct on their first frame. A rigidbody boat
    ///    would need continuous transform sync, would drift between peers, and would fight
    ///    the CharacterControllers standing on it.
    ///
    /// 2. THE COLLISION DECK STAYS LEVEL. Heave and yaw go on this root, so the deck the
    ///    player stands on rises, falls and turns with the boat. Pitch and roll go on a
    ///    child visual only. A CharacterController capsule is always world-upright and will
    ///    slide down any tilted collider, so a fully tilting deck would slowly pour
    ///    everyone over the side. The player stays vertical and turns with the boat while
    ///    the hull visibly rolls under them - which is what you actually want to look at.
    /// </summary>
    public class BoatMotion : MonoBehaviour
    {
        [Header("Visual hull")]
        [Tooltip("Child that carries pitch and roll. Must NOT hold the deck collider.")]
        [SerializeField] private Transform hullVisual;

        [Header("Course (pure function of server time)")]
        [Tooltip("Centre of the patrol circle, world space.")]
        [SerializeField] private Vector3 courseCentre = new(0f, 0f, 150f);
        [SerializeField] private float courseRadius = 38f;
        [Tooltip("Seconds for one full lap. Negative reverses the direction.")]
        [SerializeField] private float lapSeconds = 110f;

        [Header("Wave response")]
        [Tooltip("Half-length of the hull, used to sample bow and stern for pitch.")]
        [SerializeField] private float hullLength = 6f;
        [Tooltip("Half-width, used to sample port and starboard for roll.")]
        [SerializeField] private float hullBeam = 2.4f;
        [Tooltip("Metres the deck rides above the water it displaces.")]
        [SerializeField] private float freeboard = 0.9f;
        [Tooltip("0 = ignore the waves, 1 = follow them exactly. Below 1 reads as a heavy " +
                 "hull with real inertia instead of a leaf on the surface.")]
        [SerializeField, Range(0f, 1f)] private float waveFollow = 0.75f;
        [Tooltip("Smoothing on heave and tilt. A hull this size does not snap.")]
        [SerializeField] private float smoothing = 2.5f;
        [Tooltip("Hard cap on tilt. The visual can exaggerate, but past this it reads as " +
                 "capsizing rather than rough water.")]
        [SerializeField] private float maxTiltDegrees = 14f;

        /// <summary>World-space movement applied this frame. Read by <see cref="BoatRiderCarry"/>.</summary>
        public Vector3 LastFrameDelta { get; private set; }
        /// <summary>Yaw change in degrees this frame. Read by <see cref="BoatRiderCarry"/>.</summary>
        public float LastFrameYawDelta { get; private set; }

        private float _heave;
        private float _pitch;
        private float _roll;
        private bool _initialised;

        private void LateUpdate()
        {
            // LateUpdate so the water clock (WaterVolume.Update) has already advanced the
            // simulation this frame - sampling in Update would read last frame's swell.
            double t = CurrentTime();

            Vector3 previousPosition = transform.position;
            float previousYaw = transform.eulerAngles.y;

            // --- course: position and heading straight from server time ---
            float lap = Mathf.Abs(lapSeconds) < 0.01f ? 1f : lapSeconds;
            float phase = (float)(t / lap) * Mathf.PI * 2f;
            Vector3 planar = new(Mathf.Cos(phase) * courseRadius, 0f, Mathf.Sin(phase) * courseRadius);
            Vector3 target = courseCentre + planar;

            // Face along the tangent of the circle, which is the derivative of the above.
            Vector3 tangent = new(-Mathf.Sin(phase), 0f, Mathf.Cos(phase));
            if (lapSeconds < 0f) tangent = -tangent;

            // --- wave response: sample four points around the hull ---
            var water = WaterVolume.Instance;
            float heaveTarget = 0f, pitchTarget = 0f, rollTarget = 0f;

            if (water != null)
            {
                Quaternion heading = Quaternion.LookRotation(tangent, Vector3.up);
                Vector3 fwd = heading * Vector3.forward;
                Vector3 right = heading * Vector3.right;

                float bow = water.SampleHeight(target + fwd * hullLength);
                float stern = water.SampleHeight(target - fwd * hullLength);
                float port = water.SampleHeight(target - right * hullBeam);
                float starboard = water.SampleHeight(target + right * hullBeam);

                heaveTarget = (bow + stern + port + starboard) * 0.25f + freeboard;

                // Tilt is the slope across the hull: rise over run, in degrees.
                pitchTarget = -Mathf.Atan2((bow - stern) * waveFollow, hullLength * 2f) * Mathf.Rad2Deg;
                rollTarget = Mathf.Atan2((starboard - port) * waveFollow, hullBeam * 2f) * Mathf.Rad2Deg;

                pitchTarget = Mathf.Clamp(pitchTarget, -maxTiltDegrees, maxTiltDegrees);
                rollTarget = Mathf.Clamp(rollTarget, -maxTiltDegrees, maxTiltDegrees);
            }

            if (!_initialised)
            {
                // A late joiner must arrive already settled, not lerp up from zero through
                // the sea floor.
                _heave = heaveTarget;
                _pitch = pitchTarget;
                _roll = rollTarget;
                _initialised = true;
            }
            else
            {
                float k = 1f - Mathf.Exp(-smoothing * Time.deltaTime);
                _heave = Mathf.Lerp(_heave, heaveTarget, k);
                _pitch = Mathf.Lerp(_pitch, pitchTarget, k);
                _roll = Mathf.Lerp(_roll, rollTarget, k);
            }

            target.y = _heave;
            transform.SetPositionAndRotation(target, Quaternion.LookRotation(tangent, Vector3.up));

            // Pitch and roll are visual only - see the class summary.
            if (hullVisual != null)
                hullVisual.localRotation = Quaternion.Euler(_pitch, 0f, _roll);

            LastFrameDelta = transform.position - previousPosition;
            LastFrameYawDelta = Mathf.DeltaAngle(previousYaw, transform.eulerAngles.y);
        }

        private static double CurrentTime()
        {
            var net = NetworkManager.Singleton;
            return net != null && net.IsListening ? net.ServerTime.Time : Time.timeAsDouble;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(courseCentre, courseRadius);
            Gizmos.color = Color.yellow;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(hullBeam * 2f, 1f, hullLength * 2f));
        }
    }
}
