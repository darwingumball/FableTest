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

        [Header("Helm")]
        [Tooltip("Optional. While nobody has ever taken the wheel the boat runs the patrol " +
                 "course below; from the first time it is driven, the helm supplies the pose.")]
        [SerializeField] private BoatHelm helm;

        [Header("Course (pure function of server time)")]
        [Tooltip("Moored: hold the authored position and heading and only respond to the " +
                 "waves. Used by the main menu harbour, where the boat is scenery.")]
        [SerializeField] private bool moored;
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
        [Tooltip("Tilt gain. 0 = ignore the waves, 1 = lie exactly along the surface slope. " +
                 "Below 1 reads as a heavy hull with real inertia instead of a leaf.")]
        [SerializeField, Range(0f, 1f)] private float waveFollow = 0.7f;
        [Tooltip("How much of the wave height the deck actually rides. The deck is what the " +
                 "player stands on, and tracking every crest exactly throws them around; a " +
                 "heavy hull punches through the top of a wave rather than topping it.")]
        [SerializeField, Range(0f, 1f)] private float heaveFollow = 0.7f;
        [Tooltip("Smoothing on tilt. A hull this size does not snap.")]
        [SerializeField] private float smoothing = 2.5f;
        [Tooltip("Heave is smoothed harder than tilt: vertical deck movement is the part " +
                 "that fights the riders' CharacterControllers, and roll is free because it " +
                 "only ever touches the visual hull.")]
        [SerializeField] private float heaveSmoothing = 1.8f;
        [Tooltip("Tilt ceiling. Approached asymptotically rather than clipped, so ordinary " +
                 "chop stays gentle while a genuine storm sea can lean the hull right over " +
                 "without the response ever flattening off at a hard limit.")]
        [SerializeField] private float maxTiltDegrees = 24f;

        /// <summary>World-space movement applied this frame. Read by <see cref="BoatRiderCarry"/>.</summary>
        public Vector3 LastFrameDelta { get; private set; }
        /// <summary>Yaw change in degrees this frame. Read by <see cref="BoatRiderCarry"/>.</summary>
        public float LastFrameYawDelta { get; private set; }

        private float _heave;
        private float _pitch;
        private float _roll;
        private bool _initialised;
        private Pose _mooring;

        // Captured before the first LateUpdate overwrites the transform with the wave
        // response - after that there is no record of where the boat was authored.
        private void Awake() => _mooring = new Pose(transform.position, transform.rotation);

        private void LateUpdate()
        {
            // LateUpdate so the water clock (WaterVolume.Update) has already advanced the
            // simulation this frame - sampling in Update would read last frame's swell.
            double t = CurrentTime();

            Vector3 previousPosition = transform.position;
            float previousYaw = transform.eulerAngles.y;

            Vector3 target;
            Vector3 tangent;

            if (moored)
            {
                target = _mooring.position;
                tangent = _mooring.rotation * Vector3.forward;
            }
            else if (helm != null && helm.HasControl)
            {
                // Driven: the server integrates the hull and every peer eases onto the
                // replicated result. See BoatHelm for why this cannot stay a function of
                // time once a human is steering.
                target = helm.Position;
                tangent = helm.Forward;
            }
            else
            {
                // --- course: position and heading straight from server time ---
                float lap = Mathf.Abs(lapSeconds) < 0.01f ? 1f : lapSeconds;
                float phase = (float)(t / lap) * Mathf.PI * 2f;
                Vector3 planar = new(Mathf.Cos(phase) * courseRadius, 0f,
                                     Mathf.Sin(phase) * courseRadius);
                target = courseCentre + planar;

                // Face along the tangent of the circle, the derivative of the above.
                tangent = new Vector3(-Mathf.Sin(phase), 0f, Mathf.Cos(phase));
                if (lapSeconds < 0f) tangent = -tangent;
            }

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

                float meanSurface = (bow + stern + port + starboard) * 0.25f;
                heaveTarget = water.BaseLevel + (meanSurface - water.BaseLevel) * heaveFollow
                              + freeboard;

                // Tilt is the slope across the hull: rise over run, in degrees.
                pitchTarget = -Mathf.Atan2((bow - stern) * waveFollow, hullLength * 2f) * Mathf.Rad2Deg;
                rollTarget = Mathf.Atan2((starboard - port) * waveFollow, hullBeam * 2f) * Mathf.Rad2Deg;

                pitchTarget = SoftLimit(pitchTarget);
                rollTarget = SoftLimit(rollTarget);
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
                _heave = Mathf.Lerp(_heave, heaveTarget,
                                    1f - Mathf.Exp(-heaveSmoothing * Time.deltaTime));
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

        /// <summary>
        /// Saturating limiter: identity for small angles, asymptotic to the ceiling.
        ///
        /// A hard clamp made the tilt useless as a sea state read - ordinary chop already
        /// pinned it at the limit, so a storm looked exactly like a breeze. tanh leaves
        /// gentle water gentle and lets a genuinely big sea keep leaning the hull further,
        /// without ever reaching a value that would read as capsizing.
        /// </summary>
        private float SoftLimit(float degrees) =>
            maxTiltDegrees * (float)System.Math.Tanh(degrees / maxTiltDegrees);

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
