using Unity.Netcode;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// Closed-loop shuttle (the metro): dwell at A, trapezoid-profile travel to B, dwell,
    /// return. Phase is DERIVED from ((serverTime - loopStart) mod cycle) - never stored -
    /// so late joiners reconstruct the exact leg and position.
    /// </summary>
    public class ShuttlePlatform : PlatformMotionBase
    {
        [Header("Route (from the authored scene position)")]
        [SerializeField] private Vector3 travelDirection = Vector3.forward;
        [SerializeField] private float travelDistance = 40f;

        [Header("Timing (seconds)")]
        [SerializeField] private float dwellSeconds = 8f;
        [SerializeField] private float accelSeconds = 4f;
        [SerializeField] private float cruiseSeconds = 8f;
        [SerializeField] private float decelSeconds = 4f;

        private readonly NetworkVariable<double> _loopStartServerTime = new(0,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private Vector3 _pointA;
        private Quaternion _rotation;

        private float MoveSeconds => accelSeconds + cruiseSeconds + decelSeconds;
        private float CycleSeconds => 2f * (dwellSeconds + MoveSeconds);

        private void Awake()
        {
            _pointA = transform.position;
            _rotation = transform.rotation;
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer)
                _loopStartServerTime.Value = NetworkManager.ServerTime.Time;
        }

        protected override Pose EvaluatePoseAt(double serverTime)
        {
            double cycleT = (serverTime - _loopStartServerTime.Value) % CycleSeconds;
            if (cycleT < 0) cycleT += CycleSeconds;

            float t = (float)cycleT;
            float progress; // 0 at A, 1 at B
            if (t < dwellSeconds)
                progress = 0f;                                        // dwell at A
            else if (t < dwellSeconds + MoveSeconds)
                progress = TrapezoidProgress(t - dwellSeconds);        // A -> B
            else if (t < 2f * dwellSeconds + MoveSeconds)
                progress = 1f;                                        // dwell at B
            else
                progress = 1f - TrapezoidProgress(t - 2f * dwellSeconds - MoveSeconds); // B -> A

            Vector3 pos = _pointA + travelDirection.normalized * (travelDistance * progress);
            return new Pose(pos, _rotation);
        }

        /// <summary>Normalized distance (0..1) along an accel/cruise/decel velocity profile.</summary>
        private float TrapezoidProgress(float t)
        {
            // vmax chosen so the profile integrates to exactly travelDistance -> normalize to 1.
            float area = cruiseSeconds + 0.5f * (accelSeconds + decelSeconds);
            float vmax = 1f / Mathf.Max(0.01f, area);

            float x;
            if (t <= accelSeconds)
                x = 0.5f * vmax / Mathf.Max(0.01f, accelSeconds) * t * t;
            else if (t <= accelSeconds + cruiseSeconds)
                x = 0.5f * vmax * accelSeconds + vmax * (t - accelSeconds);
            else
            {
                float td = Mathf.Min(t - accelSeconds - cruiseSeconds, decelSeconds);
                x = 0.5f * vmax * accelSeconds + vmax * cruiseSeconds
                    + vmax * td - 0.5f * vmax / Mathf.Max(0.01f, decelSeconds) * td * td;
            }
            return Mathf.Clamp01(x);
        }
    }
}
