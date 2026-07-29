using Game.Net;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// Carries the LOCAL player with a moving platform: while the player stands inside the
    /// cabin bounds, the platform's per-frame delta is fed into the CharacterController
    /// through <see cref="Game.Player.FirstPersonController.ApplyCarry"/> (applied the same
    /// frame, so the rider never trails the cabin). Deltas larger than the teleport threshold (late-join snap,
    /// schedule jump) are swallowed - the rebase fix the old metro lacked.
    /// </summary>
    [RequireComponent(typeof(PlatformMotionBase))]
    public class MovingPlatformCarry : MonoBehaviour
    {
        [Tooltip("Cabin volume in platform-local space; players inside get carried.")]
        [SerializeField] private Vector3 cabinCenter = new(0f, 1.5f, 0f);
        [SerializeField] private Vector3 cabinSize = new(3f, 3f, 8f);
        [SerializeField] private float teleportThreshold = 4f;

        private PlatformMotionBase _platform;

        private void Awake() => _platform = GetComponent<PlatformMotionBase>();

        private void LateUpdate()
        {
            Vector3 delta = _platform.LastFrameDelta;
            if (delta == Vector3.zero) return;
            if (delta.magnitude > teleportThreshold) return; // snap/late-join, don't fling riders

            var local = NetworkPlayer.Local;
            if (local == null || local.Controller == null) return;

            // Player position relative to cabin, in platform space (before this frame's delta
            // was already applied to the platform transform in Update).
            Vector3 playerLocal = transform.InverseTransformPoint(local.transform.position);
            Vector3 offset = playerLocal - cabinCenter;
            Vector3 half = cabinSize * 0.5f;
            bool inside = Mathf.Abs(offset.x) <= half.x
                && Mathf.Abs(offset.y) <= half.y
                && Mathf.Abs(offset.z) <= half.z;
            if (inside)
                local.Controller.ApplyCarry(delta);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.yellow;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(cabinCenter, cabinSize);
        }
    }
}
