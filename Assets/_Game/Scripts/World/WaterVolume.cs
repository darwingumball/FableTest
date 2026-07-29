using Game.Net;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace Game.World
{
    /// <summary>
    /// Makes an HDRP water surface deterministic across peers, and exposes height queries.
    ///
    /// HDRP advances its water simulation on the local clock, which would put every peer on
    /// a different wave phase - the boat would sit in a different place on each machine and
    /// a late joiner would arrive mid-swell with no way to agree. <c>simulationTime</c> has a
    /// public setter, so driving it from <c>NetworkManager.ServerTime.Time</c> makes the
    /// water a pure function of server time, exactly like the metro and the elevator. The
    /// visuals and the CPU height queries then agree on every machine for free, and nothing
    /// about the water or anything floating on it needs replicating.
    ///
    /// Height queries need "script interactions" on BOTH the water surface and the HDRP
    /// asset, or <see cref="WaterSurface.ProjectPointOnWaterSurface"/> silently returns false.
    /// </summary>
    [RequireComponent(typeof(WaterSurface))]
    public class WaterVolume : MonoBehaviour
    {
        /// <summary>The active water body. Set on enable; null when there is no water.</summary>
        public static WaterVolume Instance { get; private set; }

        [Tooltip("Scales how fast the swell evolves. Applied to server time, so every peer " +
                 "still agrees.")]
        [SerializeField] private float timeScale = 1f;
        [Tooltip("Search iterations per height query. Higher is more accurate on steep " +
                 "waves and costs more; 4 is plenty for buoyancy.")]
        [SerializeField] private int searchIterations = 4;
        [SerializeField] private float searchError = 0.01f;

        private WaterSurface _surface;
        private Transform _decalAnchor;

        /// <summary>Approximate still-water level, for cheap "am I under water at all" tests.</summary>
        public float BaseLevel => transform.position.y;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Instance = null;

        private void OnEnable()
        {
            _surface = GetComponent<WaterSurface>();
            _surface.scriptInteractions = true;   // without this every query returns false
            Instance = this;
        }

        private void OnDisable()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (_surface == null) return;

            // Server time, not Time.time. This single line is what makes the water - and
            // therefore the boat riding on it - identical on every peer and correct for a
            // late joiner on their first frame.
            var net = NetworkManager.Singleton;
            double t = net != null && net.IsListening ? net.ServerTime.Time : Time.timeAsDouble;
            _surface.simulationTime = (float)(t * timeScale);

            UpdateDecalAnchor();
        }

        /// <summary>
        /// Keeps the wake/foam region centred on the local player.
        ///
        /// Decals only render inside a finite region, and HDRP centres it on
        /// <c>Camera.main</c> - falling back to the WORLD ORIGIN when nothing carries the
        /// MainCamera tag. That fallback is silent and looks exactly like broken decals:
        /// with a 200 m region at the origin, the only things that ever foamed were the one
        /// crate inside it, and the boat - out at z=106 and beyond - never did.
        ///
        /// Pointing the anchor at the player directly is better than relying on the tag
        /// anyway: in a networked game every peer has a player camera, and Camera.main
        /// returns whichever tagged one it finds first.
        /// </summary>
        private void UpdateDecalAnchor()
        {
            // Re-acquires by itself when the player despawns, because the destroyed
            // Transform compares equal to null.
            if (_decalAnchor != null) return;

            var local = NetworkPlayer.Local;
            if (local == null) return;

            var cam = local.GetComponentInChildren<Camera>();
            _decalAnchor = cam != null ? cam.transform : local.transform;
            _surface.decalRegionAnchor = _decalAnchor;
        }

        /// <summary>
        /// Water height and normal under a world position. Returns false when there is no
        /// water surface or the CPU simulation is not ready yet (the first frame or two),
        /// in which case callers must NOT assume the point is dry - just skip this frame.
        /// </summary>
        public bool SampleSurface(Vector3 worldPosition, out float height, out Vector3 normal)
        {
            height = BaseLevel;
            normal = Vector3.up;
            if (_surface == null) return false;

            var search = new WaterSearchParameters
            {
                targetPositionWS = worldPosition,
                startPositionWS = worldPosition,
                error = searchError,
                maxIterations = searchIterations,
                includeDeformation = true,
                excludeSimulation = false,
                outputNormal = true,
            };

            if (!_surface.ProjectPointOnWaterSurface(search, out WaterSearchResult result))
                return false;

            height = result.projectedPositionWS.y;
            normal = ((Vector3)result.normalWS).normalized;
            return true;
        }

        /// <summary>Convenience: height only, falling back to the still level.</summary>
        public float SampleHeight(Vector3 worldPosition) =>
            SampleSurface(worldPosition, out float h, out _) ? h : BaseLevel;

        /// <summary>Metres the point is below the surface. Negative means above it.</summary>
        public float SubmersionAt(Vector3 worldPosition) =>
            SampleHeight(worldPosition) - worldPosition.y;
    }
}
