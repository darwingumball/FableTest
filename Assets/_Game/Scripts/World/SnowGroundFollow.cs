using Game.Net;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// Keeps the snow mesh centred on the local player.
    ///
    /// The mesh is a concentric-LOD grid (see SnowGroundBuilder): dense in the middle,
    /// progressively coarser outward. That only pays off if the dense middle is where the
    /// camera is, so the whole thing rides along with the player.
    ///
    /// The catch is that a freely-sliding grid makes the surface crawl: every vertex lands
    /// on a different world position each frame, so it samples a different part of the
    /// deformation texture and the snow appears to shimmer and swim. Snapping the origin to
    /// a fixed world lattice fixes it - vertices then only ever occupy the same discrete set
    /// of world positions, and the mesh moves in whole cells instead of sliding.
    ///
    /// The snap step must be a multiple of the COARSEST level's cell size, or the coarse
    /// rings themselves start crawling.
    /// </summary>
    public class SnowGroundFollow : MonoBehaviour
    {
        [Tooltip("World-space lattice the mesh origin snaps to, in metres. Must be a " +
                 "multiple of the coarsest LOD step or the outer rings will crawl.")]
        [SerializeField] private float snapStep = 8f;

        private Transform _target;
        private Vector3 _origin;
        private bool _haveOrigin;

        private void OnEnable()
        {
            _origin = transform.position;
            _haveOrigin = true;
        }

        private void LateUpdate()
        {
            if (!_haveOrigin) return;

            if (_target == null)
            {
                var player = NetworkPlayer.Local;
                if (player == null) return;      // menus / pre-spawn: leave it parked
                _target = player.transform;
            }

            float step = Mathf.Max(snapStep, 0.01f);
            Vector3 p = _target.position;
            transform.position = new Vector3(
                Mathf.Round(p.x / step) * step,
                _origin.y,
                Mathf.Round(p.z / step) * step);
        }
    }
}
