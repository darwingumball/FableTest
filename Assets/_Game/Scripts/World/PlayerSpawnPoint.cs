using UnityEngine;

namespace Game.World
{
    /// <summary>Marks where players appear in a gameplay scene. Registry avoids scene-wide finds.</summary>
    public class PlayerSpawnPoint : MonoBehaviour
    {
        private static PlayerSpawnPoint _active;

        public static bool TryGetSpawnPose(out Vector3 position, out Quaternion rotation)
        {
            if (_active != null)
            {
                position = _active.transform.position;
                rotation = _active.transform.rotation;
                return true;
            }
            position = Vector3.zero;
            rotation = Quaternion.identity;
            return false;
        }

        private void OnEnable() => _active = this;

        private void OnDisable()
        {
            if (_active == this) _active = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _active = null;

        private void OnDrawGizmos()
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(transform.position, 0.4f);
            Gizmos.DrawRay(transform.position, transform.forward);
        }
    }
}
