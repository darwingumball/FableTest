using Unity.Netcode;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// Base for every moving platform (metro shuttle, elevators). THE fix for the old
    /// project's late-join desync: motion is a pure function of the replicated server
    /// clock - every peer (host and clients alike) evaluates the same
    /// <see cref="EvaluatePoseAt"/> each frame and writes the transform locally.
    /// No NetworkTransform, no local state machine, no offline fallback (solo is a host);
    /// the schedule NetworkVariables in subclasses ARE the replication, so a client that
    /// joins mid-journey computes the identical position on its first frame.
    /// </summary>
    public abstract class PlatformMotionBase : NetworkBehaviour
    {
        /// <summary>World-space delta applied this frame; consumed by MovingPlatformCarry.</summary>
        public Vector3 LastFrameDelta { get; private set; }

        protected abstract Pose EvaluatePoseAt(double serverTime);

        protected virtual void Update()
        {
            if (!IsSpawned) return;
            var pose = EvaluatePoseAt(NetworkManager.ServerTime.Time);
            LastFrameDelta = pose.position - transform.position;
            transform.SetPositionAndRotation(pose.position, pose.rotation);
        }
    }
}
