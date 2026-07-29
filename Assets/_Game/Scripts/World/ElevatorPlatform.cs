using Unity.Netcode;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// Request-driven vertical platform. Schedule NVs (from/to floor + start time) replace
    /// any local motion state; movement is smoothstep between floor heights evaluated
    /// against the server clock, so late joiners see the cab exactly where everyone else does.
    /// </summary>
    public class ElevatorPlatform : PlatformMotionBase
    {
        [Tooltip("Floor heights as offsets from the authored Y position.")]
        [SerializeField] private float[] floorOffsets = { 0f, 6f };
        [SerializeField] private float metersPerSecond = 2f;

        private readonly NetworkVariable<int> _fromFloor = new(0,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<int> _toFloor = new(0,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<double> _moveStartServerTime = new(0,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private Vector3 _basePosition;
        private Quaternion _rotation;

        public int FloorCount => floorOffsets.Length;
        public int TargetFloor => _toFloor.Value;

        private void Awake()
        {
            _basePosition = transform.position;
            _rotation = transform.rotation;
        }

        protected override Pose EvaluatePoseAt(double serverTime)
        {
            float fromY = floorOffsets[Mathf.Clamp(_fromFloor.Value, 0, floorOffsets.Length - 1)];
            float toY = floorOffsets[Mathf.Clamp(_toFloor.Value, 0, floorOffsets.Length - 1)];
            float duration = Mathf.Max(0.05f, Mathf.Abs(toY - fromY) / metersPerSecond);
            float t = Mathf.Clamp01((float)((serverTime - _moveStartServerTime.Value) / duration));
            float y = Mathf.Lerp(fromY, toY, Mathf.SmoothStep(0f, 1f, t));
            return new Pose(_basePosition + Vector3.up * y, _rotation);
        }

        // ---------------- requests ----------------

        public void RequestGoToFloor(int floor)
        {
            if (IsServer) ServerGoToFloor(floor);
            else GoToFloorServerRpc(floor);
        }

        /// <summary>Convenience for a single call button: go to the next floor, wrapping.</summary>
        public void RequestNextFloor() => RequestGoToFloor((_toFloor.Value + 1) % FloorCount);

        [ServerRpc(RequireOwnership = false)]
        private void GoToFloorServerRpc(int floor) => ServerGoToFloor(floor);

        private void ServerGoToFloor(int floor)
        {
            floor = Mathf.Clamp(floor, 0, floorOffsets.Length - 1);
            if (floor == _toFloor.Value) return;

            // Depart from wherever the cab is heading now (mid-move requests re-anchor).
            _fromFloor.Value = _toFloor.Value;
            _toFloor.Value = floor;
            _moveStartServerTime.Value = NetworkManager.ServerTime.Time;
        }
    }
}
